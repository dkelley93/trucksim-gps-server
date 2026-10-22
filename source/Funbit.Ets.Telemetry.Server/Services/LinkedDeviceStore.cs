using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Funbit.Ets.Telemetry.Server.Helpers;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public static class ConnectionMethod
    {
        public const string Direct = "direct";
        public const string Relay = "relay";
        public const string Manual = "manual";
    }

    public class LinkedDeviceStore
    {
        public static readonly TimeSpan ConnectedWindow = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan RemovedFor = TimeSpan.FromHours(24);
        static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);
        const int MaxNameLength = 64;

        const int MaxUnlinkedDevices = 20;

        readonly Settings _settings;
        readonly object _lock;
        DateTime _lastSave = DateTime.MinValue;

        public event Action<string> Revoked;

        public LinkedDeviceStore(Settings settings)
        {
            _settings = settings;
            _lock = settings.Sync;
        }

        public string PcId => _settings.PcId;

        public LinkedDevice Get(string deviceId)
        {
            lock (_lock)
                return _settings.LinkedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
        }

        public byte[] GetKey(string deviceId)
        {
            var device = Get(deviceId);
            if (device?.DeviceKey == null)
                return null;
            try
            {
                return ProtectedData.Unprotect(Crypto.FromBase64Url(device.DeviceKey), null, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // A key written under another Windows user or machine cannot be opened here: treat the device as unlinked.
                return null;
            }
        }

        public byte[] Link(string deviceId, string deviceName, string method)
        {
            var key = Crypto.RandomBytes(32);
            lock (_lock)
            {
                _settings.RemovedDevices.RemoveAll(r => r.DeviceId == deviceId);
                var device = Upsert(deviceId, deviceName, method);
                device.DeviceKey = Crypto.Base64Url(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
                _settings.Save();
                _lastSave = DateTime.UtcNow;
            }
            return key;
        }

        public LinkedDevice Touch(string deviceId, string deviceName, string method)
        {
            deviceName = Cap(deviceName);
            LinkedDevice device;
            bool changed;
            lock (_lock)
            {
                device = Get(deviceId);
                changed = device == null || device.LastMethod != method ||
                          (!string.IsNullOrEmpty(deviceName) && device.Name != deviceName);
                device = Upsert(deviceId, deviceName, method);
                if (changed || DateTime.UtcNow - _lastSave > SaveInterval)
                {
                    _settings.Save();
                    _lastSave = DateTime.UtcNow;
                }
            }
            return device;
        }

        LinkedDevice Upsert(string deviceId, string deviceName, string method)
        {
            var now = DateTime.UtcNow;
            var device = Get(deviceId);
            if (device == null)
            {
                var unlinked = _settings.LinkedDevices.Where(d => d.DeviceKey == null).OrderBy(d => d.LastSeenAt).ToList();
                foreach (var stale in unlinked.Take(Math.Max(0, unlinked.Count + 1 - MaxUnlinkedDevices)))
                    _settings.LinkedDevices.Remove(stale);
                device = new LinkedDevice { DeviceId = deviceId, AddedAt = now };
                _settings.LinkedDevices.Add(device);
            }
            if (!string.IsNullOrEmpty(deviceName))
                device.Name = Cap(deviceName);
            device.LastSeenAt = now;
            device.LastMethod = method;
            return device;
        }

        static string Cap(string name)
        {
            return name != null && name.Length > MaxNameLength ? name.Substring(0, MaxNameLength) : name;
        }

        public void Remove(string deviceId)
        {
            lock (_lock)
            {
                var device = Get(deviceId);
                if (device == null)
                    return;
                _settings.LinkedDevices.Remove(device);
                if (device.DeviceKey != null)
                {
                    _settings.RemovedDevices.RemoveAll(r => r.DeviceId == deviceId);
                    _settings.RemovedDevices.Add(new RemovedDevice { DeviceId = deviceId, Until = DateTime.UtcNow + RemovedFor });
                }
                _settings.Save();
            }
            Revoked?.Invoke(deviceId);
        }

        public bool IsRemoved(string deviceId)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (_settings.RemovedDevices.RemoveAll(r => r.Until <= now) > 0)
                    _settings.Save();
                return _settings.RemovedDevices.Any(r => r.DeviceId == deviceId);
            }
        }

        public void ClearRemoved(string deviceId)
        {
            lock (_lock)
            {
                if (_settings.RemovedDevices.RemoveAll(r => r.DeviceId == deviceId) == 0)
                    return;
                _settings.Save();
            }
        }

        public bool Unlink(string deviceId, string proof, string nonce)
        {
            byte[] nonceBytes, proofBytes;
            try
            {
                nonceBytes = Crypto.FromBase64Url(nonce);
                proofBytes = Crypto.FromBase64Url(proof);
            }
            catch (FormatException)
            {
                return false;
            }
            lock (_lock)
            {
                var key = GetKey(deviceId);
                if (key == null)
                    return false;
                var expected = Crypto.HmacSha256(key, Crypto.Concat(Encoding.UTF8.GetBytes(deviceId), nonceBytes));
                if (!Crypto.ConstantTimeEquals(expected, proofBytes))
                    return false;
                _settings.LinkedDevices.RemoveAll(d => d.DeviceId == deviceId);
                _settings.Save();
            }
            Revoked?.Invoke(deviceId);
            return true;
        }

        public void Flush()
        {
            lock (_lock)
                _settings.Save();
        }

        public static bool IsConnected(LinkedDevice device)
        {
            return DateTime.UtcNow - device.LastSeenAt < ConnectedWindow;
        }

        public List<LinkedDevice> Sorted()
        {
            lock (_lock)
            {
                return _settings.LinkedDevices
                    .OrderByDescending(IsConnected)
                    .ThenByDescending(d => d.LastSeenAt)
                    .ToList();
            }
        }
    }
}
