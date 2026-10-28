using System;
using System.Reflection;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public interface IVoiceAudio
    {
        void PlayPrompt(string reqId, short[] pcm, int sampleRate);
        void StopPrompt(string reqId, int fadeMs);
        void PlayEffect(short[] pcm, int sampleRate);
    }

    public sealed class WasapiVoiceAudio : IVoiceAudio, IDisposable
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        const int LatencyMs = 100;
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
            readonly MMDevice _device;
            readonly WasapiOut _out;
            readonly Pcm16Provider _pcm;
            bool _disposed;

            public string ReqId { get; }

            public Channel(string reqId, short[] pcm, int sampleRate)
            {
                ReqId = reqId;
                _pcm = new Pcm16Provider(pcm, sampleRate, 2 * LatencyMs);
                using (var devices = new MMDeviceEnumerator())
                    _device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                try
                {
                    _out = new WasapiOut(_device, AudioClientShareMode.Shared, true, LatencyMs);
                    _out.Init(_pcm);
                    _out.PlaybackStopped += (s, e) =>
                    {
                        if (e.Exception != null)
                            Log.Warn("Voice output stopped", e.Exception);
                        Task.Run(() => Dispose());
                    };
                    _out.Play();
                }
                catch
                {
                    _out?.Dispose();
                    _device.Dispose();
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
                Task.Delay(fadeMs + 2 * LatencyMs).ContinueWith(_ => Dispose());
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
                _device.Dispose();
            }
        }

        sealed class Pcm16Provider : IWaveProvider
        {
            readonly short[] _samples;
            readonly int _length;
            readonly object _lock = new object();
            int _position;
            int _fadeLength;
            int _fadeLeft = -1;

            // WasapiOut stops soon after Read runs dry and drops what is still buffered: the silent tail lets the voice drain.
            public Pcm16Provider(short[] samples, int sampleRate, int tailMs)
            {
                _samples = samples;
                _length = samples.Length + (int)((long)tailMs * sampleRate / 1000);
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
                    int n = Math.Min(count / 2, _length - _position);
                    for (int i = 0; i < n; i++)
                    {
                        int p = _position + i;
                        int v = p < _samples.Length ? _samples[p] : 0;
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
