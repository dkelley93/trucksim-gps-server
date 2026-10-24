using System;

namespace TruckSimGps.VoiceFx
{
    public static class VoiceFxPitch
    {
        public static float[] Shift(float[] x, int sampleRate, double semitones)
        {
            if (semitones == 0 || x.Length == 0) return (float[])x.Clone();
            var ratioQ16 = (long)Math.Round(Math.Pow(2, semitones / 12.0) * 65536);
            var stretched = Stretch(x, sampleRate, ratioQ16);
            if (ratioQ16 > 65536)
            {
                var cutoff = 0.45 * sampleRate * 65536.0 / ratioQ16;
                Biquad.LowPass(cutoff, sampleRate).Process(stretched);
                Biquad.LowPass(cutoff, sampleRate).Process(stretched);
            }
            return Resample(stretched, ratioQ16, x.Length);
        }

        public static float[] Stretch(float[] x, int sampleRate, long ratioQ16)
        {
            var frame = VoiceFxDsp.MsToSamples(40, sampleRate) & ~1;
            var hop = frame / 2;
            var tolerance = VoiceFxDsp.MsToSamples(12, sampleRate);
            var window = new double[frame];
            for (var n = 0; n < frame; n++) window[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / frame);

            var padded = new short[x.Length + frame + hop];
            for (var i = 0; i < x.Length; i++)
            {
                var v = Math.Round(x[i] * 32767.0);
                padded[i] = (short)(v > 32767 ? 32767 : v < -32767 ? -32767 : v);
            }
            var lastStart = x.Length + hop;

            var outLength = (int)((x.Length * ratioQ16) >> 16);
            var hopInQ16 = (long)hop * 65536L * 65536L / ratioQ16;
            var acc = new double[outLength + frame];
            var weight = new double[outLength + frame];
            var previous = 0;
            for (var k = 0; k * hop < outLength; k++)
            {
                var nominal = (int)((k * hopInQ16) >> 16);
                if (nominal > lastStart) nominal = lastStart;
                var start = k == 0 ? 0 : BestStart(padded, previous + hop, nominal, tolerance, hop, lastStart);
                var outAt = k * hop;
                for (var n = 0; n < frame; n++)
                {
                    var src = start + n;
                    var sample = src < x.Length ? x[src] : 0;
                    acc[outAt + n] += sample * window[n];
                    weight[outAt + n] += window[n];
                }
                previous = start;
            }
            var y = new float[outLength];
            for (var i = 0; i < outLength; i++) y[i] = weight[i] > 1e-3 ? (float)(acc[i] / weight[i]) : 0;
            return y;
        }

        static int BestStart(short[] q, int natural, int nominal, int tolerance, int compare, int lastStart)
        {
            if (natural + compare > lastStart) return nominal;
            var low = Math.Max(0, nominal - tolerance);
            var high = Math.Min(lastStart, nominal + tolerance);
            var best = nominal;
            var bestScore = long.MaxValue;
            for (var c = low; c <= high; c += 4)
            {
                var score = Distance(q, natural, c, compare, 4, bestScore);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            var coarse = best;
            bestScore = long.MaxValue;
            for (var c = Math.Max(low, coarse - 4); c <= Math.Min(high, coarse + 4); c++)
            {
                var score = Distance(q, natural, c, compare, 1, bestScore);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best;
        }

        static long Distance(short[] q, int a, int b, int length, int step, long stopAbove)
        {
            long sum = 0;
            for (var n = 0; n < length; n += step)
            {
                var d = q[a + n] - q[b + n];
                sum += d < 0 ? -d : d;
                if (sum >= stopAbove) return sum;
            }
            return sum;
        }

        static float[] Resample(float[] s, long ratioQ16, int length)
        {
            var y = new float[length];
            for (var n = 0; n < length; n++)
            {
                var pos = n * ratioQ16;
                var i = (int)(pos >> 16);
                var t = (pos & 0xFFFF) / 65536.0;
                double y0 = At(s, i - 1), y1 = At(s, i), y2 = At(s, i + 1), y3 = At(s, i + 2);
                var c1 = 0.5 * (y2 - y0);
                var c2 = y0 - 2.5 * y1 + 2 * y2 - 0.5 * y3;
                var c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
                y[n] = (float)(((c3 * t + c2) * t + c1) * t + y1);
            }
            return y;
        }

        static double At(float[] s, int i)
        {
            return i < 0 || i >= s.Length ? 0 : s[i];
        }
    }
}
