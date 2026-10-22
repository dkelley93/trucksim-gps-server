using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funbit.Ets.Telemetry.Server.Controllers;
using Funbit.Ets.Telemetry.Server.Data;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Services;
using Newtonsoft.Json;

namespace Funbit.Ets.Telemetry.Server
{
    // Raw TCP-socket HTTP server: bypasses HTTP.SYS to work around the Windows KB5066835/KB5065789 bugs.
    public class MinimalHttpServer : IDisposable
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly int _port;
        private readonly PairingSessionService _pairing;
        private readonly LinkedDeviceStore _devices;
        private TcpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _listenerTask;
        private bool _isRunning;
        private readonly object _lock = new object();

        // Keep-alive: reuse TCP connections to avoid TIME_WAIT socket accumulation.
        // At 10 polls/sec with Connection: close, each poll opens a new TCP connection
        // that lingers in TIME_WAIT for 240s, eventually exhausting ephemeral ports.
        const int KeepAliveTimeoutMs = 30000;

        // Cap on POST body size. Pair/auth endpoints take tiny JSON; this prevents memory
        // pressure from a malformed or hostile Content-Length.
        const int MaxBodyBytes = 64 * 1024;

        public MinimalHttpServer(int port, PairingSessionService pairing, LinkedDeviceStore devices)
        {
            _port = port;
            _pairing = pairing;
            _devices = devices;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    Log.Warn("Server is already running");
                    return;
                }

                try
                {
                    Log.Info("=== Minimal HTTP Server Starting ===");
                    Log.InfoFormat("Port: {0}", _port);
                    Log.Info("Mode: HTTP.SYS Bypass (workaround for KB5066835/KB5065789)");

                    _listener = new TcpListener(IPAddress.Any, _port);
                    _listener.Start();
                    _cancellationTokenSource = new CancellationTokenSource();
                    _isRunning = true;

                    // Start accepting connections in background
                    _listenerTask = Task.Run(() => AcceptConnectionsAsync(_cancellationTokenSource.Token));

                    Log.InfoFormat("Minimal HTTP Server started successfully on port {0}", _port);
                    Log.Info("Endpoints available:");
                    Log.Info("  - GET  /              (Status page)");
                    Log.Info("  - GET  /api/ets2/telemetry  (Telemetry JSON)");
                    Log.Info("  - POST /api/ets2/telemetry  (Telemetry JSON)");
                    Log.Info("  - GET  /api/game/state        (Active profile state)");
                    Log.Info("  - GET  /api/game/profiles     (Game profile list)");
                    Log.Info("  - GET  /api/game/profile-mods (Profile mod list)");
                    Log.Info("  - GET  /api/pair/hello        (PC identity for pairing)");
                    Log.Info("  - POST /api/pair/complete     (QR/code pairing completion)");
                    Log.Info("  - POST /api/pair/complete-lan (Manual-IP pairing completion)");
                    Log.Info("  - POST /api/pair/unlink       (Device leaves the linked list)");
                }
                catch (Exception ex)
                {
                    _isRunning = false;
                    Log.Error("Failed to start Minimal HTTP Server", ex);
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return;

                try
                {
                    Log.Info("Stopping Minimal HTTP Server...");

                    _cancellationTokenSource?.Cancel();
                    _listener?.Stop();

                    // Wait for listener task to complete (with timeout)
                    if (_listenerTask != null && !_listenerTask.Wait(TimeSpan.FromSeconds(5)))
                    {
                        Log.Warn("Listener task did not complete within timeout");
                    }

                    _isRunning = false;
                    Log.Info("Minimal HTTP Server stopped");
                }
                catch (Exception ex)
                {
                    Log.Error("Error stopping server", ex);
                }
            }
        }

        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();

                    // Handle each client in a separate task (long-lived with keep-alive)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleClientAsync(client, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Error handling client", ex);
                        }
                    }, cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    // Listener was stopped, exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Log.Error("Error accepting client connection", ex);
                    }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            var requestCount = 0;

            using (client)
            {
                client.SendTimeout = 5000;
                Log.InfoFormat("[KeepAlive] Connection opened from {0}", clientEndpoint);

                try
                {
                    using (var stream = client.GetStream())
                    {
                        // Keep-alive loop: handle multiple requests on the same TCP connection
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var request = await ReadHttpRequestAsync(stream, KeepAliveTimeoutMs);

                            if (request == null)
                            {
                                Log.InfoFormat("[KeepAlive] Connection closed from {0} after {1} requests (client closed or idle timeout)", clientEndpoint, requestCount);
                                break;
                            }

                            request.RemoteEndpoint = clientEndpoint;
                            requestCount++;

                            bool clientWantsClose = request.Headers != null &&
                                request.Headers.Any(h =>
                                    h.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) &&
                                    h.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0);

                            HttpResponse response;
                            try
                            {
                                response = RouteRequest(request);
                            }
                            catch (Exception ex)
                            {
                                Log.Error("Error routing request", ex);
                                response = new HttpResponse
                                {
                                    StatusCode = 500,
                                    StatusText = "Internal Server Error",
                                    ContentType = "text/plain",
                                    Body = "Internal Server Error"
                                };
                            }
                            await SendHttpResponseAsync(stream, response, closeConnection: clientWantsClose);

                            if (clientWantsClose)
                            {
                                Log.InfoFormat("[KeepAlive] Connection closed from {0} after {1} requests (client requested close)", clientEndpoint, requestCount);
                                break;
                            }
                        }
                    }
                }
                catch (IOException ex)
                {
                    Log.InfoFormat("[KeepAlive] IO exception from {0} after {1} requests: {2}", clientEndpoint, requestCount, ex.Message);
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        Log.ErrorFormat("[KeepAlive] Error from {0} after {1} requests: {2}", clientEndpoint, requestCount, ex.Message);
                }
            }
        }

        private async Task<HttpRequest> ReadHttpRequestAsync(NetworkStream stream, int timeoutMs)
        {
            var buffer = new byte[8192];

            // Idle timeout: if no request arrives within timeoutMs, return null to close
            // the keep-alive connection. NetworkStream.ReadAsync doesn't respect
            // Socket.ReceiveTimeout, so we use Task.WhenAny with Task.Delay instead.
            var bytesRead = await ReadWithTimeoutAsync(stream, buffer, 0, buffer.Length, timeoutMs);

            if (bytesRead <= 0)
                return null;

            int headerEnd = FindHeaderEnd(buffer, bytesRead);
            while (headerEnd < 0 && bytesRead < buffer.Length)
            {
                int more = await ReadWithTimeoutAsync(stream, buffer, bytesRead, buffer.Length - bytesRead, timeoutMs);
                if (more <= 0)
                    return null;
                bytesRead += more;
                headerEnd = FindHeaderEnd(buffer, bytesRead);
            }
            if (headerEnd < 0)
                return null;

            string headerText = Encoding.UTF8.GetString(buffer, 0, headerEnd);
            var headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (headerLines.Length == 0) return null;

            var requestLine = headerLines[0].Split(' ');
            if (requestLine.Length < 3) return null;

            var headers = headerLines.Skip(1).Where(l => !string.IsNullOrEmpty(l)).ToArray();

            string body = null;
            int contentLength = GetContentLength(headers);
            if (contentLength > 0)
            {
                if (contentLength > MaxBodyBytes)
                {
                    Log.WarnFormat("Request body Content-Length {0} exceeds cap {1}; rejecting", contentLength, MaxBodyBytes);
                    return null;
                }

                int bodyStart = headerEnd + 4; // skip past CRLF CRLF
                int alreadyRead = Math.Max(0, bytesRead - bodyStart);

                var bodyBuffer = new byte[contentLength];
                if (alreadyRead > 0)
                {
                    int take = Math.Min(alreadyRead, contentLength);
                    Buffer.BlockCopy(buffer, bodyStart, bodyBuffer, 0, take);
                }

                int offset = Math.Min(alreadyRead, contentLength);
                while (offset < contentLength)
                {
                    int n = await ReadWithTimeoutAsync(stream, bodyBuffer, offset, contentLength - offset, timeoutMs);
                    if (n <= 0) break;
                    offset += n;
                }

                body = Encoding.UTF8.GetString(bodyBuffer, 0, offset);
            }

            return new HttpRequest
            {
                Method = requestLine[0],
                Path = requestLine[1],
                Headers = headers,
                Body = body
            };
        }

        // NetworkStream.ReadAsync ignores Socket.ReceiveTimeout, so the idle deadline is applied here.
        static async Task<int> ReadWithTimeoutAsync(NetworkStream stream, byte[] buffer, int offset, int count, int timeoutMs)
        {
            var readTask = stream.ReadAsync(buffer, offset, count);
            if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)) != readTask)
                return -1;
            return await readTask;
        }

        static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (buffer[i] == 0x0D && buffer[i + 1] == 0x0A &&
                    buffer[i + 2] == 0x0D && buffer[i + 3] == 0x0A)
                {
                    return i;
                }
            }
            return -1;
        }

        static int GetContentLength(string[] headers)
        {
            foreach (var h in headers)
            {
                if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = h.Substring("Content-Length:".Length).Trim();
                    if (int.TryParse(value, out int n) && n >= 0)
                        return n;
                }
            }
            return 0;
        }

        private HttpResponse RouteRequest(HttpRequest request)
        {
            Log.DebugFormat("Request: {0} {1}", request.Method, request.Path);

            // Normalize path and split off the query string
            var path = request.Path;
            var query = new Dictionary<string, string>();
            int queryIndex = path.IndexOf('?');
            if (queryIndex >= 0)
            {
                foreach (var pair in path.Substring(queryIndex + 1).Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0)
                        query[Uri.UnescapeDataString(pair.Substring(0, eq))] =
                            Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
                path = path.Substring(0, queryIndex);
            }
            path = path.TrimEnd('/');
            if (string.IsNullOrEmpty(path))
                path = "/";

            string deviceId = HeaderValue(request.Headers, DeviceHeader);
            if (!Crypto.IsHexId(deviceId, 32))
                deviceId = null;
            if (deviceId != null && HeaderValue(request.Headers, PairHeader) == PairNew)
                _devices.ClearRemoved(deviceId);
            bool removed = deviceId != null && _devices.IsRemoved(deviceId);
            if (removed && !BypassesRemoval(path))
                return JsonResponse(new { error = "device_removed" }, 403, "Forbidden");

            HttpResponse response;
            if (path == "/")
                response = HandleStatusPage(request);
            else if (path == "/api/ets2/telemetry")
                response = HandleTelemetryRequest(request);
            else if (path == "/api/game/state" || path == "/api/game/profiles" || path == "/api/game/profile-mods")
                response = HandleGameProfileRequest(request, path, query);
            else if (path == "/api/pair/hello") response = HandlePairHello(request);
            else if (path == "/api/pair/complete") response = HandlePairComplete(request);
            else if (path == "/api/pair/complete-lan") response = HandlePairCompleteLan(request);
            else if (path == "/api/pair/unlink") response = HandlePairUnlink(request);
            else
            {
                return new HttpResponse
                {
                    StatusCode = 404,
                    StatusText = "Not Found",
                    ContentType = "text/plain",
                    Body = "404 Not Found"
                };
            }

            if (deviceId != null)
            {
                if (removed && _devices.IsRemoved(deviceId))
                    response.ExtraHeaders[PairHeader] = PairRemoved;
                else if (!removed && path != "/api/pair/unlink")
                    TrackDevice(request, deviceId, response);
            }
            return response;
        }

        static bool BypassesRemoval(string path)
        {
            return path == "/api/pair/hello" || path == "/api/pair/complete" || path == "/api/pair/unlink";
        }

        private HttpResponse HandleStatusPage(HttpRequest request)
        {
            if (request.Method != "GET") return MethodNotAllowed();

            var html = TelemetryEndpoints.GetStatusPageHtml(showBypassNotice: true);
            return new HttpResponse
            {
                StatusCode = 200,
                StatusText = "OK",
                ContentType = "text/html; charset=utf-8",
                Body = html,
                CacheControl = "no-cache"
            };
        }

        private HttpResponse HandleTelemetryRequest(HttpRequest request)
        {
            if (request.Method != "GET" && request.Method != "POST") return MethodNotAllowed();

            try
            {
                var telemetryJson = TelemetryEndpoints.GetEts2TelemetryJson();
                return new HttpResponse
                {
                    StatusCode = 200,
                    StatusText = "OK",
                    ContentType = "application/json; charset=utf-8",
                    Body = telemetryJson,
                    CacheControl = "no-cache"
                };
            }
            catch (Exception ex)
            {
                Log.Error("Error getting telemetry data", ex);
                return InternalServerError();
            }
        }

        private static bool IsAddressedByIp(HttpRequest request)
        {
            var header = request.Headers?.FirstOrDefault(
                h => h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
            if (header == null)
                return false;

            var host = header.Substring("Host:".Length).Trim();
            int portSeparator = host.LastIndexOf(':');
            if (portSeparator > 0 && host.IndexOf(']') < portSeparator)
                host = host.Substring(0, portSeparator);
            host = host.Trim('[', ']');

            IPAddress address;
            return host == "localhost" || IPAddress.TryParse(host, out address);
        }

        private HttpResponse HandleGameProfileRequest(HttpRequest request, string path, Dictionary<string, string> query)
        {
            if (request.Method != "GET")
            {
                return new HttpResponse
                {
                    StatusCode = 405,
                    StatusText = "Method Not Allowed",
                    ContentType = "text/plain",
                    Body = "Method Not Allowed"
                };
            }

            // Profile data is more identifying than telemetry, so only accept requests
            // addressed by address: a browser doing DNS rebinding sends its own hostname.
            if (!IsAddressedByIp(request))
            {
                return new HttpResponse
                {
                    StatusCode = 403,
                    StatusText = "Forbidden",
                    ContentType = "text/plain",
                    Body = "Forbidden"
                };
            }

            string game;
            query.TryGetValue("game", out game);
            if (!GameProfileEndpoints.IsValidGame(game))
            {
                return new HttpResponse
                {
                    StatusCode = 400,
                    StatusText = "Bad Request",
                    ContentType = "text/plain",
                    Body = "Missing or invalid 'game' parameter (expected ets2 or ats)"
                };
            }

            try
            {
                string json;
                if (path == "/api/game/state")
                {
                    json = GameProfileEndpoints.GetStateJson(game);
                }
                else if (path == "/api/game/profiles")
                {
                    json = GameProfileEndpoints.GetProfilesJson(game);
                }
                else
                {
                    string id, type;
                    query.TryGetValue("id", out id);
                    query.TryGetValue("type", out type);
                    // The id must be a hex-encoded profile name; anything else could act as a path fragment.
                    if (string.IsNullOrEmpty(id) || GameProfileScanner.TryDecodeHexName(id) == null ||
                        (type != "local" && type != "steam"))
                    {
                        return new HttpResponse
                        {
                            StatusCode = 400,
                            StatusText = "Bad Request",
                            ContentType = "text/plain",
                            Body = "Missing or invalid 'id'/'type' parameters"
                        };
                    }
                    json = GameProfileEndpoints.GetProfileModsJson(game, id, type);
                }

                return new HttpResponse
                {
                    StatusCode = 200,
                    StatusText = "OK",
                    ContentType = "application/json; charset=utf-8",
                    Body = json,
                    CacheControl = "no-cache"
                };
            }
            catch (Exception ex)
            {
                Log.Error("Error handling game profile request", ex);

                return new HttpResponse
                {
                    StatusCode = 500,
                    StatusText = "Internal Server Error",
                    ContentType = "text/plain",
                    Body = "Internal Server Error"
                };
            }
        }

        const string DeviceHeader = "X-TSGPS-Device";
        const string DeviceNameHeader = "X-TSGPS-Device-Name";
        const string PairHeader = "X-TSGPS-Pair";
        const string PairOffer = "offer";
        const string PairNew = "new";
        const string PairRemoved = "removed";

        void TrackDevice(HttpRequest request, string deviceId, HttpResponse response)
        {
            string deviceName = HeaderValue(request.Headers, DeviceNameHeader);
            bool viaRelay = RelayTunnelClient.IsTunnelEndpoint(request.RemoteEndpoint);
            var known = _devices.Get(deviceId);
            bool linked = known?.DeviceKey != null;
            string method = viaRelay ? ConnectionMethod.Relay : linked ? ConnectionMethod.Direct : ConnectionMethod.Manual;
            _devices.Touch(deviceId, deviceName, method);
            if (!linked && !viaRelay)
                response.ExtraHeaders[PairHeader] = PairOffer;
        }

        static string HeaderValue(string[] headers, string name)
        {
            var header = headers?.FirstOrDefault(h => h.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
            return header?.Substring(name.Length + 1).Trim();
        }

        object PcIdentity(byte[] deviceKey)
        {
            return new
            {
                deviceKey = deviceKey == null ? null : Crypto.Base64Url(deviceKey),
                pcId = _devices.PcId,
                name = _pairing.PcName,
                serverVersion = CloudConfig.ProtocolVersion
            };
        }

        private HttpResponse HandlePairHello(HttpRequest request)
        {
            if (request.Method != "GET") return MethodNotAllowed();
            return JsonResponse(PcIdentity(null));
        }

        private HttpResponse HandlePairComplete(HttpRequest request)
        {
            if (request.Method != "POST") return MethodNotAllowed();

            var req = TryDeserializeBody<PairCompleteRequest>(request);
            if (req == null || !Crypto.IsHexId(req.DeviceId, 32) || string.IsNullOrEmpty(req.Proof) || string.IsNullOrEmpty(req.Nonce))
                return JsonResponse(new { error = "invalid_request" }, 400, "Bad Request");

            byte[] secret;
            if (!_pairing.TryComplete(req.DeviceId, req.Proof, req.Nonce, out secret))
                return JsonResponse(new { error = "no_session" }, 404, "Not Found");

            bool viaRelay = RelayTunnelClient.IsTunnelEndpoint(request.RemoteEndpoint);
            var key = _devices.Link(req.DeviceId, req.DeviceName, viaRelay ? ConnectionMethod.Relay : ConnectionMethod.Direct);
            Log.InfoFormat("Device {0} linked ({1})", req.DeviceId, viaRelay ? "relay" : "LAN");
            string reply = JsonConvert.SerializeObject(PcIdentity(key), JsonHelper.RestSettings);
            return JsonResponse(new { env = Crypto.SealEnvelope(secret, reply) });
        }

        private HttpResponse HandlePairCompleteLan(HttpRequest request)
        {
            if (request.Method != "POST") return MethodNotAllowed();
            if (RelayTunnelClient.IsTunnelEndpoint(request.RemoteEndpoint))
                return JsonResponse(new { error = "lan_only" }, 403, "Forbidden");

            var req = TryDeserializeBody<PairCompleteRequest>(request);
            if (req == null || !Crypto.IsHexId(req.DeviceId, 32))
                return JsonResponse(new { error = "invalid_request" }, 400, "Bad Request");

            var key = _devices.Link(req.DeviceId, req.DeviceName, ConnectionMethod.Direct);
            Log.InfoFormat("Device {0} linked (manual IP)", req.DeviceId);
            return JsonResponse(PcIdentity(key));
        }

        private HttpResponse HandlePairUnlink(HttpRequest request)
        {
            if (request.Method != "POST") return MethodNotAllowed();

            var req = TryDeserializeBody<PairCompleteRequest>(request);
            if (req == null || !Crypto.IsHexId(req.DeviceId, 32) || string.IsNullOrEmpty(req.Proof) || string.IsNullOrEmpty(req.Nonce))
                return JsonResponse(new { error = "invalid_request" }, 400, "Bad Request");

            if (!_devices.Unlink(req.DeviceId, req.Proof, req.Nonce))
                return JsonResponse(new { error = "not_linked" }, 404, "Not Found");
            Log.InfoFormat("Device {0} unlinked itself", req.DeviceId);
            return JsonResponse(new { });
        }

        static T TryDeserializeBody<T>(HttpRequest request) where T : class
        {
            if (string.IsNullOrEmpty(request.Body)) return null;
            try
            {
                return JsonConvert.DeserializeObject<T>(request.Body, JsonHelper.RestSettings);
            }
            catch (Exception ex)
            {
                Log.WarnFormat("Failed to deserialize {0} body: {1}", typeof(T).Name, ex.Message);
                return null;
            }
        }

        static HttpResponse JsonResponse(object payload, int statusCode = 200, string statusText = "OK")
        {
            string json = JsonConvert.SerializeObject(payload, JsonHelper.RestSettings);
            return new HttpResponse
            {
                StatusCode = statusCode,
                StatusText = statusText,
                ContentType = "application/json; charset=utf-8",
                Body = json,
                CacheControl = "no-cache"
            };
        }

        static HttpResponse MethodNotAllowed() => new HttpResponse
        {
            StatusCode = 405, StatusText = "Method Not Allowed",
            ContentType = "text/plain", Body = "Method Not Allowed"
        };

        static HttpResponse InternalServerError() => new HttpResponse
        {
            StatusCode = 500, StatusText = "Internal Server Error",
            ContentType = "text/plain", Body = "Internal Server Error"
        };

        private async Task SendHttpResponseAsync(NetworkStream stream, HttpResponse response, bool closeConnection = false)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(response.Body ?? "");

            var responseBuilder = new StringBuilder();
            responseBuilder.AppendFormat("HTTP/1.1 {0} {1}\r\n", response.StatusCode, response.StatusText);
            responseBuilder.AppendFormat("Content-Type: {0}\r\n", response.ContentType);
            responseBuilder.AppendFormat("Content-Length: {0}\r\n", bodyBytes.Length);

            if (closeConnection)
            {
                responseBuilder.Append("Connection: close\r\n");
            }
            else
            {
                responseBuilder.Append("Connection: keep-alive\r\n");
                responseBuilder.AppendFormat("Keep-Alive: timeout={0}\r\n", KeepAliveTimeoutMs / 1000);
            }

            if (!string.IsNullOrEmpty(response.CacheControl))
            {
                responseBuilder.AppendFormat("Cache-Control: {0}\r\n", response.CacheControl);
            }

            foreach (var header in response.ExtraHeaders)
            {
                responseBuilder.AppendFormat("{0}: {1}\r\n", header.Key, header.Value);
            }

            responseBuilder.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(responseBuilder.ToString());

            // Send headers
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);

            // Send body
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
            }

            await stream.FlushAsync();
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource?.Dispose();
            _listener = null;
        }

        // Helper classes
        private class HttpRequest
        {
            public string Method { get; set; }
            public string Path { get; set; }
            public string[] Headers { get; set; }
            public string Body { get; set; }
            public string RemoteEndpoint { get; set; }
        }

        private class HttpResponse
        {
            public int StatusCode { get; set; }
            public string StatusText { get; set; }
            public string ContentType { get; set; }
            public string Body { get; set; }
            public string CacheControl { get; set; }
            public Dictionary<string, string> ExtraHeaders { get; } = new Dictionary<string, string>();
        }

        private class PairCompleteRequest
        {
            public string DeviceId { get; set; }
            public string DeviceName { get; set; }
            public string Proof { get; set; }
            public string Nonce { get; set; }
        }
    }
}
