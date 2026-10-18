using System;
using System.Collections;
using System.Collections.Generic;
using AutoUtilitiesModFramework;
using Game;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.AI;
using Game.Systems.Electricity;
using Game.Utils;
using HarmonyLib;
using KL.Clock;
using KL.Utils;
using UnityEngine;

namespace AggressiveAutoConnectUpgrade;

public sealed class AggressiveAutoConnectSys : GameSystem
{
    public const string SysId = "AggressiveAutoConnectSys";
    private const string ConsumerId = "AggressiveAutoConnectUpgrade";
    private const string HarmonyId =
        "AggressiveAutoConnectUpgrade.LightsOutRecovery";
    public const string RequiredUtilitiesVersion = "1.4.0";
    private const int AutoConnectUpgradeCountKey = 77000982;
    private const int CompleteReconnectConfirmationPulses = 2;

    private double nextPulseAt;
    private AggressiveAutoConnectPanel panel;
    private IShortcutHandle shortcut;
    private GameState loadedState;
    private Harmony harmony;
    private readonly List<(ElectricNodeComp, ElectricNodeComp)>
        coreAutoConnectPlan =
            new List<(ElectricNodeComp, ElectricNodeComp)>();
    private readonly Dictionary<int, int> snapshotNodeIndexByEntityId =
        new Dictionary<int, int>();
    private readonly int[] connectionEntityIds = new int[1024];
    private int pendingCompleteReconnectNodeEntityId = -1;
    private int pendingCompleteReconnectSocketEntityId = -1;
    private int completeReconnectConfirmations;
    private bool completeReconnectArmed = true;
    private bool completeReconnectInProgress;
    private ITopBarWidgetHandle topWidget;

    public override string Id => SysId;
    public override bool SkipInSandbox => false;
    [SelfInit]
    private static void Register()
    {
        GameSystems.Register(SysId, () => new AggressiveAutoConnectSys());
    }

    protected override void OnInitialize()
    {
        if (!AutoUtilitiesApi.TryRegisterConsumer(
                ConsumerId,
                "Aggressive Auto Connect Upgrade",
                typeof(AggressiveAutoConnectSys).Assembly.GetName().Version
                    .ToString(3),
                RequiredUtilitiesVersion))
        {
            return;
        }

        AggressiveAutoConnectSettings.Initialize();
        InstallLightsOutPatch();

        shortcut = AutoUtilitiesApi.RegisterShortcut(
            new ShortcutRegistration
            {
                ConsumerId = ConsumerId,
                Id = "ToggleAutomaticConnectionPulses",
                DisplayName = "toggle aggressive auto connect",
                PreferenceKey = AggressiveAutoConnectSettings.ShortcutKey,
                GameState = S,
                OnPressed = ToggleEnabled
            });
        panel = new AggressiveAutoConnectPanel(
            S,
            shortcut,
            SetEnabled,
            SetRestoreColonistShutdowns,
            SetCompleteReconnect,
            SetPulseInterval);
        topWidget = AutoUtilitiesApi.RegisterTopBarWidget(
            new TopBarWidgetRegistration
            {
                Id = "AggressiveAutoConnectUpgrade.Status",
                Icon = () => AggressiveAutoConnectSettings.Enabled
                    ? "Icons/Color/Energy" : "Icons/Color/NoEnergy",
                Tint = () => AggressiveAutoConnectSettings.Enabled
                    ? Color.green : Color.gray,
                Tooltip = BuildTooltip,
                OnClick = () => panel.Toggle(),
                Width = 32f,
                Order = 200
            });

        S.Sig.AfterLoadState.AddListener(OnLoadState);
    }

    private void OnLoadState(GameState state)
    {
        if (loadedState != null)
        {
            loadedState.Clock.OnTick.RemoveListener(OnTick);
        }
        loadedState = state;
        completeReconnectInProgress = false;
        ResetCompleteReconnectConfirmation(rearm: true);
        panel?.Close();
        nextPulseAt = GameTimer.realtimeSinceStartupAsDouble
            + AggressiveAutoConnectSettings.PulseIntervalSeconds;
        state.Clock.OnTick.AddListener(OnTick);
        RefreshUi();
    }

    private void OnTick(long ticks)
    {
        if (!AggressiveAutoConnectSettings.Enabled
            || completeReconnectInProgress)
        {
            return;
        }

        double now = GameTimer.realtimeSinceStartupAsDouble;
        if (now < nextPulseAt)
        {
            return;
        }

        nextPulseAt = now + AggressiveAutoConnectSettings.PulseIntervalSeconds;
        if (S.BB.Get(AutoConnectUpgradeCountKey, 0) <= 0)
        {
            ResetCompleteReconnectConfirmation(rearm: true);
            RefreshUi();
            return;
        }

        ElectricitySys electricity = S.Sys.Electricity;
        try
        {
            // Ask Core's rc322 planner whether its current Auto Connect rules
            // have useful work. This preserves Core's nearest-connector,
            // Ship Computer cap, and switched-off Switch boundary behavior.
            coreAutoConnectPlan.Clear();
            AutoConnectPlanner.PlanAll(electricity, coreAutoConnectPlan);
            if (coreAutoConnectPlan.Count > 0)
            {
                ResetCompleteReconnectConfirmation(rearm: true);
                electricity.TryAutoConnectAll();
                return;
            }

            if (AggressiveAutoConnectSettings.CompleteReconnectEnabled)
            {
                if (IsCompleteReconnectUnsafe())
                {
                    ResetCompleteReconnectConfirmation(rearm: false);
                }
                else if (TryFindCompleteReconnectCandidate(
                    AutoConnectPlanner.Capture(electricity),
                    out ElectricNodeComp strandedNode,
                    out ElectricNodeComp reachableSocket))
                {
                    if (ConfirmCompleteReconnectCandidate(
                        strandedNode,
                        reachableSocket))
                    {
                        RebuildAllElectricalConnections();
                        return;
                    }
                }
                else
                {
                    ResetCompleteReconnectConfirmation(rearm: true);
                }
            }
            else
            {
                ResetCompleteReconnectConfirmation(rearm: true);
            }
        }
        finally
        {
            coreAutoConnectPlan.Clear();
        }
    }

    private void ToggleEnabled()
    {
        SetEnabled(!AggressiveAutoConnectSettings.Enabled);
    }

    private void SetEnabled(bool enabled)
    {
        AggressiveAutoConnectSettings.SetEnabled(enabled);
        ResetCompleteReconnectConfirmation(rearm: true);
        nextPulseAt = GameTimer.realtimeSinceStartupAsDouble
            + AggressiveAutoConnectSettings.PulseIntervalSeconds;
        RefreshUi();
    }

    private void SetPulseInterval(float seconds)
    {
        AggressiveAutoConnectSettings.SetPulseInterval(seconds);
        ResetCompleteReconnectConfirmation(rearm: false);
        nextPulseAt = GameTimer.realtimeSinceStartupAsDouble
            + AggressiveAutoConnectSettings.PulseIntervalSeconds;
        // Do not rebuild the panel from inside the native input's Enter event.
        // The field already displays the submitted value, while rebuilding it
        // during submit can cause the replacement input to receive that same
        // Enter key and recursively append another content block.
        topWidget?.Refresh();
    }

    private void SetCompleteReconnect(bool enabled)
    {
        AggressiveAutoConnectSettings.SetCompleteReconnectEnabled(enabled);
        ResetCompleteReconnectConfirmation(rearm: true);
        nextPulseAt = GameTimer.realtimeSinceStartupAsDouble
            + AggressiveAutoConnectSettings.PulseIntervalSeconds;
        RefreshUi();
    }

    private void SetRestoreColonistShutdowns(bool enabled)
    {
        AggressiveAutoConnectSettings.SetRestoreColonistShutdownsEnabled(
            enabled);
        RefreshUi();
    }

    private void InstallLightsOutPatch()
    {
        try
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(AggressiveAutoConnectSys).Assembly);
        }
        catch (Exception ex)
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            D.LogEx(ex,
                "Aggressive Auto Connect could not install the Lights Out "
                + "recovery hook. Other AACU features remain available.");
        }
    }

    internal static void RestoreLightsOutTarget(Tile target)
    {
        if (!AggressiveAutoConnectSettings.RestoreColonistShutdownsEnabled
            || target == null
            || !target.IsActive
            || !target.TryGetComponent(out ElectricNodeComp node)
            || !node.IsSwitchable
            || node.IsSwitchedOn)
        {
            return;
        }

        node.Switch(true);
        D.Warn(
            "Aggressive Auto Connect restored a device switched off by the "
            + "Lights Out mental breakdown: {0}",
            target);
    }

    private string BuildTooltip()
    {
        string enabled = AggressiveAutoConnectSettings.Enabled ? "ON" : "OFF";
        string upgrade = S.BB.Get(AutoConnectUpgradeCountKey, 0) > 0
            ? "Auto Connect Upgrade detected"
            : "waiting for Auto Connect Upgrade";
        string completeReconnect =
            AggressiveAutoConnectSettings.CompleteReconnectEnabled
                ? "Complete grid reconnect: ON"
                : "Complete grid reconnect: OFF";
        string colonistShutdownRecovery =
            AggressiveAutoConnectSettings.RestoreColonistShutdownsEnabled
                ? "Colonist shutdown recovery: ON"
                : "Colonist shutdown recovery: OFF";
        return $"Aggressive Auto Connect: {enabled}\n"
            + $"Pulse every {AggressiveAutoConnectSettings.PulseIntervalSeconds:0.###} seconds\n"
            + $"{colonistShutdownRecovery}\n{completeReconnect}\n"
            + $"{upgrade}\nClick for settings.";
    }

    private void RefreshUi()
    {
        topWidget?.Refresh();
        panel?.Refresh();
    }

    private bool TryFindCompleteReconnectCandidate(
        AutoConnectSnapshot snapshot,
        out ElectricNodeComp strandedNode,
        out ElectricNodeComp reachableSocket)
    {
        strandedNode = null;
        reachableSocket = null;
        List<AutoConnectSnapshot.Node> nodes = snapshot.Nodes;

        for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            AutoConnectSnapshot.Node node = nodes[nodeIndex];
            ElectricNodeComp nodeComp = node.Comp;
            if (!nodeComp.Tile.IsConstructed
                || node.IsConnector
                || node.IsShipComputer
                || node.NumConnections <= 0
                || snapshot.IsReached(nodeIndex))
            {
                continue;
            }

            int bestCandidateIndex = -1;
            float bestDistanceSqr = float.MaxValue;
            foreach (int candidateIndex in snapshot.NearTo(node.Position))
            {
                if (candidateIndex == nodeIndex)
                {
                    continue;
                }

                AutoConnectSnapshot.Node candidate = nodes[candidateIndex];
                if (!candidate.IsConnector
                    || !candidate.Comp.Tile.IsConstructed
                    || !snapshot.HasSlot(candidateIndex)
                    || !snapshot.IsReached(candidateIndex)
                    || !AutoConnectSnapshot.InReach(candidate, node)
                    || snapshot.AreSeparated(candidateIndex, nodeIndex)
                    || !nodeComp.CanConnectTo(candidate.Comp, out _))
                {
                    continue;
                }

                float distanceSqr =
                    (candidate.Position - node.Position).sqrMagnitude;
                if (bestCandidateIndex >= 0)
                {
                    AutoConnectSnapshot.Node best =
                        nodes[bestCandidateIndex];
                    if (distanceSqr > bestDistanceSqr
                        || distanceSqr == bestDistanceSqr
                        && candidate.FreeSlots < best.FreeSlots
                        || distanceSqr == bestDistanceSqr
                        && candidate.FreeSlots == best.FreeSlots
                        && candidate.EntityId > best.EntityId)
                    {
                        continue;
                    }
                }

                bestCandidateIndex = candidateIndex;
                bestDistanceSqr = distanceSqr;
            }

            if (bestCandidateIndex >= 0)
            {
                strandedNode = nodeComp;
                reachableSocket = nodes[bestCandidateIndex].Comp;
                return true;
            }
        }

        return TryFindFullConnectorRebalanceCandidate(
            snapshot,
            out strandedNode,
            out reachableSocket);
    }

    private bool TryFindFullConnectorRebalanceCandidate(
        AutoConnectSnapshot snapshot,
        out ElectricNodeComp isolatedConnector,
        out ElectricNodeComp fullReachedConnector)
    {
        isolatedConnector = null;
        fullReachedConnector = null;
        List<AutoConnectSnapshot.Node> nodes = snapshot.Nodes;

        try
        {
            bool snapshotNodeIndexBuilt = false;
            for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                AutoConnectSnapshot.Node node = nodes[nodeIndex];
                if (!node.Comp.Tile.IsConstructed
                    || !node.IsConnector
                    || node.IsShipComputer
                    || node.NumConnections != 0
                    // One slot joins the grid and one adopts an existing device.
                    // More than two slots guarantees a net capacity increase.
                    || node.FreeSlots <= 2
                    || snapshot.IsReached(nodeIndex)
                    || snapshot.IsBlocked(nodeIndex))
                {
                    continue;
                }

                int bestCandidateIndex = -1;
                float bestDistanceSqr = float.MaxValue;
                bool foundFreeReachedConnector = false;
                foreach (int candidateIndex in snapshot.NearTo(node.Position))
                {
                    if (candidateIndex == nodeIndex)
                    {
                        continue;
                    }

                    AutoConnectSnapshot.Node candidate = nodes[candidateIndex];
                    if (!candidate.IsConnector
                        || !candidate.Comp.Tile.IsConstructed
                        || !snapshot.IsReached(candidateIndex)
                        || !AutoConnectSnapshot.InReach(candidate, node)
                        || snapshot.AreSeparated(candidateIndex, nodeIndex))
                    {
                        continue;
                    }

                    // A free reached connector is Core's ordinary case and must
                    // never escalate to a destructive full reconnect.
                    if (snapshot.HasSlot(candidateIndex))
                    {
                        foundFreeReachedConnector = true;
                        break;
                    }

                    // Rebuilding is useful only when one of this full socket's
                    // current device links can move to the isolated connector.
                    // That creates the old socket's uplink slot while leaving
                    // additional capacity on the newly joined connector.
                    if (candidate.NumConnections <= 0)
                    {
                        continue;
                    }

                    if (!snapshotNodeIndexBuilt)
                    {
                        for (int snapshotNodeIndex = 0;
                            snapshotNodeIndex < nodes.Count;
                            snapshotNodeIndex++)
                        {
                            snapshotNodeIndexByEntityId[
                                nodes[snapshotNodeIndex].EntityId] =
                                    snapshotNodeIndex;
                        }
                        snapshotNodeIndexBuilt = true;
                    }

                    if (!HasMovableDeviceConnection(
                            snapshot,
                            nodeIndex,
                            candidateIndex))
                    {
                        continue;
                    }

                    float distanceSqr =
                        (candidate.Position - node.Position).sqrMagnitude;
                    if (bestCandidateIndex >= 0
                        && (distanceSqr > bestDistanceSqr
                            || distanceSqr == bestDistanceSqr
                            && candidate.EntityId
                                > nodes[bestCandidateIndex].EntityId))
                    {
                        continue;
                    }

                    bestCandidateIndex = candidateIndex;
                    bestDistanceSqr = distanceSqr;
                }

                if (!foundFreeReachedConnector && bestCandidateIndex >= 0)
                {
                    isolatedConnector = node.Comp;
                    fullReachedConnector = nodes[bestCandidateIndex].Comp;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            snapshotNodeIndexByEntityId.Clear();
        }
    }

    private bool HasMovableDeviceConnection(
        AutoConnectSnapshot snapshot,
        int isolatedConnectorIndex,
        int fullReachedConnectorIndex)
    {
        List<AutoConnectSnapshot.Node> nodes = snapshot.Nodes;
        AutoConnectSnapshot.Node isolatedConnector =
            nodes[isolatedConnectorIndex];
        AutoConnectSnapshot.Node fullReachedConnector =
            nodes[fullReachedConnectorIndex];
        int connectionCount = S.Sys.Electricity.ConnectionsFor(
            fullReachedConnector.EntityId,
            connectionEntityIds);

        for (int i = 0; i < connectionCount; i++)
        {
            if (!snapshotNodeIndexByEntityId.TryGetValue(
                    connectionEntityIds[i],
                    out int connectedNodeIndex))
            {
                continue;
            }

            AutoConnectSnapshot.Node connectedNode = nodes[connectedNodeIndex];
            ElectricNode connectedElectricNode = connectedNode.Comp.Node;
            if (connectedNode.IsConnector
                || connectedNode.IsShipComputer
                || !(connectedElectricNode.IsConsumer
                    || connectedElectricNode.IsConsumerHighPriority
                    || connectedElectricNode.IsConsumerLowPriority
                    || connectedElectricNode.IsProducer
                    || connectedElectricNode.IsProducerSmart
                    || connectedElectricNode.IsBattery)
                || !AutoConnectSnapshot.InReach(
                    isolatedConnector,
                    connectedNode)
                || snapshot.AreSeparated(
                    isolatedConnectorIndex,
                    connectedNodeIndex)
                || !isolatedConnector.Comp.CanConnectTo(
                    connectedNode.Comp,
                    out _))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool ConfirmCompleteReconnectCandidate(
        ElectricNodeComp strandedNode,
        ElectricNodeComp reachableSocket)
    {
        if (!completeReconnectArmed)
        {
            return false;
        }

        if (pendingCompleteReconnectNodeEntityId == strandedNode.EntityId
            && pendingCompleteReconnectSocketEntityId
                == reachableSocket.EntityId)
        {
            completeReconnectConfirmations++;
        }
        else
        {
            pendingCompleteReconnectNodeEntityId = strandedNode.EntityId;
            pendingCompleteReconnectSocketEntityId = reachableSocket.EntityId;
            completeReconnectConfirmations = 1;
        }

        if (completeReconnectConfirmations
            < CompleteReconnectConfirmationPulses)
        {
            return false;
        }

        completeReconnectArmed = false;
        string candidateType = strandedNode.Node.IsConnector
            ? "full connector rebalance"
            : "stranded device";
        D.Warn(
            "Aggressive Auto Connect confirmed complete grid reconnect "
            + "candidate after {0} consecutive pulses. Type: {1}; node: {2}; "
            + "reachable socket: {3}",
            completeReconnectConfirmations,
            candidateType,
            strandedNode.Tile,
            reachableSocket.Tile);
        ResetCompleteReconnectConfirmation(rearm: false);
        return true;
    }

    private void ResetCompleteReconnectConfirmation(bool rearm)
    {
        pendingCompleteReconnectNodeEntityId = -1;
        pendingCompleteReconnectSocketEntityId = -1;
        completeReconnectConfirmations = 0;
        if (rearm)
        {
            completeReconnectArmed = true;
        }
    }

    private bool IsCompleteReconnectUnsafe()
    {
        // Ordinary hyperjumps and warp-drive jumps share these travel states.
        // A complete reconnect briefly removes every electrical connection,
        // which can make a running drive unavailable and abort the journey.
        return A.IsInHyperspace || S.Sys.ShipNav.WantsHyperspace;
    }

    private void RebuildAllElectricalConnections()
    {
        if (IsCompleteReconnectUnsafe())
        {
            return;
        }

        completeReconnectInProgress = true;
        Coroutines.StartDirect(GenRebuildAllElectricalConnections());
    }

    private IEnumerator GenRebuildAllElectricalConnections()
    {
        ElectricitySys electricity = S.Sys.Electricity;
        double reconnectStartedAt = GameTimer.realtimeSinceStartupAsDouble;
        try
        {
            List<ElectricNodeComp> reconnectNodes =
                new List<ElectricNodeComp>();
            foreach (ElectricNodeComp nodeComp in
                S.Components.AllByType<ElectricNodeComp>())
            {
                if (nodeComp != null
                    && nodeComp.Entity.IsActive
                    && nodeComp.IsRegistered
                    && nodeComp.Node.IsInitialized
                    && nodeComp.Node.IsValid)
                {
                    reconnectNodes.Add(nodeComp);
                }
            }

            long initialConnectionEnds =
                CountReportedConnectionEnds(reconnectNodes);
            double teardownStartedAt = GameTimer.realtimeSinceStartupAsDouble;
            int bulkGridPasses = 0;
            bool usedLegacyFallback = false;
            long remainingConnectionEnds = initialConnectionEnds;

            while (remainingConnectionEnds > 0
                && bulkGridPasses < reconnectNodes.Count)
            {
                if (!TryFindConnectedGrid(reconnectNodes, out int gridId))
                {
                    usedLegacyFallback = true;
                    break;
                }

                electricity.DisconnectGridConnections(gridId);
                bulkGridPasses++;

                long updatedConnectionEnds =
                    CountReportedConnectionEnds(reconnectNodes);
                if (updatedConnectionEnds >= remainingConnectionEnds)
                {
                    D.Warn(
                        "Aggressive Auto Connect bulk disconnect made no "
                        + "reported progress on grid {0}; using the legacy "
                        + "connection recovery path.",
                        gridId);
                    usedLegacyFallback = true;
                    break;
                }

                remainingConnectionEnds = updatedConnectionEnds;
            }

            if (remainingConnectionEnds > 0)
            {
                usedLegacyFallback = true;
            }

            int legacyDisconnected = 0;
            if (usedLegacyFallback)
            {
                legacyDisconnected = DisconnectRemainingConnectionsLegacy(
                    electricity,
                    reconnectNodes);
            }

            double teardownMilliseconds =
                (GameTimer.realtimeSinceStartupAsDouble - teardownStartedAt)
                * 1000d;
            D.Warn(
                "Aggressive Auto Connect complete grid reconnect triggered. "
                + "Core bulk teardown processed {0} reported connection ends "
                + "across {1} grid passes in {2:0.0} ms; the legacy fallback "
                + "was {3} and removed {4} remaining connections.",
                initialConnectionEnds,
                bulkGridPasses,
                teardownMilliseconds,
                usedLegacyFallback ? "used" : "not needed",
                legacyDisconnected);

            double coreAutoConnectStartedAt =
                GameTimer.realtimeSinceStartupAsDouble;
            electricity.TryAutoConnectAll();
            double coreAutoConnectMilliseconds =
                (GameTimer.realtimeSinceStartupAsDouble
                    - coreAutoConnectStartedAt)
                * 1000d;
            double totalMilliseconds =
                (GameTimer.realtimeSinceStartupAsDouble - reconnectStartedAt)
                * 1000d;
            D.Warn(
                "Aggressive Auto Connect complete grid reconnect finished. "
                + "Core auto-connect: {0:0.0} ms; total: {1:0.0} ms.",
                coreAutoConnectMilliseconds,
                totalMilliseconds);
            yield return null;
        }
        finally
        {
            completeReconnectInProgress = false;
            nextPulseAt = GameTimer.realtimeSinceStartupAsDouble
                + AggressiveAutoConnectSettings.PulseIntervalSeconds;
        }
    }

    private static long CountReportedConnectionEnds(
        List<ElectricNodeComp> nodes)
    {
        long connectionEnds = 0;
        foreach (ElectricNodeComp nodeComp in nodes)
        {
            if (nodeComp != null
                && nodeComp.Entity.IsActive
                && nodeComp.IsRegistered
                && nodeComp.Node.IsInitialized
                && nodeComp.Node.IsValid
                && nodeComp.Node.NumConnections > 0)
            {
                connectionEnds += nodeComp.Node.NumConnections;
            }
        }

        return connectionEnds;
    }

    private static bool TryFindConnectedGrid(
        List<ElectricNodeComp> nodes,
        out int gridId)
    {
        foreach (ElectricNodeComp nodeComp in nodes)
        {
            if (nodeComp != null
                && nodeComp.Entity.IsActive
                && nodeComp.IsRegistered
                && nodeComp.Node.IsInitialized
                && nodeComp.Node.IsValid
                && nodeComp.Node.NumConnections > 0
                && nodeComp.Node.GridId >= 0)
            {
                gridId = nodeComp.Node.GridId;
                return true;
            }
        }

        gridId = -1;
        return false;
    }

    private int DisconnectRemainingConnectionsLegacy(
        ElectricitySys electricity,
        List<ElectricNodeComp> reconnectNodes)
    {
        Dictionary<int, ElectricNodeComp> nodesByEntity =
            new Dictionary<int, ElectricNodeComp>();
        HashSet<(int, int)> connections = new HashSet<(int, int)>();

        foreach (ElectricNodeComp nodeComp in reconnectNodes)
        {
            if (nodeComp == null
                || !nodeComp.Entity.IsActive
                || !nodeComp.IsRegistered
                || !nodeComp.Node.IsInitialized
                || !nodeComp.Node.IsValid)
            {
                continue;
            }

            nodesByEntity[nodeComp.EntityId] = nodeComp;
            int count = electricity.ConnectionsFor(
                nodeComp.EntityId,
                connectionEntityIds);
            for (int i = 0; i < count; i++)
            {
                int otherEntityId = connectionEntityIds[i];
                connections.Add(nodeComp.EntityId < otherEntityId
                    ? (nodeComp.EntityId, otherEntityId)
                    : (otherEntityId, nodeComp.EntityId));
            }
        }

        int disconnected = 0;
        foreach ((int firstEntityId, int secondEntityId) in connections)
        {
            if (!nodesByEntity.TryGetValue(
                    firstEntityId,
                    out ElectricNodeComp first)
                || !nodesByEntity.TryGetValue(
                    secondEntityId,
                    out ElectricNodeComp second))
            {
                continue;
            }

            if (electricity.TryDisconnect(
                first,
                second,
                out string failReason))
            {
                disconnected++;
            }
            else
            {
                D.Warn(
                    "Aggressive Auto Connect could not disconnect {0}<->{1}: {2}",
                    firstEntityId,
                    secondEntityId,
                    failReason);
            }
        }

        // Repair stale connection counts using the authoritative connection
        // collection before rebuilding the remaining grids.
        foreach (ElectricNodeComp nodeComp in nodesByEntity.Values)
        {
            int actualConnections = electricity.ConnectionsFor(
                nodeComp.EntityId,
                connectionEntityIds);
            if (nodeComp.Node.NumConnections == actualConnections)
            {
                continue;
            }

            ElectricNode node = nodeComp.Node;
            node.NumConnections = actualConnections;
            if (actualConnections == 0)
            {
                node.Consumed = 0f;
                node.Produced = 0f;
                node.Deficit = 0f;
            }
            node.NeedsSync = true;
            nodeComp.Update(in node);
            electricity.Update(in nodeComp, in node);
        }

        electricity.RebuildGrid(force: false);
        return disconnected;
    }

    public override void Unload()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        S.Sig.AfterLoadState.RemoveListener(OnLoadState);
        if (loadedState != null)
        {
            loadedState.Clock.OnTick.RemoveListener(OnTick);
            loadedState = null;
        }
        panel?.Dispose();
        panel = null;
        topWidget?.Dispose();
        topWidget = null;
        shortcut?.Dispose();
        shortcut = null;
    }
}

[HarmonyPatch(typeof(AIBreakdownActionLightsOut), "ActOn")]
internal static class LightsOutRecoveryPatch
{
    [HarmonyPostfix]
    private static void Postfix(Tile target)
    {
        AggressiveAutoConnectSys.RestoreLightsOutTarget(target);
    }
}
