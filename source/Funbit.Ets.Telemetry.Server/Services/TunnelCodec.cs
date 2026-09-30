using System;
using System.IO;
using Funbit.Ets.Telemetry.Server.Helpers;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public static class TunnelFrame
    {
        public const byte Data = 0;
        public const byte Open = 1;
        public const byte Close = 2;
        public const int HeaderSize = 3;

        public static byte[] Build(ushort streamId, byte flag, byte[] payload, int offset, int count)
        {
            var frame = new byte[HeaderSize + count];
            frame[0] = (byte)(streamId >> 8);
            frame[1] = (byte)streamId;
            frame[2] = flag;
            Buffer.BlockCopy(payload, offset, frame, HeaderSize, count);
            return frame;
        }

        public static ushort StreamId(byte[] frame)
        {
            return (ushort)((frame[0] << 8) | frame[1]);
        }
    }

    public sealed class TunnelEncoder
    {
        public const uint AppToPc = 1;
        public const uint PcToApp = 2;

        readonly byte[] _key;
        readonly uint _direction;
        readonly Deflater _deflater = new Deflater(Deflater.DEFAULT_COMPRESSION, true);
        readonly byte[] _buffer = new byte[16 * 1024];
        readonly object _lock = new object();
        ulong _counter;

        public TunnelEncoder(byte[] key, uint direction)
        {
            _key = key;
            _direction = direction;
        }

        public byte[] Encode(byte[] frame)
        {
            lock (_lock)
            {
                var compressed = Deflate(frame);
                var body = Crypto.GcmEncrypt(_key, Crypto.TunnelNonce(_direction, _counter), compressed);
                var wire = new byte[8 + body.Length];
                for (int i = 0; i < 8; i++)
                    wire[i] = (byte)(_counter >> (56 - 8 * i));
                Buffer.BlockCopy(body, 0, wire, 8, body.Length);
                _counter++;
                return wire;
            }
        }

        byte[] Deflate(byte[] input)
        {
            _deflater.SetInput(input);
            _deflater.Flush();
            using (var output = new MemoryStream())
            {
                int n;
                while ((n = _deflater.Deflate(_buffer)) > 0)
                    output.Write(_buffer, 0, n);
                return output.ToArray();
            }
        }
    }

    public sealed class TunnelDecoder
    {
        readonly byte[] _key;
        readonly uint _direction;
        readonly Inflater _inflater = new Inflater(true);
        readonly byte[] _buffer = new byte[16 * 1024];
        readonly object _lock = new object();
        long _lastCounter = -1;

        public TunnelDecoder(byte[] key, uint direction)
        {
            _key = key;
            _direction = direction;
        }

        public byte[] Decode(byte[] wire)
        {
            if (wire.Length < 8 + 16)
                throw new InvalidDataException("tunnel frame too short");
            ulong counter = 0;
            for (int i = 0; i < 8; i++)
                counter = (counter << 8) | wire[i];
            lock (_lock)
            {
                if ((long)counter <= _lastCounter)
                    throw new InvalidDataException("tunnel counter replayed");
                var body = new byte[wire.Length - 8];
                Buffer.BlockCopy(wire, 8, body, 0, body.Length);
                var compressed = Crypto.GcmDecrypt(_key, Crypto.TunnelNonce(_direction, counter), body);
                _lastCounter = (long)counter;
                return Inflate(compressed);
            }
        }

        byte[] Inflate(byte[] input)
        {
            _inflater.SetInput(input);
            using (var output = new MemoryStream())
            {
                int n;
                while ((n = _inflater.Inflate(_buffer)) > 0)
                    output.Write(_buffer, 0, n);
                var frame = output.ToArray();
                if (frame.Length < TunnelFrame.HeaderSize)
                    throw new InvalidDataException("tunnel frame header missing");
                return frame;
            }
        }
    }
}
