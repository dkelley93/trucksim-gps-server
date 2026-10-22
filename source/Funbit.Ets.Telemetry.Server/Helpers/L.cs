using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Funbit.Ets.Telemetry.Server.Helpers
{
    public static class L
    {
        const string ResourcePrefix = "Localization.";
        const string Source = "en";

        public static readonly IReadOnlyDictionary<string, string> NativeNames = new Dictionary<string, string>
        {
            { "en", "English" },
            { "cs", "Čeština" },
            { "de", "Deutsch" },
            { "es", "Español" },
            { "fr", "Français" },
            { "id", "Bahasa Indonesia" },
            { "nl", "Nederlands" },
            { "pl", "Polski" },
            { "pt-BR", "Português (Brasil)" },
            { "ru", "Русский" },
            { "tr", "Türkçe" }
        };

        static readonly Dictionary<string, string> English = LoadTable(Source);
        static Dictionary<string, string> _active = English;

        public static string Language { get; private set; } = Source;
        public static event Action LanguageChanged;

        public static IReadOnlyList<string> Available => Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix) && n.EndsWith(".json"))
            .Select(n => n.Substring(ResourcePrefix.Length, n.Length - ResourcePrefix.Length - 5))
            .OrderBy(c => c)
            .ToList();

        public static void Apply(string setting)
        {
            string code = setting ?? Detect();
            if (!Available.Contains(code))
                code = Source;
            Language = code;
            _active = code == Source ? English : LoadTable(code);
            LanguageChanged?.Invoke();
        }

        static string Detect()
        {
            var culture = CultureInfo.CurrentUICulture.Name;
            var available = Available;
            if (available.Contains(culture))
                return culture;
            string prefix = culture.Split('-')[0];
            return available.FirstOrDefault(c => c.Split('-')[0] == prefix) ?? Source;
        }

        public static string Get(string key)
        {
            string text;
            if (_active.TryGetValue(key, out text) || English.TryGetValue(key, out text))
                return text;
            return key;
        }

        public static string Get(string key, params object[] args)
        {
            return string.Format(Get(key), args);
        }

        static Dictionary<string, string> LoadTable(string code)
        {
            var table = new Dictionary<string, string>();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + code + ".json"))
            using (var reader = new StreamReader(stream))
            {
                foreach (var entry in JObject.Parse(reader.ReadToEnd()))
                    table[entry.Key] = entry.Value.Type == JTokenType.Object ? (string)entry.Value["text"] : (string)entry.Value;
            }
            return table;
        }
    }
}
