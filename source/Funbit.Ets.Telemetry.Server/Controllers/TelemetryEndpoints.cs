using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using Funbit.Ets.Telemetry.Server.Data;
using Funbit.Ets.Telemetry.Server.Helpers;
using Newtonsoft.Json;

namespace Funbit.Ets.Telemetry.Server.Controllers
{
    /// <summary>
    /// Endpoint helpers consumed by <see cref="MinimalHttpServer"/>.
    /// Kept as static methods after the OWIN/SignalR/WebApi stack was removed.
    /// </summary>
    public static class TelemetryEndpoints
    {
        public const string TelemetryAppUriPath = "/";
        public const string TelemetryApiUriPath = "/api/ets2/telemetry";

        const string TestTelemetryJsonFileName = "Ets2TestTelemetry.json";

        static readonly bool UseTestTelemetryData = Convert.ToBoolean(
            ConfigurationManager.AppSettings["UseEts2TestTelemetryData"]);

        public static string GetEts2TelemetryJson()
        {
            if (UseTestTelemetryData)
            {
                using (var file = File.Open(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TestTelemetryJsonFileName),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite))
                using (var reader = new StreamReader(file, Encoding.UTF8))
                    return reader.ReadToEnd();
            }

            var v1 = ScsTelemetryDataReader.Instance.Read();
            return JsonConvert.SerializeObject(v1, JsonHelper.RestSettings);
        }

        const string StatusPageHtmlTemplate = @"<!DOCTYPE html>
<html lang=""{LANG}"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>{TITLE}</title>
<style>
* { box-sizing: border-box; }
body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 16px; color: #fff;
  background: radial-gradient(80% 60% at 50% 0%, rgba(45, 221, 255, .14), rgba(45, 221, 255, 0) 70%), radial-gradient(90% 70% at 50% 100%, #12171e, #1a212b);
  font: 400 17px/1.55 'Segoe UI', system-ui, -apple-system, sans-serif; -webkit-font-smoothing: antialiased; }
main { width: 100%; max-width: 460px; display: flex; flex-direction: column; align-items: center; gap: 22px; text-align: center; }
.brand { display: flex; align-items: center; gap: 11px; font-size: 18px; font-weight: 600; line-height: 1; letter-spacing: -0.02em; }
.brand img { width: 32px; height: 32px; border-radius: 8px; display: block; }
.brand span { color: rgba(255, 255, 255, .55); font-weight: 500; }
.card { width: 100%; padding: 32px 28px 28px; border-radius: 24px; display: flex; flex-direction: column; align-items: center; gap: 12px;
  background: linear-gradient(180deg, rgba(255, 255, 255, .085), rgba(255, 255, 255, .03)), rgba(26, 34, 45, .46);
  border: 1px solid; border-color: rgba(255, 255, 255, .2) rgba(255, 255, 255, .09) rgba(255, 255, 255, .05) rgba(255, 255, 255, .11);
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, .1), 0 40px 80px -42px rgba(0, 0, 0, .8); }
.ok { width: 68px; height: 68px; margin-bottom: 6px; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  background: radial-gradient(120% 110% at 50% -30%, rgba(74, 222, 128, .34), rgba(74, 222, 128, 0) 62%), rgba(74, 222, 128, .1);
  border: 1px solid rgba(74, 222, 128, .6); box-shadow: inset 0 1px 0 rgba(255, 255, 255, .35), 0 0 26px -6px rgba(74, 222, 128, .45); }
.ok svg { width: 32px; height: 32px; }
h1 { margin: 0; font-size: 30px; line-height: 1.1; font-weight: 600; letter-spacing: -0.035em; text-wrap: balance; }
.body { margin: 0; color: rgba(255, 255, 255, .74); }
.tip { margin: 10px 0 0; padding-top: 18px; border-top: 1px solid rgba(255, 255, 255, .11); color: rgba(255, 255, 255, .55); font-size: 15px; line-height: 1.5; }
.ver { color: rgba(255, 255, 255, .34); font: 500 13px/1 ui-monospace, Consolas, monospace; }
</style>
</head>
<body>
<main>
<div class=""brand""><img src=""data:image/png;base64,{ICON}"" alt="""">TruckSim GPS <span>PC Telemetry</span></div>
<div class=""card"">
<div class=""ok""><svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#4ade80"" stroke-width=""2.5"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M5 12.5l4.5 4.5L19 7""/></svg></div>
<h1>{HEADING}</h1>
<p class=""body"">{BODY}</p>
<p class=""tip"">{TIP}</p>
</div>
<div class=""ver"">v{VERSION}</div>
</main>
</body>
</html>";

        static readonly Lazy<string> IconBase64 = new Lazy<string>(() =>
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app_icon.png"))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return Convert.ToBase64String(memory.ToArray());
            }
        });

        public static string GetStatusPageHtml()
        {
            return StatusPageHtmlTemplate
                .Replace("{LANG}", L.Language)
                .Replace("{TITLE}", WebUtility.HtmlEncode(L.Get("tray.title")))
                .Replace("{ICON}", IconBase64.Value)
                .Replace("{VERSION}", AssemblyHelper.Version)
                .Replace("{HEADING}", WebUtility.HtmlEncode(L.Get("test_page.heading")))
                .Replace("{BODY}", WebUtility.HtmlEncode(L.Get("test_page.body")))
                .Replace("{TIP}", WebUtility.HtmlEncode(L.Get("test_page.tip")));
        }
    }
}
