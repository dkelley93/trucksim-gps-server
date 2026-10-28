using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funbit.Ets.Telemetry.Server.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public sealed class StandbyHandshake
    {
        public string SessionId;
        public string DeviceId;
        public string NonceA;
        public string Env;
        public string PcTicket;
        public string StandbyToken;
    }

    public sealed class StandbyPolicy
    {
        public bool ParkWithoutGame = true;
        public int GraceMinutes = 10;
    }

    public enum WindowPresence
    {
        Hidden,
        Background,
        Front
    }

    public sealed class ParkingRules
    {
        bool _game;
        WindowPresence _window = WindowPresence.Hidden;
        DateTime _graceUntil = DateTime.MinValue;

        public bool Update(bool game, WindowPresence window, StandbyPolicy policy, DateTime now)
        {
            var grace = now + TimeSpan.FromMinutes(Math.Max(1, policy.GraceMinutes));
            if (_game && !game)
                _graceUntil = grace;
            else if (!game && policy.ParkWithoutGame && _window == WindowPresence.Front && window == WindowPresence.Background && grace > _graceUntil)
                _graceUntil = grace;
            if (!game && window == WindowPresence.Hidden && _window != WindowPresence.Hidden)
                _graceUntil = DateTime.MinValue;
            _game = game;
            _window = window;
            return game || policy.ParkWithoutGame && window == WindowPresence.Front || now < _graceUntil;
        }
    }

    public sealed class StandbyClient : IDisposable
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly int[] ReconnectSeconds = { 30, 120, 600, 1800 };
        static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
        static readonly TimeSpan SteadyPark = TimeSpan.FromMinutes(5);
        static readonly TimeSpan FreshTokenMargin = TimeSpan.FromDays(7);
        const int MaxFrameBytes = 64 * 1024;
        const int CloseReplaced = 4001;
        const int CloseFull = 4003;

        readonly Settings _settings;
        readonly Func<Task<IReadOnlyList<string>>> _relays;
        readonly Func<StandbyHandshake, Task> _onHandshake;
        readonly TimeSpan _silenceLimit;
        readonly Func<int, TimeSpan> _retryDelay;
        readonly ParkingRules _rules = new ParkingRules();
        readonly object _lock = new object();
        readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        ClientWebSocket _socket;
        CancellationTokenSource _cts;
        Timer _retryTimer;
        bool _serverRunning;
        bool _game;
        WindowPresence _window;
        bool _wanted;
        bool _connecting;
        bool _held;
        int _failures;
        long _lastFrame;

        public StandbyClient(Settings settings, Func<Task<IReadOnlyList<string>>> relays, Func<StandbyHandshake, Task> onHandshake)
            : this(settings, relays, onHandshake, TimeSpan.FromSeconds(150), failures => PresenceClient.Jittered(ReconnectSeconds[Math.Min(failures, ReconnectSeconds.Length - 1)]))
        {
        }

        public StandbyClient(Settings settings, Func<Task<IReadOnlyList<string>>> relays, Func<StandbyHandshake, Task> onHandshake,
            TimeSpan silenceLimit, Func<int, TimeSpan> retryDelay)
        {
            _settings = settings;
            _relays = relays;
            _onHandshake = onHandshake;
            _silenceLimit = silenceLimit;
            _retryDelay = retryDelay;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
        }

        public bool IsParked
        {
            get
            {
                lock (_lock)
                    return _socket != null;
            }
        }

        public string ParkedRelay { get; private set; }

        public event Action Connected;

        public static bool IsFreshToken(string token, DateTime now)
        {
            byte[] bytes;
            try
            {
                bytes = string.IsNullOrEmpty(token) ? null : Crypto.FromBase64Url(token);
            }
            catch (FormatException)
            {
                return false;
            }
            if (bytes == null || bytes.Length != 56)
                return false;
            long exp = 0;
            for (int i = 16; i < 24; i++)
                exp = exp << 8 | bytes[i];
            return exp - new DateTimeOffset(now).ToUnixTimeSeconds() > (long)FreshTokenMargin.TotalSeconds;
        }

        public void Update(bool serverRunning, bool game, WindowPresence window)
        {
            lock (_lock)
            {
                _serverRunning = serverRunning;
                _game = game;
                _window = window;
            }
            Reevaluate();
        }

        public void Reevaluate()
        {
            bool want;
            lock (_lock)
            {
                want = _rules.Update(_game, _window, _settings.StandbyPolicy, DateTime.UtcNow) && _serverRunning && !string.IsNullOrEmpty(_settings.StandbyToken);
                if (want == _wanted)
                    return;
                _wanted = want;
                _held = false;
                _failures = 0;
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
            if (want)
                Connect();
            else
                Unpark();
        }

        public void NewToken()
        {
            lock (_lock)
            {
                _held = false;
                _failures = 0;
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
            Reevaluate();
            Connect();
        }

        public void Close()
        {
            lock (_lock)
                _wanted = false;
            Unpark();
        }

        void Unpark()
        {
            lock (_lock)
            {
                _retryTimer?.Dispose();
                _retryTimer = null;
                _cts?.Cancel();
            }
        }

        void OnNetworkAvailability(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable)
                return;
            lock (_lock)
            {
                if (_retryTimer == null)
                    return;
                _retryTimer.Dispose();
                _retryTimer = null;
            }
            Connect();
        }

        void Connect()
        {
            string token;
            CancellationToken cancel;
            lock (_lock)
            {
                if (!_wanted || _held || _socket != null || _connecting)
                    return;
                _connecting = true;
                _cts = new CancellationTokenSource();
                cancel = _cts.Token;
                token = _settings.StandbyToken;
            }
            _ = Task.Run(() => Run(token, cancel));
        }

        async Task Run(string token, CancellationToken cancel)
        {
            IReadOnlyList<string> relays;
            try
            {
                relays = await _relays();
            }
            catch (Exception ex)
            {
                Log.InfoFormat("Standby relay list failed ({0})", ex.Message);
                relays = new string[0];
            }

            int refused = 0;
            foreach (var relay in relays)
            {
                if (cancel.IsCancellationRequested)
                    break;
                var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.Zero;
                socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        timeout.CancelAfter(ConnectTimeout);
                        await socket.ConnectAsync(StandbyUri(relay), timeout.Token);
                    }
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    if (StatusOf(ex) == 401)
                    {
                        refused++;
                        continue;
                    }
                    if (!cancel.IsCancellationRequested)
                        Log.InfoFormat("Standby on {0} failed ({1})", relay, StatusOf(ex) > 0 ? StatusOf(ex).ToString() : ex.Message);
                    continue;
                }

                var parkedAt = DateTime.UtcNow;
                lock (_lock)
                {
                    _socket = socket;
                    _connecting = false;
                    ParkedRelay = relay;
                    Interlocked.Exchange(ref _lastFrame, parkedAt.Ticks);
                }
                int code = await Hold(socket, cancel);
                bool again = false;
                lock (_lock)
                {
                    _socket = null;
                    ParkedRelay = null;
                    socket.Dispose();
                    if (code == CloseFull && !cancel.IsCancellationRequested)
                    {
                        Log.InfoFormat("Standby relay {0} is full, trying the next", relay);
                        _connecting = true;
                        continue;
                    }
                    if (cancel.IsCancellationRequested)
                    {
                        Log.Info("Standby unparked");
                        again = _wanted && !_held;
                    }
                    else if (code == CloseReplaced)
                    {
                        Log.Info("Standby replaced by another PC Telemetry with this PC's id");
                        _held = true;
                    }
                    else
                    {
                        if (DateTime.UtcNow - parkedAt >= SteadyPark)
                            _failures = 0;
                        Log.InfoFormat("Standby dropped ({0})", code);
                        ScheduleRetryLocked();
                    }
                }
                if (again)
                    Connect();
                return;
            }

            if (refused > 0 && refused == relays.Count && !cancel.IsCancellationRequested)
            {
                DropToken(token);
                return;
            }

            bool retry;
            lock (_lock)
            {
                _connecting = false;
                retry = !cancel.IsCancellationRequested;
                if (retry)
                    ScheduleRetryLocked();
            }
            if (!retry && _wanted)
                Connect();
        }

        void ScheduleRetryLocked()
        {
            if (!_wanted || _held)
                return;
            var delay = _retryDelay(_failures);
            _failures++;
            _retryTimer?.Dispose();
            _retryTimer = new Timer(_ =>
            {
                lock (_lock)
                    _retryTimer = null;
                Connect();
            }, null, delay, Timeout.InfiniteTimeSpan);
        }

        void DropToken(string token)
        {
            Log.Info("Standby token refused, waiting for a relay connection to issue a new one");
            lock (_lock)
            {
                _connecting = false;
                if (_settings.StandbyToken == token)
                {
                    _settings.StandbyToken = null;
                    _settings.Save();
                }
            }
            Reevaluate();
            Connect();
        }

        static Uri StandbyUri(string relay)
        {
            var uri = new UriBuilder(relay.TrimEnd('/') + "/standby");
            uri.Scheme = uri.Scheme == "http" ? "ws" : "wss";
            return uri.Uri;
        }

        async Task<int> Hold(ClientWebSocket socket, CancellationToken cancel)
        {
            var receive = Receive(socket);
            while (true)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(1), cancel);
                if (await Task.WhenAny(receive, tick) == receive)
                    return await receive;
                if (cancel.IsCancellationRequested)
                {
                    try
                    {
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                    }
                    catch (Exception)
                    {
                        socket.Abort();
                    }
                    return await receive;
                }
                if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastFrame)) > _silenceLimit)
                {
                    Log.Info("Standby line silent");
                    socket.Abort();
                    return await receive;
                }
            }
        }

        async Task<int> Receive(ClientWebSocket socket)
        {
            var buffer = new byte[4096];
            var message = new MemoryStream();
            bool parked = false;
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return (int?)result.CloseStatus ?? 0;
                    Interlocked.Exchange(ref _lastFrame, DateTime.UtcNow.Ticks);
                    if (message.Length + result.Count > MaxFrameBytes)
                        return 0;
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;
                    string text = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    if (!parked)
                    {
                        parked = true;
                        Log.InfoFormat("Standby parked on {0}", ParkedRelay);
                        Connected?.Invoke();
                    }
                    if (text == "ping")
                        await Send(socket, "pong");
                    else
                        OnFrame(text);
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        void OnFrame(string text)
        {
            JObject frame;
            try
            {
                frame = JObject.Parse(text);
            }
            catch (Exception)
            {
                return;
            }
            var type = (string)frame["t"];
            if (type == "policy")
                ApplyPolicy(frame);
            if (type != "handshake")
                return;
            var handshake = new StandbyHandshake
            {
                SessionId = (string)frame["sessionId"],
                DeviceId = (string)frame["deviceId"],
                NonceA = (string)frame["nonceA"],
                Env = (string)frame["env"],
                PcTicket = (string)frame["pcTicket"],
                StandbyToken = (string)frame["standbyToken"]
            };
            if (handshake.SessionId == null || handshake.DeviceId == null || handshake.NonceA == null || handshake.Env == null)
                return;
            _ = Task.Run(() => _onHandshake(handshake));
        }

        void ApplyPolicy(JObject frame)
        {
            var current = _settings.StandbyPolicy;
            StandbyPolicy policy;
            try
            {
                policy = new StandbyPolicy
                {
                    ParkWithoutGame = (bool?)frame["parkWithoutGame"] ?? current.ParkWithoutGame,
                    GraceMinutes = Math.Max(1, (int?)frame["graceMinutes"] ?? current.GraceMinutes)
                };
            }
            catch (Exception)
            {
                return;
            }
            if (policy.ParkWithoutGame == current.ParkWithoutGame && policy.GraceMinutes == current.GraceMinutes)
                return;
            _settings.StandbyPolicy = policy;
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("Standby policy not saved: " + ex.Message);
            }
            Log.InfoFormat("Standby policy: park without the game {0}, grace {1} min", policy.ParkWithoutGame, policy.GraceMinutes);
            Reevaluate();
        }

        public async Task<bool> SendReply(string sessionId, bool ok, string envR, string relayUrl)
        {
            ClientWebSocket socket;
            lock (_lock)
                socket = _socket;
            return socket != null && await Send(socket, JsonConvert.SerializeObject(new { t = "reply", sessionId, ok, envR, relayUrl }));
        }

        async Task<bool> Send(ClientWebSocket socket, string text)
        {
            await _sendLock.WaitAsync();
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        static int StatusOf(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var response = (e as WebException)?.Response as HttpWebResponse;
                if (response != null)
                {
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    return status;
                }
            }
            return 0;
        }

        public void Dispose()
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
            Close();
        }
    }
}
