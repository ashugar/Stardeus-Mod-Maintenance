using Game;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Utils;
using KL.Utils;
using UnityEngine;

namespace AutoUtilitiesModFramework;

/// <summary>Owns widget lifecycle and provides an independent smoke test.</summary>
public sealed class TopBarWidgetSys : GameSystem
{
    /// <summary>Stable Stardeus system registration ID.</summary>
    public const string SysId = "AutoUtilitiesTopBarWidgetSys";

    // ======================================================================
    // DEVELOPMENT TEST HARNESS: HOW TO ENABLE THE TEST WIDGET AND TEST UI
    // ======================================================================
    // 1. Change the value below from false to true.
    // 2. Rebuild .vscode/mod.csproj in Release configuration.
    // 3. Restart Stardeus (or fully reload the mod assembly).
    // 4. Load a game. The cyan copy/settings icon appears in the top bar.
    // 5. Hover it to test dynamic tooltips, then click it to open the test UI.
    //    Controls, Layout, Sections, Scroll and Theme exercise the public helpers.
    //
    // IMPORTANT: Set this back to false and rebuild before Workshop release.
    // The service system itself must remain active because it creates widgets
    // registered by dependent mods. This flag disables only our demonstration.
    // FrameworkUiTestPanel.cs is a living, compilable consumer example and is
    // intentionally retained even while this switch is false.
    // Disabled for 1.4.0 after in-game acceptance; the Theme palette is retained.
    private static readonly bool EnableDemonstrationUi = false;
    private const string CompatibilityWidgetId =
        "AutoUtilitiesModFramework.CompatibilityAlert";

    private static TopBarWidgetSys activeInstance;

    private GameState loadedState;
    private ITopBarWidgetHandle demonstrationWidget;
    private ITopBarWidgetHandle compatibilityWidget;
    private FrameworkUiTestPanel testPanel;
    private CompatibilityAlertPanel compatibilityPanel;
    private bool compatibilityAlertShown;
    private bool compatibilityWidgetRegistrationInProgress;
    private bool compatibilityAlertFaulted;
    private int displayedCompatibilityRevision = -1;

    /// <summary>Framework system ID used by the game registry.</summary>
    public override string Id => SysId;
    /// <summary>Keeps framework services available in sandbox games.</summary>
    public override bool SkipInSandbox => false;

    [SelfInit]
    private static void Register()
    {
        GameSystems.Register(SysId, () => new TopBarWidgetSys());
    }

    protected override void OnInitialize()
    {
        activeInstance = this;
        if (EnableDemonstrationUi)
        {
            testPanel = new FrameworkUiTestPanel(RefreshDemonstrationWidget);
            demonstrationWidget = AutoUtilitiesApi.RegisterTopBarWidget(
                new TopBarWidgetRegistration
                {
                    Id = "AutoUtilitiesModFramework.Demonstration",
                    Icon = () => GraphicsId.IconsColorCopySettings,
                    Tint = () => testPanel != null && testPanel.Enabled
                        ? Color.white : Color.gray,
                    Tooltip = BuildDemonstrationTooltip,
                    OnClick = () => testPanel?.Toggle(),
                    Order = int.MinValue
                });
        }
        S.Sig.AfterLoadState.AddListener(OnLoadState);
    }

    private void OnLoadState(GameState state)
    {
        DetachState();
        testPanel?.Dispose();
        compatibilityPanel?.Dispose();
        compatibilityAlertShown = false;
        compatibilityAlertFaulted = false;
        displayedCompatibilityRevision = -1;
        loadedState = state;
        TopBarWidgetService.DestroyCreatedWidgets();
        state.Clock.EveryTickOrFrame.AddListener(EnsureWidgets);
    }

    internal static void RequestEnsure()
    {
        activeInstance?.EnsureWidgets();
    }

    internal static void NotifyCompatibilityChanged()
    {
        activeInstance?.EnsureCompatibilityAlertSafely();
    }

    private void EnsureWidgets()
    {
        EnsureCompatibilityAlertSafely();
        TopBarWidgetService.EnsureAll(loadedState);
        ShowCompatibilityAlertSafely();
    }

    private void EnsureCompatibilityAlertSafely()
    {
        if (compatibilityAlertFaulted) return;
        try
        {
            EnsureCompatibilityAlert();
        }
        catch (System.Exception ex)
        {
            compatibilityAlertFaulted = true;
            D.Err("Auto Utilities compatibility alert initialization failed. "
                + "The alert has been disabled for this load so gameplay can "
                + "continue: {0}", ex);
        }
    }

    private void ShowCompatibilityAlertSafely()
    {
        if (compatibilityAlertFaulted
            || compatibilityAlertShown
            || compatibilityWidget == null
            || !compatibilityWidget.IsCreated)
        {
            return;
        }

        // Mark first so any UI-triggered re-entry cannot show a second panel.
        compatibilityAlertShown = true;
        try
        {
            compatibilityPanel?.Show();
        }
        catch (System.Exception ex)
        {
            compatibilityAlertFaulted = true;
            compatibilityPanel?.Dispose();
            D.Err("Auto Utilities compatibility window failed to open. The "
                + "window has been disabled for this load so gameplay can "
                + "continue: {0}", ex);
        }
    }

    private void EnsureCompatibilityAlert()
    {
        if (!ConsumerCompatibilityService.HasIssues)
        {
            compatibilityPanel?.Dispose();
            compatibilityPanel = null;
            compatibilityWidget?.Dispose();
            compatibilityWidget = null;
            return;
        }

        compatibilityPanel ??= new CompatibilityAlertPanel();
        compatibilityWidget ??=
            TopBarWidgetService.FindRegistered(CompatibilityWidgetId);
        if (compatibilityWidget == null
            && !compatibilityWidgetRegistrationInProgress)
        {
            compatibilityWidgetRegistrationInProgress = true;
            try
            {
                compatibilityWidget = AutoUtilitiesApi.RegisterTopBarWidget(
                    new TopBarWidgetRegistration
                    {
                        Id = CompatibilityWidgetId,
                        Icon = () => GraphicsId.IconsColorInfo,
                        Tint = () => new Color(1f, 0.72f, 0.12f, 1f),
                        Tooltip = BuildCompatibilityTooltip,
                        BadgeText = () => ConsumerCompatibilityService.IssueCount
                            .ToString(),
                        OnClick = () =>
                            activeInstance?.compatibilityPanel?.Toggle(),
                        Order = int.MinValue + 1
                    });
            }
            finally
            {
                compatibilityWidgetRegistrationInProgress = false;
            }
        }

        int revision = ConsumerCompatibilityService.Revision;
        if (displayedCompatibilityRevision != revision)
        {
            // Consumer registration is rare. Refresh only when its reported
            // data changes; rebuilding every frame destroys hover state,
            // causes button-highlight flicker, and prevents tooltip delays.
            compatibilityPanel.Refresh();
            compatibilityWidget?.Refresh();
            displayedCompatibilityRevision = revision;
        }
    }

    private static string BuildCompatibilityTooltip()
    {
        return "Auto Utilities compatibility warning"
            + "\nAffected mods: " + ConsumerCompatibilityService.IssueCount
            + "\nStable features remain available where safe."
            + "\nClick for details.";
    }

    private void RefreshDemonstrationWidget()
    {
        demonstrationWidget?.Refresh();
    }

    private string BuildDemonstrationTooltip()
    {
        return "Auto Utilities Mod Framework v"
            + AutoUtilitiesApi.FrameworkVersion
            + "\nTop-bar widget service: ACTIVE"
            + "\nDemonstration state: "
            + (testPanel != null && testPanel.Enabled ? "ON" : "OFF")
            + "\nActions run: " + (testPanel?.ActionCount ?? 0)
            + "\nClick to open the framework UI test panel.";
    }

    private void DetachState()
    {
        if (loadedState == null) return;
        loadedState.Clock.EveryTickOrFrame.RemoveListener(EnsureWidgets);
        loadedState = null;
    }

    /// <summary>
    /// Game-system lifecycle callback that releases the demonstration and all
    /// framework-created native widgets. Consumer mods do not call this.
    /// </summary>
    public override void Unload()
    {
        S.Sig.AfterLoadState.RemoveListener(OnLoadState);
        DetachState();
        testPanel?.Dispose();
        testPanel = null;
        compatibilityPanel?.Dispose();
        compatibilityPanel = null;
        demonstrationWidget?.Dispose();
        demonstrationWidget = null;
        compatibilityWidget?.Dispose();
        compatibilityWidget = null;
        TopBarWidgetService.DestroyCreatedWidgets();
        if (activeInstance == this) activeInstance = null;
    }
}
