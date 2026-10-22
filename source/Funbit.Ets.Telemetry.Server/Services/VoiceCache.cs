using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Funbit.Ets.Telemetry.Server.Helpers;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public sealed class VoiceCache
    {
        public const long DefaultMaxBytesPerDevice = 64L * 1024 * 1024;
        const string KeyIdFile = "key.id";
        const string ClipExtension = ".clip";

        public static readonly string DefaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckSim GPS", "voice");

        readonly string _root;
        readonly long _maxBytesPerDevice;
        readonly ConcurrentDictionary<string, byte[]> _keys = new ConcurrentDictionary<string, byte[]>();
        readonly object _lock = new object();

        public VoiceCache(string root, long maxBytesPerDevice = DefaultMaxBytesPerDevice)
        {
            _root = root;
            _maxBytesPerDevice = maxBytesPerDevice;
        }

        public static bool IsClipId(string id)
        {
            return Crypto.IsHexId(id, 32);
        }

        public bool HasKey(string deviceId)
        {
            return _keys.ContainsKey(deviceId);
        }

        public List<string> Hello(string deviceId, byte[] cacheKey)
        {
            lock (_lock)
            {
                _keys[deviceId] = cacheKey;
                string dir = DeviceDir(deviceId);
                string keyId = Crypto.ToHex(Crypto.HmacSha256(cacheKey, Encoding.ASCII.GetBytes("tsgps-voice-cache-id"))).Substring(0, 32);
                string keyIdPath = Path.Combine(dir, KeyIdFile);
                if (Directory.Exists(dir) && (!File.Exists(keyIdPath) || File.ReadAllText(keyIdPath) != keyId))
                    Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                File.WriteAllText(keyIdPath, keyId);
                foreach (var orphan in Directory.GetFiles(dir, "*.tmp"))
                    File.Delete(orphan);
                return Directory.GetFiles(dir, "*" + ClipExtension).Select(Path.GetFileNameWithoutExtension).Where(IsClipId).ToList();
            }
        }

        public VoiceClip Store(string deviceId, string id, byte[] blob)
        {
            var clip = VoiceClip.Open(Key(deviceId), blob);
            if (clip.Id != id)
                throw new InvalidDataException("clip id mismatch");
            lock (_lock)
            {
                string path = ClipPath(deviceId, id);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, blob);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
                Trim(DeviceDir(deviceId), id);
            }
            return clip;
        }

        public VoiceClip Load(string deviceId, string id)
        {
            var key = Key(deviceId);
            byte[] blob;
            lock (_lock)
            {
                string path = ClipPath(deviceId, id);
                if (!File.Exists(path))
                    return null;
                blob = File.ReadAllBytes(path);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            try
            {
                var clip = VoiceClip.Open(key, blob);
                return clip.Id == id ? clip : null;
            }
            catch (Exception)
            {
                lock (_lock)
                    File.Delete(ClipPath(deviceId, id));
                return null;
            }
        }

        public void Forget(string deviceId)
        {
            lock (_lock)
            {
                byte[] removed;
                _keys.TryRemove(deviceId, out removed);
                string dir = DeviceDir(deviceId);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        byte[] Key(string deviceId)
        {
            byte[] key;
            if (!_keys.TryGetValue(deviceId, out key))
                throw new InvalidOperationException("no voice key for " + deviceId);
            return key;
        }

        void Trim(string dir, string keep)
        {
            var files = new DirectoryInfo(dir).GetFiles("*" + ClipExtension).OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= _maxBytesPerDevice)
                    break;
                if (Path.GetFileNameWithoutExtension(file.Name) == keep)
                    continue;
                total -= file.Length;
                file.Delete();
            }
        }

        string DeviceDir(string deviceId)
        {
            return Path.Combine(_root, deviceId);
        }

        string ClipPath(string deviceId, string id)
        {
            return Path.Combine(DeviceDir(deviceId), id + ClipExtension);
        }
    }
}
