using System;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public interface IVoiceAudio
    {
        void PlayPrompt(string reqId, short[] pcm, int sampleRate);
        void StopPrompt(string reqId, int fadeMs);
        void PlayEffect(short[] pcm, int sampleRate);
    }

    public sealed class WaveOutVoiceAudio : IVoiceAudio, IDisposable
    {
        readonly object _lock = new object();
        Channel _prompt;
        Channel _effect;

        public void PlayPrompt(string reqId, short[] pcm, int sampleRate)
        {
            lock (_lock)
            {
                _prompt?.Dispose();
                _prompt = new Channel(reqId, pcm, sampleRate);
            }
        }

        public void StopPrompt(string reqId, int fadeMs)
        {
            lock (_lock)
            {
                if (_prompt == null || (reqId != null && _prompt.ReqId != reqId))
                    return;
                var stopping = _prompt;
                _prompt = null;
                stopping.FadeOutAndDispose(fadeMs);
            }
        }

        public void PlayEffect(short[] pcm, int sampleRate)
        {
            lock (_lock)
            {
                _effect?.Dispose();
                _effect = new Channel(null, pcm, sampleRate);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _prompt?.Dispose();
                _effect?.Dispose();
                _prompt = _effect = null;
            }
        }

        sealed class Channel : IDisposable
        {
            readonly WaveOutEvent _out = new WaveOutEvent { DesiredLatency = 120 };
            readonly Pcm16Provider _pcm;
            bool _disposed;

            public string ReqId { get; }

            public Channel(string reqId, short[] pcm, int sampleRate)
            {
                ReqId = reqId;
                _pcm = new Pcm16Provider(pcm, sampleRate);
                try
                {
                    _out.Init(_pcm);
                    _out.PlaybackStopped += (s, e) => Task.Run(() => Dispose());
                    _out.Play();
                }
                catch
                {
                    _out.Dispose();
                    throw;
                }
            }

            public void FadeOutAndDispose(int fadeMs)
            {
                if (fadeMs <= 0)
                {
                    Dispose();
                    return;
                }
                _pcm.BeginFadeOut(fadeMs);
                Task.Delay(fadeMs + 20).ContinueWith(_ => Dispose());
            }

            public void Dispose()
            {
                lock (_out)
                {
                    if (_disposed)
                        return;
                    _disposed = true;
                }
                _out.Dispose();
            }
        }

        sealed class Pcm16Provider : IWaveProvider
        {
            readonly short[] _samples;
            readonly object _lock = new object();
            int _position;
            int _fadeLength;
            int _fadeLeft = -1;

            public Pcm16Provider(short[] samples, int sampleRate)
            {
                _samples = samples;
                WaveFormat = new WaveFormat(sampleRate, 16, 1);
            }

            public WaveFormat WaveFormat { get; }

            public void BeginFadeOut(int fadeMs)
            {
                lock (_lock)
                {
                    _fadeLength = Math.Max(1, (int)((long)fadeMs * WaveFormat.SampleRate / 1000));
                    _fadeLeft = _fadeLength;
                }
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                lock (_lock)
                {
                    int n = Math.Min(count / 2, _samples.Length - _position);
                    for (int i = 0; i < n; i++)
                    {
                        int v = _samples[_position + i];
                        if (_fadeLeft >= 0)
                        {
                            v = (int)Math.Round(v * (double)_fadeLeft / _fadeLength);
                            if (_fadeLeft > 0)
                                _fadeLeft--;
                        }
                        buffer[offset + 2 * i] = (byte)v;
                        buffer[offset + 2 * i + 1] = (byte)(v >> 8);
                    }
                    _position += n;
                    return 2 * n;
                }
            }
        }
    }
}
