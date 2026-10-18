using System;

namespace AutoUtilitiesModFramework;

/// <summary>Public entry point for dependent mods.</summary>
/// <remarks>
/// Check <see cref="FrameworkVersion"/> before registering any UI. A consumer
/// should keep its returned widget handles and dispose them during unload.
/// </remarks>
public static class AutoUtilitiesApi
{
    /*
     * VERSIONING SAFETY CONTRACT
     *
     * FrameworkVersion is the canonical compiled API version and must exactly
     * match ModInfo.json ModVersion and the project VersionPrefix.
     *
     * Every dependent mod must hardcode the framework version it was built and
     * tested against and register through TryRegisterConsumer before using any
     * framework feature. Same-major older consumers may continue using the
     * stable API, but features introduced later are explicitly gated.
     *
     * The framework must never reference or validate dependent-mod versions.
     * Compatibility flows in one direction: a dependent mod validates this
     * version before it registers UI or uses any other framework service.
     *
     * RELEASE VERSION POLICY
     * X.X.X means MajorFeature.MinorFeature.NotUsed. Bug fixes do not change
     * FrameworkVersion. Increment the minor feature component only when a new
     * cataloged capability is introduced. Increment the major feature
     * component after ten new features in the current major line, or sooner
     * for a major/breaking capability. The third component remains zero.
     */
    public const string FrameworkVersion = "1.4.0";

    /// <summary>
    /// Registers a consumer's tested AUMF version and reports mismatches.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> for an exact match or an older same-major
    /// consumer that may safely continue with stable features. Returns
    /// <see langword="false"/> when the consumer requires a newer or different
    /// major framework; it must skip all framework-dependent initialization.
    /// </returns>
    /// <remarks>
    /// This is a permanent bootstrap API. Its signature must not be removed or
    /// changed in future AUMF versions. Calling it again for the same mod ID is
    /// safe and replaces that mod's previous compatibility report.
    /// </remarks>
    /// <example><code>
    /// private const string ConsumerId = "MyMod";
    /// private const string TestedAumfVersion = "1.0.0";
    /// if (!AutoUtilitiesApi.TryRegisterConsumer(
    ///         ConsumerId, "My Mod", TestedAumfVersion))
    ///     return;
    /// </code></example>
    public static bool TryRegisterConsumer(string modId, string displayName,
        string consumerFrameworkVersion)
    {
        return TryRegisterConsumer(modId, displayName, "Unknown",
            consumerFrameworkVersion);
    }

    /// <summary>
    /// Registers both the consumer mod's release version and its tested AUMF
    /// compatibility level. Prefer this overload for player-facing diagnostics.
    /// </summary>
    /// <param name="consumerModVersion">
    /// The dependent mod's own release version, normally obtained from its
    /// assembly version. It is displayed but never used to decide compatibility.
    /// </param>
    public static bool TryRegisterConsumer(string modId, string displayName,
        string consumerModVersion, string consumerFrameworkVersion)
    {
        return ConsumerCompatibilityService.Register(modId, displayName,
            consumerModVersion, consumerFrameworkVersion);
    }

    /// <summary>
    /// Checks whether a registered consumer and the installed AUMF both meet a
    /// feature's introduction version.
    /// </summary>
    /// <remarks>
    /// Use this around every optional section that depends on a newer AUMF
    /// feature. An older consumer therefore keeps its stable functionality and
    /// simply omits the section it has never declared or tested.
    /// </remarks>
    /// <example><code>
    /// if (AutoUtilitiesApi.IsFeatureAvailable(ConsumerId, "1.2.0"))
    ///     BuildFeatureIntroducedInAumf12();
    /// </code></example>
    public static bool IsFeatureAvailable(string modId,
        string introducedFrameworkVersion)
    {
        return ConsumerCompatibilityService.IsFeatureAvailable(modId,
            introducedFrameworkVersion);
    }

    /// <summary>
    /// Returns AUMF's canonical feature/version documentation table.
    /// </summary>
    /// <remarks>
    /// AUMF maintainers add one entry whenever a new public helper or optional
    /// consumer capability is introduced. Consumers may use the table for
    /// diagnostics; feature activation must still use
    /// <see cref="IsFeatureAvailable"/>.
    /// </remarks>
    public static System.Collections.Generic.IReadOnlyList<
        FrameworkFeatureDefinition> GetFeatureCatalog()
    {
        return FrameworkFeatureCatalog.All;
    }

    /// <summary>Checks the consumer's required version for an exact match.</summary>
    /// <param name="requiredVersion">
    /// Hardcoded version against which the consumer was built and tested.
    /// </param>
    /// <returns><see langword="true"/> only for an ordinal exact match.</returns>
    /// <example><code>
    /// private const string RequiredUtilitiesVersion = "1.0.0";
    /// if (!AutoUtilitiesApi.IsExactVersion(RequiredUtilitiesVersion))
    ///     return; // Do not use framework services with an untested version.
    /// </code></example>
    public static bool IsExactVersion(string requiredVersion)
    {
        return string.Equals(requiredVersion, FrameworkVersion,
            StringComparison.Ordinal);
    }

    /// <summary>Registers a cooperative top-bar icon widget.</summary>
    /// <param name="registration">Widget identity, appearance, and callbacks.</param>
    /// <returns>
    /// A consumer-owned handle. Store it, call <c>Refresh()</c> after displayed
    /// state changes, and call <c>Dispose()</c> when the consumer unloads.
    /// </returns>
    /// <example><code>
    /// widget = AutoUtilitiesApi.RegisterTopBarWidget(
    ///     new TopBarWidgetRegistration {
    ///         Id = "MyMod.Status",
    ///         Icon = () =&gt; GraphicsId.IconsColorInfo,
    ///         Tint = () =&gt; enabled ? Color.white : Color.gray,
    ///         Tooltip = () =&gt; enabled ? "My Mod: ON" : "My Mod: OFF",
    ///         OnClick = () =&gt; panel.Toggle()
    ///     });
    /// </code></example>
    public static ITopBarWidgetHandle RegisterTopBarWidget(
        TopBarWidgetRegistration registration)
    {
        return TopBarWidgetService.Register(registration);
    }

    /// <summary>Registers a guarded, persistent keyboard shortcut.</summary>
    /// <remarks>
    /// Available beginning with AUMF 1.2.0. AUMF owns key capture, persistence,
    /// conflict checks, text-input suppression, dispatch, and cleanup. The
    /// consumer owns only its registration data, callback, UI wording, and the
    /// returned handle. Dispose the handle during consumer unload.
    /// </remarks>
    /// <example><code>
    /// shortcut = AutoUtilitiesApi.RegisterShortcut(new ShortcutRegistration {
    ///     ConsumerId = ConsumerId,
    ///     Id = "TogglePanel",
    ///     DisplayName = "open or close My Mod",
    ///     PreferenceKey = "mymod.ui.shortcut",
    ///     GameState = S,
    ///     OnPressed = TogglePanel
    /// });
    /// // Settings buttons call shortcut.BeginCapture() and shortcut.Clear().
    /// </code></example>
    public static IShortcutHandle RegisterShortcut(ShortcutRegistration registration)
    {
        if (registration == null)
            throw new ArgumentNullException(nameof(registration));
        if (!IsFeatureAvailable(registration.ConsumerId, "1.2.0"))
            throw new InvalidOperationException("The consumer must register and declare AUMF 1.2.0 before using centralized shortcuts.");
        return ShortcutService.Register(registration);
    }
}
