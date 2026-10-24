using System;
using System.Collections.Generic;
using Funbit.Ets.Telemetry.Server.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public sealed class HandshakeAnswer
    {
        public bool Ok;
        public string EnvR = "";
        public string DeviceName;
        public bool Warmup;
        public byte[] KeyAppToPc;
        public byte[] KeyPcToApp;
    }

    public static class HandshakeResponder
    {
        const int NonceBytes = 16;

        public static HandshakeAnswer Answer(byte[] key, IReadOnlyList<string> candidates, string relayUrl, string nonceA, string env)
        {
            var rejected = new HandshakeAnswer();
            if (key == null)
                return rejected;

            byte[] nonceABytes;
            JObject request;
            try
            {
                nonceABytes = Crypto.FromBase64Url(nonceA);
                request = JObject.Parse(Crypto.OpenEnvelope(key, env));
            }
            catch (Exception)
            {
                return rejected;
            }
            if (nonceABytes.Length != NonceBytes || (string)request["nonceA"] != nonceA)
                return rejected;

            var nonceB = Crypto.RandomBytes(NonceBytes);
            var okm = Crypto.Hkdf(key, Crypto.Concat(nonceABytes, nonceB), Crypto.RelayInfo, 128);
            var answer = new HandshakeAnswer
            {
                Ok = true,
                DeviceName = (string)request["deviceName"],
                Warmup = (bool?)request["warmup"] ?? false,
                KeyAppToPc = new byte[32],
                KeyPcToApp = new byte[32],
                EnvR = Crypto.SealEnvelope(key, JsonConvert.SerializeObject(new
                {
                    candidates,
                    relayUrl,
                    nonceB = Crypto.Base64Url(nonceB)
                }))
            };
            Buffer.BlockCopy(okm, 0, answer.KeyAppToPc, 0, 32);
            Buffer.BlockCopy(okm, 32, answer.KeyPcToApp, 0, 32);
            return answer;
        }
    }
}
