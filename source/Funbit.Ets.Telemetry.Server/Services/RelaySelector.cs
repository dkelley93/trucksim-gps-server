using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public static class RelaySelector
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);
        const int Probes = 3;

        public static async Task<IReadOnlyList<string>> ListAsync(HttpClient http, string apiBaseUrl)
        {
            return JArray.Parse(await http.GetStringAsync(apiBaseUrl + "/api/relays"))
                .Select(relay => ((string)relay["url"]).TrimEnd('/'))
                .ToList();
        }

        public static async Task<string> MeasureAsync(HttpClient http, string apiBaseUrl, ICollection<string> excluded)
        {
            var listed = await ListAsync(http, apiBaseUrl);
            var candidates = listed.Where(url => !excluded.Contains(url)).ToList();
            if (candidates.Count == 0)
                candidates = listed.ToList();

            var rtts = await Task.WhenAll(candidates.Select(url => MedianRttAsync(http, url)));
            string best = null;
            double bestRtt = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (rtts[i] < bestRtt)
                {
                    bestRtt = rtts[i];
                    best = candidates[i];
                }
            }
            return best;
        }

        public static async Task<bool> PingAsync(HttpClient http, string url)
        {
            try
            {
                using (var cts = new CancellationTokenSource(PingTimeout))
                using (var response = await http.GetAsync(url + "/ping", cts.Token))
                    return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static async Task<double> MedianRttAsync(HttpClient http, string url)
        {
            var samples = new List<double>();
            for (int i = 0; i < Probes; i++)
            {
                var watch = Stopwatch.StartNew();
                if (!await PingAsync(http, url))
                    break;
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            if (samples.Count == 0)
            {
                Log.InfoFormat("Relay {0}: not answering", url);
                return double.MaxValue;
            }
            samples.Sort();
            double median = samples[(samples.Count - 1) / 2];
            Log.InfoFormat("Relay {0}: median RTT {1:F0} ms", url, median);
            return median;
        }
    }
}
