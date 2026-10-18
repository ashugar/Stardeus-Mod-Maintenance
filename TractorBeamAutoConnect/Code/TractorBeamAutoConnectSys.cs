using System.Collections.Generic;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Utils;
using KL.Clock;
using KL.Utils;
using UnityEngine;

namespace TractorBeamAutoConnect;

public sealed class TractorBeamAutoConnectSys : GameSystem
{
    public const string SysId = "TractorBeamAutoConnectSys";
    private const string ConsumerId = "TractorBeamAutoConnect";
    public const string RequiredUtilitiesVersion = "1.4.0";
    private const double ScanIntervalSeconds = 10.0;

    private readonly List<TractorBeamComp> beams = new();
    private readonly List<TractorBeamTargetComp> targets = new();
    private readonly List<TractorBeamComp> unassigned = new();
    private readonly List<TractorBeamTargetComp> eligible = new();
    private readonly Dictionary<TractorBeamTargetComp, int> assignmentCounts = new();
    private GameState loadedState;
    private double nextScanAt;
    private TractorBeamAutoConnectPanel panel;
    private ITopBarWidgetHandle topWidget;
    private IShortcutHandle shortcut;

    public int LastUnassignedCount { get; private set; }
    public int LastAssignedCount { get; private set; }

    public override string Id => SysId;
    public override bool SkipInSandbox => false;

    [SelfInit]
    private static void Register()
    {
        GameSystems.Register(SysId, () => new TractorBeamAutoConnectSys());
    }

    protected override void OnInitialize()
    {
        if (!AutoUtilitiesApi.TryRegisterConsumer(
                ConsumerId, "Tractor Beam Auto Connect",
                typeof(TractorBeamAutoConnectSys).Assembly.GetName().Version
                    .ToString(3),
                RequiredUtilitiesVersion))
        {
            return;
        }

        TractorBeamAutoConnectSettings.Initialize();
        shortcut = AutoUtilitiesApi.RegisterShortcut(
            new ShortcutRegistration
            {
                ConsumerId = ConsumerId,
                Id = "TogglePanel",
                DisplayName = "open or close Tractor Beam Auto Connect",
                PreferenceKey = TractorBeamAutoConnectSettings.ShortcutKey,
                GameState = S,
                OnPressed = TogglePanel
            });
        panel = new TractorBeamAutoConnectPanel(shortcut, SetEnabled,
            ValidateConnections, () => LastUnassignedCount,
            () => LastAssignedCount);
        topWidget = AutoUtilitiesApi.RegisterTopBarWidget(
            new TopBarWidgetRegistration
            {
                Id = "TractorBeamAutoConnect.Status",
                Icon = () => GraphicsId.StructureFloorsTractorBeamTarget,
                Tint = () => TractorBeamAutoConnectSettings.Enabled
                    ? Color.white : Color.gray,
                Tooltip = BuildTooltip,
                OnClick = TogglePanel,
                Width = 32f,
                Order = 100
            });
        S.Sig.AfterLoadState.AddListener(OnLoadState);
    }

    private void OnLoadState(GameState state)
    {
        DetachStateListeners();
        panel?.Close();
        loadedState = state;
        nextScanAt = GameTimer.realtimeSinceStartupAsDouble + 1.0;
        state.Clock.OnTick.AddListener(OnTick);
        state.Sig.TractorBeamTargetBuilt.AddListener(OnTargetBuilt);
    }

    private void OnTargetBuilt(int pos, TractorBeamTargetComp target)
    {
        if (TractorBeamAutoConnectSettings.Enabled) ValidateConnections();
    }

    private void OnTick(long ticks)
    {
        if (!TractorBeamAutoConnectSettings.Enabled) return;
        double now = GameTimer.realtimeSinceStartupAsDouble;
        if (now < nextScanAt) return;
        nextScanAt = now + ScanIntervalSeconds;
        ValidateConnections();
    }

    public int ValidateConnections()
    {
        beams.Clear();
        targets.Clear();
        unassigned.Clear();
        eligible.Clear();
        assignmentCounts.Clear();

        S.Components.FindAll(beams);
        S.Components.FindAll(targets);

        foreach (TractorBeamTargetComp target in targets)
        {
            if (target != null && target.IsValidNow)
            {
                eligible.Add(target);
                assignmentCounts[target] = 0;
            }
        }

        foreach (TractorBeamComp beam in beams)
        {
            if (beam == null || beam.Tile == null || !beam.Tile.IsConstructed)
                continue;

            TractorBeamTargetComp current = beam.Target;
            if (current == null) unassigned.Add(beam);
            else if (assignmentCounts.ContainsKey(current))
                assignmentCounts[current]++;
        }

        LastUnassignedCount = unassigned.Count;
        if (eligible.Count == 0)
        {
            LastAssignedCount = 0;
            RefreshUi();
            return 0;
        }

        int assigned = 0;
        foreach (TractorBeamComp beam in unassigned)
        {
            TractorBeamTargetComp chosen = ChooseTarget();
            if (chosen == null) continue;
            beam.SetTargetSpot(chosen);
            assignmentCounts[chosen]++;
            assigned++;
        }

        LastAssignedCount = assigned;
        LastUnassignedCount -= assigned;
        RefreshUi();
        return assigned;
    }

    private void SetEnabled(bool enabled)
    {
        TractorBeamAutoConnectSettings.SetEnabled(enabled);
        if (enabled) ValidateConnections();
        RefreshUi();
    }

    private void RefreshUi()
    {
        topWidget?.Refresh();
        panel?.Refresh();
    }

    private void TogglePanel() => panel?.Toggle();

    private string BuildTooltip()
    {
        return "Auto Tractor Assignment: "
            + (TractorBeamAutoConnectSettings.Enabled ? "ON" : "OFF")
            + "\nUnassigned beams: " + LastUnassignedCount
            + "\nLast scan assigned: " + LastAssignedCount
            + "\nClick for controls.";
    }

    private TractorBeamTargetComp ChooseTarget()
    {
        int start = Random.Range(0, eligible.Count);
        for (int offset = 0; offset < eligible.Count; offset++)
        {
            TractorBeamTargetComp target =
                eligible[(start + offset) % eligible.Count];
            if (assignmentCounts[target] == 0) return target;
        }
        return eligible[Random.Range(0, eligible.Count)];
    }

    private void DetachStateListeners()
    {
        if (loadedState == null) return;
        loadedState.Clock.OnTick.RemoveListener(OnTick);
        loadedState.Sig.TractorBeamTargetBuilt.RemoveListener(OnTargetBuilt);
        loadedState = null;
    }

    public override void Unload()
    {
        S.Sig.AfterLoadState.RemoveListener(OnLoadState);
        DetachStateListeners();
        panel?.Dispose();
        panel = null;
        topWidget?.Dispose();
        topWidget = null;
        shortcut?.Dispose();
        shortcut = null;
        beams.Clear();
        targets.Clear();
        unassigned.Clear();
        eligible.Clear();
        assignmentCounts.Clear();
    }
}
