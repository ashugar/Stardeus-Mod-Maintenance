using System;
using AutoUtilitiesModFramework;
using Game.Constants;

namespace TractorBeamAutoConnect;

public sealed class TractorBeamAutoConnectPanel : IDisposable
{
    private readonly IShortcutHandle shortcut;
    private readonly Action<bool> setEnabled;
    private readonly Func<int> validate;
    private readonly Func<int> getUnassigned;
    private readonly Func<int> getLastAssigned;
    private readonly FrameworkPanel panel;

    public bool IsOpen => panel.IsOpen;

    public TractorBeamAutoConnectPanel(IShortcutHandle shortcut,
        Action<bool> setEnabled,
        Func<int> validate, Func<int> getUnassigned,
        Func<int> getLastAssigned)
    {
        this.shortcut = shortcut;
        this.setEnabled = setEnabled;
        this.validate = validate;
        this.getUnassigned = getUnassigned;
        this.getLastAssigned = getLastAssigned;

        panel = new FrameworkPanel(new FrameworkPanelOptions
        {
            Id = "TractorBeamAutoConnect.Controls",
            Title = "Auto Tractor Assignment",
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
        bool enabled = TractorBeamAutoConnectSettings.Enabled;
        context.AddToggle(enabled ? IconId.CCheck : "Icons/Color/NoEnergy",
            "Enable automation", enabled ? "On" : "Off", enabled,
            setEnabled);

        context.AddInfo(IconId.CInfo, "Unassigned beams",
            getUnassigned().ToString());
        context.AddInfo(IconId.CInfo, "Last scan assigned",
            getLastAssigned().ToString());

        context.AddButton(IconId.CCog, "Validate Connections", null,
            () => validate(),
            "Assigns targets to currently unassigned Tractor Beams. "
            + "Existing assignments are never changed.");

        context.AddInfo(IconId.CInfo, "Panel shortcut",
            shortcut.DisplayName);
        context.AddButton(IconId.CCog,
            shortcut.IsCapturing ? "Press a key..." : "Set shortcut", null,
            shortcut.BeginCapture);
        context.AddButton("Icons/Color/NoEnergy", "Clear shortcut", null,
            shortcut.Clear);
        context.AddInfo(IconId.CInfo, "Shortcut status",
            BuildShortcutStatusSummary(), shortcut.StatusMessage);
    }

    private string BuildShortcutStatusSummary()
    {
        if (shortcut.IsCapturing) return "Waiting for key";
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
}
