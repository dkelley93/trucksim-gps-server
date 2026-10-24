using System;
using System.Collections.Generic;
using Funbit.Ets.Telemetry.Server.Helpers;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public static class ConnectPayload
    {
        public const int SchemaVersion = 2;

        public static string BuildUri(string pcId, byte[] secret, string pcName, int port, IReadOnlyList<string> candidates, string code)
        {
            var hash = new byte[12];
            Buffer.BlockCopy(Crypto.Sha256(System.Text.Encoding.UTF8.GetBytes(code)), 0, hash, 0, hash.Length);
            return "trucksimgps://connect" +
                   $"?v={SchemaVersion}" +
                   $"&pc={pcId}" +
                   $"&s={Crypto.Base64Url(secret)}" +
                   $"&n={Uri.EscapeDataString(pcName)}" +
                   $"&p={port}" +
                   $"&i={string.Join(",", candidates)}" +
                   $"&h={Crypto.Base64Url(hash)}";
        }
    }
}
