using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Services;
using SCSSdkClient;
using SIIDecryptSharp;

namespace Funbit.Ets.Telemetry.Server.Data
{
    public class GpsSyncSnapshot
    {
        public GpsRouteV1 Route { get; set; }
        public SelectedJobV1 SelectedJob { get; set; }
        public bool OnJob { get; set; }
    }

    public static class GpsTargetWatcher
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(
            MethodBase.GetCurrentMethod().DeclaringType);

        public const int MinPluginRevision = 14;
        const int SampleIntervalMs = 100;
        const int IdleIntervalMs = 1000;
        const int MaxParseAttempts = 3;
        static readonly TimeSpan SaveScanInterval = TimeSpan.FromMilliseconds(500);
        static readonly TimeSpan SaveQuietFor = TimeSpan.FromMilliseconds(300);

        class ParsedSave
        {
            public string FullName;
            public DateTime WrittenAt;
            public SaveGpsState State;
            public Exception Error;
            public long Milliseconds;
        }

        static readonly GpsSyncPolicy Policy = new GpsSyncPolicy();
        static ScsTelemetryDataReader _telemetry;
        static LinkedDeviceStore _devices;
        static Thread _thread;
        static volatile GpsSyncSnapshot _published;
        static ParsedSave _save;
        static Task<ParsedSave> _reading;
        static string _saveDir;
        static DateTime _lastScan;
        static string _failedPath;
        static DateTime _failedWrittenAt;
        static int _failedAttempts;
        static int _loggedEpoch;
        static uint _request;
        static uint _countAtRequest;
        static string _lastError;

        public static GpsSyncSnapshot Published => _published;

        public static void Start(LinkedDeviceStore devices)
        {
            if (_thread != null)
                return;
            _devices = devices;
            _telemetry = ScsTelemetryDataReader.Instance;
            _thread = new Thread(Loop) { IsBackground = true, Name = "GpsTargetWatcher", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        static void Loop()
        {
            while (true)
            {
                bool running = Ets2ProcessHelper.IsEts2Running;
                try
                {
                    Tick(running);
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _published = null;
                    string error = ex.GetType().Name + ": " + ex.Message;
                    if (error != _lastError)
                        Log.Warn("GPS sync failed; retrying silently", ex);
                    _lastError = error;
                }
                Thread.Sleep(running ? SampleIntervalMs : IdleIntervalMs);
            }
        }

        static void Tick(bool running)
        {
            var scs = running ? _telemetry.Sample(TimeSpan.FromMilliseconds(SampleIntervalMs)) : null;
            bool plugin = scs != null && scs.SdkActive && scs.DllVersion >= MinPluginRevision;
            string game = scs?.Game == SCSGame.Ats ? "ats" : "ets2";
            var profile = plugin ? ProfileWatcher.GetState(game) : null;
            bool live = plugin && profile.ActiveSaveDir != null;
            bool onJob = live && ScsTelemetryDataReader.OnJob(scs);
            var now = DateTime.UtcNow;
            var truck = live ? scs.TruckValues.CurrentValues.PositionValue.Position : null;
            var app = _devices.AppState();

            string saveDir = live ? profile.ActiveSaveDir : null;
            if (saveDir != _saveDir)
            {
                _saveDir = saveDir;
                _save = null;
                _reading = null;
            }
            if (_reading?.IsCompleted == true)
            {
                var done = _reading;
                _reading = null;
                TakeSave(done.Result, game);
            }

            bool press = Policy.Update(new GpsSyncSample
            {
                Now = now,
                Live = live,
                ProfileKey = live ? game + "/" + profile.ActiveProfileType + "/" + profile.ActiveProfileId : null,
                Paused = live && scs.Paused,
                RouteDistance = live ? scs.NavigationValues.NavigationDistance : 0f,
                OnJob = onJob,
                AppConnected = app.Connected,
                AppWantsGameRoute = app.WantsGameRoute,
                TruckX = (float)(truck?.X ?? 0),
                TruckZ = (float)(truck?.Z ?? 0),
                NextWaypoint = _published?.Route.Waypoints[0]
            });
            if (Policy.Epoch != _loggedEpoch)
            {
                _loggedEpoch = Policy.Epoch;
                Log.InfoFormat("[{0}] GPS sync: {1}", game, Policy.EpochReason);
            }
            if (!live)
                _request = 0;
            else if (press)
                RequestPress(game);
            else if (_request != 0)
                CheckPress(game);

            if (_reading == null && saveDir != null && Policy.RouteUp && (!onJob || app.WantsGameRoute) && now - _lastScan >= SaveScanInterval)
            {
                _lastScan = now;
                var newest = NewestSave(saveDir, now);
                if (newest != null)
                {
                    var writtenAt = newest.LastWriteTimeUtc;
                    _reading = Task.Run(() => ReadSave(newest.FullName, writtenAt));
                }
            }

            bool show = live && _save != null && Policy.Shows(_save.State, _save.WrittenAt, onJob, app.WantsGameRoute);
            _published = show
                ? new GpsSyncSnapshot { Route = new GpsRouteV1 { SavedAt = _save.WrittenAt, Waypoints = _save.State.Waypoints }, SelectedJob = _save.State.SelectedJob, OnJob = onJob }
                : null;
        }

        static void RequestPress(string game)
        {
            var zone = _telemetry.RequestQuickSave();
            _request = zone.PressRequest;
            _countAtRequest = zone.PressCount;
            Log.InfoFormat("[{0}] GPS sync: quick save requested{1}{2}", game,
                zone.InputActive == 0 ? " (the game is not taking input from TruckSim GPS right now)" : "",
                TruckersMpWatcher.GetState(game).Active ? " (TruckersMP is running and may block it)" : "");
        }

        static void CheckPress(string game)
        {
            if (_telemetry.ReadGpsSync().PressAck != _request)
                return;
            _request = 0;
            if (_telemetry.ReadGpsSync().PressCount != _countAtRequest)
                return;
            Policy.PressLost();
            Log.InfoFormat("[{0}] GPS sync: the game did not take the quick save", game);
        }

        static FileInfo NewestSave(string saveDir, DateTime now)
        {
            FileInfo newest;
            try
            {
                if (!Directory.Exists(saveDir))
                    return null;
                newest = Directory.EnumerateDirectories(saveDir)
                    .Select(slot => new FileInfo(Path.Combine(slot, "game.sii")))
                    .Where(file => file.Exists)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A slot folder rewritten while listed.
                return null;
            }
            if (newest == null || now - newest.LastWriteTimeUtc < SaveQuietFor)
                return null;
            var writtenAt = newest.LastWriteTimeUtc;
            if (writtenAt <= Policy.ValidAfter || _save != null && _save.FullName == newest.FullName && _save.WrittenAt == writtenAt)
                return null;
            if (newest.FullName == _failedPath && writtenAt == _failedWrittenAt && _failedAttempts >= MaxParseAttempts)
                return null;
            return newest;
        }

        static ParsedSave ReadSave(string fullName, DateTime writtenAt)
        {
            var read = new ParsedSave { FullName = fullName, WrittenAt = writtenAt };
            var timer = Stopwatch.StartNew();
            try
            {
                using (var reader = new StreamReader(new MemoryStream(Decryptor.Decrypt(fullName)), Encoding.UTF8))
                    read.State = SaveGpsParser.Parse(reader);
            }
            catch (Exception ex)
            {
                read.Error = ex;
            }
            read.Milliseconds = timer.ElapsedMilliseconds;
            return read;
        }

        static void TakeSave(ParsedSave read, string game)
        {
            if (read.Error != null)
            {
                _failedAttempts = read.FullName == _failedPath && read.WrittenAt == _failedWrittenAt ? _failedAttempts + 1 : 1;
                _failedPath = read.FullName;
                _failedWrittenAt = read.WrittenAt;
                if (_failedAttempts >= MaxParseAttempts)
                    Log.Warn($"[{game}] GPS sync: could not read '{read.FullName}'", read.Error);
                return;
            }
            _save = read;
            Policy.SaveWritten(read.WrittenAt, read.State.Waypoints.Count > 0);
            Log.InfoFormat("[{0}] GPS sync: read {1} in {2} ms: {3} waypoints ahead, selected job {4}", game,
                Path.GetFileName(Path.GetDirectoryName(read.FullName)), read.Milliseconds, read.State.Waypoints.Count,
                read.State.SelectedJob?.Market ?? "none");
        }
    }
}
