using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public static class RelaySelector
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        const int Probes = 3;

        public static async Task<string> MeasureAsync(HttpClient http, string apiBaseUrl)
        {
            var relays = JArray.Parse(await http.GetStringAsync(apiBaseUrl + "/api/relays"));
            string best = null;
            double bestRtt = double.MaxValue;
            foreach (var relay in relays)
            {
                string url = ((string)relay["url"]).TrimEnd('/');
                var samples = new List<double>();
                for (int i = 0; i < Probes; i++)
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        using (await http.GetAsync(url + "/ping")) { }
                        samples.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    catch (Exception)
                    {
                    }
                }
                if (samples.Count == 0)
                    continue;
                samples.Sort();
                double median = samples[(samples.Count - 1) / 2];
                Log.InfoFormat("Relay {0}: median RTT {1:F0} ms", url, median);
                if (median < bestRtt)
                {
                    bestRtt = median;
                    best = url;
                }
            }
            return best;
        }
    }
}
