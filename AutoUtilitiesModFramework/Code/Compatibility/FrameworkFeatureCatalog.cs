using System.Collections.Generic;

namespace AutoUtilitiesModFramework;

/// <summary>Documents one versioned AUMF capability.</summary>
public sealed class FrameworkFeatureDefinition
{
    /// <summary>Stable identifier suitable for code and documentation.</summary>
    public string Id { get; }
    /// <summary>Short player-facing feature name.</summary>
    public string Name { get; }
    /// <summary>First AUMF version containing the feature.</summary>
    public string IntroducedVersion { get; }
    /// <summary>Explanation displayed in compatibility details.</summary>
    public string Description { get; }

    internal FrameworkFeatureDefinition(string id, string name,
        string introducedVersion, string description)
    {
        Id = id;
        Name = name;
        IntroducedVersion = introducedVersion;
        Description = description;
    }
}

/// <summary>
/// Canonical feature/version table. Add an entry whenever a public helper or
/// optional consumer capability is introduced. The compatibility window and
/// public documentation API both read this one source of truth.
/// </summary>
internal static class FrameworkFeatureCatalog
{
    internal static readonly IReadOnlyList<FrameworkFeatureDefinition> All =
        new[]
        {
            new FrameworkFeatureDefinition(
                "CompatibilityDiagnostics",
                "Compatibility diagnostics",
                "1.1.0",
                "Consolidated mismatch alerts, dependent-mod release "
                + "versions, and direct Workshop access."),
            new FrameworkFeatureDefinition(
                "VersionGatedFeatures",
                "Version-gated optional features",
                "1.1.0",
                "Allows newer UI sections and helpers to remain disabled "
                + "until a consumer explicitly declares and tests the AUMF "
                + "version that introduced them."),
            new FrameworkFeatureDefinition(
                "CentralizedShortcuts",
                "Centralized guarded shortcuts",
                "1.2.0",
                "Provides persistent keyboard assignment, native and mod "
                + "conflict detection, capture safeguards, text-entry "
                + "suppression, and lifecycle-safe dispatch for consumers."),
            new FrameworkFeatureDefinition(
                "CollapsibleSections",
                "Collapsible UI sections",
                "1.3.0",
                "Provides pooled-widget-safe expandable and collapsible "
                + "headers for normal panel content and managed scrolling."),
            new FrameworkFeatureDefinition(
                "ThemeAwareUi",
                "Theme-aware custom UI",
                "1.4.0",
                "Provides opt-in Stardeus theme colors for custom surfaces, "
                + "text, buttons, sliders, scrollbars and reusable state cards, "
                + "with live theme reload and distinct unavailable states.")
        };
}
