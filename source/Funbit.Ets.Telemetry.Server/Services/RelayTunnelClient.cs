using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Funbit.Ets.Telemetry.Server.Services
{
    [DataContract]
    public sealed class JoinResult
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "peerPresent")] public bool PeerPresent { get; set; }
        [DataMember(Name = "reason")] public string Reason { get; set; }
    }

    public sealed class RelayTunnelClient : IDisposable
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly ConcurrentDictionary<string, byte> LocalEndpoints = new ConcurrentDictionary<string, byte>();
        static readonly TimeSpan PeerWait = TimeSpan.FromSeconds(60);
        const int ReadBufferSize = 16 * 1024;

        readonly string _relayUrl;
        readonly string _ticket;
        readonly int _loopbackPort;
        readonly TunnelEncoder _encoder;
        readonly TunnelDecoder _decoder;
        readonly ConcurrentDictionary<ushort, TcpClient> _streams = new ConcurrentDictionary<ushort, TcpClient>();
        readonly BlockingCollection<byte[]> _outbound = new BlockingCollection<byte[]>();
        readonly object _sendLock = new object();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        HubConnection _hub;
        Timer _peerTimer;
        int _closed;
        long _bytesSent, _bytesReceived;

        public string DeviceId { get; }
        public ThroughputMeter Throughput { get; } = new ThroughputMeter();
        public long BytesSent => Interlocked.Read(ref _bytesSent);
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);
        public event Action<RelayTunnelClient> Closed;

        public RelayTunnelClient(string relayUrl, string ticket, byte[] keyAppToPc, byte[] keyPcToApp, int loopbackPort, string deviceId)
        {
            _relayUrl = relayUrl.TrimEnd('/');
            _ticket = ticket;
            _loopbackPort = loopbackPort;
            _encoder = new TunnelEncoder(keyPcToApp, TunnelEncoder.PcToApp);
            _decoder = new TunnelDecoder(keyAppToPc, TunnelEncoder.AppToPc);
            DeviceId = deviceId;
        }

        public static bool IsTunnelEndpoint(string remoteEndpoint)
        {
            return remoteEndpoint != null && LocalEndpoints.ContainsKey(remoteEndpoint);
        }

        public async Task StartAsync()
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(_relayUrl + "/relay")
                .AddMessagePackProtocol()
                .Build();
            _hub.On<byte[]>("Recv", OnRecv);
            _hub.On("PeerJoined", () => _peerTimer?.Dispose());
            _hub.On("PeerLeft", () => Close("peer left"));
            _hub.Closed += _ =>
            {
                Close("relay connection closed");
                return Task.CompletedTask;
            };

            await _hub.StartAsync();
            _peerTimer = new Timer(_ => Close("app never joined"), null, PeerWait, Timeout.InfiniteTimeSpan);
            var result = await _hub.InvokeAsync<JoinResult>("Join", _ticket);
            if (!result.Ok)
            {
                Close("join rejected: " + result.Reason);
                return;
            }
            Log.InfoFormat("Relay tunnel joined at {0} (peer present: {1})", _relayUrl, result.PeerPresent);
            if (result.PeerPresent)
                _peerTimer.Dispose();
            _ = Task.Run(SendLoop);
        }

        void OnRecv(byte[] wire)
        {
            Interlocked.Add(ref _bytesReceived, wire.Length);
            byte[] frame;
            try
            {
                frame = _decoder.Decode(wire);
            }
            catch (Exception ex)
            {
                Log.Warn("Relay frame rejected: " + ex.Message);
                Close("bad frame");
                return;
            }

            ushort streamId = TunnelFrame.StreamId(frame);
            switch (frame[2])
            {
                case TunnelFrame.Open:
                    OpenStream(streamId);
                    break;
                case TunnelFrame.Data:
                    TcpClient client;
                    if (_streams.TryGetValue(streamId, out client))
                    {
                        try
                        {
                            client.GetStream().Write(frame, TunnelFrame.HeaderSize, frame.Length - TunnelFrame.HeaderSize);
                        }
                        catch (Exception)
                        {
                            CloseStream(streamId, notifyPeer: true);
                        }
                    }
                    break;
                case TunnelFrame.Close:
                    CloseStream(streamId, notifyPeer: false);
                    break;
            }
        }

        void OpenStream(ushort streamId)
        {
            CloseStream(streamId, notifyPeer: false);
            var client = new TcpClient { NoDelay = true };
            try
            {
                client.Connect(IPAddress.Loopback, _loopbackPort);
            }
            catch (Exception ex)
            {
                Log.Warn("Relay stream could not reach the local server: " + ex.Message);
                client.Dispose();
                Send(streamId, TunnelFrame.Close, Array.Empty<byte>(), 0, 0);
                return;
            }
            LocalEndpoints[client.Client.LocalEndPoint.ToString()] = 0;
            _streams[streamId] = client;
            _ = ReadLoop(streamId, client);
        }

        async Task ReadLoop(ushort streamId, TcpClient client)
        {
            var buffer = new byte[ReadBufferSize];
            try
            {
                var stream = client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                    if (n <= 0)
                        break;
                    Send(streamId, TunnelFrame.Data, buffer, 0, n);
                }
            }
            catch (Exception)
            {
            }
            CloseStream(streamId, notifyPeer: true);
        }

        void CloseStream(ushort streamId, bool notifyPeer)
        {
            TcpClient client;
            if (!_streams.TryRemove(streamId, out client))
                return;
            byte dummy;
            LocalEndpoints.TryRemove(client.Client.LocalEndPoint.ToString(), out dummy);
            client.Dispose();
            if (notifyPeer)
                Send(streamId, TunnelFrame.Close, Array.Empty<byte>(), 0, 0);
        }

        void Send(ushort streamId, byte flag, byte[] payload, int offset, int count)
        {
            if (_closed != 0)
                return;
            lock (_sendLock)
            {
                var wire = _encoder.Encode(TunnelFrame.Build(streamId, flag, payload, offset, count));
                try
                {
                    _outbound.Add(wire);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        async Task SendLoop()
        {
            try
            {
                foreach (var wire in _outbound.GetConsumingEnumerable(_cts.Token))
                {
                    await _hub.SendAsync("Send", wire, _cts.Token);
                    Interlocked.Add(ref _bytesSent, wire.Length);
                }
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    Close("send failed: " + ex.Message);
            }
        }

        void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            Log.InfoFormat("Relay tunnel closed ({0})", reason);
            _peerTimer?.Dispose();
            _outbound.CompleteAdding();
            _cts.Cancel();
            foreach (var streamId in new List<ushort>(_streams.Keys))
                CloseStream(streamId, notifyPeer: false);
            var hub = _hub;
            if (hub != null)
                _ = hub.DisposeAsync();
            Closed?.Invoke(this);
        }

        public void Dispose()
        {
            Close("disposed");
        }
    }
}
