using System;
using System.IO;
using System.Text;
using Funbit.Ets.Telemetry.Server.Helpers;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public enum VoiceCodec : byte
    {
        Mp3 = 1,
        Pcm16 = 2
    }

    public sealed class VoiceClip
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("TSGC");
        const byte Version = 1;
        const int NonceBytes = 12;

        public string Id { get; set; }
        public VoiceCodec Codec { get; set; }
        public int SampleRate { get; set; }
        public int SkipSamples { get; set; }
        public int ValidSamples { get; set; }
        public byte[] Payload { get; set; }

        public static VoiceClip Open(byte[] key, byte[] blob)
        {
            if (blob.Length < NonceBytes + 16)
                throw new InvalidDataException("clip blob too short");
            var nonce = new byte[NonceBytes];
            var body = new byte[blob.Length - NonceBytes];
            Buffer.BlockCopy(blob, 0, nonce, 0, NonceBytes);
            Buffer.BlockCopy(blob, NonceBytes, body, 0, body.Length);
            var plain = Crypto.GcmDecrypt(key, nonce, body);

            using (var r = new BinaryReader(new MemoryStream(plain, false), Encoding.ASCII))
            {
                var magic = r.ReadBytes(Magic.Length);
                if (!Crypto.ConstantTimeEquals(magic, Magic) || r.ReadByte() != Version)
                    throw new InvalidDataException("not a voice clip");
                var codec = (VoiceCodec)r.ReadByte();
                if (codec != VoiceCodec.Mp3 && codec != VoiceCodec.Pcm16)
                    throw new InvalidDataException("unknown voice codec " + (int)codec);
                var clip = new VoiceClip
                {
                    Codec = codec,
                    SampleRate = checked((int)r.ReadUInt32()),
                    SkipSamples = checked((int)r.ReadUInt32()),
                    ValidSamples = checked((int)r.ReadUInt32()),
                    Id = Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte()))
                };
                clip.Payload = r.ReadBytes(plain.Length - (int)r.BaseStream.Position);
                if (clip.SampleRate <= 0 || clip.ValidSamples <= 0)
                    throw new InvalidDataException("voice clip has no audio");
                if (codec == VoiceCodec.Pcm16 && clip.Payload.Length < 2L * clip.ValidSamples)
                    throw new InvalidDataException("voice clip is shorter than its sample count");
                return clip;
            }
        }

        public static byte[] Seal(byte[] key, VoiceClip clip)
        {
            var id = Encoding.ASCII.GetBytes(clip.Id);
            var plain = new MemoryStream();
            using (var w = new BinaryWriter(plain, Encoding.ASCII, true))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write((byte)clip.Codec);
                w.Write((uint)clip.SampleRate);
                w.Write((uint)clip.SkipSamples);
                w.Write((uint)clip.ValidSamples);
                w.Write((byte)id.Length);
                w.Write(id);
                w.Write(clip.Payload);
            }
            var nonce = Crypto.RandomBytes(NonceBytes);
            return Crypto.Concat(nonce, Crypto.GcmEncrypt(key, nonce, plain.ToArray()));
        }
    }
}
