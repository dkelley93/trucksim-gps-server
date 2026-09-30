using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Funbit.Ets.Telemetry.Server.Helpers
{
    public class Settings
    {
        #region Settings

        public string DefaultNetworkInterfaceId { get; set; }
        public string Ets2GamePath { get; set; }
        public string AtsGamePath { get; set; }

        public bool FirewallSetupHadErrors { get; set; }
        public bool UrlReservationSetupHadErrors { get; set; }

        public string PcId { get; set; }
        public List<LinkedDevice> LinkedDevices { get; set; } = new List<LinkedDevice>();
        public string PreferredRelayUrl { get; set; }
        public string Language { get; set; }
        public string PresenceMode { get; set; } = "gameOnly";

        #endregion

        const string ApplicationName = "TruckSim GPS Telemetry Server";
        const string SettingsName = "Settings.json";

        public static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationName);
        static readonly string SettingsFileName = Path.Combine(SettingsDirectory, SettingsName);

        [JsonIgnore]
        public string FilePath { get; private set; }

        [JsonIgnore]
        public readonly object Sync = new object();

        public static void Clear()
        {
            if (File.Exists(SettingsFileName))
                File.Delete(SettingsFileName);
            if (Directory.Exists(SettingsDirectory))
                Directory.Delete(SettingsDirectory);
        }

        public static Settings Load(string path)
        {
            Settings settings = null;
            if (File.Exists(path))
            {
                try
                {
                    settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path, Encoding.UTF8));
                }
                catch
                {
                    File.Copy(path, path + ".broken", true);
                }
            }
            settings = settings ?? new Settings();
            settings.FilePath = path;
            if (settings.PcId == null)
            {
                settings.PcId = Crypto.RandomHexId();
                settings.Save();
            }
            return settings;
        }

        public void Save()
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
                if (File.Exists(FilePath))
                    File.Replace(temp, FilePath, null);
                else
                    File.Move(temp, FilePath);
            }
        }

        static readonly Lazy<Settings> LazyInstance = new Lazy<Settings>(() => Load(SettingsFileName));
        public static Settings Instance => LazyInstance.Value;
    }

    public class LinkedDevice
    {
        public string DeviceId { get; set; }
        public string DeviceKey { get; set; }
        public string Name { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public string LastMethod { get; set; }
    }
}
