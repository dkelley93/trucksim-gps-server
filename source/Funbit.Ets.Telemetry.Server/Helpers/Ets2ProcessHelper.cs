using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Funbit.Ets.Telemetry.Server.Helpers
{
    public static class Ets2ProcessHelper
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        static long _lastCheckTime;
        static int _scanning;
        static bool _cachedRunningFlag;
        static int _lastProcessId;

        /// <summary>
        /// Returns last running game name: "ETS2", "ATS" or null if undefined.
        /// </summary>
        public static string LastRunningGameName { get; set; }

        /// <summary>
        /// Returns the installation path of the last detected running game, or null if not available.
        /// </summary>
        public static string LastRunningGamePath { get; set; }

        /// <summary>
        /// Returns the product version of the last detected running game's exe (e.g. "1.59.1"),
        /// or null if not available. Read from the file's version metadata, not from the SCS SDK
        /// (which only reports its own API schema version).
        /// </summary>
        public static string LastRunningGameProductVersion { get; set; }

        public static bool IsEts2Running
        {
            get
            {
                long last = Interlocked.Read(ref _lastCheckTime);
                if (last == 0)
                    Scan();
                else if (Stopwatch.GetTimestamp() - last > Stopwatch.Frequency && Interlocked.CompareExchange(ref _scanning, 1, 0) == 0)
                    ThreadPool.QueueUserWorkItem(_ => Scan());
                return _cachedRunningFlag;
            }
        }

        static void Scan()
        {
            try
            {
                var processes = Process.GetProcesses();
                bool running = false;
                foreach (var process in processes)
                {
                    // ProcessName only: MainWindowTitle is mutated by third-party overlays (VTLog) and localized game builds.
                    if (running || process.ProcessName != "eurotrucks2" && process.ProcessName != "amtrucks")
                        continue;
                    running = true;
                    LastRunningGameName = process.ProcessName == "eurotrucks2" ? "ETS2" : "ATS";
                    if (process.Id != _lastProcessId)
                    {
                        ReadGameLocation(process);
                        _lastProcessId = process.Id;
                    }
                }
                foreach (var process in processes)
                    process.Dispose();
                if (!running)
                {
                    _lastProcessId = 0;
                    LastRunningGameProductVersion = null;
                }
                _cachedRunningFlag = running;
            }
            catch (Exception ex)
            {
                Log.Warn("Game process scan failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _lastCheckTime, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _scanning, 0);
            }
        }

        // The exe lives in bin\win_x64 or bin\win_x86; the game root is the first parent holding base.scs and bin.
        static void ReadGameLocation(Process process)
        {
            try
            {
                string exePath = process.MainModule.FileName;
                string gameRoot = null;
                for (var dir = new DirectoryInfo(Path.GetDirectoryName(exePath)); dir != null && dir.Parent != null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "base.scs")) && Directory.Exists(Path.Combine(dir.FullName, "bin")))
                    {
                        gameRoot = dir.FullName;
                        break;
                    }
                }
                LastRunningGamePath = gameRoot;
                LastRunningGameProductVersion = FileVersionInfo.GetVersionInfo(exePath).ProductVersion;
            }
            catch (Exception)
            {
                LastRunningGamePath = null;
                LastRunningGameProductVersion = null;
            }
        }
    }
}