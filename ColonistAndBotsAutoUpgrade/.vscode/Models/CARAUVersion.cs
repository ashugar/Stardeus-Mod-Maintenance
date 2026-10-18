using System;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public static class CARAUVersion
    {
        private static readonly Version assemblyVersion =
            typeof(CARAUVersion).Assembly.GetName().Version;

        public static string Current =>
            $"{assemblyVersion.Major}." +
            $"{assemblyVersion.Minor}." +
            $"{assemblyVersion.Build}";

        public static string DisplayTitle =>
            $"Colonist And Bots Auto Upgrade v{Current}";

        public static string StardeusCurrent
        {
            get
            {
                var version = UnityEngine.Application.version;

                return string.IsNullOrWhiteSpace(version)
                    ? "Unknown"
                    : version;
            }
        }
    }
}
