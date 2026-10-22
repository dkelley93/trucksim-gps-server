using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funbit.Ets.Telemetry.Server;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Services;
using Newtonsoft.Json.Linq;

namespace PairingTests
{
    // Run after a Release build: dotnet run --project tests/PairingTests -c Release
    static class Program
    {
        static int _failures;

        static int Main(string[] args)
        {
            Run("crypto vectors", CryptoVectors);
            Run("tunnel codec", TunnelCodec);
            Run("pairing session", () => PairingSession().GetAwaiter().GetResult());
            Run("http routes", () => HttpRoutes().GetAwaiter().GetResult());
            Run("handshake", Handshake);
            Run("throughput meter", ThroughputMeter);

            Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"FAILED ({_failures})");
            return _failures == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            Console.WriteLine($"== {name}");
            try
            {
                test();
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"  [FAIL] {name}: {ex}");
            }
        }

        static void Check(bool condition, string what)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {what}");
            if (!condition)
                _failures++;
        }

        static void ThroughputMeter()
        {
            var meter = new ThroughputMeter();
            var t0 = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            meter.Sample(0, 0, t0);
            Check(meter.UpBytesPerSecond == 0 && meter.DownBytesPerSecond == 0, "single sample reads zero");
            meter.Sample(2048, 512, t0.AddSeconds(1));
            Check(meter.UpBytesPerSecond == 2048 && meter.DownBytesPerSecond == 512, "two samples one second apart give bytes per second");
            for (int i = 2; i <= 5; i++)
                meter.Sample(2048L * i, 512L * i, t0.AddSeconds(i));
            Check(meter.UpBytesPerSecond == 2048 && meter.DownBytesPerSecond == 512, "steady stream over the full 5 s window");
            meter.Sample(2048L * 5, 512L * 5, t0.AddSeconds(6));
            Check(Math.Abs(meter.UpBytesPerSecond - 2048 * 4 / 5.0) < 0.001, "oldest sample drops out once it is older than 5 s: " + meter.UpBytesPerSecond);
            for (int i = 7; i <= 12; i++)
                meter.Sample(2048L * 5, 512L * 5, t0.AddSeconds(i));
            Check(meter.UpBytesPerSecond == 0 && meter.DownBytesPerSecond == 0, "idle tunnel decays to zero within the window");
            meter.Sample(2048L * 5 + 100, 512L * 5, t0.AddSeconds(12));
            Check(meter.UpBytesPerSecond == 20 && meter.DownBytesPerSecond == 0, "a second sample at the same tick keeps the window span");
        }

        static void CryptoVectors()
        {
            var doc = JObject.Parse(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crypto_vectors.json")));
            foreach (var v in doc["hkdf"])
            {
                var okm = Crypto.Hkdf(Crypto.FromHex((string)v["ikm"]), Crypto.FromHex((string)v["salt"]),
                    Crypto.FromHex((string)v["infoHex"]), (int)v["length"]);
                Check(Crypto.ToHex(okm) == (string)v["okm"], "hkdf " + v["name"]);
            }
            foreach (var v in doc["aesGcm"])
            {
                var key = Crypto.FromHex((string)v["key"]);
                var nonce = Crypto.FromHex((string)v["nonce"]);
                var plaintext = Crypto.FromHex((string)v["plaintext"]);
                var expected = (string)v["ciphertext"] + (string)v["tag"];
                Check(Crypto.ToHex(Crypto.GcmEncrypt(key, nonce, plaintext)) == expected, "gcm encrypt " + v["name"]);
                Check(Crypto.ToHex(Crypto.GcmDecrypt(key, nonce, Crypto.FromHex(expected))) == (string)v["plaintext"], "gcm decrypt " + v["name"]);
            }
            var env = doc["aesGcm"].First(x => (string)x["name"] == "env_under_deviceKey");
            string envelope = Crypto.Base64Url(Crypto.Concat(Crypto.FromHex((string)env["nonce"]), Crypto.FromHex((string)env["ciphertext"] + (string)env["tag"])));
            Check(Crypto.OpenEnvelope(Crypto.FromHex((string)env["key"]), envelope) == Encoding.UTF8.GetString(Crypto.FromHex((string)env["plaintext"])), "envelope open matches vector");
            var tampered = Crypto.FromBase64Url(envelope);
            tampered[tampered.Length - 1] ^= 1;
            Check(Throws(() => Crypto.OpenEnvelope(Crypto.FromHex((string)env["key"]), Crypto.Base64Url(tampered))), "tampered envelope rejected");
            Check(Crypto.ToHex(Crypto.TunnelNonce(2, 1)) == "000000020000000000000001", "tunnel nonce layout");
            Check(Crypto.ToHex(Crypto.TunnelNonce(1, ulong.MaxValue)) == "00000001ffffffffffffffff", "tunnel nonce max counter");
        }

        static bool Throws(Action action)
        {
            try { action(); return false; } catch (Exception) { return true; }
        }

        static void TunnelCodec()
        {
            var key = Crypto.RandomBytes(32);
            var encoder = new TunnelEncoder(key, TunnelEncoder.AppToPc);
            var decoder = new TunnelDecoder(key, TunnelEncoder.AppToPc);
            var payloads = new List<byte[]>();
            var rng = new Random(7);
            for (int i = 0; i < 50; i++)
            {
                var payload = new byte[rng.Next(0, 3000)];
                for (int j = 0; j < payload.Length; j++)
                    payload[j] = (byte)"GET /api/ets2/telemetry HTTP/1.1\r\n"[j % 34];
                payloads.Add(payload);
            }
            var wires = payloads.Select((p, i) => encoder.Encode(TunnelFrame.Build((ushort)(i % 7), (byte)(i % 3), p, 0, p.Length))).ToList();
            bool allMatch = true;
            long wireBytes = 0, plainBytes = 0;
            for (int i = 0; i < wires.Count; i++)
            {
                var frame = decoder.Decode(wires[i]);
                allMatch &= TunnelFrame.StreamId(frame) == i % 7 && frame[2] == i % 3 &&
                            frame.Skip(3).SequenceEqual(payloads[i]);
                wireBytes += wires[i].Length;
                plainBytes += payloads[i].Length;
            }
            Check(allMatch, $"50 frames round trip in order ({plainBytes} B plain -> {wireBytes} B wire)");
            Check(wireBytes < plainBytes / 2, "repetitive payload compresses with the shared context");
            ulong counter = 0;
            for (int i = 0; i < 8; i++)
                counter = (counter << 8) | wires[0][i];
            Check(counter == 0 && wires[1][7] == 1, "counters start at 0 and increment");
            Check(Throws(() => decoder.Decode(wires[10])), "replayed counter rejected");
            var other = new TunnelDecoder(key, TunnelEncoder.PcToApp);
            Check(Throws(() => other.Decode(encoder.Encode(TunnelFrame.Build(1, 0, new byte[4], 0, 4)))), "wrong direction id fails authentication");
        }

        static async Task PairingSession()
        {
            int conflicts = 1;
            using (var api = new FakeApi((method, path, body) =>
            {
                if (method == "POST" && path == "/api/pair/sessions")
                {
                    if (conflicts-- > 0)
                        return Tuple.Create(409, "{}");
                    var json = JObject.Parse(body);
                    bool valid = ((string)json["codeHash"]).Length == 64 && ((string)json["pcId"]).Length == 32
                                 && Crypto.FromBase64Url((string)json["salt"]).Length == 16 && ((string)json["blob"]).Length > 40;
                    return valid ? Tuple.Create(201, "{}") : Tuple.Create(400, "{}");
                }
                if (method == "DELETE" && path.StartsWith("/api/pair/sessions/"))
                    return Tuple.Create(204, "");
                return Tuple.Create(404, "");
            }))
            {
                string pcId = Crypto.RandomHexId();
                var candidates = new List<string> { "192.168.1.42/24", "127.0.0.1/8" };
                var states = new List<PairingState>();
                using (var service = new PairingSessionService(api.BaseUrl, pcId, 31377, () => candidates, TimeSpan.FromSeconds(2)))
                {
                    service.StateChanged += () => states.Add(service.State);
                    await service.GenerateAsync();
                    Check(service.State == PairingState.Active && states.SequenceEqual(new[] { PairingState.Generating, PairingState.Active }), "generate: Generating -> Active");
                    Check(api.Requests.Count(r => r.StartsWith("POST /api/pair/sessions")) == 2, "409 code clash retried with a new code");
                    var session = service.Current;
                    Check(session.Code.Length == 6 && session.Code.All(c => PairingSessionService.CodeAlphabet.Contains(c)), "code is 6 chars of the alphabet: " + session.Code);
                    Check(session.CodeHash == Crypto.ToHex(Crypto.Sha256(Encoding.UTF8.GetBytes(session.Code))), "codeHash = sha256(code)");

                    var uri = new Uri(session.QrPayload);
                    var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split(new[] { '=' }, 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                    Check(uri.Scheme == "trucksimgps" && uri.Host == "connect" && query["v"] == "2", "QR payload is trucksimgps://connect?v=2");
                    Check(query["pc"] == pcId && query["p"] == "31377" && query["i"] == "192.168.1.42/24,127.0.0.1/8" && query["n"] == Environment.MachineName, "QR carries pcId, port, candidates, name");
                    Check(Crypto.FromBase64Url(query["s"]).SequenceEqual(session.Secret), "QR carries S");
                    var hash = Crypto.Sha256(Encoding.UTF8.GetBytes(session.Code)).Take(12).ToArray();
                    Check(query["h"] == Crypto.Base64Url(hash), "QR h = first 12 bytes of sha256(code)");

                    var posted = JObject.Parse(api.Bodies.Last());
                    var blobKey = Crypto.Hkdf(Encoding.UTF8.GetBytes(session.Code), Crypto.FromBase64Url((string)posted["salt"]), Crypto.PairInfo, 32);
                    var blob = JObject.Parse(Crypto.OpenEnvelope(blobKey, (string)posted["blob"]));
                    Check((string)blob["pcId"] == pcId && (int)blob["port"] == 31377 && (string)blob["s"] == query["s"]
                          && blob["candidates"].Select(c => (string)c).SequenceEqual(candidates), "blob decrypts under HKDF(code, salt) with pcId/name/candidates/port/s");

                    string deviceId = Crypto.RandomHexId();
                    var nonce = Crypto.RandomBytes(16);
                    byte[] secret;
                    Check(!service.TryComplete(deviceId, Crypto.Base64Url(Crypto.RandomBytes(32)), Crypto.Base64Url(nonce), out secret), "wrong proof rejected");
                    Check(service.State == PairingState.Active, "session still live after a wrong proof");
                    var proof = Crypto.HmacSha256(session.Secret, Crypto.Concat(Encoding.UTF8.GetBytes(deviceId), nonce));
                    Check(service.TryComplete(deviceId, Crypto.Base64Url(proof), Crypto.Base64Url(nonce), out secret) && secret.SequenceEqual(session.Secret), "correct proof completes and yields S");
                    Check(service.State == PairingState.Idle && service.Live == null, "completed session is consumed (Idle)");
                    Check(!service.TryComplete(deviceId, Crypto.Base64Url(proof), Crypto.Base64Url(nonce), out secret), "single use: second completion rejected");
                    await Task.Delay(300);
                    Check(api.Requests.Any(r => r == "DELETE /api/pair/sessions/" + session.CodeHash), "session deleted on the cloud after completion");

                    await service.GenerateAsync();
                    Check(service.State == PairingState.Active, "second session active");
                    await Task.Delay(2600);
                    Check(service.State == PairingState.Expired && service.Live == null, "session expires after the TTL");
                    Check(!service.TryComplete(deviceId, Crypto.Base64Url(proof), Crypto.Base64Url(nonce), out secret), "expired session cannot complete");
                    service.Cancel();
                    Check(service.State == PairingState.Idle, "cancel returns to Idle");
                }

                using (var offline = new PairingSessionService("http://127.0.0.1:9", pcId, 31377, () => candidates, TimeSpan.FromMinutes(2)))
                {
                    await offline.GenerateAsync();
                    Check(offline.State == PairingState.Error, "unreachable cloud -> Error state");
                }
            }
        }

        static async Task HttpRoutes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tsgps-pairing-tests-" + Guid.NewGuid().ToString("N"));
            var settings = Settings.Load(Path.Combine(dir, "Settings.json"));
            Check(Crypto.IsHexId(settings.PcId, 32) && File.Exists(settings.FilePath), "fresh settings mint a 32-hex pcId and persist it");
            var devices = new LinkedDeviceStore(settings);
            using (var api = new FakeApi((m, p, b) => Tuple.Create(m == "POST" ? 201 : 204, "{}")))
            using (var pairing = new PairingSessionService(api.BaseUrl, settings.PcId, 31377, () => new List<string> { "127.0.0.1/8" }, TimeSpan.FromMinutes(2)))
            {
                int port = FreePort();
                using (var server = new MinimalHttpServer(port, pairing, devices))
                using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") })
                {
                    server.Start();
                    var hello = JObject.Parse(await http.GetStringAsync("/api/pair/hello"));
                    Check((string)hello["pcId"] == settings.PcId && (string)hello["name"] == Environment.MachineName && (int)hello["serverVersion"] == 7, "GET /api/pair/hello -> {pcId, name, serverVersion: 7}");

                    var telemetry = JObject.Parse(await http.GetStringAsync("/api/ets2/telemetry"));
                    Check((int)telemetry["serverVersion"] == 7, "telemetry serverVersion is 7");

                    string deviceId = Crypto.RandomHexId();
                    var missing = await http.PostAsync("/api/pair/complete", Json(new { deviceId, deviceName = "Pixel 8", proof = "AA", nonce = "AA" }));
                    Check(missing.StatusCode == HttpStatusCode.NotFound, "complete without a live session -> 404");

                    await pairing.GenerateAsync();
                    var session = pairing.Current;
                    var nonce = Crypto.RandomBytes(16);
                    var proof = Crypto.HmacSha256(session.Secret, Crypto.Concat(Encoding.UTF8.GetBytes(deviceId), nonce));
                    var bad = await http.PostAsync("/api/pair/complete", Json(new { deviceId, deviceName = "Pixel 8", proof = Crypto.Base64Url(Crypto.RandomBytes(32)), nonce = Crypto.Base64Url(nonce) }));
                    Check(bad.StatusCode == HttpStatusCode.NotFound, "complete with a bad proof -> 404");
                    var ok = await http.PostAsync("/api/pair/complete", Json(new { deviceId, deviceName = "Pixel 8", proof = Crypto.Base64Url(proof), nonce = Crypto.Base64Url(nonce) }));
                    Check(ok.StatusCode == HttpStatusCode.OK, "complete with the right proof -> 200");
                    var reply = JObject.Parse(Crypto.OpenEnvelope(session.Secret, (string)JObject.Parse(await ok.Content.ReadAsStringAsync())["env"]));
                    var deviceKey = Crypto.FromBase64Url((string)reply["deviceKey"]);
                    Check(deviceKey.Length == 32 && (string)reply["pcId"] == settings.PcId && (string)reply["name"] == Environment.MachineName, "reply env decrypts under S to {deviceKey (32 B), pcId, name}");
                    var linked = devices.Get(deviceId);
                    Check(linked != null && linked.Name == "Pixel 8" && devices.GetKey(deviceId).SequenceEqual(deviceKey) && linked.LastMethod == ConnectionMethod.Direct, "device stored with the same key, name, method direct");
                    Check(pairing.State == PairingState.Idle, "pairing session consumed");

                    var request = new HttpRequestMessage(HttpMethod.Get, "/api/ets2/telemetry");
                    request.Headers.Add("X-TSGPS-Device", deviceId);
                    request.Headers.Add("X-TSGPS-Device-Name", "Pixel 8");
                    var tracked = await http.SendAsync(request);
                    Check(!tracked.Headers.Contains("X-TSGPS-Pair"), "linked device request carries no pair offer");

                    string manualId = Crypto.RandomHexId();
                    request = new HttpRequestMessage(HttpMethod.Get, "/api/ets2/telemetry");
                    request.Headers.Add("X-TSGPS-Device", manualId);
                    request.Headers.Add("X-TSGPS-Device-Name", "Galaxy Tab");
                    var offered = await http.SendAsync(request);
                    var manual = devices.Get(manualId);
                    Check(offered.Headers.Contains("X-TSGPS-Pair") && offered.Headers.GetValues("X-TSGPS-Pair").First() == "offer", "unknown device header -> X-TSGPS-Pair: offer");
                    Check(manual != null && manual.DeviceKey == null && manual.Name == "Galaxy Tab" && manual.LastMethod == ConnectionMethod.Manual, "unknown device auto-registered as manual");

                    var lan = await http.PostAsync("/api/pair/complete-lan", Json(new { deviceId = manualId, deviceName = "Galaxy Tab", nonce = Crypto.Base64Url(Crypto.RandomBytes(16)) }));
                    var lanReply = JObject.Parse(await lan.Content.ReadAsStringAsync());
                    Check(lan.StatusCode == HttpStatusCode.OK && Crypto.FromBase64Url((string)lanReply["deviceKey"]).Length == 32 && (string)lanReply["pcId"] == settings.PcId, "complete-lan -> plaintext {deviceKey, pcId, name}");
                    Check(devices.GetKey(manualId).SequenceEqual(Crypto.FromBase64Url((string)lanReply["deviceKey"])), "manual device now linked with that key");

                    request = new HttpRequestMessage(HttpMethod.Get, "/api/ets2/telemetry");
                    request.Headers.Add("X-TSGPS-Device", manualId);
                    var after = await http.SendAsync(request);
                    Check(!after.Headers.Contains("X-TSGPS-Pair") && devices.Get(manualId).LastMethod == ConnectionMethod.Direct, "after complete-lan: no offer, method direct");

                    var plain = await http.GetAsync("/api/ets2/telemetry");
                    Check(plain.StatusCode == HttpStatusCode.OK && !plain.Headers.Contains("X-TSGPS-Pair") && devices.Sorted().Count == 2, "request without device headers is untouched");

                    var invalid = await http.PostAsync("/api/pair/complete-lan", Json(new { deviceId = "nope" }));
                    Check(invalid.StatusCode == HttpStatusCode.BadRequest, "malformed deviceId -> 400");

                    devices.Remove(manualId);
                    Check(devices.Get(manualId) == null && Settings.Load(settings.FilePath).LinkedDevices.Count == 1, "remove persists");

                    await RemovedDevices(http, devices, pairing, settings, deviceId, deviceKey);
                }
            }
            Directory.Delete(dir, true);
        }

        static async Task RemovedDevices(HttpClient http, LinkedDeviceStore devices, PairingSessionService pairing, Settings settings, string deviceId, byte[] deviceKey)
        {
            var revoked = new List<string>();
            devices.Revoked += id => revoked.Add(id);
            devices.Remove(deviceId);
            var stored = Settings.Load(settings.FilePath);
            var tombstone = stored.RemovedDevices.FirstOrDefault(r => r.DeviceId == deviceId);
            Check(devices.Get(deviceId) == null && devices.IsRemoved(deviceId) && tombstone != null
                  && tombstone.Until > DateTime.UtcNow + TimeSpan.FromHours(23), "removing a linked device writes a 24 h tombstone");
            Check(revoked.SequenceEqual(new[] { deviceId }), "remove raises Revoked for the device");
            Check(new LinkedDeviceStore(stored).IsRemoved(deviceId), "tombstone survives a restart");

            var refused = await http.SendAsync(WithDevice(HttpMethod.Get, "/api/ets2/telemetry", deviceId));
            Check(refused.StatusCode == HttpStatusCode.Forbidden && (string)JObject.Parse(await refused.Content.ReadAsStringAsync())["error"] == "device_removed", "removed device: telemetry -> 403 device_removed");
            var refusedProfile = await http.SendAsync(WithDevice(HttpMethod.Get, "/api/game/state?game=ets2", deviceId));
            Check(refusedProfile.StatusCode == HttpStatusCode.Forbidden && (string)JObject.Parse(await refusedProfile.Content.ReadAsStringAsync())["error"] == "device_removed", "removed device: the gate sits ahead of every route");
            Check(devices.Get(deviceId) == null, "removed device is not registered again");

            var hello = await http.SendAsync(WithDevice(HttpMethod.Get, "/api/pair/hello", deviceId));
            Check(hello.StatusCode == HttpStatusCode.OK && hello.Headers.GetValues("X-TSGPS-Pair").First() == "removed", "removed device: hello answers with X-TSGPS-Pair: removed");

            var nonceA = Crypto.RandomBytes(16);
            string env = Crypto.SealEnvelope(deviceKey, Newtonsoft.Json.JsonConvert.SerializeObject(new { deviceName = "Pixel 8", nonceA = Crypto.Base64Url(nonceA), ts = 1789000000 }));
            Check(!HandshakeResponder.Answer(devices.GetKey(deviceId), new List<string>(), "", Crypto.Base64Url(nonceA), env).Ok, "removed device: handshake under its old key is rejected");

            var unlink = await http.PostAsync("/api/pair/unlink", Json(new { deviceId, nonce = "AA", proof = "AA" }));
            Check(unlink.StatusCode == HttpStatusCode.NotFound, "removed device: unlink -> 404 not_linked");
            var garbage = await http.PostAsync("/api/pair/unlink", Json(new { deviceId, nonce = "!!", proof = "!!" }));
            Check(garbage.StatusCode == HttpStatusCode.NotFound, "unlink with unparseable base64 -> 404, not 500");

            var fresh = WithDevice(HttpMethod.Get, "/api/ets2/telemetry", deviceId);
            fresh.Headers.Add("X-TSGPS-Pair", "new");
            var accepted = await http.SendAsync(fresh);
            Check(accepted.StatusCode == HttpStatusCode.OK && accepted.Headers.GetValues("X-TSGPS-Pair").First() == "offer" && !devices.IsRemoved(deviceId)
                  && devices.Get(deviceId)?.LastMethod == ConnectionMethod.Manual, "X-TSGPS-Pair: new clears the tombstone, registers manual, offers");
            var again = await http.SendAsync(WithDevice(HttpMethod.Get, "/api/ets2/telemetry", deviceId));
            Check(again.StatusCode == HttpStatusCode.OK, "after the new marker plain requests pass again");

            devices.Link(deviceId, "Pixel 8", ConnectionMethod.Direct);
            devices.Remove(deviceId);
            Check(devices.IsRemoved(deviceId), "linked again and removed again: tombstone back");
            await pairing.GenerateAsync();
            var session = pairing.Current;
            var nonce = Crypto.RandomBytes(16);
            var proof = Crypto.HmacSha256(session.Secret, Crypto.Concat(Encoding.UTF8.GetBytes(deviceId), nonce));
            var complete = await http.SendAsync(WithDevice(HttpMethod.Post, "/api/pair/complete", deviceId, Json(new { deviceId, deviceName = "Pixel 8", proof = Crypto.Base64Url(proof), nonce = Crypto.Base64Url(nonce) })));
            Check(complete.StatusCode == HttpStatusCode.OK && !devices.IsRemoved(deviceId) && devices.GetKey(deviceId) != null
                  && !complete.Headers.Contains("X-TSGPS-Pair"), "pairing completion bypasses and clears the tombstone, and its reply carries no removed header");
            var linkedAgain = await http.SendAsync(WithDevice(HttpMethod.Get, "/api/ets2/telemetry", deviceId));
            Check(linkedAgain.StatusCode == HttpStatusCode.OK && !linkedAgain.Headers.Contains("X-TSGPS-Pair"), "re-paired device polls without an offer");

            var key = devices.GetKey(deviceId);
            var unlinkNonce = Crypto.RandomBytes(16);
            var badProof = await http.SendAsync(WithDevice(HttpMethod.Post, "/api/pair/unlink", deviceId, Json(new { deviceId, nonce = Crypto.Base64Url(unlinkNonce), proof = Crypto.Base64Url(Crypto.RandomBytes(32)) })));
            Check(badProof.StatusCode == HttpStatusCode.NotFound && devices.Get(deviceId) != null, "unlink with a wrong proof changes nothing");
            int revokedBefore = revoked.Count;
            var unlinkProof = Crypto.HmacSha256(key, Crypto.Concat(Encoding.UTF8.GetBytes(deviceId), unlinkNonce));
            var unlinked = await http.SendAsync(WithDevice(HttpMethod.Post, "/api/pair/unlink", deviceId, Json(new { deviceId, nonce = Crypto.Base64Url(unlinkNonce), proof = Crypto.Base64Url(unlinkProof) })));
            Check(unlinked.StatusCode == HttpStatusCode.OK && devices.Get(deviceId) == null && !devices.IsRemoved(deviceId), "unlink with the device proof deletes the row without a tombstone, and its own device headers do not re-register it");
            Check(revoked.Count == revokedBefore + 1 && revoked.Last() == deviceId, "unlink raises Revoked for the device");
            var offeredAgain = await http.SendAsync(WithDevice(HttpMethod.Get, "/api/ets2/telemetry", deviceId));
            Check(offeredAgain.StatusCode == HttpStatusCode.OK && offeredAgain.Headers.Contains("X-TSGPS-Pair"), "unlinked device is offered a pairing again");

            string keyless = Crypto.RandomHexId();
            await http.SendAsync(WithDevice(HttpMethod.Get, "/api/ets2/telemetry", keyless));
            devices.Remove(keyless);
            var keylessAgain = await http.SendAsync(WithDevice(HttpMethod.Get, "/api/ets2/telemetry", keyless));
            Check(!devices.IsRemoved(keyless) && keylessAgain.StatusCode == HttpStatusCode.OK && keylessAgain.Headers.Contains("X-TSGPS-Pair"), "removing a keyless manual row just deletes it");

            devices.Link(deviceId, "Pixel 8", ConnectionMethod.Direct);
            devices.Remove(deviceId);
            lock (settings.Sync)
                settings.RemovedDevices.First(r => r.DeviceId == deviceId).Until = DateTime.UtcNow - TimeSpan.FromSeconds(1);
            Check(!devices.IsRemoved(deviceId) && Settings.Load(settings.FilePath).RemovedDevices.All(r => r.DeviceId != deviceId), "expired tombstone is ignored and pruned");
            var expired = await http.SendAsync(WithDevice(HttpMethod.Get, "/api/ets2/telemetry", deviceId));
            Check(expired.StatusCode == HttpStatusCode.OK, "device with an expired tombstone is served again");
        }

        static HttpRequestMessage WithDevice(HttpMethod method, string path, string deviceId, HttpContent content = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Add("X-TSGPS-Device", deviceId);
            request.Headers.Add("X-TSGPS-Device-Name", "Pixel 8");
            return request;
        }

        static StringContent Json(object body)
        {
            return new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        }

        static void Handshake()
        {
            var deviceKey = Crypto.RandomBytes(32);
            var nonceA = Crypto.RandomBytes(16);
            string nonceAText = Crypto.Base64Url(nonceA);
            string env = Crypto.SealEnvelope(deviceKey, Newtonsoft.Json.JsonConvert.SerializeObject(new { deviceName = "Pixel 8", nonceA = nonceAText, ts = 1789000000 }));
            var candidates = new List<string> { "192.168.1.42/24", "127.0.0.1/8" };

            var answer = HandshakeResponder.Answer(deviceKey, candidates, "https://eu1.relay.example", nonceAText, env);
            Check(answer.Ok && answer.DeviceName == "Pixel 8", "valid env under the device key is accepted");
            var envR = JObject.Parse(Crypto.OpenEnvelope(deviceKey, answer.EnvR));
            var nonceB = Crypto.FromBase64Url((string)envR["nonceB"]);
            Check(nonceB.Length == 16 && (string)envR["relayUrl"] == "https://eu1.relay.example" && envR["candidates"].Select(c => (string)c).SequenceEqual(candidates), "envR = {candidates, relayUrl, nonceB}");
            var okm = Crypto.Hkdf(deviceKey, Crypto.Concat(nonceA, nonceB), Crypto.RelayInfo, 128);
            Check(okm.Take(32).SequenceEqual(answer.KeyAppToPc) && okm.Skip(32).Take(32).SequenceEqual(answer.KeyPcToApp), "session keys = HKDF(deviceKey, nonceA||nonceB, tsgps-relay-v1)[0..64]");

            Check(!HandshakeResponder.Answer(null, candidates, "", nonceAText, env).Ok, "unknown device (no key) rejected");
            Check(!HandshakeResponder.Answer(Crypto.RandomBytes(32), candidates, "", nonceAText, env).Ok, "wrong key rejected");
            Check(!HandshakeResponder.Answer(deviceKey, candidates, "", Crypto.Base64Url(Crypto.RandomBytes(16)), env).Ok, "nonceA mismatch rejected");
            Check(!HandshakeResponder.Answer(deviceKey, candidates, "", nonceAText, "not-base64!").Ok, "garbage env rejected");
        }

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        sealed class FakeApi : IDisposable
        {
            readonly TcpListener _listener;
            readonly Func<string, string, string, Tuple<int, string>> _handler;
            public readonly List<string> Requests = new List<string>();
            public readonly List<string> Bodies = new List<string>();
            public string BaseUrl { get; }

            public FakeApi(Func<string, string, string, Tuple<int, string>> handler)
            {
                _handler = handler;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
                Task.Run(Accept);
            }

            async Task Accept()
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch (Exception) { return; }
                    _ = Task.Run(() => Serve(client));
                }
            }

            async Task Serve(TcpClient client)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[64 * 1024];
                    int total = 0;
                    int headerEnd = -1;
                    while (headerEnd < 0)
                    {
                        int n = await stream.ReadAsync(buffer, total, buffer.Length - total);
                        if (n <= 0) return;
                        total += n;
                        headerEnd = Encoding.ASCII.GetString(buffer, 0, total).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    }
                    string head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var parts = lines[0].Split(' ');
                    int contentLength = lines.Skip(1).Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        .Select(l => int.Parse(l.Substring(15).Trim())).FirstOrDefault();
                    int bodyStart = headerEnd + 4;
                    while (total - bodyStart < contentLength)
                    {
                        int n = await stream.ReadAsync(buffer, total, buffer.Length - total);
                        if (n <= 0) return;
                        total += n;
                    }
                    string body = Encoding.UTF8.GetString(buffer, bodyStart, contentLength);
                    lock (Requests)
                    {
                        Requests.Add(parts[0] + " " + parts[1]);
                        Bodies.Add(body);
                    }
                    var result = _handler(parts[0], parts[1], body);
                    var payload = Encoding.UTF8.GetBytes(result.Item2);
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {result.Item1} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, 0, response.Length);
                    await stream.WriteAsync(payload, 0, payload.Length);
                }
            }

            public void Dispose()
            {
                _listener.Stop();
            }
        }
    }
}
