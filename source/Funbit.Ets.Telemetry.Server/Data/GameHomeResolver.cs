using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace Funbit.Ets.Telemetry.Server.Data
{
    /// <summary>
    /// Resolves the folder a game passes as -homedir, which replaces Documents for its
    /// profiles, mods and logs. The running game's command line wins; without it, the
    /// Steam launch options of the active Steam user. Null means the game uses Documents.
    /// </summary>
    public static class GameHomeResolver
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(
            MethodBase.GetCurrentMethod().DeclaringType);

        static readonly ConcurrentDictionary<string, string> HomeDirs = new ConcurrentDictionary<string, string>();

        public static string GetHomeDir(string game)
        {
            string dir = HomeDirs.GetOrAdd(game, g => FromSteamLaunchOptions(g) ?? "");
            return dir.Length > 0 ? dir : null;
        }

        public static void ResolveFromRunningGame(string game)
        {
            string commandLine;
            try
            {
                commandLine = ReadCommandLine(game);
            }
            catch (Exception ex)
            {
                Log.Warn($"[{game}] Could not read the game command line", ex);
                return;
            }
            if (commandLine == null)
            {
                Log.InfoFormat("[{0}] Game command line not readable; home dir from Steam launch options: {1}",
                    game, GetHomeDir(game) ?? "none");
                return;
            }
            string dir = ParseHomeDir(commandLine);
            HomeDirs[game] = dir ?? "";
            Log.InfoFormat("[{0}] Home dir from the game command line: {1}", game, dir ?? "none");
        }

        static string ReadCommandLine(string game)
        {
            string exe = game == "ats" ? "amtrucks.exe" : "eurotrucks2.exe";
            using (var searcher = new ManagementObjectSearcher(
                       $"SELECT CommandLine FROM Win32_Process WHERE Name = '{exe}'"))
            using (var processes = searcher.Get())
            {
                foreach (ManagementBaseObject process in processes)
                {
                    using (process)
                        return process["CommandLine"] as string;
                }
            }
            return null;
        }

        static string FromSteamLaunchOptions(string game)
        {
            try
            {
                string config = FindActiveLocalConfig();
                if (config == null)
                    return null;
                string appId = game == "ats" ? "270880" : "227300";
                var root = ParseVdf(File.ReadAllText(config));
                string options = Lookup(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", appId,
                    "LaunchOptions");
                string dir = options == null ? null : ParseHomeDir(options);
                Log.InfoFormat("[{0}] Home dir from Steam launch options ({1}): {2}", game, config, dir ?? "none");
                return dir;
            }
            catch (Exception ex)
            {
                Log.Warn($"[{game}] Could not read the Steam launch options", ex);
                return null;
            }
        }

        static string FindActiveLocalConfig()
        {
            string steamRoot;
            int activeUser;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                steamRoot = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            }
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                activeUser = key?.GetValue("ActiveUser") as int? ?? 0;
            }
            if (string.IsNullOrEmpty(steamRoot))
                return null;
            string userData = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userData))
                return null;
            string active = Path.Combine(userData, activeUser.ToString(), "config", "localconfig.vdf");
            if (activeUser != 0 && File.Exists(active))
                return active;
            return Directory.GetDirectories(userData)
                .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        internal static string ParseHomeDir(string commandLine)
        {
            var args = Tokenize(commandLine);
            int index = args.FindLastIndex(a => a.Equals("-homedir", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Count)
                return null;
            string dir = args[index + 1].TrimEnd('\\', '/');
            if (dir.Length == 0 || dir.StartsWith("-") || !Path.IsPathRooted(dir))
                return null;
            if (dir.EndsWith(":"))
                dir += "\\";
            return dir;
        }

        static List<string> Tokenize(string commandLine)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in commandLine)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any)
                        tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
                else
                {
                    current.Append(c);
                    any = true;
                }
            }
            if (any)
                tokens.Add(current.ToString());
            return tokens;
        }

        static string Lookup(Dictionary<string, object> node, params string[] path)
        {
            object value = node;
            foreach (string key in path)
            {
                var dict = value as Dictionary<string, object>;
                if (dict == null || !dict.TryGetValue(key, out value))
                    return null;
            }
            return value as string;
        }

        internal static Dictionary<string, object> ParseVdf(string text)
        {
            int pos = 0;
            return ParseVdfObject(text, ref pos);
        }

        static Dictionary<string, object> ParseVdfObject(string text, ref int pos)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string key = ReadVdfToken(text, ref pos);
                if (key == null || key == "}")
                    return result;
                string next = ReadVdfToken(text, ref pos);
                if (next == null)
                    return result;
                result[key] = next == "{" ? ParseVdfObject(text, ref pos) : (object)next;
            }
        }

        static string ReadVdfToken(string text, ref int pos)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;
            if (pos >= text.Length)
                return null;
            char c = text[pos];
            if (c == '{' || c == '}')
            {
                pos++;
                return c.ToString();
            }
            if (c != '"')
            {
                int start = pos;
                while (pos < text.Length && !char.IsWhiteSpace(text[pos]))
                    pos++;
                return text.Substring(start, pos - start);
            }
            var token = new StringBuilder();
            pos++;
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length && (text[pos + 1] == '\\' || text[pos + 1] == '"'))
                    pos++;
                token.Append(text[pos++]);
            }
            pos++;
            return token.ToString();
        }
    }
}
