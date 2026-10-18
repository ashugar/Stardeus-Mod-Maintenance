using System;
using System.Globalization;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Data;
using TMPro;

namespace AggressiveAutoConnectUpgrade;

public sealed class AggressiveAutoConnectPanel : IDisposable
{
    private const int AutoConnectUpgradeCountKey = 77000982;
    private readonly GameState state;
    private readonly IShortcutHandle shortcut;
    private readonly Action<bool> setEnabled;
    private readonly Action<bool> setRestoreColonistShutdowns;
    private readonly Action<bool> setCompleteReconnect;
    private readonly Action<float> setPulseInterval;
    private readonly FrameworkPanel panel;

    public bool IsOpen => panel.IsOpen;

    public AggressiveAutoConnectPanel(GameState state,
        IShortcutHandle shortcut, Action<bool> setEnabled,
        Action<bool> setRestoreColonistShutdowns,
        Action<bool> setCompleteReconnect, Action<float> setPulseInterval)
    {
        this.state = state;
        this.shortcut = shortcut;
        this.setEnabled = setEnabled;
        this.setRestoreColonistShutdowns = setRestoreColonistShutdowns;
        this.setCompleteReconnect = setCompleteReconnect;
        this.setPulseInterval = setPulseInterval;
        panel = new FrameworkPanel(new FrameworkPanelOptions
        {
            Id = "AggressiveAutoConnectUpgrade.Controls",
            Title = "Aggressive Auto Connect",
            RememberGeometry = true
        }, Build);
        shortcut.StateChanged += Refresh;
    }

    public void Show() => panel.Show();
    public void Close() => panel.Close();
    public void Toggle() => panel.Toggle();
    public void Refresh() => panel.Refresh();

    private void Build(PanelContentContext context)
    {
        bool enabled = AggressiveAutoConnectSettings.Enabled;
        context.AddToggle(enabled ? IconId.CCheck : "Icons/Color/NoEnergy",
            "Automatic connection pulses", enabled ? "On" : "Off", enabled,
            setEnabled);

        bool restoreColonistShutdowns =
            AggressiveAutoConnectSettings.RestoreColonistShutdownsEnabled;
        context.AddToggle(
            restoreColonistShutdowns ? IconId.CCheck : "Icons/Color/NoEnergy",
            "Restore colonist-disabled devices",
            restoreColonistShutdowns ? "On" : "Off",
            restoreColonistShutdowns,
            setRestoreColonistShutdowns,
            "Optional and disabled by default. Automatically switches back on "
            + "only devices turned off during the Lights Out mental breakdown. "
            + "Player and other shutdowns remain untouched.");

        bool complete = AggressiveAutoConnectSettings.CompleteReconnectEnabled;
        context.AddToggle(complete ? IconId.CCheck : "Icons/Color/NoEnergy",
            "Complete grid reconnect", complete ? "On" : "Off", complete,
            setCompleteReconnect,
            "Optional and disabled by default. If a built device remains "
            + "connected but cannot reach a Ship Computer while a reachable "
            + "socket is available, AACU disconnects and rebuilds the entire "
            + "electrical network. This may override Switch-controlled subgrids.");

        context.AddInfo("Icons/Color/Energy", "Pulse interval",
            "Seconds (0.25-3600)");
        context.AddInput(IconId.CCog, "Pulse interval in seconds",
            FormatPulseInterval(AggressiveAutoConnectSettings.PulseIntervalSeconds),
            CommitPulseInterval, TMP_InputField.ContentType.DecimalNumber,
            "How often automatic connection checks run while enabled.");

        context.AddInfo("Icons/Color/Upgrade", "Required upgrade",
            state.BB.Get(AutoConnectUpgradeCountKey, 0) > 0
                ? "Auto Connect installed" : "Waiting for Auto Connect");
        context.AddInfo(IconId.CInfo, "Shortcut", shortcut.DisplayName);
        context.AddButton(IconId.CCog,
            shortcut.IsCapturing ? "Press a key..." : "Set shortcut", null,
            shortcut.BeginCapture);
        context.AddButton("Icons/Color/NoEnergy", "Clear shortcut", null,
            shortcut.Clear);
        context.AddInfo(IconId.CInfo, "Status", BuildShortcutStatusSummary(),
            shortcut.StatusMessage);
    }

    private void CommitPulseInterval(string value)
    {
        if (TryParsePulseInterval(value, out float seconds))
            setPulseInterval(seconds);
    }

    private string BuildShortcutStatusSummary()
    {
        if (shortcut.IsCapturing)
            return "Waiting for key";
        if (shortcut.StatusMessage.IndexOf("conflict",
                StringComparison.OrdinalIgnoreCase) >= 0
            || shortcut.StatusMessage.IndexOf("Cannot assign",
                StringComparison.OrdinalIgnoreCase) >= 0)
            return "Shortcut conflict";
        return shortcut.HasShortcut ? "Ready" : "Not assigned";
    }

    public void Dispose()
    {
        shortcut.StateChanged -= Refresh;
        panel.Dispose();
    }

    private static bool TryParsePulseInterval(string value, out float seconds)
    {
        bool parsed = float.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out seconds);
        if (!parsed)
            parsed = float.TryParse(value, NumberStyles.Float,
                CultureInfo.CurrentCulture, out seconds);
        return parsed
            && seconds >= AggressiveAutoConnectSettings.MinPulseIntervalSeconds
            && seconds <= AggressiveAutoConnectSettings.MaxPulseIntervalSeconds;
    }

    private static string FormatPulseInterval(float seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
