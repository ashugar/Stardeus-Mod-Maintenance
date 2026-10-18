using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Game.Constants;
using Game.Data;

namespace PlanningOverhaul;

/// <summary>
/// Reconciles already-displaced equipment with ready final blueprint
/// destinations before room-local construction advances. Destination phase
/// rules remain authoritative; staging is still handled by the clearing path
/// only when a building must vacate planned construction or clearance.
/// </summary>
public sealed partial class PlanningOverhaulSys
{
    private const int EquipmentReconciliationCandidateLimit = 64;
    private readonly Dictionary<int, int> liveEquipmentReadyPhaseByRoom = new();
    private GameState liveEquipmentReadinessState;
    private long liveEquipmentReadinessTick = long.MinValue;
    private int liveEquipmentReadinessPlanRevision = -1;
    private int equipmentReconciliationCursor;

    private bool TryAdvanceEquipmentReconciliation(out string outcome)
    {
        outcome = null;
        if (!Continuous || stagedBuildings.Count == 0) return false;
        if (!TryReturnStagedBuildings(0, null, directOnly: true)) return false;
        outcome = "reconcile-ready-equipment-destinations";
        return true;
    }

    private int[] EquipmentReconciliationCandidates()
    {
        int[] candidates = stagedBuildings.Keys.OrderBy(id => id).ToArray();
        if (candidates.Length <= EquipmentReconciliationCandidateLimit)
        {
            equipmentReconciliationCursor = 0;
            return candidates;
        }

        int start = equipmentReconciliationCursor % candidates.Length;
        int[] batch = new int[EquipmentReconciliationCandidateLimit];
        for (int i = 0; i < batch.Length; i++)
            batch[i] = candidates[(start + i) % candidates.Length];
        equipmentReconciliationCursor =
            (start + batch.Length) % candidates.Length;
        return batch;
    }

    private bool DestinationAllowsEquipmentPhase(PlanEntry entry,
        EquipmentStageState state)
    {
        if (entry == null || entry.Layer != WorldLayer.Objects) return true;
        if (IsExteriorRoom(entry.RoomId))
            return ExteriorRelocationDestinationFloorIsReady(entry);
        if (state != null)
            return state.IsStructureFinished(entry.RoomId)
                && RoomAllowsEquipmentPhase(entry, state);
        return EquipmentPhase(entry) <= LiveEquipmentReadyPhase(entry.RoomId);
    }

    private int LiveEquipmentReadyPhase(int roomId)
    {
        if (!ReferenceEquals(liveEquipmentReadinessState, S)
                || liveEquipmentReadinessTick != G.Ticks
                || liveEquipmentReadinessPlanRevision != planIndex.Revision)
        {
            liveEquipmentReadinessState = S;
            liveEquipmentReadinessTick = G.Ticks;
            liveEquipmentReadinessPlanRevision = planIndex.Revision;
            liveEquipmentReadyPhaseByRoom.Clear();
        }
        if (liveEquipmentReadyPhaseByRoom.TryGetValue(roomId,
                out int cached)) return cached;

        int readyPhase = -1;
        if (IsRoomStructureFinished(roomId))
        {
            readyPhase = 0;
            IReadOnlyList<PlanEntry> roomEntries = RoomPostFloorEntries(roomId);
            if (!roomEntries.Any(entry => EquipmentPhase(entry) == 0
                    && !IsPowerInfrastructureReady(entry)))
            {
                readyPhase = 1;
                if (!roomEntries.Any(entry => EquipmentPhase(entry) == 1
                        && !IsEquipmentEntrySatisfied(entry)))
                {
                    readyPhase = 2;
                    if (!roomEntries.Any(entry => EquipmentPhase(entry) == 2
                            && !IsEquipmentEntrySatisfied(entry)))
                        readyPhase = 3;
                }
            }
        }
        liveEquipmentReadyPhaseByRoom[roomId] = readyPhase;
        return readyPhase;
    }
}
