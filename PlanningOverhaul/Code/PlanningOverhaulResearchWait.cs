using System.Collections.Generic;
using System.Linq;
using Game;
using Game.Constants;
using Game.Data;
using Game.Utils;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private readonly Dictionary<int, ResearchWaitState> researchWaits = new();
    private int researchWaitRoomId;

    private void YieldRoomForResearch(PlanRoom room, string phaseName,
        IReadOnlyCollection<PlanEntry> lockedEntries)
    {
        if (room == null) return;
        var wait = new ResearchWaitState
        {
            RoomId = room.Id,
            RoomName = room.Name,
            PhaseName = phaseName,
            Stage = ConstructionStageForMaterialPhase(phaseName),
            NextRecheckTick = G.Ticks + MaterialWaitRecheckIntervalTicks
        };
        wait.Entries.AddRange(lockedEntries
            .Where(entry => entry != null && planIndex.Contains(entry)));
        researchWaits[room.Id] = wait;
        continuousRoomId = 0;
        ClearContinuousConstructionPreview();
        lastStatus = room.Name + " " + phaseName
            + " stage is waiting for research: "
            + SummarizePlanEntries(wait.Entries) + ". "
            + "This room will remain pending while later rooms are checked.";
    }

    private void RefreshResearchWaits(EquipmentStageState stageState, long ticks)
    {
        foreach (KeyValuePair<int, ResearchWaitState> pair in
                 researchWaits.ToArray())
        {
            ResearchWaitState wait = pair.Value;
            bool sameStage = rooms.Any(room => room.Id == wait.RoomId)
                && RoomNeedsWork(wait.RoomId, stageState)
                && ConstructionStageForRoom(wait.RoomId, stageState)
                    == wait.Stage;
            bool allStillLocked = wait.Entries.Count > 0
                && wait.Entries.All(IsStillResearchLocked);
            if (!sameStage || !allStillLocked || ticks >= wait.NextRecheckTick)
                ClearRoomResearchWait(wait.RoomId);
        }
    }

    private bool IsStillResearchLocked(PlanEntry entry)
    {
        if (entry == null || !planIndex.Contains(entry)) return false;
        Tile existing = ExistingAt(entry);
        if (existing?.IsConstructed == true
                && string.Equals(existing.DefinitionId, entry.DefId,
                    System.StringComparison.Ordinal)) return false;
        var def = The.Defs.TryGet(entry.DefId);
        return def != null && !S.Research.IsDefUnlocked(def);
    }

    private bool TryHoldForCompletedResearchRotation(
        EquipmentStageState stageState)
    {
        if (researchWaitRoomId == 0) return false;
        if (!researchWaits.TryGetValue(researchWaitRoomId,
                out ResearchWaitState wait)
            || !RoomNeedsWork(researchWaitRoomId, stageState))
        {
            researchWaitRoomId = 0;
            return false;
        }

        bool anotherRoomReady = rooms.Any(room => room.Id != wait.RoomId
            && !stagingBlockedRooms.Contains(room.Id)
            && !materialWaits.ContainsKey(room.Id)
            && !researchWaits.ContainsKey(room.Id)
            && !protectedBuildingWaits.ContainsKey(room.Id)
            && !floorFrontierBlockedRooms.Contains(room.Id)
            && RoomNeedsWork(room.Id, stageState));
        if (anotherRoomReady)
        {
            researchWaitRoomId = 0;
            continuousRoomId = 0;
            return false;
        }

        continuousRoomId = wait.RoomId;
        lastStatus = "Continuous construction completed a full pass without "
            + "finding more work it can start. Waiting on " + wait.RoomName
            + " " + wait.PhaseName + " research: "
            + SummarizePlanEntries(wait.Entries)
            + ". Other rooms will be checked again when research changes or "
            + "the periodic recheck runs.";
        return true;
    }

    private bool BeginResearchRotationWait(EquipmentStageState stageState)
    {
        PlanRoom first = rooms
            .Where(room => researchWaits.ContainsKey(room.Id)
                && RoomNeedsWork(room.Id, stageState))
            .OrderBy(room => researchWaits[room.Id].Stage)
            .ThenBy(room => room.Order)
            .ThenBy(room => room.Id)
            .FirstOrDefault();
        if (first == null) return false;

        researchWaitRoomId = first.Id;
        continuousRoomId = first.Id;
        ResearchWaitState wait = researchWaits[first.Id];
        lastStatus = "Continuous construction completed a full pass without "
            + "finding more work it can start. Waiting on " + wait.RoomName
            + " " + wait.PhaseName + " research: "
            + SummarizePlanEntries(wait.Entries)
            + ". Other rooms will be checked again when research changes or "
            + "the periodic recheck runs.";
        return true;
    }

    private void ClearRoomResearchWait(int roomId)
    {
        researchWaits.Remove(roomId);
        if (researchWaitRoomId == roomId) researchWaitRoomId = 0;
    }

    private void ClearAllResearchWaits()
    {
        researchWaits.Clear();
        researchWaitRoomId = 0;
    }

    private sealed class ResearchWaitState
    {
        internal int RoomId;
        internal string RoomName;
        internal string PhaseName;
        internal int Stage;
        internal long NextRecheckTick;
        internal readonly List<PlanEntry> Entries = new();
    }
}
