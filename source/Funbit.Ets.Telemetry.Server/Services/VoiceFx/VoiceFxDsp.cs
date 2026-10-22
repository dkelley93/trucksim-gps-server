using System;

// Shared byte-identical by the TruckSim GPS app and PC Telemetry (net48, C# 7.3): keep to that language level, no Math
// calls in per-sample loops whose result feeds a decision, integers for every decision.
namespace TruckSimGps.VoiceFx
{
    public sealed class VoiceFxRandom
    {
        uint _state;

        public VoiceFxRandom(uint seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
        }

        public uint Next()
        {
            var x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        public int Below(int max)
        {
            return (int)(Next() % (uint)max);
        }
    }

    public sealed class Biquad
    {
        readonly double _b0, _b1, _b2, _a1, _a2;
        double _x1, _x2, _y1, _y2;

        Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public static Biquad HighPass(double hz, int sampleRate)
        {
            double w = 2 * Math.PI * hz / sampleRate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * 0.7071067811865476);
            return new Biquad((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }

        public static Biquad LowPass(double hz, int sampleRate)
        {
            double w = 2 * Math.PI * hz / sampleRate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * 0.7071067811865476);
            return new Biquad((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }

        public static Biquad LowShelf(double hz, double gainDb, int sampleRate)
        {
            double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * hz / sampleRate, c = Math.Cos(w);
            double alpha = Math.Sin(w) / 2 * Math.Sqrt((a + 1 / a) * (1 / 0.5 - 1) + 2), sq = 2 * Math.Sqrt(a) * alpha;
            return new Biquad(a * ((a + 1) - (a - 1) * c + sq), 2 * a * ((a - 1) - (a + 1) * c), a * ((a + 1) - (a - 1) * c - sq),
                (a + 1) + (a - 1) * c + sq, -2 * ((a - 1) + (a + 1) * c), (a + 1) + (a - 1) * c - sq);
        }

        public static Biquad HighShelf(double hz, double gainDb, int sampleRate)
        {
            double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * hz / sampleRate, c = Math.Cos(w);
            double alpha = Math.Sin(w) / 2 * Math.Sqrt((a + 1 / a) * (1 / 0.5 - 1) + 2), sq = 2 * Math.Sqrt(a) * alpha;
            return new Biquad(a * ((a + 1) + (a - 1) * c + sq), -2 * a * ((a - 1) + (a + 1) * c), a * ((a + 1) + (a - 1) * c - sq),
                (a + 1) - (a - 1) * c + sq, 2 * ((a - 1) - (a + 1) * c), (a + 1) - (a - 1) * c - sq);
        }

        public void Process(float[] x)
        {
            for (var i = 0; i < x.Length; i++)
            {
                double input = x[i];
                var y = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                if (y < 1e-20 && y > -1e-20) y = 0;
                _x2 = _x1;
                _x1 = input;
                _y2 = _y1;
                _y1 = y;
                x[i] = (float)y;
            }
        }
    }

    public static class VoiceFxDsp
    {
        public static int MsToSamples(double ms, int sampleRate)
        {
            return (int)Math.Round(ms * sampleRate / 1000.0);
        }

        public static double DbToGain(double db)
        {
            return Math.Pow(10, db / 20);
        }

        public static void Gain(float[] x, double gain)
        {
            for (var i = 0; i < x.Length; i++) x[i] = (float)(x[i] * gain);
        }

        public static double SoftClip(double x)
        {
            if (x >= 3) return 1;
            if (x <= -3) return -1;
            var x2 = x * x;
            return x * (27 + x2) / (27 + 9 * x2);
        }

        public static void Drive(float[] x, double gainDb, bool hard)
        {
            var g = DbToGain(gainDb);
            for (var i = 0; i < x.Length; i++)
            {
                var v = x[i] * g;
                if (hard) v = v > 1 ? 1 : v < -1 ? -1 : v;
                else v = SoftClip(v);
                x[i] = (float)v;
            }
        }

        public static void Compress(float[] x, int sampleRate, double thresholdDb, double ratio, double attackMs, double releaseMs)
        {
            double rmsCoef = Math.Exp(-1.0 / (0.010 * sampleRate));
            double attack = Math.Exp(-1.0 / (attackMs / 1000.0 * sampleRate)), release = Math.Exp(-1.0 / (releaseMs / 1000.0 * sampleRate));
            const double kneeDb = 6;
            double meanSquare = 0, gainDb = 0;
            for (var i = 0; i < x.Length; i++)
            {
                double v = x[i];
                meanSquare = rmsCoef * meanSquare + (1 - rmsCoef) * v * v;
                var levelDb = 10 * Math.Log10(meanSquare + 1e-12);
                var over = levelDb - thresholdDb;
                double target;
                if (over <= -kneeDb / 2) target = 0;
                else if (over >= kneeDb / 2) target = -over * (1 - 1 / ratio);
                else target = -(1 - 1 / ratio) * (over + kneeDb / 2) * (over + kneeDb / 2) / (2 * kneeDb);
                var coef = target < gainDb ? attack : release;
                gainDb = coef * gainDb + (1 - coef) * target;
                x[i] = (float)(v * Math.Pow(10, gainDb / 20));
            }
        }

        public static void Crush(float[] x, int bits, double mix)
        {
            if (bits >= 16) return;
            var levels = (1 << (bits - 1)) - 1;
            double mu = 255, logMu = Math.Log(1 + mu);
            var steps = new double[levels + 1];
            var edges = new double[levels];
            for (var k = 0; k <= levels; k++) steps[k] = (Math.Exp((double)k / levels * logMu) - 1) / mu;
            for (var k = 0; k < levels; k++) edges[k] = (Math.Exp((k + 0.5) / levels * logMu) - 1) / mu;
            for (var i = 0; i < x.Length; i++)
            {
                double v = x[i], a = v < 0 ? -v : v;
                int low = 0, high = levels;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (edges[middle] <= a) low = middle + 1;
                    else high = middle;
                }
                var quantized = v < 0 ? -steps[low] : steps[low];
                x[i] = (float)(mix * quantized + (1 - mix) * v);
            }
        }

        const int SineBits = 12;
        static readonly float[] Sine = BuildSine();

        static float[] BuildSine()
        {
            var table = new float[(1 << SineBits) + 1];
            for (var i = 0; i < table.Length; i++) table[i] = (float)Math.Sin(2 * Math.PI * i / (1 << SineBits));
            return table;
        }

        static double SineAt(uint phase)
        {
            const int shift = 32 - SineBits;
            const uint fracMask = (1u << shift) - 1;
            var index = (int)(phase >> shift);
            var frac = (phase & fracMask) / (double)(1u << shift);
            return Sine[index] + (Sine[index + 1] - Sine[index]) * frac;
        }

        static uint PhaseStep(double hz, int sampleRate)
        {
            return (uint)Math.Round(hz / sampleRate * 4294967296.0);
        }

        public static void RingModulate(float[] x, double hz, int sampleRate)
        {
            var step = PhaseStep(hz, sampleRate);
            uint phase = 0;
            for (var i = 0; i < x.Length; i++)
            {
                x[i] = (float)(x[i] * SineAt(phase));
                phase = unchecked(phase + step);
            }
        }

        public static float[] Echo(float[] x, int sampleRate, double[] delaysMs, double[] decays)
        {
            var delays = new int[delaysMs.Length];
            var longest = 0;
            for (var t = 0; t < delays.Length; t++)
            {
                delays[t] = MsToSamples(delaysMs[t], sampleRate);
                if (decays[t] != 0 && delays[t] > longest) longest = delays[t];
            }
            var y = new float[x.Length + longest];
            for (var i = 0; i < y.Length; i++)
            {
                double v = i < x.Length ? x[i] : 0;
                for (var t = 0; t < delays.Length; t++)
                {
                    var j = i - delays[t];
                    if (j >= 0 && j < x.Length) v += decays[t] * x[j];
                }
                y[i] = (float)v;
            }
            return y;
        }

        public static double[] Fades(int length, int sampleRate, double perSecond, double depthDb, VoiceFxRandom random)
        {
            var envelope = new double[length];
            for (var i = 0; i < length; i++) envelope[i] = 1;
            if (perSecond <= 0) return envelope;
            var step = MsToSamples(50, sampleRate);
            var threshold = (uint)(Math.Min(1.0, perSecond * 0.05) * 4294967295.0);
            int down = MsToSamples(25, sampleRate), up = MsToSamples(35, sampleRate);
            var at = 0;
            while (at < length)
            {
                if (random.Next() >= threshold)
                {
                    at += step;
                    continue;
                }
                var hold = MsToSamples(40 + random.Below(150), sampleRate);
                var floor = DbToGain(depthDb * (0.6 + 0.4 * random.Below(1001) / 1000.0));
                Ramp(envelope, at, down, floor, true);
                for (var i = at + down; i < at + down + hold && i < length; i++) envelope[i] = floor;
                Ramp(envelope, at + down + hold, up, floor, false);
                at += down + hold + up + step;
            }
            return envelope;
        }

        static void Ramp(double[] envelope, int start, int length, double floor, bool falling)
        {
            for (var k = 0; k < length && start + k < envelope.Length; k++)
            {
                var shape = 0.5 * (1 + Math.Cos(Math.PI * k / length));
                envelope[start + k] = floor + (1 - floor) * (falling ? shape : 1 - shape);
            }
        }

        public static double Level(float[] x, int from, int count)
        {
            const double gate = 1e-4;
            double sum = 0, weights = 0;
            var block = 882;
            for (var start = from; start < from + count; start += block)
            {
                var end = Math.Min(from + count, start + block);
                double ms = 0;
                for (var i = start; i < end; i++) ms += (double)x[i] * x[i];
                ms /= Math.Max(1, end - start);
                var w = ms / (ms + gate);
                sum += w * ms;
                weights += w;
            }
            return weights <= 0 ? 0 : Math.Sqrt(sum / weights);
        }

        public static double Rms(float[] x)
        {
            double sum = 0;
            for (var i = 0; i < x.Length; i++) sum += (double)x[i] * x[i];
            return x.Length == 0 ? 0 : Math.Sqrt(sum / x.Length);
        }

        public static void Limit(float[] x, int sampleRate, double ceiling)
        {
            var look = MsToSamples(2, sampleRate);
            var release = Math.Exp(-1.0 / (0.050 * sampleRate));
            var need = new double[x.Length];
            for (var i = 0; i < x.Length; i++)
            {
                double a = x[i] < 0 ? -x[i] : x[i];
                need[i] = a > ceiling ? ceiling / a : 1;
            }
            var window = new double[x.Length];
            for (var i = 0; i < x.Length; i++)
            {
                var m = 1.0;
                var end = Math.Min(x.Length - 1, i + look);
                for (var j = i; j <= end; j++) if (need[j] < m) m = need[j];
                window[i] = m;
            }
            var gain = 1.0;
            for (var i = 0; i < x.Length; i++)
            {
                gain = window[i] < gain ? window[i] : release * gain + (1 - release) * window[i];
                if (gain > window[i]) gain = window[i];
                x[i] = (float)(x[i] * gain);
            }
        }

        public static void Fade(float[] x, int start, int length, bool fadeIn)
        {
            for (var i = 0; i < length && start + i < x.Length; i++)
            {
                var g = (double)i / length;
                x[start + i] = (float)(x[start + i] * (fadeIn ? g : 1 - g));
            }
        }
    }
}
