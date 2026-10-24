using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;

namespace Funbit.Ets.Telemetry.Server.Helpers
{
    public static class NetworkHelper
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static NetworkInterfaceInfo[] GetAllActiveNetworkInterfaces()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            Log.InfoFormat("Found following network interfaces: {0}{1}", Environment.NewLine,
                string.Join(", " + Environment.NewLine,
                    interfaces.Select(a => $"'{a.Id}': '{a.Name}' ({a.OperationalStatus})")));

            var foundInterfaces = interfaces.Where(
                    a => a.OperationalStatus.ToString() == "Up" &&
                    a.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    a.GetIPProperties()
                     .UnicastAddresses.Any(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork))
                     .Select(i => new NetworkInterfaceInfo
                     {
                         Id = i.Id,
                         Name = i.Name,
                         Ip = i.GetIPProperties().UnicastAddresses
                            .First(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork).Address.ToString()
                     }).ToArray();

            return foundInterfaces;
        }

        // "ip/prefixLength" for every Up interface except loopback and link-local (169.254.x), the
        // preferred interface first; the prefix length lets the phone compare subnets against its own address.
        public static List<string> GetConnectCandidates(string preferredInterfaceId = null)
        {
            var candidates = new List<KeyValuePair<bool, string>>();

            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                bool preferred = iface.Id == preferredInterfaceId;
                foreach (var ua in iface.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    byte[] bytes = ua.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) // link-local, never reachable from a phone
                        continue;

                    int prefix = GetPrefixLength(ua);
                    candidates.Add(new KeyValuePair<bool, string>(preferred, $"{ua.Address}/{prefix}"));
                }
            }

            return candidates
                .OrderByDescending(c => c.Key)
                .Select(c => c.Value)
                .Distinct()
                .ToList();
        }

        static int GetPrefixLength(UnicastIPAddressInformation ua)
        {
            try
            {
                IPAddress mask = ua.IPv4Mask;
                if (mask == null)
                    return 24;
                uint value = BitConverter.ToUInt32(mask.GetAddressBytes().Reverse().ToArray(), 0);
                int prefix = 0;
                while (value != 0)
                {
                    prefix += (int)(value & 1);
                    value >>= 1;
                }
                return prefix;
            }
            catch (Exception)
            {
                // Some virtual adapters throw on IPv4Mask; /24 is the least-wrong default.
                return 24;
            }
        }
    }

    public class NetworkInterfaceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Ip { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}
