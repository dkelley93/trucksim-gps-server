using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Funbit.Ets.Telemetry.Server.Helpers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public sealed class PresenceClient : IDisposable
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly TimeSpan Linger = TimeSpan.FromMinutes(10);
        static readonly TimeSpan RelayRemeasure = TimeSpan.FromMinutes(10);
        static readonly int[] ReconnectSeconds = { 0, 2, 5, 10, 30, 60 };
        const int MaxBackoffSeconds = 300;

        string _apiBaseUrl;
        readonly Settings _settings;
        readonly LinkedDeviceStore _devices;
        readonly PairingSessionService _pairing;
        readonly Func<IReadOnlyList<string>> _candidates;
        readonly int _loopbackPort;
        readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        readonly List<RelayTunnelClient> _tunnels = new List<RelayTunnelClient>();
        readonly object _lock = new object();
        HubConnection _hub;
        CancellationTokenSource _runCts;
        Timer _lingerTimer;
        Timer _relayTimer;
        volatile string _relayUrl;
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

#if DEBUG
        public void UseApi(string apiBaseUrl)
        {
            bool wanted;
            lock (_lock)
                wanted = _wanted;
            Stop();
            lock (_lock)
            {
                _apiBaseUrl = apiBaseUrl;
                _relayUrl = "";
            }
            if (wanted)
                SetWanted(true);
        }
#endif

        public void SetWanted(bool wanted)
        {
            lock (_lock)
            {
                if (wanted)
                {
                    _lingerTimer?.Dispose();
                    _lingerTimer = null;
                    if (!_wanted)
                    {
                        _wanted = true;
                        _runCts = new CancellationTokenSource();
                        StartRun();
                    }
                }
                else if (_wanted && _lingerTimer == null)
                {
                    _lingerTimer = new Timer(_ => Stop(), null, Linger, Timeout.InfiniteTimeSpan);
                }
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
                _lingerTimer?.Dispose();
                _lingerTimer = null;
                _relayTimer?.Dispose();
                _relayTimer = null;
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
                        Log.WarnFormat("Presence registration refused, retrying in {0} s", backoff);
                    }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested)
                        {
                            if (hub != null)
                                await hub.DisposeAsync();
                            return;
                        }
                        Log.WarnFormat("Presence connect failed ({0}), retrying in {1} s", ex.Message, backoff);
                    }
                    if (hub != null)
                        await hub.DisposeAsync();
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoff), token);
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
                (sessionId, deviceId, nonceA, env, pairing) => Task.Run(() => OnHandshake(hub, sessionId, deviceId, nonceA, env, pairing)));
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
                ScheduleRelayMeasurement();
            return ok;
        }

        void ScheduleRelayMeasurement()
        {
            lock (_lock)
            {
                _relayTimer?.Dispose();
                _relayTimer = new Timer(_ => MeasureRelays(), null, TimeSpan.Zero, RelayRemeasure);
            }
        }

        void MeasureRelays()
        {
            Task.Run(async () =>
            {
                try
                {
                    string best = await RelaySelector.MeasureAsync(_http, _apiBaseUrl);
                    _relayUrl = best ?? "";
                    if (best != null && _settings.PreferredRelayUrl != best)
                    {
                        _settings.PreferredRelayUrl = best;
                        _settings.Save();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Relay measurement failed: " + ex.Message);
                }
            });
        }

        async Task OnHandshake(HubConnection hub, string sessionId, string deviceId, string nonceA, string env, bool pairing)
        {
            try
            {
                byte[] key = pairing ? _pairing.LiveSecret : _devices.GetKey(deviceId);
                string relayUrl = _relayUrl;
                var answer = HandshakeResponder.Answer(key, _candidates(), relayUrl, nonceA, env);
                Log.InfoFormat("Handshake from device {0}... (pairing: {1}): {2}", deviceId.Substring(0, 8), pairing, answer.Ok ? "accepted" : "rejected");
                string ticket = await hub.InvokeAsync<string>("HandshakeReply", sessionId, answer.Ok, answer.EnvR, answer.Ok ? relayUrl : "");
                if (!answer.Ok || string.IsNullOrEmpty(ticket) || relayUrl.Length == 0)
                    return;

                if (!pairing)
                    _devices.Touch(deviceId, answer.DeviceName, ConnectionMethod.Relay);
                var tunnel = new RelayTunnelClient(relayUrl, ticket, answer.KeyAppToPc, answer.KeyPcToApp, _loopbackPort, deviceId);
                tunnel.Closed += t => { lock (_tunnels) _tunnels.Remove(t); };
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
                    tunnel.Dispose();
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
            RelayTunnelClient[] tunnels;
            lock (_tunnels)
                tunnels = _tunnels.ToArray();
            foreach (var tunnel in tunnels)
                tunnel.Dispose();
            _http.Dispose();
        }

        sealed class RetryPolicy : IRetryPolicy
        {
            public TimeSpan? NextRetryDelay(RetryContext context)
            {
                int index = Math.Min((int)context.PreviousRetryCount, ReconnectSeconds.Length - 1);
                return TimeSpan.FromSeconds(ReconnectSeconds[index]);
            }
        }
    }
}
