using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Funbit.Ets.Telemetry.Server.Data
{
    public class SaveGpsState
    {
        public List<GpsWaypointV1> Waypoints { get; set; }
        public SelectedJobV1 SelectedJob { get; set; }
        public bool JobActive { get; set; }
    }

    public static class SaveGpsParser
    {
        const double NavNodeScale = 256.0;

        static readonly Dictionary<string, HashSet<string>> KeptKeys = new Dictionary<string, HashSet<string>>
        {
            { "economy", new HashSet<string> { "stored_gps_ahead_waypoints" } },
            { "player", new HashSet<string> { "current_job", "current_car_job", "selected_job" } },
            { "job_info", new HashSet<string> { "cargo", "source_company", "target_company", "is_cargo_market_job", "planned_distance_km", "car", "units_count" } },
            { "car_offer_data", new HashSet<string> { "cargo" } },
            { "gps_waypoint_storage", new HashSet<string> { "nav_node_position" } },
        };

        class Unit
        {
            public string Type;
            public HashSet<string> Keys;
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string Get(string key) => Values.TryGetValue(key, out var value) && value != "null" ? value : null;
        }

        // A plain-text save caught mid-write lacks its closing brace and is rejected rather than read as a partial route.
        public static SaveGpsState Parse(TextReader reader)
        {
            var units = new Dictionary<string, Unit>();
            Unit economy = null, player = null, current = null;
            bool inUnit = false, closed = false;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line == "}")
                {
                    closed |= !inUnit;
                    inUnit = false;
                    current = null;
                    continue;
                }
                if (!inUnit)
                {
                    int separator = line.IndexOf(" : ", StringComparison.Ordinal);
                    if (separator <= 0 || !line.EndsWith(" {", StringComparison.Ordinal))
                        continue;
                    inUnit = true;
                    string type = line.Substring(0, separator);
                    if (!KeptKeys.TryGetValue(type, out var keys))
                        continue;
                    current = new Unit { Type = type, Keys = keys };
                    units[line.Substring(separator + 3, line.Length - separator - 5)] = current;
                    if (type == "economy" && economy == null) economy = current;
                    if (type == "player" && player == null) player = current;
                    continue;
                }
                if (current == null || line.Length < 2 || line[0] != ' ')
                    continue;
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon <= 1)
                    continue;
                string key = line.Substring(1, colon - 1);
                int index = key.IndexOf('[');
                if (current.Keys.Contains(index > 0 ? key.Substring(0, index) : key))
                    current.Values[key] = line.Substring(colon + 2).Trim();
            }
            if (!closed)
                throw new InvalidDataException("The save ends before its closing brace");

            return new SaveGpsState
            {
                Waypoints = ReadWaypoints(economy, units),
                SelectedJob = ReadSelectedJob(player?.Get("selected_job"), units),
                JobActive = player?.Get("current_job") != null || player?.Get("current_car_job") != null
            };
        }

        static List<GpsWaypointV1> ReadWaypoints(Unit economy, Dictionary<string, Unit> units)
        {
            var waypoints = new List<GpsWaypointV1>();
            if (economy == null || !int.TryParse(economy.Get("stored_gps_ahead_waypoints"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int count))
                return waypoints;
            for (int i = 0; i < count; i++)
            {
                var id = economy.Get($"stored_gps_ahead_waypoints[{i}]");
                var position = id != null && units.TryGetValue(id, out var storage) ? ParseTriple(storage.Get("nav_node_position")) : null;
                if (position == null)
                    continue;
                waypoints.Add(new GpsWaypointV1
                {
                    X = (float)(position[0] / NavNodeScale),
                    Y = (float)(position[1] / NavNodeScale),
                    Z = (float)(position[2] / NavNodeScale)
                });
            }
            return waypoints;
        }

        static SelectedJobV1 ReadSelectedJob(string id, Dictionary<string, Unit> units)
        {
            if (id == null || !units.TryGetValue(id, out var job) || job.Type != "job_info")
                return null;
            Unit offer = null;
            var car = job.Get("car");
            if (car != null)
                units.TryGetValue(car, out offer);
            var source = SplitCompany(job.Get("source_company"));
            var target = SplitCompany(job.Get("target_company"));
            int.TryParse(job.Get("planned_distance_km"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int plannedKm);
            int.TryParse(job.Get("units_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int unitCount);
            return new SelectedJobV1
            {
                CargoId = StripPrefix(job.Get("cargo") ?? offer?.Get("cargo")),
                SourceCompanyId = source?[0],
                SourceCityId = source?[1],
                DestinationCompanyId = target?[0],
                DestinationCityId = target?[1],
                PlannedDistanceKm = plannedKm,
                UnitCount = unitCount,
                Market = car != null ? "dispatch_job" : job.Get("is_cargo_market_job") == "true" ? "cargo_market" : "freight_market"
            };
        }

        static string[] SplitCompany(string id)
        {
            var parts = id?.Split('.');
            if (parts == null || parts.Length < 4 || parts[0] != "company")
                return null;
            return new[] { parts[parts.Length - 2], parts[parts.Length - 1] };
        }

        static string StripPrefix(string id)
        {
            if (id == null)
                return null;
            int dot = id.IndexOf('.');
            return dot >= 0 ? id.Substring(dot + 1) : id;
        }

        static double[] ParseTriple(string value)
        {
            if (value == null || !value.StartsWith("(") || !value.EndsWith(")"))
                return null;
            var parts = value.Substring(1, value.Length - 2).Split(',');
            if (parts.Length != 3)
                return null;
            var result = new double[3];
            for (int i = 0; i < 3; i++)
                if (!TryParseNumber(parts[i].Trim(), out result[i]))
                    return null;
            return result;
        }

        // The decoder prints some floats as "&" + their IEEE-754 bits in hex.
        static bool TryParseNumber(string token, out double value)
        {
            if (token.StartsWith("&"))
            {
                bool ok = uint.TryParse(token.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits);
                value = ok ? BitConverter.ToSingle(BitConverter.GetBytes(bits), 0) : 0;
                return ok;
            }
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
