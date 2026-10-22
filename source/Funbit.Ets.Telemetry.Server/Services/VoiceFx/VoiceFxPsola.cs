using System;
using System.Collections.Generic;

namespace TruckSimGps.VoiceFx
{
    // TD-PSOLA: moves the pitch and keeps the vowels' formants, and moves the formants (a bigger or smaller speaker) by
    // resampling each grain. Grains only sound clean when they stay in phase, so the pitch marks are careful: voicing by
    // normalised correlation, octave errors fixed against the local median period, and each mark placed where its
    // waveform best matches the previous one.
    public static class VoiceFxPsola
    {
        const int MinHz = 60;
        const int MaxHz = 400;

        struct Mark
        {
            public int Position;
            public int Period;
            public bool Voiced;
        }

        public static float[] Shift(float[] x, int sampleRate, double pitchSemitones, double formantSemitones)
        {
            var pitchQ16 = (long)Math.Round(Math.Pow(2, pitchSemitones / 12.0) * 65536);
            var formantQ16 = (long)Math.Round(Math.Pow(2, formantSemitones / 12.0) * 65536);
            if ((pitchQ16 == 65536 && formantQ16 == 65536) || x.Length == 0) return (float[])x.Clone();
            var marks = Marks(x, sampleRate);
            if (marks.Count == 0) return (float[])x.Clone();

            var n = x.Length;
            var y = new double[n];
            var weight = new double[n];
            var windows = new Dictionary<int, double[]>();
            var k = 0;
            var t = marks[0].Position;
            while (t < n)
            {
                while (k + 1 < marks.Count && Math.Abs(marks[k + 1].Position - t) <= Math.Abs(marks[k].Position - t)) k++;
                var mark = marks[k];
                var half = mark.Period;
                var window = Hann(windows, half);
                var length = (int)((2L * half * 65536 + formantQ16 / 2) / formantQ16);
                var start = t - length / 2;
                for (var i = 0; i < length; i++)
                {
                    var target = start + i;
                    if (target < 0 || target >= n) continue;
                    var position = i * formantQ16;
                    var j = (int)(position >> 16);
                    var frac = (position & 0xFFFF) / 65536.0;
                    var w = window[j] + (window[j + 1] - window[j]) * frac;
                    y[target] += Cubic(x, mark.Position - half + j, frac) * w;
                    weight[target] += w;
                }
                var step = mark.Voiced ? (int)(half * 65536L / pitchQ16) : half;
                t += step < 1 ? 1 : step;
            }

            // Normalise by the grains' weight averaged over 15 ms, not sample by sample: per-sample division boosts the
            // window tails between widely spaced grains (pitch down) and bends the waveform.
            var span = VoiceFxDsp.MsToSamples(15, sampleRate);
            var prefix = new double[n + 1];
            for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + weight[i];
            var output = new float[n];
            for (var i = 0; i < n; i++)
            {
                int from = Math.Max(0, i - span / 2), to = Math.Min(n, i + span / 2 + 1);
                var mean = (prefix[to] - prefix[from]) / (to - from);
                output[i] = mean > 0.05 ? (float)(y[i] / mean) : 0;
            }
            return output;
        }

        // A growl: every other pitch period of each voiced run turned down by depth (0-1), with 1.5 ms ramps, low-passed
        // at 1.5 kHz so the octave-down roughness sits in the voice's body instead of buzzing over it.
        public static float[] Subharmonic(float[] x, int sampleRate, double depth)
        {
            var marks = Marks(x, sampleRate);
            var gain = new double[x.Length];
            var ramp = VoiceFxDsp.MsToSamples(1.5, sampleRate);
            var odd = false;
            for (var k = 0; k + 1 < marks.Count; k++)
            {
                if (!marks[k].Voiced || !marks[k + 1].Voiced)
                {
                    odd = false;
                    continue;
                }
                odd = !odd;
                if (!odd) continue;
                int from = marks[k].Position, to = marks[k + 1].Position;
                for (var i = from; i < to; i++)
                {
                    var edge = Math.Min(i - from, to - 1 - i);
                    var shape = edge >= ramp ? 1 : 0.5 - 0.5 * Math.Cos(Math.PI * edge / ramp);
                    gain[i] = -depth * shape;
                }
            }
            var difference = new float[x.Length];
            for (var i = 0; i < x.Length; i++) difference[i] = (float)(x[i] * gain[i]);
            Biquad.LowPass(1500, sampleRate).Process(difference);
            var y = new float[x.Length];
            for (var i = 0; i < x.Length; i++) y[i] = x[i] + difference[i];
            return y;
        }

        static double[] Hann(Dictionary<int, double[]> windows, int half)
        {
            double[] window;
            if (windows.TryGetValue(half, out window)) return window;
            window = new double[2 * half + 2];
            for (var j = 0; j <= 2 * half; j++) window[j] = 0.5 - 0.5 * Math.Cos(Math.PI * j / half);
            windows[half] = window;
            return window;
        }

        static double Cubic(float[] x, int i, double t)
        {
            double a = At(x, i - 1), b = At(x, i), c = At(x, i + 1), d = At(x, i + 2);
            return b + 0.5 * t * (c - a + t * (2 * a - 5 * b + 4 * c - d + t * (3 * (b - c) + d - a)));
        }

        static double At(float[] x, int i)
        {
            return i < 0 || i >= x.Length ? 0 : x[i];
        }

        static List<Mark> Marks(float[] x, int sampleRate)
        {
            var n = x.Length;
            var q = new int[n];
            for (var i = 0; i < n; i++)
            {
                var v = Math.Round(x[i] * 32767.0);
                q[i] = (int)(v > 32767 ? 32767 : v < -32767 ? -32767 : v);
            }
            var decimation = Math.Max(1, sampleRate / 11025);
            var decimated = new int[n / decimation];
            for (var i = 0; i < decimated.Length; i++)
            {
                var sum = 0;
                for (var j = 0; j < decimation; j++) sum += q[i * decimation + j];
                decimated[i] = sum / decimation;
            }

            var hop = VoiceFxDsp.MsToSamples(10, sampleRate);
            var frames = (n + hop - 1) / hop;
            var periods = new int[frames];
            for (var f = 0; f < frames; f++) periods[f] = Period(decimated, (f * hop + hop / 2) / decimation, sampleRate, decimation);
            FixOctaves(periods);
            var sign = Polarity(q, periods, hop);

            var marks = new List<Mark>();
            var position = 0;
            var last = -1;
            while (position < n)
            {
                var period = periods[Math.Min(frames - 1, position / hop)];
                if (period == 0)
                {
                    marks.Add(new Mark { Position = position, Period = hop, Voiced = false });
                    position += hop;
                    last = -1;
                    continue;
                }
                int best;
                if (last < 0)
                {
                    // A voiced run starts at its first peak of the dominant polarity.
                    var end = Math.Min(n - 1, position + period - 1);
                    best = position;
                    for (var i = position + 1; i <= end; i++)
                        if (sign * q[i] > sign * q[best]) best = i;
                }
                else
                {
                    // The next mark: where the waveform around it best matches the waveform around the last one.
                    var expected = last + period;
                    int from = Math.Max(last + 1, expected - period / 4), to = Math.Min(n - 1, expected + period / 4);
                    if (from > to) break;
                    best = from;
                    var bestCorrelation = long.MinValue;
                    var reach = period / 2;
                    for (var c = from; c <= to; c++)
                    {
                        long correlation = 0;
                        for (var j = -reach; j <= reach; j++)
                        {
                            int a = last + j, b = c + j;
                            if (a >= 0 && b < n) correlation += (long)(q[a] >> 4) * (q[b] >> 4);
                        }
                        if (correlation > bestCorrelation)
                        {
                            bestCorrelation = correlation;
                            best = c;
                        }
                    }
                }
                marks.Add(new Mark { Position = best, Period = period, Voiced = true });
                last = best;
                position = best + period;
            }
            return marks;
        }

        // +1 when the voiced frames' positive peaks outweigh their negative ones, else -1.
        static int Polarity(int[] q, int[] periods, int hop)
        {
            long up = 0, down = 0;
            for (var f = 0; f < periods.Length; f++)
            {
                if (periods[f] == 0) continue;
                int high = int.MinValue, low = int.MaxValue;
                for (var i = f * hop; i < Math.Min(q.Length, (f + 1) * hop); i++)
                {
                    if (q[i] > high) high = q[i];
                    if (q[i] < low) low = q[i];
                }
                up += high;
                down -= low;
            }
            return up >= down ? 1 : -1;
        }

        // A period more than 30% off the median of the voiced periods within 70 ms either side is an octave error:
        // replace it with the multiple (x2, /2, x3, /3) closest to that median.
        static void FixOctaves(int[] periods)
        {
            const int reach = 7;
            var raw = (int[])periods.Clone();
            var near = new List<int>();
            for (var f = 0; f < raw.Length; f++)
            {
                if (raw[f] == 0) continue;
                near.Clear();
                for (var g = Math.Max(0, f - reach); g <= Math.Min(raw.Length - 1, f + reach); g++)
                    if (raw[g] != 0) near.Add(raw[g]);
                if (near.Count < 5) continue;
                near.Sort();
                var median = near[near.Count / 2];
                var p = raw[f];
                if (10L * p <= 13L * median && 10L * p >= 7L * median) continue;
                var best = p;
                foreach (var c in new[] { p * 2, p / 2, p * 3, p / 3 })
                    if (c > 0 && Further(best, c, median)) best = c;
                periods[f] = best;
            }
        }

        // Whether a is further from m than b is, as a ratio.
        static bool Further(int a, int b, int m)
        {
            return (long)Math.Max(a, m) * Math.Min(b, m) > (long)Math.Max(b, m) * Math.Min(a, m);
        }

        // The period at a 10 ms frame in decimated samples' terms, or 0 when the frame is quiet or not periodic.
        // Voicing: some lag's normalised correlation reaches 0.5 (4 r^2 >= e0 e1, exact in longs on 11-bit samples).
        // Period: the first AMDF dip within 10% of the deepest, which avoids picking a multiple of the period.
        static int Period(int[] d, int center, int sampleRate, int decimation)
        {
            var minLag = Math.Max(2, sampleRate / (decimation * MaxHz));
            var maxLag = sampleRate / (decimation * MinHz);
            var window = VoiceFxDsp.MsToSamples(30, sampleRate) / decimation;
            var start = center - window / 2;
            if (start < 0 || start + window + maxLag >= d.Length) return 0;

            long energy = 0;
            for (var i = start; i < start + window; i++) energy += Math.Abs(d[i]);
            if (energy < 300L * window) return 0;

            long e0 = 0, e1 = 0;
            for (var i = start; i < start + window; i++)
            {
                long v = d[i] >> 5, w = d[i + minLag] >> 5;
                e0 += v * v;
                e1 += w * w;
            }
            var amdf = new long[maxLag + 1];
            var lowest = long.MaxValue;
            var periodic = false;
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                long sum = 0, correlation = 0;
                for (var i = start; i < start + window; i++)
                {
                    sum += Math.Abs(d[i] - d[i + lag]);
                    correlation += (long)(d[i] >> 5) * (d[i + lag] >> 5);
                }
                amdf[lag] = sum;
                if (sum < lowest) lowest = sum;
                if (correlation > 0 && 4 * correlation * correlation >= e0 * e1) periodic = true;
                long drop = d[start + lag] >> 5, add = d[start + lag + window] >> 5;
                e1 += add * add - drop * drop;
            }
            if (!periodic) return 0;
            for (var lag = minLag + 1; lag < maxLag; lag++)
                if (amdf[lag] <= amdf[lag - 1] && amdf[lag] <= amdf[lag + 1] && 10 * amdf[lag] <= 11 * lowest)
                    return lag * decimation;
            return 0;
        }
    }
}
