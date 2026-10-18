using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Loads session-start diagnostic switches from the mod root. No diagnostic
    /// Harmony entry point is installed unless the global switch is enabled.
    /// </summary>
    internal static class OptimizationDiagnosticsSettings {
        private const string FileName = "Diagnostics.json";
        private static readonly Regex DiagnosticsEnabledPattern = new Regex(
            "\\\"DiagnosticsEnabled\\\"\\s*:\\s*(true|false)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex WholeFrameProfilerPattern = new Regex(
            "\\\"WholeFrameProfiler\\\"\\s*:\\s*(true|false)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CoreProfilerSuitePattern = new Regex(
            "\\\"CoreProfilerSuite\\\"\\s*:\\s*(true|false)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ExternalNanobotProfilerPattern = new Regex(
            "\\\"ExternalNanobotProfiler\\\"\\s*:\\s*(true|false)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool DiagnosticsEnabled { get; private set; }

        internal static bool WholeFrameProfilerEnabled { get; private set; }

        internal static bool CoreProfilerSuiteEnabled { get; private set; }

        internal static bool ExternalNanobotProfilerEnabled {
            get;
            private set;
        }

        internal static string SettingsPath { get; private set; }

        internal static void Load() {
            DiagnosticsEnabled = false;
            WholeFrameProfilerEnabled = false;
            CoreProfilerSuiteEnabled = false;
            ExternalNanobotProfilerEnabled = false;
            SettingsPath = null;
            try {
                string assemblyPath = typeof(OptimizationDiagnosticsSettings)
                    .Assembly.Location;
                string librariesPath = System.IO.Path.GetDirectoryName(
                    assemblyPath);
                string modPath = string.IsNullOrEmpty(librariesPath)
                    ? null
                    : Directory.GetParent(librariesPath)?.FullName;
                if (string.IsNullOrEmpty(modPath)) {
                    D.Err("Optimization Mod could not resolve its mod folder; "
                        + "diagnostics remain disabled.");
                    return;
                }

                SettingsPath = System.IO.Path.Combine(modPath, FileName);
                if (!File.Exists(SettingsPath)) {
                    return;
                }

                string text = File.ReadAllText(SettingsPath);
                Match enabledMatch = DiagnosticsEnabledPattern.Match(
                    text ?? "");
                if (!enabledMatch.Success
                        || !bool.TryParse(enabledMatch.Groups[1].Value,
                            out bool enabled)) {
                    D.Err("Optimization Mod could not read "
                        + "DiagnosticsEnabled from Diagnostics.json; all "
                        + "diagnostics remain disabled.");
                    return;
                }

                DiagnosticsEnabled = enabled;
                Match coreSuiteMatch = CoreProfilerSuitePattern.Match(
                    text ?? "");
                bool coreSuiteRequested = !coreSuiteMatch.Success
                    || bool.TryParse(coreSuiteMatch.Groups[1].Value,
                        out bool coreRequested)
                        && coreRequested;
                CoreProfilerSuiteEnabled = enabled && coreSuiteRequested;
                Match wholeFrameMatch = WholeFrameProfilerPattern.Match(
                    text ?? "");
                bool wholeFrameRequested = wholeFrameMatch.Success
                    && bool.TryParse(wholeFrameMatch.Groups[1].Value,
                        out bool requested)
                    && requested;
                WholeFrameProfilerEnabled = enabled
                    && coreSuiteRequested
                    && wholeFrameRequested;
                Match externalNanobotMatch =
                    ExternalNanobotProfilerPattern.Match(text ?? "");
                bool externalNanobotRequested =
                    externalNanobotMatch.Success
                    && bool.TryParse(externalNanobotMatch.Groups[1].Value,
                        out bool externalRequested)
                    && externalRequested;
                ExternalNanobotProfilerEnabled =
                    enabled && externalNanobotRequested;
            } catch (Exception ex) {
                DiagnosticsEnabled = false;
                WholeFrameProfilerEnabled = false;
                CoreProfilerSuiteEnabled = false;
                ExternalNanobotProfilerEnabled = false;
                D.Err("Optimization Mod failed to load Diagnostics.json; the "
                    + "diagnostic probes remain disabled: {0}", ex);
            }
        }
    }
}
