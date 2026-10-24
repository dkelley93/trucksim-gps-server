using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Funbit.Ets.Telemetry.Server.Helpers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Funbit.Ets.Telemetry.Server.Services
{
    [DataContract]
    public sealed class HandshakeTicket
    {
        [DataMember(Name = "ticket")] public string Ticket { get; set; }
        [DataMember(Name = "standbyToken")] public string StandbyToken { get; set; }
    }

    public sealed class PresenceClient : IDisposable
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly TimeSpan RelayRemeasure = TimeSpan.FromMinutes(10);
        static readonly TimeSpan FailedRelayHold = TimeSpan.FromSeconds(90);
        static readonly TimeSpan MeasurementWait = TimeSpan.FromSeconds(3);
        const int MeasureRetryFirstSeconds = 15;
        const int MeasureRetryMaxSeconds = 120;
        const int MaxMeasureRetries = 5;
        static readonly int[] ReconnectSeconds = { 1, 2, 5, 10, 30, 60 };
        const int MaxBackoffSeconds = 300;
        static readonly Random Rng = new Random();

        string _apiBaseUrl;
        readonly Settings _settings;
        readonly LinkedDeviceStore _devices;
        readonly PairingSessionService _pairing;
        readonly Func<IReadOnlyList<string>> _candidates;
        readonly int _loopbackPort;
        readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        readonly List<RelayTunnelClient> _tunnels = new List<RelayTunnelClient>();
        readonly Dictionary<string, DateTime> _failedRelays = new Dictionary<string, DateTime>();
        readonly object _lock = new object();
        readonly StandbyClient _standby;
        HubConnection _hub;
        CancellationTokenSource _runCts;
        Timer _measureRetryTimer;
        DateTime _measuredAt;
        volatile string _relayUrl;
        Task _measurement = Task.CompletedTask;
        bool _measuring;
        bool _measureAgain;
        int _measureRetries;
        bool _wanted;
        bool _connecting;
        bool _restartPending;

        public PresenceClient(string apiBaseUrl, Settings settings, LinkedDeviceStore devices, PairingSessionService pairing,
            Func<IReadOnlyList<string>> candidates, int loopbackPort)
        {
            _apiBaseUrl = apiBaseUrl;
            _settings = settings;
            _devices = devices;
            _pairing = pairing;
            _candidates = candidates;
            _loopbackPort = loopbackPort;
            _relayUrl = settings.PreferredRelayUrl ?? "";
            devices.Revoked += DropTunnels;
            _standby = new StandbyClient(settings, () => RelaySelector.ListAsync(_http, _apiBaseUrl), OnStandbyHandshake);
            _standby.Connected += MeasureIfStale;
        }

        // A moment later, so an unlink request that arrived through the tunnel still gets its reply.
        void DropTunnels(string deviceId)
        {
            Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ =>
            {
                RelayTunnelClient[] open;
                lock (_tunnels)
                    open = _tunnels.Where(t => t.DeviceId == deviceId).ToArray();
                foreach (var tunnel in open)
                    tunnel.Dispose();
            });
        }

        public bool IsConnected => _hub?.State == HubConnectionState.Connected;

        public void UpdateStandby(bool serverRunning, bool game, WindowPresence window) => _standby.Update(serverRunning, game, window);

        public Dictionary<string, ThroughputMeter> SampleThroughput()
        {
            RelayTunnelClient[] tunnels;
            lock (_tunnels)
                tunnels = _tunnels.ToArray();
            var now = DateTime.UtcNow;
            var result = new Dictionary<string, ThroughputMeter>();
            foreach (var tunnel in tunnels)
            {
                tunnel.Throughput.Sample(tunnel.BytesSent, tunnel.BytesReceived, now);
                result[tunnel.DeviceId] = tunnel.Throughput;
            }
            return result;
        }

#if DEBUG
        public void UseApi(string apiBaseUrl)
        {
            bool wanted;
            lock (_lock)
                wanted = _wanted;
            Stop();
            _standby.Close();
            lock (_lock)
            {
                _apiBaseUrl = apiBaseUrl;
                _relayUrl = "";
                _measuredAt = DateTime.MinValue;
            }
            if (wanted)
                SetWanted(true);
        }
#endif

        public void SetWanted(bool wanted)
        {
            if (!wanted)
            {
                Stop();
                return;
            }
            lock (_lock)
            {
                if (_wanted)
                    return;
                _wanted = true;
                _runCts = new CancellationTokenSource();
                StartRun();
            }
        }

        void Stop()
        {
            HubConnection hub;
            lock (_lock)
            {
                if (!_wanted)
                    return;
                _wanted = false;
                _runCts.Cancel();
                hub = _hub;
                _hub = null;
            }
            if (hub != null)
                _ = hub.DisposeAsync();
            Log.Info("Presence disconnected");
        }

        void StartRun()
        {
            lock (_lock)
            {
                if (!_wanted)
                    return;
                if (_connecting)
                {
                    _restartPending = true;
                    return;
                }
                _connecting = true;
                _ = Run(_runCts.Token);
            }
        }

        async Task Run(CancellationToken token)
        {
            try
            {
                int backoff = 5;
                while (!token.IsCancellationRequested)
                {
                    HubConnection hub = null;
                    try
                    {
                        hub = BuildHub();
                        await hub.StartAsync(token);
                        if (await RegisterAsync(hub, token))
                        {
                            lock (_lock)
                                _hub = hub;
                            Log.Info("Presence connected");
                            return;
                        }
                        Log.WarnFormat("Presence registration refused, retrying in about {0} s", backoff);
                    }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested)
                        {
                            if (hub != null)
                                await hub.DisposeAsync();
                            return;
                        }
                        Log.WarnFormat("Presence connect failed ({0}), retrying in about {1} s", ex.Message, backoff);
                    }
                    if (hub != null)
                        await hub.DisposeAsync();
                    try
                    {
                        await Task.Delay(Jittered(backoff), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    backoff = Math.Min(backoff * 2, MaxBackoffSeconds);
                }
            }
            finally
            {
                bool restart;
                lock (_lock)
                {
                    _connecting = false;
                    restart = _restartPending && _wanted;
                    _restartPending = false;
                }
                if (restart)
                    StartRun();
            }
        }

        HubConnection BuildHub()
        {
            var hub = new HubConnectionBuilder()
                .WithUrl($"{_apiBaseUrl}/hubs/pc?pcId={_settings.PcId}")
                .AddMessagePackProtocol()
                .WithAutomaticReconnect(new RetryPolicy())
                .Build();
            hub.On<string, string, string, string, bool>("HandshakeRequest",
                (sessionId, deviceId, nonceA, env, pairing) => Task.Run(() => OnHandshake(sessionId, deviceId, nonceA, env, pairing, async (answer, relayUrl) =>
                {
                    var result = await hub.InvokeAsync<HandshakeTicket>("HandshakeReply", sessionId, answer.Ok, answer.EnvR, relayUrl);
                    StoreStandbyToken(result.StandbyToken);
                    return result.Ticket;
                })));
            hub.Reconnected += async _ =>
            {
                if (!await RegisterAsync(hub, CancellationToken.None))
                    Restart(hub);
            };
            hub.Closed += _ =>
            {
                Restart(hub);
                return Task.CompletedTask;
            };
            return hub;
        }

        void Restart(HubConnection hub)
        {
            lock (_lock)
            {
                if (hub != _hub)
                    return;
                _hub = null;
            }
            _ = hub.DisposeAsync();
            StartRun();
        }

        async Task<bool> RegisterAsync(HubConnection hub, CancellationToken token)
        {
            bool ok = await hub.InvokeAsync<bool>("Register", _settings.PcId, CloudConfig.ProtocolVersion, token);
            if (ok)
                MeasureIfStale();
            return ok;
        }

        void MeasureIfStale()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _measuredAt < RelayRemeasure)
                    return;
            }
            MeasureRelays(false);
        }

        Task MeasureRelays(bool againIfBusy)
        {
            lock (_lock)
            {
                if (_measuring)
                    _measureAgain |= againIfBusy;
                else
                {
                    _measuring = true;
                    _measurement = Task.Run(() => MeasureUntilSettled());
                }
                return _measurement;
            }
        }

        async Task MeasureUntilSettled()
        {
            while (true)
            {
                bool measured = await MeasureOnce();
                lock (_lock)
                {
                    if (_measureAgain)
                    {
                        _measureAgain = false;
                        continue;
                    }
                    _measuring = false;
                    ScheduleMeasureRetry(measured);
                    return;
                }
            }
        }

        async Task<bool> MeasureOnce()
        {
            var excluded = FailedRelays();
            string best;
            try
            {
                best = await RelaySelector.MeasureAsync(_http, _apiBaseUrl, excluded);
            }
            catch (Exception ex)
            {
                Log.Warn("Relay measurement failed: " + ex.Message);
                return false;
            }
            if (best == null)
            {
                Log.Info("Relay measurement found no relay");
                return false;
            }
            lock (_lock)
            {
                if (!excluded.Contains(best) && _failedRelays.ContainsKey(best))
                    return false;
                _relayUrl = best;
                _measuredAt = DateTime.UtcNow;
            }
            if (_settings.PreferredRelayUrl != best)
            {
                _settings.PreferredRelayUrl = best;
                _settings.Save();
            }
            return true;
        }

        void ScheduleMeasureRetry(bool measured)
        {
            _measureRetryTimer?.Dispose();
            _measureRetryTimer = null;
            if (measured || _measureRetries >= MaxMeasureRetries)
            {
                _measureRetries = 0;
                return;
            }
            var delay = Jittered(Math.Min(MeasureRetryFirstSeconds << _measureRetries, MeasureRetryMaxSeconds));
            _measureRetries++;
            _measureRetryTimer = new Timer(_ => MeasureRelays(false), null, delay, Timeout.InfiniteTimeSpan);
        }

        HashSet<string> FailedRelays()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                foreach (var url in _failedRelays.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                    _failedRelays.Remove(url);
                return new HashSet<string>(_failedRelays.Keys);
            }
        }

        async Task<string> RelayForAnswer()
        {
            string url = _relayUrl;
            if (url.Length > 0)
                return url;
            await Task.WhenAny(MeasureRelays(false), Task.Delay(MeasurementWait));
            return _relayUrl;
        }

        async Task OnRelayFault(string url, bool full, bool joinFailed = false)
        {
            if (!full && !joinFailed)
            {
                await Task.Delay(Jittered(1));
                if (await RelaySelector.PingAsync(_http, url))
                    return;
            }
            lock (_lock)
            {
                _failedRelays[url] = DateTime.UtcNow + FailedRelayHold;
                if (_relayUrl == url)
                    _relayUrl = "";
            }
            Log.InfoFormat("Relay {0} {1}, picking another", url, full ? "is full" : "is not answering");
            MeasureRelays(true);
        }

        Task OnStandbyHandshake(StandbyHandshake handshake)
        {
            return OnHandshake(handshake.SessionId, handshake.DeviceId, handshake.NonceA, handshake.Env, false, async (answer, relayUrl) =>
            {
                if (!await _standby.SendReply(handshake.SessionId, answer.Ok, answer.EnvR, relayUrl) || !answer.Ok)
                    return "";
                StoreStandbyToken(handshake.StandbyToken);
                return handshake.PcTicket;
            });
        }

        void StoreStandbyToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token == _settings.StandbyToken)
                return;
            _settings.StandbyToken = token;
            _settings.Save();
            _standby.NewToken();
        }

        async Task OnHandshake(string sessionId, string deviceId, string nonceA, string env, bool pairing, Func<HandshakeAnswer, string, Task<string>> reply)
        {
            try
            {
                byte[] key = pairing ? _pairing.LiveSecret : _devices.GetKey(deviceId);
                string relayUrl = await RelayForAnswer();
                var answer = HandshakeResponder.Answer(key, _candidates(), relayUrl, nonceA, env);
                Log.InfoFormat("Handshake from device {0}... (pairing: {1}): {2}", deviceId.Substring(0, 8), pairing, answer.Ok ? "accepted" : "rejected");
                string ticket = await reply(answer, answer.Ok ? relayUrl : "");
                MeasureIfStale();
                if (!answer.Ok || string.IsNullOrEmpty(ticket) || relayUrl.Length == 0)
                    return;

                if (!pairing)
                    _devices.Touch(deviceId, answer.DeviceName, ConnectionMethod.Relay);
                var tunnel = new RelayTunnelClient(relayUrl, ticket, answer.KeyAppToPc, answer.KeyPcToApp, _loopbackPort, deviceId);
                tunnel.Closed += t =>
                {
                    lock (_tunnels)
                        _tunnels.Remove(t);
                    if (t.RelayFull || t.RelayLost)
                        _ = OnRelayFault(t.RelayUrl, t.RelayFull);
                };
                RelayTunnelClient[] stale;
                lock (_tunnels)
                {
                    stale = _tunnels.Where(t => t.DeviceId == deviceId).ToArray();
                    _tunnels.Add(tunnel);
                }
                foreach (var old in stale)
                    old.Dispose();
                try
                {
                    await tunnel.StartAsync();
                }
                catch (Exception)
                {
                    bool closedFirst = tunnel.IsClosed;
                    tunnel.Dispose();
                    if (!tunnel.RelayLost)
                        _ = OnRelayFault(tunnel.RelayUrl, false, !closedFirst);
                    throw;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Handshake failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            _devices.Revoked -= DropTunnels;
            Stop();
            _standby.Dispose();
            _measureRetryTimer?.Dispose();
            RelayTunnelClient[] tunnels;
            lock (_tunnels)
                tunnels = _tunnels.ToArray();
            foreach (var tunnel in tunnels)
                tunnel.Dispose();
            _http.Dispose();
        }

        public static TimeSpan ReconnectDelay(long previousRetryCount)
        {
            int index = (int)Math.Min(previousRetryCount, ReconnectSeconds.Length - 1);
            return Jittered(ReconnectSeconds[index]);
        }

        public static TimeSpan Jittered(double seconds)
        {
            double unit;
            lock (Rng)
                unit = Rng.NextDouble();
            return TimeSpan.FromSeconds(seconds * (0.5 + unit));
        }

        sealed class RetryPolicy : IRetryPolicy
        {
            public TimeSpan? NextRetryDelay(RetryContext context)
            {
                return ReconnectDelay(context.PreviousRetryCount);
            }
        }
    }
}
