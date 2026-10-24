using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funbit.Ets.Telemetry.Server.Helpers;
using Newtonsoft.Json;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public enum PairingState { Idle, Generating, Active, Expired, Error }

    public sealed class PairingSession
    {
        public byte[] Secret;
        public string Code;
        public string CodeHash;
        public DateTime ExpiresAt;
        public string QrPayload;
        public bool Used;
    }

    public sealed class PairingSessionService : IDisposable
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        public const int CodeLength = 6;
        const int CreateAttempts = 5;

        string _apiBaseUrl;
        readonly string _pcId;
        readonly int _port;
        readonly Func<IReadOnlyList<string>> _candidates;
        readonly TimeSpan _ttl;
        readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        readonly object _lock = new object();
        Timer _expiryTimer;

        public PairingState State { get; private set; } = PairingState.Idle;
        public PairingSession Current { get; private set; }
        public event Action StateChanged;

        public PairingSessionService(string apiBaseUrl, string pcId, int port, Func<IReadOnlyList<string>> candidates, TimeSpan ttl)
        {
            _apiBaseUrl = apiBaseUrl;
            _pcId = pcId;
            _port = port;
            _candidates = candidates;
            _ttl = ttl;
        }

        public string PcName => Environment.MachineName;

#if DEBUG
        public void UseApi(string apiBaseUrl)
        {
            Cancel();
            _apiBaseUrl = apiBaseUrl;
        }
#endif

        public TimeSpan Remaining
        {
            get
            {
                var session = Current;
                if (session == null)
                    return TimeSpan.Zero;
                var left = session.ExpiresAt - DateTime.UtcNow;
                return left < TimeSpan.Zero ? TimeSpan.Zero : left;
            }
        }

        public PairingSession Live
        {
            get
            {
                lock (_lock)
                {
                    var session = Current;
                    return State == PairingState.Active && session != null && !session.Used && DateTime.UtcNow < session.ExpiresAt
                        ? session
                        : null;
                }
            }
        }

        public byte[] LiveSecret => Live?.Secret;

        public async Task GenerateAsync()
        {
            PairingSession previous;
            lock (_lock)
            {
                previous = Current;
                Current = null;
                SetState(PairingState.Generating);
            }
            if (previous != null)
                DeleteRemote(previous.CodeHash);

            try
            {
                var session = await CreateAsync();
                lock (_lock)
                {
                    Current = session;
                    _expiryTimer?.Dispose();
                    _expiryTimer = new Timer(_ => Expire(session), null, _ttl, Timeout.InfiniteTimeSpan);
                    SetState(PairingState.Active);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Pairing session could not be created", ex);
                lock (_lock)
                    SetState(PairingState.Error);
            }
        }

        async Task<PairingSession> CreateAsync()
        {
            var secret = Crypto.RandomBytes(32);
            var candidates = _candidates();
            for (int attempt = 0; attempt < CreateAttempts; attempt++)
            {
                string code = RandomCode();
                var salt = Crypto.RandomBytes(16);
                var codeHash = Crypto.Sha256(Encoding.UTF8.GetBytes(code));
                var blobKey = Crypto.Hkdf(Encoding.UTF8.GetBytes(code), salt, Crypto.PairInfo, 32);
                var blob = Crypto.SealEnvelope(blobKey, JsonConvert.SerializeObject(new
                {
                    pcId = _pcId,
                    name = PcName,
                    candidates,
                    port = _port,
                    s = Crypto.Base64Url(secret)
                }));
                var body = JsonConvert.SerializeObject(new
                {
                    codeHash = Crypto.ToHex(codeHash),
                    salt = Crypto.Base64Url(salt),
                    blob,
                    pcId = _pcId
                });

                using (var response = await _http.PostAsync(_apiBaseUrl + "/api/pair/sessions",
                    new StringContent(body, Encoding.UTF8, "application/json")))
                {
                    if (response.StatusCode == HttpStatusCode.Conflict)
                        continue;
                    if (response.StatusCode != HttpStatusCode.Created)
                        throw new HttpRequestException($"POST /api/pair/sessions returned {(int)response.StatusCode}");
                }

                return new PairingSession
                {
                    Secret = secret,
                    Code = code,
                    CodeHash = Crypto.ToHex(codeHash),
                    ExpiresAt = DateTime.UtcNow + _ttl,
                    QrPayload = ConnectPayload.BuildUri(_pcId, secret, PcName, _port, candidates, code)
                };
            }
            throw new InvalidOperationException("Every pairing code clashed");
        }

        void Expire(PairingSession session)
        {
            lock (_lock)
            {
                if (Current != session || session.Used)
                    return;
                SetState(PairingState.Expired);
            }
            DeleteRemote(session.CodeHash);
        }

        public void Cancel()
        {
            PairingSession session;
            lock (_lock)
            {
                session = Current;
                Current = null;
                _expiryTimer?.Dispose();
                _expiryTimer = null;
                SetState(PairingState.Idle);
            }
            if (session != null)
                DeleteRemote(session.CodeHash);
        }

        public bool TryComplete(string deviceId, string proof, string nonce, out byte[] secret)
        {
            secret = null;
            PairingSession session;
            lock (_lock)
            {
                session = Live;
                if (session == null)
                    return false;
                byte[] nonceBytes, proofBytes;
                try
                {
                    nonceBytes = Crypto.FromBase64Url(nonce);
                    proofBytes = Crypto.FromBase64Url(proof);
                }
                catch (FormatException)
                {
                    return false;
                }
                var expected = Crypto.HmacSha256(session.Secret, Crypto.Concat(Encoding.UTF8.GetBytes(deviceId), nonceBytes));
                if (!Crypto.ConstantTimeEquals(expected, proofBytes))
                    return false;
                session.Used = true;
                Current = null;
                _expiryTimer?.Dispose();
                _expiryTimer = null;
                SetState(PairingState.Idle);
            }
            secret = session.Secret;
            DeleteRemote(session.CodeHash);
            return true;
        }

        void DeleteRemote(string codeHash)
        {
            string url = _apiBaseUrl + "/api/pair/sessions/" + codeHash;
            Task.Run(async () =>
            {
                try
                {
                    using (await _http.DeleteAsync(url)) { }
                }
                catch (Exception ex)
                {
                    Log.Debug("Pairing session delete failed: " + ex.Message);
                }
            });
        }

        void SetState(PairingState state)
        {
            State = state;
            StateChanged?.Invoke();
        }

        static string RandomCode()
        {
            var bytes = Crypto.RandomBytes(CodeLength);
            var chars = new char[CodeLength];
            for (int i = 0; i < CodeLength; i++)
                chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
            return new string(chars);
        }

        public void Dispose()
        {
            _expiryTimer?.Dispose();
            _http.Dispose();
        }
    }
}
