using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Funbit.Ets.Telemetry.Server.Data
{
    public class TruckersMpState
    {
        public bool Active { get; set; }
        public string ClientVersion { get; set; }
        public List<string> MountedFiles { get; set; }
    }

    public static class TruckersMpWatcher
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(
            MethodBase.GetCurrentMethod().DeclaringType);

        const int ModuleProbeSeconds = 60;
        static readonly Regex MountRegex = new Regex(
            @"\[fs\] device (.+?) mounted to mod pool\.$", RegexOptions.Compiled);
        static readonly Regex UnmountRegex = new Regex(
            @"\] ([^\]]+?): Unmounted\.?$", RegexOptions.Compiled);
        static readonly Regex LogCreatedRegex = new Regex(
            @"log created on : \w+ (\w+ \d+ \d{4} @ \d{2}:\d{2}:\d{2})", RegexOptions.Compiled);
        static readonly Regex PackVersionRegex = new Regex(
            @"\[ufs\] Loaded pack set version (\d+(?:\.\d+)+)", RegexOptions.Compiled);

        static readonly object Lock = new object();
        static string _game;
        static int _pid;
        static DateTime? _processStart;
        static DateTime _probeUntil;
        static bool _active;
        static bool _moduleSeen;
        static string _version;
        static string _gameVersion;
        static readonly List<string> _mounted = new List<string>();
        static bool _logCurrent;
        static long _logOffset;
        static byte[] _remainder = new byte[0];

        public static TruckersMpState GetState(string game)
        {
            lock (Lock)
            {
                bool active = _active && _game == game;
                return new TruckersMpState
                {
                    Active = active,
                    ClientVersion = active ? _version : null,
                    MountedFiles = active ? _mounted.ToList() : null,
                };
            }
        }

        public static string LoggedGameVersion
        {
            get
            {
                lock (Lock)
                    return _gameVersion;
            }
        }

        /// <summary>Called once per watcher sweep; true when the reported state changed.</summary>
        internal static bool Poll(string game)
        {
            try
            {
                var processes = Process.GetProcessesByName(game == "ats" ? "amtrucks" : "eurotrucks2");
                if (processes.Length == 0)
                    return Reset(null, 0, null);
                bool changed;
                using (var process = processes[0])
                {
                    changed = process.Id != _pid && Reset(game, process.Id, ReadProcessStart(game, process.Id));
                    if (!_moduleSeen && DateTime.Now <= _probeUntil)
                    {
                        var module = FindClientModule(process);
                        if (module != null)
                        {
                            lock (Lock)
                            {
                                changed |= !_active;
                                _active = true;
                                _moduleSeen = true;
                                _version = module.FileVersionInfo.FileVersion;
                            }
                            Log.InfoFormat("[{0}] TruckersMP client {1} detected", game, _version);
                        }
                    }
                }
                return ReadLog(game) || changed;
            }
            catch (Exception ex)
            {
                // An elevated game hides its process details; the profile sweep must go on.
                Log.Debug("TruckersMP probe failed", ex);
                return false;
            }
        }

        // The client DLL is injected at launch, so probing stops a minute after first sight.
        static bool Reset(string game, int pid, DateTime? processStart)
        {
            lock (Lock)
            {
                bool changed = _active;
                _game = game;
                _pid = pid;
                _processStart = processStart;
                _probeUntil = DateTime.Now.AddSeconds(ModuleProbeSeconds);
                _active = false;
                _moduleSeen = false;
                _version = null;
                _gameVersion = null;
                _mounted.Clear();
                ResetLog();
                return changed;
            }
        }

        static void ResetLog()
        {
            _logCurrent = false;
            _logOffset = 0;
            _remainder = new byte[0];
        }

        static DateTime? ReadProcessStart(string game, int pid)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                           $"SELECT CreationDate FROM Win32_Process WHERE ProcessId = {pid}"))
                using (var processes = searcher.Get())
                {
                    foreach (ManagementBaseObject process in processes)
                    {
                        using (process)
                        {
                            var date = process["CreationDate"] as string;
                            if (date == null)
                                break;
                            var start = ManagementDateTimeConverter.ToDateTime(date);
                            Log.InfoFormat("[{0}] Game process {1} started at {2:HH:mm:ss}", game, pid, start);
                            return start;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[{game}] Could not read the start time of process {pid}", ex);
            }
            Log.InfoFormat("[{0}] Game process {1} start time unknown; game.log is read without a freshness check",
                game, pid);
            return null;
        }

        static ProcessModule FindClientModule(Process process)
        {
            try
            {
                return process.Modules.Cast<ProcessModule>().FirstOrDefault(m =>
                    m.ModuleName.Equals("core_ets2mp.dll", StringComparison.OrdinalIgnoreCase) ||
                    m.ModuleName.Equals("core_atsmp.dll", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                // Module list is unavailable while the process is still starting, and always
                // when the game runs elevated and this server does not.
                return null;
            }
        }

        static bool ReadLog(string game)
        {
            string root = GameProfileScanner.GetDocumentsRoot(game);
            string logPath = Path.Combine(root, "game.log.txt");
            string modRoot = Path.Combine(root, "mod") + Path.DirectorySeparatorChar;
            if (!File.Exists(logPath))
                return false;

            bool changed = false;
            byte[] buffer;
            using (var stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length < _logOffset)
                {
                    if (_processStart == null)
                    {
                        lock (Lock)
                        {
                            changed = _active && (!_moduleSeen || _mounted.Count > 0);
                            _active = _moduleSeen;
                            _gameVersion = null;
                            _mounted.Clear();
                        }
                    }
                    ResetLog();
                }
                if (!_logCurrent)
                {
                    if (!IsCurrentSessionLog(stream))
                        return changed;
                    _logCurrent = true;
                }
                if (stream.Length == _logOffset)
                    return changed;
                stream.Seek(_logOffset, SeekOrigin.Begin);
                buffer = new byte[_remainder.Length + stream.Length - _logOffset];
                Array.Copy(_remainder, buffer, _remainder.Length);
                int read = stream.Read(buffer, _remainder.Length, buffer.Length - _remainder.Length);
                _logOffset += read;
                Array.Resize(ref buffer, _remainder.Length + read);
            }

            // Decode complete lines only; a partial line stays as bytes for the next read.
            int end = Array.LastIndexOf(buffer, (byte)'\n') + 1;
            _remainder = buffer.Skip(end).ToArray();
            var lines = Encoding.UTF8.GetString(buffer, 0, end).Split('\n');
            bool detected = false, mountsChanged = false;
            lock (Lock)
            {
                foreach (var raw in lines)
                {
                    string line = raw.TrimEnd('\r');
                    if (_gameVersion == null)
                    {
                        var pack = PackVersionRegex.Match(line);
                        if (pack.Success)
                        {
                            _gameVersion = pack.Groups[1].Value;
                            continue;
                        }
                    }
                    var mount = MountRegex.Match(line);
                    if (mount.Success)
                    {
                        string path = mount.Groups[1].Value.Replace('/', '\\').TrimEnd('\\');
                        if (path.StartsWith(modRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            string file = Path.GetFileName(path);
                            if (!_mounted.Contains(file))
                            {
                                _mounted.Add(file);
                                mountsChanged = true;
                            }
                        }
                        else if (!_active && path.EndsWith(".mp", StringComparison.OrdinalIgnoreCase))
                        {
                            _active = true;
                            detected = true;
                        }
                        continue;
                    }
                    var unmount = UnmountRegex.Match(line);
                    if (unmount.Success && _mounted.Remove(unmount.Groups[1].Value))
                        mountsChanged = true;
                }
                changed |= detected || (mountsChanged && _active);
            }
            if (detected)
                Log.InfoFormat("[{0}] TruckersMP session detected from its data files in game.log", game);
            if (mountsChanged && _active)
                Log.InfoFormat("[{0}] TruckersMP mounted {1} mod files from the mod folder", game, _mounted.Count);
            return changed;
        }

        // A log older than the running game belongs to the previous session and is about to be rewritten.
        static bool IsCurrentSessionLog(Stream stream)
        {
            if (_processStart == null)
                return true;
            var head = new byte[256];
            int read = stream.Read(head, 0, head.Length);
            var match = LogCreatedRegex.Match(Encoding.UTF8.GetString(head, 0, read));
            if (!match.Success)
                return read == head.Length;
            DateTime created;
            if (!DateTime.TryParseExact(match.Groups[1].Value, "MMMM d yyyy '@' HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out created))
                return true;
            return created >= _processStart.Value.AddSeconds(-2);
        }
    }
}
