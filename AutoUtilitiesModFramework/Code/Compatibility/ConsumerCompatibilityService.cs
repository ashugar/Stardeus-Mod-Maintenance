using System;
using System.Collections.Generic;
using System.Linq;
using KL.Utils;

namespace AutoUtilitiesModFramework;

internal enum CompatibilitySeverity
{
    Warning,
    Blocked
}

internal sealed class ConsumerCompatibilityIssue
{
    internal string ModId { get; set; }
    internal string DisplayName { get; set; }
    internal string ConsumerModVersion { get; set; }
    internal string ConsumerFrameworkVersion { get; set; }
    internal string InstalledFrameworkVersion { get; set; }
    internal CompatibilitySeverity Severity { get; set; }

    internal string Summary => Severity == CompatibilitySeverity.Blocked
        ? "Framework-dependent features disabled"
        : "Running with stable features only";
}

/// <summary>
/// Owns the durable consumer handshake and the mismatch list displayed by the
/// framework. Keep this service independent of optional UI features so old
/// consumers can always reach it.
/// </summary>
internal static class ConsumerCompatibilityService
{
    private static readonly Dictionary<string, ConsumerCompatibilityIssue>
        Issues = new Dictionary<string, ConsumerCompatibilityIssue>(
            StringComparer.Ordinal);
    private static readonly Dictionary<string, Version> Consumers =
        new Dictionary<string, Version>(StringComparer.Ordinal);

    internal static int IssueCount => Issues.Count;
    internal static bool HasIssues => Issues.Count != 0;
    /// <summary>
    /// Changes only when consumer compatibility data is registered or updated.
    /// UI owners use this to avoid rebuilding immutable rows every frame.
    /// </summary>
    internal static int Revision { get; private set; }

    internal static IReadOnlyList<ConsumerCompatibilityIssue> GetIssues()
    {
        return Issues.Values
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool Register(string modId, string displayName,
        string consumerModVersion, string consumerFrameworkVersion)
    {
        ValidateIdentity(modId, displayName);
        Version consumer = ParseVersion(consumerFrameworkVersion,
            nameof(consumerFrameworkVersion));
        Version installed = ParseVersion(AutoUtilitiesApi.FrameworkVersion,
            nameof(AutoUtilitiesApi.FrameworkVersion));

        Consumers[modId] = consumer;
        Issues.Remove(modId);
        Revision++;

        int comparison = consumer.CompareTo(installed);
        if (comparison == 0)
        {
            TopBarWidgetSys.NotifyCompatibilityChanged();
            return true;
        }

        bool canUseStableApi = comparison < 0
            && consumer.Major == installed.Major;
        var issue = new ConsumerCompatibilityIssue
        {
            ModId = modId,
            DisplayName = displayName,
            ConsumerModVersion = string.IsNullOrWhiteSpace(consumerModVersion)
                ? "Unknown"
                : consumerModVersion,
            ConsumerFrameworkVersion = consumerFrameworkVersion,
            InstalledFrameworkVersion = AutoUtilitiesApi.FrameworkVersion,
            Severity = canUseStableApi
                ? CompatibilitySeverity.Warning
                : CompatibilitySeverity.Blocked
        };
        Issues[modId] = issue;

        if (canUseStableApi)
        {
            D.Err("{0} was tested with Auto Utilities Mod Framework {1}, "
                + "but {2} is installed. Stable features will continue; "
                + "newer features remain gated.", displayName,
                consumerFrameworkVersion, AutoUtilitiesApi.FrameworkVersion);
        }
        else
        {
            D.Err("{0} requires Auto Utilities Mod Framework {1}, but {2} "
                + "is installed. Framework-dependent features are disabled.",
                displayName, consumerFrameworkVersion,
                AutoUtilitiesApi.FrameworkVersion);
        }

        TopBarWidgetSys.NotifyCompatibilityChanged();
        return canUseStableApi;
    }

    internal static bool IsFeatureAvailable(string modId,
        string introducedFrameworkVersion)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("Consumer mod ID is required.",
                nameof(modId));
        if (!Consumers.TryGetValue(modId, out Version consumer))
            return false;

        Version introduced = ParseVersion(introducedFrameworkVersion,
            nameof(introducedFrameworkVersion));
        Version installed = ParseVersion(AutoUtilitiesApi.FrameworkVersion,
            nameof(AutoUtilitiesApi.FrameworkVersion));
        return consumer.Major == installed.Major
            && consumer.CompareTo(installed) <= 0
            && consumer.CompareTo(introduced) >= 0
            && installed.CompareTo(introduced) >= 0;
    }

    internal static IReadOnlyList<FrameworkFeatureDefinition>
        GetUnavailableFeatures()
    {
        Version installed = ParseVersion(AutoUtilitiesApi.FrameworkVersion,
            nameof(AutoUtilitiesApi.FrameworkVersion));
        return FrameworkFeatureCatalog.All
            .Where(feature =>
            {
                Version introduced = ParseVersion(feature.IntroducedVersion,
                    nameof(feature.IntroducedVersion));
                return introduced.CompareTo(installed) <= 0
                    && Issues.Values.Any(issue =>
                        ParseVersion(issue.ConsumerFrameworkVersion,
                                nameof(issue.ConsumerFrameworkVersion))
                            .CompareTo(introduced) < 0);
            })
            .ToArray();
    }

    internal static int CountAffectedConsumers(
        FrameworkFeatureDefinition feature)
    {
        Version introduced = ParseVersion(feature.IntroducedVersion,
            nameof(feature.IntroducedVersion));
        return Issues.Values.Count(issue =>
            ParseVersion(issue.ConsumerFrameworkVersion,
                    nameof(issue.ConsumerFrameworkVersion))
                .CompareTo(introduced) < 0);
    }

    private static void ValidateIdentity(string modId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("Consumer mod ID is required.",
                nameof(modId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Consumer display name is required.",
                nameof(displayName));
    }

    private static Version ParseVersion(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Version.TryParse(value, out Version result))
        {
            throw new ArgumentException(
                "A valid dotted framework version is required.",
                parameterName);
        }
        return result;
    }
}
