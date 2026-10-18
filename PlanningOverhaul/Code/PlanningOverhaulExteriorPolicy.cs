using System;
using System.Collections.Generic;
using System.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using KL.Utils;

namespace PlanningOverhaul;

/// <summary>
/// Keeps the Exterior room's deliberately shallow construction policy separate
/// from the enclosed-room shell, power, atmosphere, and equipment sequence.
/// </summary>
public sealed partial class PlanningOverhaulSys
{
    private readonly struct ExteriorFrontierAdmissionState
    {
        internal ExteriorFrontierAdmissionState(int planRevision,
            int relocationLimit, int relocationCount, long relocationIdSum,
            int relocationIdXor, int constructionCount,
            long constructionIdSum, int constructionIdXor, long retryAfterTick)
        {
            PlanRevision = planRevision;
            RelocationLimit = relocationLimit;
            RelocationCount = relocationCount;
            RelocationIdSum = relocationIdSum;
            RelocationIdXor = relocationIdXor;
            ConstructionCount = constructionCount;
            ConstructionIdSum = constructionIdSum;
            ConstructionIdXor = constructionIdXor;
            RetryAfterTick = retryAfterTick;
        }

        internal int PlanRevision { get; }
        internal int RelocationLimit { get; }
        internal int RelocationCount { get; }
        internal long RelocationIdSum { get; }
        internal int RelocationIdXor { get; }
        internal int ConstructionCount { get; }
        internal long ConstructionIdSum { get; }
        internal int ConstructionIdXor { get; }
        internal long RetryAfterTick { get; }

        internal bool Matches(ExteriorFrontierAdmissionState other)
        {
            return PlanRevision == other.PlanRevision
                && RelocationLimit == other.RelocationLimit
                && RelocationCount == other.RelocationCount
                && RelocationIdSum == other.RelocationIdSum
                && RelocationIdXor == other.RelocationIdXor
                && ConstructionCount == other.ConstructionCount
                && ConstructionIdSum == other.ConstructionIdSum
                && ConstructionIdXor == other.ConstructionIdXor;
        }
    }

    private sealed class ExteriorFrontierWorkCache
    {
        // These are candidates, not permissions. A cache hit still rechecks
        // live satisfaction, floor readiness, capacity, power, and Core placement.
        // Keep the bounded expiry: player work can change outside our trackers.
        internal ExteriorFrontierAdmissionState Signature;
        internal EquipmentStageState EquipmentState;
        internal PlanEntry[] StageEntries = Array.Empty<PlanEntry>();
    }

    private const long ExteriorFrontierAdmissionRecheckTicks =
        AutomationIntervalTicks * 10;
    private readonly HashSet<int> exteriorRoomIds = new();
    private readonly Dictionary<int, ExteriorFrontierWorkCache>
        exteriorFrontierWorkCaches = new();
    private readonly Dictionary<string, bool> exteriorBuiltInSpaceByDefId =
        new(StringComparer.Ordinal);
    private float nextExteriorFrontierPerfLogTime;

    private bool IsExteriorRoom(int roomId) =>
        exteriorRoomIds.Contains(roomId);

    private void RefreshExteriorRoomIndex()
    {
        // A room can change semantic type without changing entry membership.
        // Invalidate the cached structural/post-floor classification as well.
        postFloorPlanRevision = -1;
        postFloorPlanEntries = Array.Empty<PlanEntry>();
        exteriorRoomIds.Clear();
        exteriorFrontierWorkCaches.Clear();
        exteriorBuiltInSpaceByDefId.Clear();
        foreach (PlanRoom room in rooms)
            if (room.IsExterior) exteriorRoomIds.Add(room.Id);
    }

    private void ResetExteriorFrontierAdmissionGate()
    {
        exteriorFrontierWorkCaches.Clear();
    }

    private bool IsRoomStructuralEntry(PlanEntry entry)
    {
        if (entry == null) return false;
        return IsExteriorRoom(entry.RoomId)
            ? entry.Layer == WorldLayer.Floor
            : IsStructural(entry);
    }

    private bool IsRoomPostFloorEntry(PlanEntry entry)
    {
        return entry != null && !IsRoomStructuralEntry(entry);
    }

    private IReadOnlyList<PlanEntry> RoomStructuralEntries(int roomId)
    {
        if (!IsExteriorRoom(roomId))
            return planIndex.StructuralForRoom(roomId);
        return planIndex.FloorsForRoom(roomId);
    }

    private IReadOnlyList<PlanEntry> RoomPostFloorEntries(int roomId)
    {
        if (!IsExteriorRoom(roomId))
            return planIndex.EquipmentForRoom(roomId);
        return planIndex.NonFloorsForRoom(roomId);
    }

    private bool ExteriorDestinationFloorIsReady(PlanEntry entry)
    {
        return ExteriorDestinationFloorIsReady(entry,
            requirePlannedFloorMatch: true);
    }

    private bool ExteriorRelocationDestinationFloorIsReady(PlanEntry entry)
    {
        // Exterior equipment may move into its final blueprint position as soon
        // as Core has a stable supporting floor. The Exterior floor frontier can
        // replace that floor with the planned type later without requiring an
        // unnecessary trip through staging.
        return ExteriorDestinationFloorIsReady(entry,
            requirePlannedFloorMatch: false);
    }

    private bool ExteriorDestinationFloorIsReady(PlanEntry entry,
        bool requirePlannedFloorMatch)
    {
        if (!IsExteriorRoom(entry?.RoomId ?? 0)) return true;
        if (entry.Layer == WorldLayer.Floor) return false;
        if (ExteriorDefinitionIsBuiltInSpace(entry.DefId))
            return EntryFootprint(entry).Any(HasAdjacentConstructedFloor);
        foreach (int position in EntryFootprint(entry))
        {
            Tile floor = EntityUtils.ParentObj(EntityUtils.FloorAt(position));
            if (floor?.IsConstructed != true
                    || floor.Goal?.JobIdH == JobTypeIdH.Demolition
                    || floor.PendingReplacement?.IsPending == true)
                return false;

            if (!requirePlannedFloorMatch
                    || !planEntriesByFootprint.TryGetValue(position,
                    out List<PlanEntry> planned)) continue;
            PlanEntry plannedFloor = planned.FirstOrDefault(candidate =>
                candidate.RoomId == entry.RoomId
                && candidate.Layer == WorldLayer.Floor);
            if (plannedFloor != null && !string.Equals(floor.DefinitionId,
                    plannedFloor.DefId, StringComparison.Ordinal))
                return false;
        }
        return entry.Layer != WorldLayer.Walls
            || HasConstructedWallSupport(entry);
    }

    private bool ExteriorDefinitionIsBuiltInSpace(string definitionId)
    {
        string key = definitionId ?? string.Empty;
        if (exteriorBuiltInSpaceByDefId.TryGetValue(key,
                out bool builtInSpace)) return builtInSpace;
        Def definition = The.Defs.TryGet(definitionId);
        Tile prototype = definition == null
            ? null : EntityUtils.CreatePrototype<Tile>(definition);
        builtInSpace = prototype?.Constructable?.IsBuiltInSpace == true;
        exteriorBuiltInSpaceByDefId[key] = builtInSpace;
        return builtInSpace;
    }

    private bool ExteriorHasIncompleteFloors(int roomId)
    {
        return IsExteriorRoom(roomId) && RoomStructuralEntries(roomId).Any(entry =>
        {
            Tile tile = ExistingAt(entry);
            return tile?.IsConstructed != true
                || !string.Equals(tile.DefinitionId, entry.DefId,
                    StringComparison.Ordinal);
        });
    }

    private bool ExteriorHasActivePostFloorConstruction(int roomId)
    {
        return planConstructionRooms.Any(work =>
        {
            if (work.Value != roomId) return false;
            Tile tile = S.FindEntity<Tile>(work.Key);
            return IsActivePlanConstruction(tile)
                && tile.Definition?.LayerId != WorldLayer.Floor;
        });
    }

    private bool TryCaptureExteriorFrontierAdmissionState(int roomId,
        long retryAfterTick, out ExteriorFrontierAdmissionState state)
    {
        int relocationLimit = PlannedRelocationLimit();
        int relocationCount = 0;
        long relocationIdSum = 0;
        int relocationIdXor = 0;
        foreach (int targetId in relocationTargets.Keys)
        {
            ReinstallableComp target = S.Components.Find<ReinstallableComp>(
                targetId);
            if (target?.Entity?.IsActive != true || !target.IsPendingQuick)
                continue;
            relocationCount++;
            relocationIdSum += targetId;
            relocationIdXor ^= targetId;
        }

        int constructionCount = 0;
        long constructionIdSum = 0;
        int constructionIdXor = 0;
        foreach (KeyValuePair<int, int> work in planConstructionRooms)
        {
            if (work.Value != roomId) continue;
            Tile tile = S.FindEntity<Tile>(work.Key);
            if (!IsActivePlanConstruction(tile)
                    || tile.Definition?.LayerId == WorldLayer.Floor) continue;
            constructionCount++;
            constructionIdSum += work.Key;
            constructionIdXor ^= work.Key;
        }

        state = new ExteriorFrontierAdmissionState(planIndex.Revision,
            relocationLimit, relocationCount, relocationIdSum,
            relocationIdXor, constructionCount, constructionIdSum,
            constructionIdXor, retryAfterTick);
        return constructionCount > 0;
    }

    private bool TryGetExteriorFrontierWorkCache(int roomId,
        out ExteriorFrontierWorkCache cache, out bool relocationCapacityFull)
    {
        cache = null;
        relocationCapacityFull = false;
        if (!TryCaptureExteriorFrontierAdmissionState(roomId, 0,
                out ExteriorFrontierAdmissionState current))
        {
            exteriorFrontierWorkCaches.Remove(roomId);
            return false;
        }
        if (!exteriorFrontierWorkCaches.TryGetValue(roomId,
                out ExteriorFrontierWorkCache cached)
                || !cached.Signature.Matches(current)
                || G.Ticks >= cached.Signature.RetryAfterTick)
        {
            exteriorFrontierWorkCaches.Remove(roomId);
            return false;
        }
        relocationCapacityFull = current.RelocationCount
            >= current.RelocationLimit;
        cache = cached;
        return true;
    }

    private bool CacheExteriorFrontierActiveWork(int roomId,
        EquipmentStageState equipmentState, PlanEntry[] stageEntries)
    {
        if (!TryCaptureExteriorFrontierAdmissionState(roomId,
                G.Ticks + ExteriorFrontierAdmissionRecheckTicks,
                out ExteriorFrontierAdmissionState current))
        {
            exteriorFrontierWorkCaches.Remove(roomId);
            return false;
        }
        exteriorFrontierWorkCaches[roomId] = new ExteriorFrontierWorkCache
        {
            Signature = current,
            EquipmentState = equipmentState,
            StageEntries = stageEntries ?? Array.Empty<PlanEntry>()
        };
        return true;
    }

    private bool TryAdvanceExteriorFrontierWork(PlanRoom room,
        EquipmentStageState state, out string outcome)
    {
        outcome = null;
        var perf = Stopwatch.StartNew();
        long supportMs = 0;
        long admissionGateMs = 0;
        long equipmentStateMs = 0;
        long conformanceMs = 0;
        long stageEntriesMs = 0;
        long relocationMs = 0;
        long activeWorkMs = 0;
        long constructionMs = 0;
        int stageEntryCount = 0;
        bool admissionGateHit = false;
        bool builtEquipmentState = false;
        try
        {
            if (room?.IsExterior != true) return false;

            long phaseStart = perf.ElapsedMilliseconds;
            if (TryAdvanceOptionalConstructionSupport(room, state,
                    out bool supportQueued) && supportQueued)
            {
                supportMs = perf.ElapsedMilliseconds - phaseStart;
                ResetExteriorFrontierAdmissionGate();
                RecordAutomationProgress(room.Id);
                outcome = "construct-optional-exterior-support";
                return true;
            }
            supportMs = perf.ElapsedMilliseconds - phaseStart;

            phaseStart = perf.ElapsedMilliseconds;
            admissionGateHit = TryGetExteriorFrontierWorkCache(room.Id,
                out ExteriorFrontierWorkCache cachedWork,
                out bool relocationCapacityFull);
            admissionGateMs = perf.ElapsedMilliseconds - phaseStart;
            if (admissionGateHit)
            {
                if (relocationCapacityFull)
                {
                    outcome = "exterior-capacity-and-work-gated";
                    return false;
                }

                // Reuse only the expensive discovery snapshot. Entry
                // satisfaction, floor readiness, live source state, native
                // placement validation, and relocation admission are checked
                // again before any order is issued.
                phaseStart = perf.ElapsedMilliseconds;
                PlanEntry[] cachedStageEntries = cachedWork.StageEntries
                    .Where(entry => entry?.RoomId == room.Id
                        && !IsEquipmentEntrySatisfied(entry)
                        && ExteriorDestinationFloorIsReady(entry))
                    .ToArray();
                stageEntriesMs = perf.ElapsedMilliseconds - phaseStart;
                stageEntryCount = cachedStageEntries.Length;

                phaseStart = perf.ElapsedMilliseconds;
                bool cachedRelocationProgress = false;
                bool cachedMoveHandled = cachedStageEntries.Length > 0
                    && TryMoveAvailableBuildingsIntoPlan(
                        cachedWork.EquipmentState, cachedStageEntries,
                        out _, out _, out _, out cachedRelocationProgress);
                relocationMs = perf.ElapsedMilliseconds - phaseStart;
                if (cachedMoveHandled && cachedRelocationProgress)
                {
                    exteriorFrontierWorkCaches.Remove(room.Id);
                    RecordAutomationProgress(room.Id);
                    outcome = "move-cached-ready-exterior-building";
                    return true;
                }
                outcome = "exterior-active-work-candidate-cache";
                return false;
            }

            if (state == null)
            {
                phaseStart = perf.ElapsedMilliseconds;
                state = BuildEquipmentStageState();
                equipmentStateMs = perf.ElapsedMilliseconds - phaseStart;
                builtEquipmentState = true;
            }

            phaseStart = perf.ElapsedMilliseconds;
            bool conformanceHandled = TryConformUnplannedBuildings(state,
                room.Id, out bool conformanceProgress,
                out _, out _, out _, out _);
            conformanceMs = perf.ElapsedMilliseconds - phaseStart;
            if (conformanceHandled && conformanceProgress)
            {
                exteriorFrontierWorkCaches.Remove(room.Id);
                RecordAutomationProgress(room.Id);
                outcome = "clear-ready-exterior-footprint";
                return true;
            }

            phaseStart = perf.ElapsedMilliseconds;
            PlanEntry[] stageEntries = NextEquipmentStageEntries(state, room.Id);
            stageEntriesMs = perf.ElapsedMilliseconds - phaseStart;
            stageEntryCount = stageEntries.Length;
            if (stageEntries.Length == 0)
            {
                exteriorFrontierWorkCaches.Remove(room.Id);
                return false;
            }

            phaseStart = perf.ElapsedMilliseconds;
            bool moved = TryMoveAvailableBuildingsIntoPlan(state, stageEntries,
                out HashSet<PlanEntry> relocationWaits,
                out _, out _, out bool relocationProgress);
            relocationMs = perf.ElapsedMilliseconds - phaseStart;
            if (moved && relocationProgress)
            {
                exteriorFrontierWorkCaches.Remove(room.Id);
                RecordAutomationProgress(room.Id);
                outcome = "move-ready-exterior-building";
                return true;
            }

            phaseStart = perf.ElapsedMilliseconds;
            bool activePostFloorWork =
                ExteriorHasActivePostFloorConstruction(room.Id);
            activeWorkMs = perf.ElapsedMilliseconds - phaseStart;
            if (activePostFloorWork)
            {
                bool cached = CacheExteriorFrontierActiveWork(room.Id,
                    state, stageEntries);
                outcome = cached ? "exterior-active-work-cached"
                    : "exterior-post-floor-work-active";
                return false;
            }
            exteriorFrontierWorkCaches.Remove(room.Id);

            phaseStart = perf.ElapsedMilliseconds;
            PlanEntry[] constructionEntries = stageEntries
                .Where(entry => !relocationWaits.Contains(entry)
                    && RoomAllowsEquipmentPhase(entry, state))
                .ToArray();
            bool materialBlocked = false;
            bool researchBlocked = false;
            bool advanced = constructionEntries.Length > 0
                && TryBuildPlannedEquipmentStage(constructionEntries,
                    out materialBlocked, out researchBlocked);
            constructionMs = perf.ElapsedMilliseconds - phaseStart;
            if (advanced)
            {
                RecordAutomationProgress(room.Id);
                outcome = "construct-ready-exterior-buildings";
                return true;
            }
            if (materialBlocked) outcome = "exterior-building-material-deferred";
            else if (researchBlocked) outcome = "exterior-building-research-deferred";
            return false;
        }
        finally
        {
            ReportSlowOperation("ExteriorFrontier",
                perf.ElapsedMilliseconds,
                "roomId=" + (room?.Id ?? 0)
                    + "; outcome=" + outcome
                    + "; support=" + supportMs
                    + "; admissionGate=" + admissionGateMs
                    + "; admissionGateHit=" + admissionGateHit
                    + "; equipmentState=" + equipmentStateMs
                    + "; builtEquipmentState=" + builtEquipmentState
                    + "; conformance=" + conformanceMs
                    + "; stageEntries=" + stageEntriesMs
                    + "; stageEntryCount=" + stageEntryCount
                    + "; relocation=" + relocationMs
                    + "; activeWork=" + activeWorkMs
                    + "; construction=" + constructionMs,
                ref nextExteriorFrontierPerfLogTime);
        }
    }
}
