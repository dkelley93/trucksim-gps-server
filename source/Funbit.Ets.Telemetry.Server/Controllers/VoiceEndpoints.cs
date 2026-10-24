using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Services;
using Newtonsoft.Json;
using TruckSimGps.VoiceFx;

namespace Funbit.Ets.Telemetry.Server.Controllers
{
    public sealed class VoiceReply
    {
        public int Status { get; set; }
        public object Body { get; set; }

        public static VoiceReply Ok(object body)
        {
            return new VoiceReply { Status = 200, Body = body };
        }

        public static VoiceReply Error(int status, string error, object extra = null)
        {
            return new VoiceReply { Status = status, Body = extra ?? new { error } };
        }
    }

    public sealed class VoiceEndpoints
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly TimeSpan CancelledFor = TimeSpan.FromSeconds(30);
        const int MaxClipsPerPrompt = 8;
        const int MaxClipsPerUpload = 256;
        const int MaxReqIdLength = 64;
        const int MaxFadeMs = 2000;
        const float MaxGain = 2f;

        readonly LinkedDeviceStore _devices;
        readonly VoiceCache _cache;
        readonly IVoiceAudio _audio;
        readonly ConcurrentDictionary<string, DateTime> _cancelled = new ConcurrentDictionary<string, DateTime>();
        readonly object _playLock = new object();

        public VoiceEndpoints(LinkedDeviceStore devices, VoiceCache cache, IVoiceAudio audio)
        {
            _devices = devices;
            _cache = cache;
            _audio = audio;
            devices.Revoked += cache.Forget;
        }

        public VoiceReply Hello(string deviceId, string body)
        {
            var req = Parse<HelloRequest>(body);
            if (req == null)
                return VoiceReply.Error(400, "invalid_request");
            var deviceKey = deviceId == null ? null : _devices.GetKey(deviceId);
            if (deviceKey == null || req.PcId != _devices.PcId || string.IsNullOrEmpty(req.Env))
                return VoiceReply.Error(403, "not_linked");

            byte[] cacheKey;
            try
            {
                var payload = JsonConvert.DeserializeObject<HelloPayload>(Crypto.OpenEnvelope(Crypto.VoiceHelloKey(deviceKey), req.Env));
                cacheKey = Crypto.FromBase64Url(payload?.CacheKey ?? "");
            }
            catch (Exception ex)
            {
                Log.WarnFormat("Voice hello from {0} did not open: {1}", deviceId, ex.Message);
                return VoiceReply.Error(403, "not_linked");
            }
            if (cacheKey.Length != 32)
                return VoiceReply.Error(403, "not_linked");

            var ids = _cache.Hello(deviceId, cacheKey);
            Log.InfoFormat("Voice hello from {0}: {1} clips cached", deviceId, ids.Count);
            return VoiceReply.Ok(new { v = 1, ids });
        }

        public VoiceReply Play(string deviceId, string body)
        {
            var watch = Stopwatch.StartNew();
            var req = Parse<PlayRequest>(body);
            if (req == null || req.Ids == null || req.Ids.Count == 0 || req.Ids.Count > MaxClipsPerPrompt
                || (req.ReqId != null && req.ReqId.Length > MaxReqIdLength) || (req.Fx != null && !req.Fx.IsValid()))
                return VoiceReply.Error(400, "invalid_request");
            if (deviceId == null || !_cache.HasKey(deviceId))
                return VoiceReply.Error(401, "hello_required");
            if (req.ReqId != null && _cancelled.ContainsKey(req.ReqId))
            {
                Log.InfoFormat("Voice play {0} dropped: the device already gave up on it", req.ReqId);
                return VoiceReply.Error(410, "cancelled");
            }

            var voice = req.ThenId == null ? req.Ids : req.Ids.Concat(new[] { req.ThenId }).ToList();
            var needed = req.Fx == null ? voice : voice.Concat(req.Fx.Used().Select(s => req.Fx.Sounds[s])).ToList();
            ClipSet set;
            var failure = Resolve(deviceId, needed, out set);
            if (failure != null)
                return failure;

            short[] pcm;
            int sampleRate;
            long renderMs = 0;
            try
            {
                var main = req.Ids.Select(id => set.Clips[id]).ToList();
                var then = req.ThenId == null ? null : set.Clips[req.ThenId];
                var samples = VoiceDecoder.Join(main, then, req.ThenPauseMs, out sampleRate);
                if (req.Fx != null)
                {
                    var render = Stopwatch.StartNew();
                    samples = VoiceFxRenderer.Render(samples, sampleRate, req.Fx.Id, req.Fx.Values, req.Fx.Seed.Value,
                        Sounds(req.Fx, set, sampleRate)).Samples;
                    renderMs = render.ElapsedMilliseconds;
                }
                pcm = VoiceDecoder.ToPcm16(samples, req.LeadInMs, sampleRate, Gain(req.Gain));
                lock (_playLock)
                {
                    if (req.ReqId != null && _cancelled.ContainsKey(req.ReqId))
                    {
                        Log.InfoFormat("Voice play {0} dropped: stopped while it was being prepared", req.ReqId);
                        return VoiceReply.Error(410, "cancelled");
                    }
                    _audio.PlayPrompt(req.ReqId, pcm, sampleRate);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Voice play " + req.ReqId + " failed", ex);
                return VoiceReply.Error(503, "audio");
            }

            int durationMs = (int)((long)pcm.Length * 1000 / sampleRate);
            Log.InfoFormat("Voice play {0}: {1} clips, {2}, {3} ms of audio, started in {4} ms", req.ReqId, voice.Count,
                req.Fx == null ? "no effect" : "effect " + req.Fx.Id + " rendered in " + renderMs + " ms", durationMs, watch.ElapsedMilliseconds);
            return VoiceReply.Ok(new { durationMs, startMs = (int)watch.ElapsedMilliseconds });
        }

        public VoiceReply Effect(string deviceId, string body)
        {
            var req = Parse<EffectRequest>(body);
            if (req == null || req.Id == null)
                return VoiceReply.Error(400, "invalid_request");
            if (deviceId == null || !_cache.HasKey(deviceId))
                return VoiceReply.Error(401, "hello_required");

            ClipSet set;
            var failure = Resolve(deviceId, new List<string> { req.Id }, out set);
            if (failure != null)
                return failure;

            short[] pcm;
            int sampleRate;
            try
            {
                pcm = VoiceDecoder.ToPcm16(VoiceDecoder.Join(new[] { set.Clips[req.Id] }, null, 0, out sampleRate), 0, sampleRate, Gain(req.Gain));
                _audio.PlayEffect(pcm, sampleRate);
            }
            catch (Exception ex)
            {
                Log.Error("Voice effect failed", ex);
                return VoiceReply.Error(503, "audio");
            }
            Log.InfoFormat("Voice effect {0}", req.Id);
            return VoiceReply.Ok(new { durationMs = (int)((long)pcm.Length * 1000 / sampleRate) });
        }

        public VoiceReply Clips(string deviceId, string body)
        {
            var req = Parse<ClipsRequest>(body);
            if (req == null || req.Clips == null || req.Clips.Count == 0 || req.Clips.Count > MaxClipsPerUpload || req.Clips.Contains(null))
                return VoiceReply.Error(400, "invalid_request");
            if (deviceId == null || !_cache.HasKey(deviceId))
                return VoiceReply.Error(401, "hello_required");

            var stored = new List<string>();
            var rejected = new List<string>();
            long bytes = 0;
            foreach (var upload in req.Clips)
            {
                try
                {
                    if (!VoiceCache.IsClipId(upload.Id) || string.IsNullOrEmpty(upload.Blob))
                        throw new InvalidDataException("not a clip id or no blob");
                    var blob = Crypto.FromBase64Url(upload.Blob);
                    _cache.Store(deviceId, upload.Id, blob);
                    stored.Add(upload.Id);
                    bytes += blob.Length;
                }
                catch (Exception ex)
                {
                    Log.WarnFormat("Voice clip {0} from {1} rejected: {2}", upload.Id, deviceId, ex.Message);
                    rejected.Add(upload.Id);
                }
            }
            Log.InfoFormat("Voice clips from {0}: {1} stored ({2} B), {3} rejected", deviceId, stored.Count, bytes, rejected.Count);
            return VoiceReply.Ok(new { stored, rejected });
        }

        public VoiceReply Stop(string deviceId, string body)
        {
            var req = Parse<StopRequest>(body) ?? new StopRequest();
            if (deviceId == null || !_cache.HasKey(deviceId))
                return VoiceReply.Error(401, "hello_required");
            if (req.ReqId != null && req.ReqId.Length > MaxReqIdLength)
                return VoiceReply.Error(400, "invalid_request");
            int fadeMs = Math.Max(0, Math.Min(MaxFadeMs, req.FadeMs));
            var now = DateTime.UtcNow;
            foreach (var expired in _cancelled.Where(c => now - c.Value > CancelledFor).Select(c => c.Key).ToList())
            {
                DateTime ignored;
                _cancelled.TryRemove(expired, out ignored);
            }
            lock (_playLock)
            {
                if (req.ReqId != null)
                    _cancelled[req.ReqId] = now;
                _audio.StopPrompt(req.ReqId, fadeMs);
            }
            Log.InfoFormat("Voice stop {0} (fade {1} ms)", req.ReqId ?? "(current)", fadeMs);
            return VoiceReply.Ok(new { });
        }

        VoiceReply Resolve(string deviceId, IList<string> needed, out ClipSet set)
        {
            set = new ClipSet();
            if (needed.Any(id => !VoiceCache.IsClipId(id)))
                return VoiceReply.Error(400, "invalid_request");

            var missing = new List<string>();
            foreach (var id in needed.Distinct())
            {
                if (set.Clips.ContainsKey(id))
                    continue;
                var clip = _cache.Load(deviceId, id);
                if (clip == null)
                    missing.Add(id);
                else
                    set.Clips[id] = clip;
            }
            if (missing.Count > 0)
            {
                Log.InfoFormat("Voice clips missing for {0}: {1}", deviceId, missing.Count);
                return VoiceReply.Error(409, "missing", new { error = "missing", ids = missing });
            }
            return null;
        }

        static VoiceFxSounds Sounds(FxRequest fx, ClipSet set, int sampleRate)
        {
            var sounds = new Dictionary<string, float[]>();
            foreach (var id in fx.Used())
            {
                var clip = set.Clips[fx.Sounds[id]];
                if (clip.SampleRate != sampleRate)
                    throw new InvalidDataException("effect sound " + id + " is not at the voice's sample rate");
                sounds[id] = VoiceDecoder.Decode(clip);
            }
            return new VoiceFxSounds(sampleRate, sounds);
        }

        static float Gain(float gain)
        {
            return Math.Max(0f, Math.Min(MaxGain, gain));
        }

        static T Parse<T>(string body) where T : class
        {
            if (string.IsNullOrEmpty(body))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<T>(body, JsonHelper.RestSettings);
            }
            catch (Exception ex)
            {
                Log.WarnFormat("Voice request {0} unreadable: {1}", typeof(T).Name, ex.Message);
                return null;
            }
        }

        sealed class ClipSet
        {
            public readonly Dictionary<string, VoiceClip> Clips = new Dictionary<string, VoiceClip>();
        }

        sealed class HelloRequest
        {
            public string PcId { get; set; }
            public string Env { get; set; }
        }

        sealed class HelloPayload
        {
            public string CacheKey { get; set; }
        }

        sealed class ClipUpload
        {
            public string Id { get; set; }
            public string Blob { get; set; }
        }

        sealed class PlayRequest
        {
            public string ReqId { get; set; }
            public List<string> Ids { get; set; }
            public string ThenId { get; set; }
            public int LeadInMs { get; set; }
            public int ThenPauseMs { get; set; }
            public float Gain { get; set; } = 1f;
            public FxRequest Fx { get; set; }
        }

        sealed class FxRequest
        {
            public string Id { get; set; }
            public Dictionary<string, int> Values { get; set; }
            public uint? Seed { get; set; }
            public Dictionary<string, string> Sounds { get; set; }

            public bool IsValid()
            {
                string clipId;
                return VoiceFxCatalog.Find(Id) != null && Seed.HasValue && Sounds != null
                    && Used().All(s => Sounds.TryGetValue(s, out clipId) && VoiceCache.IsClipId(clipId));
            }

            public string[] Used()
            {
                return VoiceFxRenderer.SoundsUsed(Id, Values);
            }
        }

        sealed class EffectRequest
        {
            public string Id { get; set; }
            public float Gain { get; set; } = 1f;
        }

        sealed class ClipsRequest
        {
            public List<ClipUpload> Clips { get; set; }
        }

        sealed class StopRequest
        {
            public string ReqId { get; set; }
            public int FadeMs { get; set; }
        }
    }
}
