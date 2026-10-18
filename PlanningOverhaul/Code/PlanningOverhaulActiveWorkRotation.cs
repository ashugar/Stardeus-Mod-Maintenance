using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game;
using Game.Components;
using Game.Data;
using Game.Systems.AI;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private readonly Dictionary<int, string> activePlanWorkWaits = new();
    private bool activeWorkRotationObservedChange;
    private string warnedActiveWorkFingerprint;

    private void ResetActiveWorkRotation(bool clearWarning)
    {
        activePlanWorkWaits.Clear();
        activeWorkRotationObservedChange = false;
        if (clearWarning) warnedActiveWorkFingerprint = null;
    }

    private void RecordActiveWorkRotationChange()
    {
        activeWorkRotationObservedChange = true;
        warnedActiveWorkFingerprint = null;
    }

    private void RefreshActiveWorkWaits(EquipmentStageState state)
    {
        if (activePlanWorkWaits.Count == 0) return;
        Dictionary<int, string> currentFingerprints =
            SnapshotActivePlanWorkFingerprints();
        foreach (KeyValuePair<int, string> wait in activePlanWorkWaits.ToArray())
        {
            bool hasFingerprint = currentFingerprints.TryGetValue(wait.Key,
                out string current);
            bool stillWaiting = rooms.Any(room => room.Id == wait.Key)
                && RoomNeedsWork(wait.Key, state)
                && hasFingerprint;
            if (stillWaiting && string.Equals(current, wait.Value,
                    System.StringComparison.Ordinal)) continue;

            activePlanWorkWaits.Remove(wait.Key);
            RecordActiveWorkRotationChange();
        }
    }

    private void YieldRoomForActivePlanWork(int roomId, string description)
    {
        if (roomId == 0 || !RoomHasTrackedPlanWork(roomId)) return;
        string fingerprint = ActivePlanWorkFingerprint(roomId);
        if (fingerprint.Length == 0) return;

        activePlanWorkWaits[roomId] = fingerprint;
        continuousRoomId = 0;
        ClearContinuousConstructionPreview();
        lastStatus = (string.IsNullOrWhiteSpace(description)
                ? DescribePendingPlanWork(roomId) : description)
            + " Active work is already in Core's task list, so later rooms "
            + "will be checked before this room is revisited.";
    }

    private bool RoomHasActivePlanConstruction(int roomId)
    {
        return planConstructionRooms.Any(work => work.Value == roomId
            && IsActivePlanConstruction(S.FindEntity<Tile>(work.Key)));
    }

    private static bool IsActivePlanConstruction(Tile tile)
    {
        return tile?.IsActive == true && tile.Goal != null
            && (!tile.IsConstructed
                || tile.PendingReplacement?.IsPending == true);
    }

    private string DescribeActivePlanConstructionWait(int roomId)
    {
        int active = planConstructionRooms.Count(work => work.Value == roomId
            && IsActivePlanConstruction(S.FindEntity<Tile>(work.Key)));
        string roomName = rooms.FirstOrDefault(room => room.Id == roomId)?.Name
            ?? "The active room";
        return roomName + " has " + active + " construction order"
            + (active == 1 ? string.Empty : "s") + " in progress. "
            + "Eligible equipment moves were still checked, but no additional "
            + "construction will be queued in this room until those orders finish.";
    }

    private bool FinishActiveWorkRotation(EquipmentStageState state,
        out bool warned)
    {
        warned = false;
        if (activePlanWorkWaits.Count == 0) return false;
        Dictionary<int, string> currentFingerprints =
            SnapshotActivePlanWorkFingerprints();
        int[] waitingRooms = activePlanWorkWaits.Keys
            .Where(roomId => rooms.Any(room => room.Id == roomId)
                && RoomNeedsWork(roomId, state)
                && currentFingerprints.ContainsKey(roomId))
            .OrderBy(roomId => state.OrderFor(roomId))
            .ThenBy(roomId => roomId)
            .ToArray();
        if (waitingRooms.Length == 0) return false;

        string fingerprint = string.Join(";", waitingRooms.Select(roomId =>
            roomId + "=" + currentFingerprints[roomId]));
        bool observedChange = activeWorkRotationObservedChange;
        activePlanWorkWaits.Clear();
        activeWorkRotationObservedChange = false;
        continuousRoomId = 0;
        continuousRoomCursorId = 0;

        string roomNames = string.Join(", ", waitingRooms.Select(roomId =>
            rooms.FirstOrDefault(room => room.Id == roomId)?.Name
                ?? "Room " + roomId));
        if (observedChange)
        {
            lastStatus = "Continuous construction completed a full room pass "
                + "after queueing work or observing task progress. Starting "
                + "another pass while active plan tasks remain in: "
                + roomNames + ".";
            return true;
        }

        warned = true;
        lastStatus = "Continuous construction completed a full room pass "
            + "without queueing additional work or observing changes in active "
            + "plan tasks. Investigate the remaining tasks for: " + roomNames
            + ". Stop and start Continuous Construction to clear the cache.\n"
            + DescribePendingPlanWork(waitingRooms[0]);
        if (!string.Equals(warnedActiveWorkFingerprint, fingerprint,
                System.StringComparison.Ordinal))
        {
            warnedActiveWorkFingerprint = fingerprint;
            The.Dialog.ShowSimple(Game.Constants.IconId.CWarning, lastStatus);
        }
        return true;
    }

    private string ActivePlanWorkFingerprint(int roomId)
    {
        var parts = new List<string>();
        foreach (var work in PendingPlanWork())
            if (work.RoomId == roomId)
                parts.Add(ActivePlanWorkFingerprint(work.Kind,
                    work.EntityId, work.RoomId));
        parts.Sort(System.StringComparer.Ordinal);
        return string.Join("|", parts);
    }

    private Dictionary<int, string> SnapshotActivePlanWorkFingerprints()
    {
        var partsByRoom = new Dictionary<int, List<string>>();
        foreach (var work in PendingPlanWork())
        {
            if (!partsByRoom.TryGetValue(work.RoomId, out List<string> parts))
            {
                parts = new List<string>();
                partsByRoom[work.RoomId] = parts;
            }
            parts.Add(ActivePlanWorkFingerprint(work.Kind,
                work.EntityId, work.RoomId));
        }

        var fingerprints = new Dictionary<int, string>();
        foreach (KeyValuePair<int, List<string>> room in partsByRoom)
        {
            room.Value.Sort(System.StringComparer.Ordinal);
            fingerprints[room.Key] = string.Join("|", room.Value);
        }
        return fingerprints;
    }

    private string ActivePlanWorkFingerprint(string kind, int entityId,
        int roomId)
    {
        Tile tile = S.FindEntity<Tile>(entityId);
        ReinstallableComp relocation = kind == "Relocation"
            ? S.Components.Find<ReinstallableComp>(entityId) : null;
        Tile source = relocation?.SourceId > 0
            ? S.FindEntity<Tile>(relocation.SourceId) : null;
        AIGoal goal = tile?.Goal ?? relocation?.Entity?.Goal;
        return kind + ":" + entityId + ":" + roomId
            + ":target=" + (tile?.PosIdx ?? -1)
            + ":source=" + (source?.PosIdx ?? -1)
            + ":targetWork=" + WorkFingerprint(tile)
            + ":sourceWork=" + WorkFingerprint(source)
            + ":state=" + (goal == null ? "none" : goal.State.ToString())
            + ":stateTick=" + (goal?.StateStartTick ?? 0)
            + ":agent=" + (goal?.Agent?.EntityId ?? 0)
            + ":agentPos=" + (goal?.Agent?.PosIdx ?? -1)
            + ":priority=" + (goal?.GoalPriority ?? 0)
            + ":failure=" + (goal?.FailReason ?? string.Empty);
    }

    private static string WorkFingerprint(Tile tile)
    {
        return tile?.Constructable == null ? "none"
            : tile.Constructable.WorkDone.ToString("R",
                CultureInfo.InvariantCulture);
    }
}
