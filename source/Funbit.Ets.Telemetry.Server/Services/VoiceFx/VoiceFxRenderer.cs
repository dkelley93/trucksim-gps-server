using System;
using System.Collections.Generic;

namespace TruckSimGps.VoiceFx
{
    public sealed class VoiceFxSounds
    {
        readonly Dictionary<string, float[]> _sounds;
        readonly Func<string, float[]> _load;
        public readonly int SampleRate;

        public VoiceFxSounds(int sampleRate, IDictionary<string, float[]> sounds)
        {
            SampleRate = sampleRate;
            _sounds = new Dictionary<string, float[]>(sounds);
        }

        public VoiceFxSounds(int sampleRate, Func<string, float[]> load)
        {
            SampleRate = sampleRate;
            _sounds = new Dictionary<string, float[]>();
            _load = load;
        }

        public float[] Get(string id)
        {
            lock (_sounds)
            {
                float[] sound;
                if (_sounds.TryGetValue(id, out sound)) return sound;
                if (_load == null) throw new KeyNotFoundException("Voice effect sound missing: " + id);
                sound = _load(id);
                _sounds[id] = sound;
                return sound;
            }
        }
    }

    public struct VoiceFxResult
    {
        public float[] Samples;
        public int PreRollSamples;
    }

    public static class VoiceFxRenderer
    {
        public const double Ceiling = 0.84;
        const double PreGapMs = 70;
        const double PostGapMs = 220;
        static readonly double[] SlapMs = { 25.0 };
        static readonly double[] MetalMs = { 8.0, 12.0 };
        static readonly double[] ArenaMs = { 80.0, 160.0, 240.0 };

        struct Insert
        {
            public string Sound;
            public double RelativeDb;

            public Insert(string sound, double relativeDb)
            {
                Sound = sound;
                RelativeDb = relativeDb;
            }
        }

        sealed class Plan
        {
            public readonly List<Insert> Before = new List<Insert>();
            public readonly List<Insert> After = new List<Insert>();
            public double StaticDb = double.NaN;
            public double Reception = 1;
            public long LengthQ16 = 65536;
            public int TailSamples;
        }

        public static VoiceFxResult Render(float[] voice, int sampleRate, string effectId, IDictionary<string, int> values, uint seed,
            VoiceFxSounds sounds)
        {
            var effect = Find(effectId);
            if (sounds.SampleRate != sampleRate) throw new ArgumentException("Voice effect sounds are not at " + sampleRate + " Hz");
            var plan = Build(effect, values, sampleRate);
            var random = new VoiceFxRandom(seed);
            var level = VoiceFxDsp.Level(voice, 0, voice.Length);
            var x = Process(effect, values, (float[])voice.Clone(), sampleRate, plan);
            var expected = (int)((voice.Length * plan.LengthQ16) >> 16) + plan.TailSamples;
            if (x.Length != expected)
                throw new InvalidOperationException("Voice effect " + effect.Id + " made " + x.Length + " samples, planned " + expected);

            var processed = VoiceFxDsp.Level(x, 0, x.Length);
            if (processed > 0 && level > 0)
            {
                var match = level / processed;
                VoiceFxDsp.Gain(x, match < 0.05 ? 0.05 : match > 20 ? 20 : match);
            }
            double[] fades = null;
            if (plan.Reception < 1)
            {
                fades = VoiceFxDsp.Fades(x.Length, sampleRate, 2.5 * (1 - plan.Reception), -6 - 14 * (1 - plan.Reception), random);
                for (var i = 0; i < x.Length; i++) x[i] = (float)(x[i] * fades[i]);
            }
            return Assemble(x, sampleRate, level, plan, fades, random, sounds);
        }

        // The samples an effect adds around the voice (sounds before and after, an echo tail), apart from the voice's own
        // change of length (LengthQ16).
        public static int AddedSamples(string effectId, IDictionary<string, int> values, int sampleRate, VoiceFxSounds sounds, out int preRoll)
        {
            var plan = Build(Find(effectId), values, sampleRate);
            preRoll = 0;
            foreach (var s in plan.Before) preRoll += sounds.Get(s.Sound).Length;
            if (plan.Before.Count > 0) preRoll += VoiceFxDsp.MsToSamples(PreGapMs, sampleRate);
            var tail = 0;
            foreach (var s in plan.After) tail += sounds.Get(s.Sound).Length;
            if (plan.After.Count > 0) tail += VoiceFxDsp.MsToSamples(PostGapMs, sampleRate);
            return preRoll + tail + plan.TailSamples;
        }

        // The sounds a render with these values uses: the chosen in and out sounds, and the static bed when it's on.
        public static string[] SoundsUsed(string effectId, IDictionary<string, int> values)
        {
            var plan = Build(Find(effectId), values, 1); // the rate only sizes the echo tail
            var used = new List<string>();
            foreach (var s in plan.Before) used.Add(s.Sound);
            foreach (var s in plan.After) used.Add(s.Sound);
            if (!double.IsNaN(plan.StaticDb)) used.Add(VoiceFxCatalog.SoundStatic);
            return used.ToArray();
        }

        // How the effect scales the voice's length, in 1/65536 (65536 = unchanged): the voice part of a render is
        // (length * LengthQ16) >> 16 samples.
        public static long LengthQ16(string effectId, IDictionary<string, int> values, int sampleRate)
        {
            return Build(Find(effectId), values, sampleRate).LengthQ16;
        }

        static VoiceFxEffect Find(string effectId)
        {
            var effect = VoiceFxCatalog.Find(effectId);
            if (effect == null) throw new ArgumentException("Unknown voice effect " + effectId);
            return effect;
        }

        static Plan Build(VoiceFxEffect effect, IDictionary<string, int> values, int sampleRate)
        {
            var plan = new Plan();
            switch (effect.Id)
            {
                case VoiceFxCatalog.Radio:
                    if (effect.Unit(values, "static") > 0) plan.StaticDb = Line(effect.Unit(values, "static"), -48, -27, -12);
                    plan.Reception = effect.Unit(values, "reception");
                    break;
                case VoiceFxCatalog.Custom:
                    plan.LengthQ16 = (long)Math.Round(65536 / Line(effect.Unit(values, "speed"), 0.7, 1.0, 1.4));
                    break;
                case VoiceFxCatalog.Megaphone:
                    if (effect.Unit(values, "echo") > 0) plan.TailSamples = VoiceFxDsp.MsToSamples(SlapMs[SlapMs.Length - 1], sampleRate);
                    break;
                case VoiceFxCatalog.Robot:
                    plan.TailSamples = VoiceFxDsp.MsToSamples(MetalMs[MetalMs.Length - 1], sampleRate);
                    break;
                case VoiceFxCatalog.Arena:
                    if (effect.Unit(values, "echo") > 0) plan.TailSamples = VoiceFxDsp.MsToSamples(ArenaMs[ArenaMs.Length - 1], sampleRate);
                    break;
            }
            var before = effect.Choice(values, "in");
            if (before != VoiceFxCatalog.RadioNone) plan.Before.Add(new Insert(before, RadioSoundDb(before)));
            var after = effect.Choice(values, "out");
            if (after != VoiceFxCatalog.RadioNone) plan.After.Add(new Insert(after, RadioSoundDb(after)));
            return plan;
        }

        // Each radio sound's RMS against the voice's level (dB), measured so its loudest 20 ms sits 10-18 dB over the voice
        // like the approved CB and walkie sounds; the handheld's lone 3 ms click stays lower so the limiter doesn't duck
        // the voice behind it.
        static readonly Dictionary<string, double> RadioSoundLevels = new Dictionary<string, double>
        {
            { "radio_in_cb", 9.5 },
            { "radio_in_walkie", 15.3 },
            { "radio_in_repeater", 11 },
            { "radio_in_handheld", -5 },
            { "radio_in_tones", 12 },
            { "radio_in_dispatch", 10 },
            { "radio_in_apollo", 10.3 },
            { "radio_in_police", 12 },
            { "radio_in_megaphone", 0.5 },
            { "radio_in_permit", 12.7 },
            { "radio_out_cb", 5.5 },
            { "radio_out_handheld", 11 },
            { "radio_out_tones", 7.5 },
            { "radio_out_dispatch", 12 },
            { "radio_out_walkie", 10.3 },
            { "radio_out_police", 10.2 },
            { "radio_out_repeater", 9.9 },
            { "radio_out_courtesy", 12 },
            { "radio_out_megaphone", 4.9 },
            { "radio_out_talkabout", 12.7 },
        };

        static double RadioSoundDb(string sound)
        {
            double db;
            if (RadioSoundLevels.TryGetValue(sound, out db)) return db;
            throw new ArgumentException("Radio sound " + sound + " has no level");
        }

        static float[] Process(VoiceFxEffect effect, IDictionary<string, int> values, float[] x, int sampleRate, Plan plan)
        {
            switch (effect.Id)
            {
                case VoiceFxCatalog.Clean:
                    return x;
                case VoiceFxCatalog.Radio:
                    Biquad.HighPass(Curve(effect.Unit(values, "tone"), 200, 500, 820), sampleRate).Process(x);
                    Biquad.LowPass(Curve(effect.Unit(values, "tone"), 4500, 1900, 1190), sampleRate).Process(x);
                    VoiceFxDsp.Compress(x, sampleRate, -24, 6, 5, 60);
                    VoiceFxDsp.Drive(x, Line(effect.Unit(values, "grit"), 0, 16, 26), false);
                    return x;
                case VoiceFxCatalog.Custom:
                    return Custom(effect, values, x, sampleRate, plan);
                case VoiceFxCatalog.Megaphone:
                    Biquad.HighPass(700, sampleRate).Process(x);
                    Biquad.LowPass(3300, sampleRate).Process(x);
                    VoiceFxDsp.Drive(x, 6 + 24 * effect.Unit(values, "distortion"), true);
                    VoiceFxDsp.Crush(x, 10, 0.5);
                    return VoiceFxDsp.Echo(x, sampleRate, SlapMs, new[] { 0.75 * effect.Unit(values, "echo") });
                case VoiceFxCatalog.Robot:
                    VoiceFxDsp.RingModulate(x, 25 * Math.Pow(8, effect.Unit(values, "buzz")), sampleRate);
                    Biquad.HighPass(120, sampleRate).Process(x);
                    x = VoiceFxDsp.Echo(x, sampleRate, MetalMs, new[] { 0.625, 0.5 });
                    VoiceFxDsp.Compress(x, sampleRate, -22, 6, 20, 250);
                    VoiceFxDsp.Drive(x, 20 * effect.Unit(values, "grit"), false);
                    return x;
                case VoiceFxCatalog.Arena:
                    x = VoiceFxPitch.Shift(x, sampleRate, -(1.5 + 5.5 * effect.Unit(values, "depth")));
                    Biquad.LowShelf(110, 8, sampleRate).Process(x);
                    VoiceFxDsp.Compress(x, sampleRate, -20, 5, 20, 250);
                    VoiceFxDsp.Drive(x, 6, false);
                    var arena = 2 * effect.Unit(values, "echo");
                    return VoiceFxDsp.Echo(x, sampleRate, ArenaMs, new[] { 0.5625 * arena, 0.375 * arena, 0.25 * arena });
                case VoiceFxCatalog.Chipmunk:
                    return VoiceFxPitch.Shift(x, sampleRate, 3 + 7 * effect.Unit(values, "pitch"));
            }
            throw new ArgumentException("Voice effect " + effect.Id + " has no processing");
        }

        // The custom voice, in chain order: pitch (6 down to 4 up; higher starts to sound processed) and size (formants;
        // 0 small, 100 big) in one PSOLA pass, growl, tone (a tilt: 0 dark, 100 bright), rasp, speed.
        static float[] Custom(VoiceFxEffect effect, IDictionary<string, int> values, float[] x, int sampleRate, Plan plan)
        {
            var pitch = Line(effect.Unit(values, "pitch"), -6, 0, 4);
            var size = Line(effect.Unit(values, "size"), 4, 0, -4);
            if (pitch != 0 || size != 0) x = VoiceFxPsola.Shift(x, sampleRate, pitch, size);
            var growl = effect.Unit(values, "growl");
            if (growl > 0) x = VoiceFxPsola.Subharmonic(x, sampleRate, growl);
            var tilt = Line(effect.Unit(values, "tone"), 6, 0, -6);
            if (tilt != 0)
            {
                Biquad.LowShelf(250, tilt, sampleRate).Process(x);
                Biquad.HighShelf(2500, -tilt, sampleRate).Process(x);
            }
            var rasp = effect.Unit(values, "rasp");
            if (rasp > 0)
            {
                var rough = (float[])x.Clone();
                Biquad.HighPass(300, sampleRate).Process(rough);
                VoiceFxDsp.Drive(rough, 6 + 20 * rasp, false);
                for (var i = 0; i < x.Length; i++) x[i] = (float)((1 - 0.6 * rasp) * x[i] + 0.6 * rasp * rough[i]);
            }
            return plan.LengthQ16 == 65536 ? x : VoiceFxPitch.Stretch(x, sampleRate, plan.LengthQ16);
        }

        static double Line(double unit, double low, double middle, double high)
        {
            return unit <= 0.5 ? low + (middle - low) * unit * 2 : middle + (high - middle) * (unit * 2 - 1);
        }

        static double Curve(double unit, double low, double middle, double high)
        {
            return unit <= 0.5 ? low * Math.Pow(middle / low, unit * 2) : middle * Math.Pow(high / middle, unit * 2 - 1);
        }

        static VoiceFxResult Assemble(float[] voice, int sampleRate, double level, Plan plan, double[] fades, VoiceFxRandom random, VoiceFxSounds sounds)
        {
            var preLength = 0;
            foreach (var s in plan.Before) preLength += sounds.Get(s.Sound).Length;
            var postLength = 0;
            foreach (var s in plan.After) postLength += sounds.Get(s.Sound).Length;
            var preGap = plan.Before.Count > 0 ? VoiceFxDsp.MsToSamples(PreGapMs, sampleRate) : 0;
            var postGap = plan.After.Count > 0 ? VoiceFxDsp.MsToSamples(PostGapMs, sampleRate) : 0;
            var voiceStart = preLength + preGap;
            var bedEnd = voiceStart + voice.Length + postGap;
            var y = new float[bedEnd + postLength];

            var at = 0;
            foreach (var s in plan.Before) at = Place(y, at, sounds.Get(s.Sound), level, s.RelativeDb);
            Array.Copy(voice, 0, y, voiceStart, voice.Length);
            at = bedEnd;
            foreach (var s in plan.After) at = Place(y, at, sounds.Get(s.Sound), level, s.RelativeDb);

            if (!double.IsNaN(plan.StaticDb) && level > 0)
                AddStatic(y, preLength, bedEnd, voiceStart - preLength, fades, sampleRate, level, plan.StaticDb, random, sounds.Get(VoiceFxCatalog.SoundStatic));
            var whole = VoiceFxDsp.Level(y, voiceStart, voice.Length);
            if (whole > level && level > 0) VoiceFxDsp.Gain(y, level / whole);
            VoiceFxDsp.Limit(y, sampleRate, Ceiling);
            return new VoiceFxResult { Samples = y, PreRollSamples = voiceStart };
        }

        static int Place(float[] y, int at, float[] sound, double level, double relativeDb)
        {
            var rms = VoiceFxDsp.Rms(sound);
            var gain = rms > 0 ? level * VoiceFxDsp.DbToGain(relativeDb) / rms : 0;
            for (var i = 0; i < sound.Length; i++) y[at + i] = (float)(sound[i] * gain);
            return at + sound.Length;
        }

        static void AddStatic(float[] y, int start, int end, int fadesAt, double[] fades, int sampleRate, double level, double staticDb,
            VoiceFxRandom random, float[] loop)
        {
            var length = end - start;
            if (length <= 0 || loop.Length == 0) return;
            var bed = new float[length];
            var offset = random.Below(loop.Length);
            for (var i = 0; i < length; i++) bed[i] = loop[(offset + i) % loop.Length];
            Biquad.HighPass(500, sampleRate).Process(bed);
            Biquad.LowPass(4000, sampleRate).Process(bed);
            var rms = VoiceFxDsp.Rms(bed);
            if (rms <= 0) return;
            VoiceFxDsp.Gain(bed, level * VoiceFxDsp.DbToGain(staticDb) / rms);
            if (fades != null)
                for (var i = 0; i < fades.Length && fadesAt + i < length; i++)
                {
                    var swell = 1 / Math.Sqrt(fades[i]);
                    bed[fadesAt + i] = (float)(bed[fadesAt + i] * (swell > 4 ? 4 : swell));
                }
            var fade = Math.Min(length / 2, VoiceFxDsp.MsToSamples(10, sampleRate));
            VoiceFxDsp.Fade(bed, 0, fade, true);
            VoiceFxDsp.Fade(bed, length - fade, fade, false);
            for (var i = 0; i < length; i++) y[start + i] += bed[i];
        }
    }
}
