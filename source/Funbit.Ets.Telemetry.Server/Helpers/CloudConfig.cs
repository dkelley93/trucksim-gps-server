using System.Configuration;

namespace Funbit.Ets.Telemetry.Server.Helpers
{
    public static class CloudConfig
    {
        public const string WebsiteUrl = "https://trucksimgps.com/";
        public const string ConnectHelpUrl = "https://trucksimgps.com/connect-help";
        public const int ProtocolVersion = 7;

        public static readonly string ApiBaseUrl =
            (ConfigurationManager.AppSettings["ApiBaseUrl"] ?? "https://api.trucksimgps.com").TrimEnd('/');

        public static readonly int Port = int.Parse(ConfigurationManager.AppSettings["Port"] ?? "31377");
    }
}
