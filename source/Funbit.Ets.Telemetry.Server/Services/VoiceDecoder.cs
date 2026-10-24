using System;
using System.Collections.Generic;
using System.IO;
using NLayer;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public static class VoiceDecoder
    {
        static readonly object NLayerLock = new object();

        public static void DecodeInto(VoiceClip clip, float[] dest, int offset)
        {
            if (clip.Codec == VoiceCodec.Pcm16)
            {
                for (int i = 0; i < clip.ValidSamples; i++)
                    dest[offset + i] = Math.Max(-1f, (short)(clip.Payload[2 * i] | clip.Payload[2 * i + 1] << 8) / 32767f);
                return;
            }
            lock (NLayerLock)
            {
                using (var mpeg = new MpegFile(new MemoryStream(clip.Payload, false)))
                {
                    ReadExactly(mpeg, new float[clip.SkipSamples], 0, clip.SkipSamples);
                    ReadExactly(mpeg, dest, offset, clip.ValidSamples);
                }
            }
        }

        public static float[] Decode(VoiceClip clip)
        {
            var samples = new float[clip.ValidSamples];
            DecodeInto(clip, samples, 0);
            return samples;
        }

        public static float[] Join(IList<VoiceClip> clips, VoiceClip then, int thenPauseMs, out int sampleRate)
        {
            sampleRate = clips[0].SampleRate;
            int mainEnd = 0;
            foreach (var clip in clips)
            {
                if (clip.SampleRate != sampleRate)
                    throw new InvalidDataException("clips of one prompt differ in sample rate");
                mainEnd += clip.ValidSamples;
            }
            if (then != null && then.SampleRate != sampleRate)
                throw new InvalidDataException("clips of one prompt differ in sample rate");
            int thenStart = mainEnd + MsToSamples(thenPauseMs, sampleRate);
            var samples = new float[then == null ? mainEnd : thenStart + then.ValidSamples];
            int at = 0;
            foreach (var clip in clips)
            {
                DecodeInto(clip, samples, at);
                at += clip.ValidSamples;
            }
            if (then != null)
                DecodeInto(then, samples, thenStart);
            return samples;
        }

        public static short[] ToPcm16(float[] samples, int leadInMs, int sampleRate, float gain)
        {
            int leadIn = MsToSamples(leadInMs, sampleRate);
            var pcm = new short[leadIn + samples.Length];
            for (int i = 0; i < samples.Length; i++)
                pcm[leadIn + i] = ToPcm16(samples[i] * gain);
            return pcm;
        }

        public static short ToPcm16(float sample)
        {
            return (short)Math.Round(Math.Max(-1f, Math.Min(1f, sample)) * short.MaxValue);
        }

        static int MsToSamples(int ms, int sampleRate)
        {
            return (int)((long)Math.Max(0, ms) * sampleRate / 1000);
        }

        static void ReadExactly(MpegFile mpeg, float[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = mpeg.ReadSamples(buffer, offset + read, count - read);
                if (n == 0)
                    throw new InvalidDataException("MP3 frames ended " + (count - read) + " samples short");
                read += n;
            }
        }
    }
}
