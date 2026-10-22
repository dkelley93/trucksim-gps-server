using System.Configuration;

namespace Funbit.Ets.Telemetry.Server.Helpers
{
    public static class CloudConfig
    {
        public const string WebsiteUrl = "https://trucksimgps.com/";
        public const string ConnectHelpUrl = "https://trucksimgps.com/connect-help";
        public const int ProtocolVersion = 7;
        const string ProductionApiBaseUrl = "https://api.trucksimgps.com";

#if DEBUG
        public const string StagingApiBaseUrl = "https://api-staging.trucksimgps.com";

        public static string ApiBaseUrl => Settings.Instance.ApiEnvironment == "staging"
            ? StagingApiBaseUrl
            : (ConfigurationManager.AppSettings["ApiBaseUrl"] ?? ProductionApiBaseUrl).TrimEnd('/');
#else
        public const string ApiBaseUrl = ProductionApiBaseUrl;
#endif

        public static readonly int Port = int.Parse(ConfigurationManager.AppSettings["Port"] ?? "31377");
    }
}
