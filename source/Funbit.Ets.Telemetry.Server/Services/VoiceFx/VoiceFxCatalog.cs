using System;
using System.Collections.Generic;

namespace TruckSimGps.VoiceFx
{
    public sealed class VoiceFxParam
    {
        public readonly string Id;
        public readonly int Min;
        public readonly int Max;
        public readonly int Default;
        public readonly string[] Options;

        public VoiceFxParam(string id, int min, int max, int defaultValue)
        {
            Id = id;
            Min = min;
            Max = max;
            Default = defaultValue;
        }

        public VoiceFxParam(string id, string[] options, int defaultValue)
            : this(id, 0, options.Length - 1, defaultValue)
        {
            Options = options;
        }

        public bool IsToggle
        {
            get { return Options == null && Min == 0 && Max == 1; }
        }

        public bool IsChoice
        {
            get { return Options != null; }
        }

        public int Clamp(int value)
        {
            return value < Min ? Min : value > Max ? Max : value;
        }
    }

    public sealed class VoiceFxEffect
    {
        public readonly string Id;
        public readonly string[] Sounds;
        public readonly VoiceFxParam[] Params;

        public VoiceFxEffect(string id, string[] sounds, params VoiceFxParam[] parameters)
        {
            Id = id;
            Sounds = sounds;
            Params = parameters;
        }

        public int Value(IDictionary<string, int> values, string paramId)
        {
            foreach (var p in Params)
            {
                if (p.Id != paramId) continue;
                int v;
                return values != null && values.TryGetValue(paramId, out v) ? p.Clamp(v) : p.Default;
            }
            throw new ArgumentException("Effect " + Id + " has no parameter " + paramId);
        }

        public double Unit(IDictionary<string, int> values, string paramId)
        {
            foreach (var p in Params)
                if (p.Id == paramId) return (double)(Value(values, paramId) - p.Min) / (p.Max - p.Min);
            throw new ArgumentException("Effect " + Id + " has no parameter " + paramId);
        }

        public string Choice(IDictionary<string, int> values, string paramId)
        {
            foreach (var p in Params)
                if (p.Id == paramId && p.IsChoice) return p.Options[Value(values, paramId)];
            throw new ArgumentException("Effect " + Id + " has no choice " + paramId);
        }
    }

    public static class VoiceFxCatalog
    {
        public const int Version = 1;

        // "No effects" in Voice Setup: the voice untouched, with the radio in/out sounds still available.
        public const string Clean = "clean";
        public const string Radio = "radio";
        public const string Custom = "custom";
        public const string Arena = "arena";
        public const string Megaphone = "megaphone";
        public const string Robot = "robot";
        public const string Chipmunk = "chipmunk";

        public const string SoundStatic = "static_bed";

        public const string RadioNone = "none";

        // The radio "in" sound (before the voice) and "out" sound (after it) go with every effect, picked by number in
        // Voice Setup: option 0 is none, every other option is a sound id. The order is the numbering, and in #k with
        // out #k is a matched pair (the same radio keyed and released).
        public static readonly string[] RadioIn =
        {
            RadioNone, "radio_in_walkie", "radio_in_cb", "radio_in_handheld", "radio_in_tones", "radio_in_dispatch",
            "radio_in_police", "radio_in_repeater", "radio_in_megaphone", "radio_in_apollo", "radio_in_permit",
        };
        public static readonly string[] RadioOut =
        {
            RadioNone, "radio_out_walkie", "radio_out_cb", "radio_out_handheld", "radio_out_tones", "radio_out_dispatch",
            "radio_out_police", "radio_out_repeater", "radio_out_megaphone", "radio_out_courtesy",
            "radio_out_talkabout",
        };

        static readonly VoiceFxParam In = new VoiceFxParam("in", RadioIn, 0);
        static readonly VoiceFxParam Out = new VoiceFxParam("out", RadioOut, 0);
        static readonly string[] RadioSounds = Concat(RadioIn, RadioOut);

        public static readonly string[] Sounds = Concat(RadioSounds, new[] { SoundStatic });

        public static readonly VoiceFxEffect[] All =
        {
            Effect(Clean, false),
            Effect(Radio, true, P("tone", 50), P("grit", 50), P("static", 50), P("reception", 85)),
            Effect(Custom, false, P("pitch", 20), P("size", 80), P("tone", 50), P("growl", 100), P("rasp", 40), P("speed", 50)),
            Effect(Arena, false, P("depth", 50), P("echo", 50)),
            Effect(Megaphone, false, P("distortion", 50), P("echo", 50)),
            Effect(Robot, false, P("buzz", 50), P("grit", 50)),
            Effect(Chipmunk, false, P("pitch", 50)),
        };

        static VoiceFxEffect Effect(string id, bool staticBed, params VoiceFxParam[] own)
        {
            var parameters = new List<VoiceFxParam>(own) { In, Out };
            return new VoiceFxEffect(id, staticBed ? Sounds : RadioSounds, parameters.ToArray());
        }

        static string[] Concat(string[] a, string[] b)
        {
            var all = new List<string>();
            foreach (var id in a)
                if (id != RadioNone) all.Add(id);
            foreach (var id in b)
                if (id != RadioNone) all.Add(id);
            return all.ToArray();
        }

        public static VoiceFxEffect Find(string id)
        {
            foreach (var e in All)
                if (e.Id == id) return e;
            return null;
        }

        static VoiceFxParam P(string id, int defaultValue)
        {
            return new VoiceFxParam(id, 0, 100, defaultValue);
        }
    }
}
