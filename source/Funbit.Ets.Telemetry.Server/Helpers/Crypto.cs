using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Funbit.Ets.Telemetry.Server.Helpers
{
    public static class Crypto
    {
        public const string PairInfo = "tsgps-pair-v1";
        public const string RelayInfo = "tsgps-relay-v1";
        const int NonceBytes = 12;
        const int TagBits = 128;

        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        public static string RandomHexId()
        {
            return ToHex(RandomBytes(16));
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        public static bool IsHexId(string value, int length)
        {
            if (value == null || value.Length != length)
                return false;
            foreach (var c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            return true;
        }

        public static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] FromBase64Url(string text)
        {
            string s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        public static byte[] HmacSha256(byte[] key, byte[] data)
        {
            using (var hmac = new HMACSHA256(key))
                return hmac.ComputeHash(data);
        }

        public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
        {
            return Hkdf(ikm, salt, Encoding.UTF8.GetBytes(info), length);
        }

        public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length)
        {
            var generator = new HkdfBytesGenerator(new Sha256Digest());
            generator.Init(new HkdfParameters(ikm, salt, info));
            var output = new byte[length];
            generator.GenerateBytes(output, 0, length);
            return output;
        }

        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        public static byte[] GcmEncrypt(byte[] key, byte[] nonce, byte[] plaintext)
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(true, new AeadParameters(new KeyParameter(key), TagBits, nonce));
            var output = new byte[cipher.GetOutputSize(plaintext.Length)];
            int n = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
            cipher.DoFinal(output, n);
            return output;
        }

        public static byte[] GcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertextAndTag)
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(false, new AeadParameters(new KeyParameter(key), TagBits, nonce));
            var output = new byte[cipher.GetOutputSize(ciphertextAndTag.Length)];
            int n = cipher.ProcessBytes(ciphertextAndTag, 0, ciphertextAndTag.Length, output, 0);
            cipher.DoFinal(output, n);
            return output;
        }

        public static string SealEnvelope(byte[] key, string json)
        {
            var nonce = RandomBytes(NonceBytes);
            var body = GcmEncrypt(key, nonce, Encoding.UTF8.GetBytes(json));
            return Base64Url(Concat(nonce, body));
        }

        public static string OpenEnvelope(byte[] key, string envelope)
        {
            var raw = FromBase64Url(envelope);
            if (raw.Length < NonceBytes + TagBits / 8)
                throw new ArgumentException("envelope too short");
            var nonce = new byte[NonceBytes];
            var body = new byte[raw.Length - NonceBytes];
            Buffer.BlockCopy(raw, 0, nonce, 0, NonceBytes);
            Buffer.BlockCopy(raw, NonceBytes, body, 0, body.Length);
            return Encoding.UTF8.GetString(GcmDecrypt(key, nonce, body));
        }

        public static byte[] TunnelNonce(uint directionId, ulong counter)
        {
            var nonce = new byte[NonceBytes];
            nonce[0] = (byte)(directionId >> 24);
            nonce[1] = (byte)(directionId >> 16);
            nonce[2] = (byte)(directionId >> 8);
            nonce[3] = (byte)directionId;
            for (int i = 0; i < 8; i++)
                nonce[4 + i] = (byte)(counter >> (56 - 8 * i));
            return nonce;
        }
    }
}
