using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Game.CodeGen;
using Game.Commands;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Nanobots;
using Game.Utils;
using KL.Grid;

namespace PlanningOverhaul;

/// <summary>
/// Optional construction-time infrastructure. These buildings are deliberately
/// kept outside the blueprint entry list so a convenience added to one ship is
/// not silently exported into every copy of its reusable blueprint.
/// </summary>
public sealed partial class PlanningOverhaulSys
{
    private const string NanobotHiveDefId = "Objects/Devices/NanobotHive";
    private const string RawQuantumStorageDefId =
        "Objects/Devices/QuantumStorage";
    private const string QuantumStorageComponentName = "QuantumStorageNode";
    private const int MaximumPlacementCandidates = 32;
    private const long ExteriorSupportRetryTicks = 240;

    private readonly Dictionary<int, ConstructionSupportAddition>
        constructionSupportAdditions = new();
    private readonly HashSet<int> constructionSupportEvaluatedRooms = new();
    private readonly Dictionary<int, ConstructionSupportRoomPlan>
        constructionSupportPlans = new();
    private readonly Dictionary<int, long> constructionSupportRetryAfter = new();
    private bool? optionalConstructionSupportAvailable;

    private bool IsConstructionSupportInfrastructure(PlanEntry entry)
    {
        if (entry == null) return false;
        if (string.Equals(entry.DefId, NanobotHiveDefId,
                StringComparison.Ordinal)) return true;
        Def def = The.Defs.TryGet(entry.DefId);
        return def != null
            && EntityUtils.HasComponent(def, QuantumStorageComponentName);
    }

    private bool IsQuantumStorageDefinition(string definitionId)
    {
        Def def = The.Defs.TryGet(definitionId);
        return def != null
            && EntityUtils.HasComponent(def, QuantumStorageComponentName);
    }

    private bool IsOptionalConstructionSupportAvailable()
    {
        if (optionalConstructionSupportAvailable.HasValue)
            return optionalConstructionSupportAvailable.Value;
        Def hive = The.Defs.TryGet(NanobotHiveDefId);
        Def storage = The.Defs.TryGet(RawQuantumStorageDefId);
        optionalConstructionSupportAvailable = hive != null
            && storage != null
            && EntityUtils.HasComponent(storage,
                QuantumStorageComponentName);
        return optionalConstructionSupportAvailable.Value;
    }

    private bool ConstructionSupportNeedsPass(int roomId,
        EquipmentStageState state)
    {
        if (!Continuous) return false;
        if (!IsOptionalConstructionSupportAvailable()
                || constructionSupportEvaluatedRooms.Contains(roomId)) return false;
        ConstructionSupportRoomPlan plan = ConstructionSupportPlanFor(roomId);
        if (plan.FloorPositions.Length == 0) return false;
        if (plan.IsExterior)
        {
            if (constructionSupportRetryAfter.TryGetValue(roomId,
                    out long retryAfter) && G.Ticks < retryAfter) return false;
            return !HasReadyPlannedConstructionSupport(roomId);
        }
        return state?.IsStructureFinished(roomId) == true
            && !state.HasUnplannedBuildings(roomId)
            && !state.RoomsWaitingForPower.Contains(roomId);
    }

    private bool TryAdvanceOptionalConstructionSupport(PlanRoom room,
        EquipmentStageState state, out bool queued)
    {
        queued = false;
        if (room == null || !ConstructionSupportNeedsPass(room.Id, state))
            return false;

        PruneConstructionSupportAdditions();
        bool exterior = IsExteriorRoom(room.Id);
        if (exterior ? ExteriorHasActivePostFloorConstruction(room.Id)
                : RoomHasActivePlanConstruction(room.Id)) return false;

        Def hiveDef = The.Defs.TryGet(NanobotHiveDefId);
        Def storageDef = The.Defs.TryGet(RawQuantumStorageDefId);
        if (hiveDef == null || storageDef == null)
        {
            CompleteConstructionSupportEvaluation(room.Id,
                "one or both optional building definitions are unavailable");
            return true;
        }
        if (!S.Research.IsDefUnlocked(hiveDef)
                || !S.Research.IsDefUnlocked(storageDef))
        {
            if (exterior)
                DeferExteriorConstructionSupport(room.Id);
            else
                CompleteConstructionSupportEvaluation(room.Id,
                    "Nanobot Hive and Quantum Storage research are not both unlocked");
            return true;
        }

        ConstructionSupportRoomPlan plan = ConstructionSupportPlanFor(room.Id);
        float hiveRangeSqr = ConstructionSupportHiveRangeSqr(hiveDef);
        List<int> hivePositions = ConstructionSupportPositions(room.Id,
            NanobotHiveDefId);
        List<int> storagePositions = ConstructionSupportPositions(room.Id,
            null, quantumStorage: true);

        Def requestedDef = null;
        int requestedNear = 0;
        float maximumDistanceSqr = float.MaxValue;

        // Keep each Hive close enough to a raw-material Quantum endpoint before
        // expanding exterior coverage with another pair.
        int hiveWithoutStorage = hivePositions.FirstOrDefault(hive =>
            !storagePositions.Any(storage =>
                Pos.SqrDistance(hive, storage) <= hiveRangeSqr));
        int maximumPairs = plan.IsExterior
            ? Math.Max(1, plan.TargetPositions.Length) : 1;
        int automaticStorageCount = constructionSupportAdditions.Values.Count(
            addition => addition.RoomId == room.Id
                && string.Equals(addition.DefId, RawQuantumStorageDefId,
                    StringComparison.Ordinal));
        if (hiveWithoutStorage > 0
                && automaticStorageCount < maximumPairs)
        {
            requestedDef = storageDef;
            requestedNear = hiveWithoutStorage;
            maximumDistanceSqr = hiveRangeSqr;
        }
        else
        {
            int uncoveredTarget = FindUncoveredSupportTarget(plan,
                hivePositions, hiveRangeSqr);
            if (uncoveredTarget > 0 && hivePositions.Count < maximumPairs)
            {
                requestedDef = hiveDef;
                requestedNear = uncoveredTarget;
                maximumDistanceSqr = hiveRangeSqr;
            }
        }

        if (requestedDef == null)
        {
            if (plan.IsExterior && ExteriorHasIncompleteFloors(room.Id))
                DeferExteriorConstructionSupport(room.Id);
            else
                CompleteConstructionSupportEvaluation(room.Id, null);
            return true;
        }

        if (!TryFindConstructionSupportPosition(plan, requestedDef,
                requestedNear, maximumDistanceSqr,
                requestedDef == hiveDef ? hivePositions : null,
                hiveRangeSqr,
                out int position, out int connectorPosition))
        {
            if (plan.IsExterior)
                DeferExteriorConstructionSupport(room.Id);
            else
                CompleteConstructionSupportEvaluation(room.Id,
                    "no safe powered floor position is currently available for "
                        + requestedDef.NameT);
            return true;
        }

        var supportEntry = new PlanEntry
        {
            RoomId = room.Id,
            Position = position,
            Rotation = 0,
            Layer = requestedDef.LayerId,
            DefId = requestedDef.Id
        };
        PlanEntry[] affordable = SelectAffordableConstructionEntries(
            new[] { supportEntry }, 1, out _, out _);
        if (affordable.Length == 0)
        {
            if (plan.IsExterior)
                DeferExteriorConstructionSupport(room.Id);
            else
                CompleteConstructionSupportEvaluation(room.Id,
                    "the normal material cost plus the 10% reserve is not available for "
                        + requestedDef.NameT);
            return true;
        }

        Tile prototype = EntityUtils.CreatePrototype<Tile>(requestedDef);
        if (prototype == null
                || !CanPlannerPlaceTile(position, prototype,
                    requestedDef.LayerId))
        {
            CompleteConstructionSupportEvaluation(room.Id,
                "native placement validation rejected the selected position for "
                    + requestedDef.NameT);
            return true;
        }
        if (prototype.ENode != null)
            prototype.ENode.TryConnectPosAfterBuild = connectorPosition;

        var command = new CmdPlaceTile(position, prototype, warn: true,
            instant: A.InstantBuild);
        command.Execute(S);
        Tile placed = command.PlacedTile
            ?? EntityUtils.ParentObjAt(position, requestedDef.LayerId);
        if (!command.IsSuccess || placed == null)
        {
            CompleteConstructionSupportEvaluation(room.Id,
                "Core did not accept the native construction order for "
                    + requestedDef.NameT);
            return true;
        }

        constructionSupportAdditions[placed.Id] =
            new ConstructionSupportAddition
            {
                RoomId = room.Id,
                Position = placed.PosIdx,
                DefId = requestedDef.Id
            };
        if (placed.Goal != null)
        {
            if (placed.Goal.GoalPriority <= 0)
                placed.Goal.SetPriority(G.Ticks, 3);
            planConstructionRooms[placed.Id] = room.Id;
        }
        queued = true;
        constructionSupportRetryAfter.Remove(room.Id);
        lastStatus = room.Name + (plan.IsExterior
                ? " added expanding exterior construction support: "
                : " power phase added optional construction support: ")
            + requestedDef.NameT + " at " + PositionText(position)
            + ". It uses Core's normal construction, power, material, and worker rules.";
        return true;
    }

    private void CompleteConstructionSupportEvaluation(int roomId,
        string skipReason)
    {
        constructionSupportRetryAfter.Remove(roomId);
        constructionSupportEvaluatedRooms.Add(roomId);
        if (!string.IsNullOrWhiteSpace(skipReason))
            PlanningOverhaulLog.Info("Optional construction "
                + "support skipped for Room " + roomId + ": "
                + skipReason + ". The room remains non-blocking.");
    }

    private void DeferExteriorConstructionSupport(int roomId)
    {
        constructionSupportRetryAfter[roomId] = G.Ticks
            + ExteriorSupportRetryTicks;
    }

    private bool HasReadyPlannedConstructionSupport(int roomId)
    {
        return RoomPostFloorEntries(roomId)
            .Where(IsConstructionSupportInfrastructure)
            .Any(entry => !IsEquipmentEntrySatisfied(entry)
                && ExteriorDestinationFloorIsReady(entry));
    }

    private ConstructionSupportRoomPlan ConstructionSupportPlanFor(int roomId)
    {
        if (constructionSupportPlans.TryGetValue(roomId,
                out ConstructionSupportRoomPlan cached)) return cached;

        int[] floors = plannedFloorRoomByPosition
            .Where(item => item.Value == roomId)
            .Select(item => item.Key)
            .Distinct()
            .OrderBy(position => position)
            .ToArray();
        bool exterior = IsExteriorRoom(roomId);
        int[] equipmentTargets = RoomPostFloorEntries(roomId)
            .Where(entry => !IsConstructionSupportInfrastructure(entry))
            .SelectMany(EntryFootprint)
            .Where(position => position > 0 && position < S.GridSize)
            .Distinct()
            .ToArray();
        int[] targets = exterior
            ? floors.Concat(equipmentTargets).Distinct().ToArray()
            : equipmentTargets.Length == 0 ? floors : equipmentTargets;
        cached = new ConstructionSupportRoomPlan
        {
            IsExterior = exterior,
            FloorPositions = floors,
            FloorPositionSet = new HashSet<int>(floors),
            TargetPositions = targets
        };
        constructionSupportPlans[roomId] = cached;
        return cached;
    }

    private List<int> ConstructionSupportPositions(int roomId,
        string definitionId, bool quantumStorage = false)
    {
        var positions = new List<int>();
        positions.AddRange(RoomPostFloorEntries(roomId)
            .Where(entry => quantumStorage
                ? IsQuantumStorageDefinition(entry.DefId)
                : string.Equals(entry.DefId, definitionId,
                    StringComparison.Ordinal))
            .Where(entry =>
            {
                Tile tile = ExistingAt(entry);
                return tile?.IsActive == true
                    && string.Equals(tile.DefinitionId, entry.DefId,
                        StringComparison.Ordinal)
                    && tile.IsConstructed
                    && (tile.ENode == null || tile.ENode.IsConnected);
            })
            .Select(entry => entry.Position));
        foreach (KeyValuePair<int, ConstructionSupportAddition> item in
                 constructionSupportAdditions)
        {
            ConstructionSupportAddition addition = item.Value;
            if (addition.RoomId != roomId
                    || (quantumStorage
                        ? !IsQuantumStorageDefinition(addition.DefId)
                        : !string.Equals(addition.DefId, definitionId,
                            StringComparison.Ordinal))) continue;
            Tile tile = S.FindEntity<Tile>(item.Key);
            if (tile?.IsActive == true && tile.IsConstructed
                    && (tile.ENode == null || tile.ENode.IsConnected))
                positions.Add(tile.PosIdx);
        }
        return positions.Distinct().ToList();
    }

    private int FindUncoveredSupportTarget(ConstructionSupportRoomPlan plan,
        IReadOnlyList<int> hivePositions, float hiveRangeSqr)
    {
        int[] readyTargets = plan.TargetPositions
            .Where(position => !plan.IsExterior
                || EntityUtils.ParentObj(EntityUtils.FloorAt(position))
                    ?.IsConstructed == true)
            .ToArray();
        if (readyTargets.Length == 0) return 0;
        if (hivePositions.Count == 0)
            return PositionNearestRoomCenter(readyTargets);

        int selected = 0;
        float selectedDistance = -1f;
        foreach (int target in readyTargets)
        {
            float nearest = hivePositions.Min(hive =>
                (float)Pos.SqrDistance(target, hive));
            if (nearest <= hiveRangeSqr || nearest <= selectedDistance) continue;
            selected = target;
            selectedDistance = nearest;
        }
        return selected;
    }

    private int PositionNearestRoomCenter(IReadOnlyList<int> positions)
    {
        if (positions == null || positions.Count == 0) return 0;
        long sumX = 0;
        long sumY = 0;
        foreach (int position in positions)
        {
            sumX += position / S.GridHeight;
            sumY += position % S.GridHeight;
        }
        double centerX = (double)sumX / positions.Count;
        double centerY = (double)sumY / positions.Count;
        int selected = positions[0];
        double selectedDistance = double.MaxValue;
        foreach (int position in positions)
        {
            double dx = position / S.GridHeight - centerX;
            double dy = position % S.GridHeight - centerY;
            double distance = dx * dx + dy * dy;
            if (distance >= selectedDistance) continue;
            selected = position;
            selectedDistance = distance;
        }
        return selected;
    }

    private bool TryFindConstructionSupportPosition(
        ConstructionSupportRoomPlan plan, Def definition, int nearPosition,
        float maximumDistanceSqr,
        IReadOnlyList<int> builderHivePositions, float builderRangeSqr,
        out int position, out int connectorPosition)
    {
        position = 0;
        connectorPosition = 0;
        Tile prototype = EntityUtils.CreatePrototype<Tile>(definition);
        if (prototype == null) return false;
        var nearest = new List<SupportPositionCandidate>(
            MaximumPlacementCandidates);
        HashSet<int> reservedSupportPositions =
            ConstructionSupportReservedPositions();
        foreach (int candidate in plan.FloorPositions)
        {
            if (!IsBasicConstructionSupportPositionReady(plan, prototype,
                    candidate, reservedSupportPositions))
                continue;
            int distance = Pos.SqrDistance(candidate, nearPosition);
            if (distance > maximumDistanceSqr) continue;
            if (builderHivePositions?.Count > 0
                    && !builderHivePositions.Any(hive =>
                        Pos.SqrDistance(candidate, hive) <= builderRangeSqr))
                continue;
            int insertAt = nearest.FindIndex(item => distance < item.Distance
                || distance == item.Distance && candidate < item.Position);
            if (insertAt < 0) insertAt = nearest.Count;
            if (insertAt >= MaximumPlacementCandidates) continue;
            nearest.Insert(insertAt, new SupportPositionCandidate
            {
                Position = candidate,
                Distance = distance
            });
            if (nearest.Count > MaximumPlacementCandidates)
                nearest.RemoveAt(nearest.Count - 1);
        }

        foreach (SupportPositionCandidate candidate in nearest)
        {
            if (ConstructionSupportConflictsWithPlan(prototype,
                        candidate.Position)
                    || !CanPlannerPlaceTile(candidate.Position, prototype,
                        definition.LayerId)
                    || !TryFindConstructionSupportPowerConnector(prototype,
                        candidate.Position, out int connector)) continue;
            position = candidate.Position;
            connectorPosition = connector;
            return true;
        }
        return false;
    }

    private HashSet<int> ConstructionSupportReservedPositions()
    {
        var reserved = new HashSet<int>();
        foreach (KeyValuePair<int, ConstructionSupportAddition> item in
                 constructionSupportAdditions)
        {
            Tile tile = S.FindEntity<Tile>(item.Key);
            if (tile?.IsActive == true)
            {
                reserved.UnionWith(TileFootprint(tile, tile.PosIdx));
                continue;
            }
            reserved.Add(item.Value.Position);
        }
        return reserved;
    }

    private bool IsBasicConstructionSupportPositionReady(
        ConstructionSupportRoomPlan plan, Tile prototype, int position,
        ISet<int> reservedSupportPositions)
    {
        foreach (int footprintPosition in TileFootprint(prototype, position))
        {
            if (footprintPosition <= 0 || footprintPosition >= S.GridSize
                    || !plan.FloorPositionSet.Contains(footprintPosition)
                    || S.Sys.Areas.IsOutside(footprintPosition)
                        != plan.IsExterior)
                return false;
            Tile floor = EntityUtils.ParentObj(
                EntityUtils.FloorAt(footprintPosition));
            if (floor?.IsConstructed != true
                    || EntityUtils.ParentObjAt(footprintPosition,
                        WorldLayer.Objects) != null
                    || EntityUtils.ParentObjAt(footprintPosition,
                        WorldLayer.Walls) != null
                    || indoorStaging.Contains(footprintPosition)
                    || outdoorStaging.Contains(footprintPosition)
                    || outdoorEdgeStaging.Contains(footprintPosition)
                    || reservedSupportPositions.Contains(footprintPosition))
                return false;
            if (planEntriesByFootprint.TryGetValue(footprintPosition,
                    out List<PlanEntry> planned)
                    && planned.Any(entry => entry.Layer != WorldLayer.Floor))
                return false;
        }
        foreach (FunctionalClearanceCell clearance in
                 FunctionalClearanceCells(prototype, position))
        {
            int cell = clearance.Position;
            if (cell <= 0 || cell >= S.GridSize
                    || !plan.FloorPositionSet.Contains(cell)
                    || S.Sys.Areas.IsOutside(cell) != plan.IsExterior
                    || indoorStaging.Contains(cell)
                    || outdoorStaging.Contains(cell)
                    || outdoorEdgeStaging.Contains(cell)
                    || reservedSupportPositions.Contains(cell))
                return false;
        }
        return !ConstructionSupportFunctionalClearanceConflictsWithPlan(
            prototype, position);
    }

    private bool ConstructionSupportConflictsWithPlan(Tile support,
        int position)
    {
        if (ConstructionSupportFunctionalClearanceConflictsWithPlan(
                support, position)) return true;

        var supportFootprint = new HashSet<int>(
            TileFootprint(support, position));
        var nearbyEntries = new HashSet<PlanEntry>();
        foreach (int footprintPosition in supportFootprint)
        {
            Pos.ForEachAround(footprintPosition, 1, false, false, neighbor =>
            {
                if (!S.Grid.IsWithinBounds(neighbor)) return true;
                if (planEntriesByFootprint.TryGetValue(neighbor,
                        out List<PlanEntry> planned))
                    foreach (PlanEntry entry in planned)
                        if (entry.Layer != WorldLayer.Floor)
                            nearbyEntries.Add(entry);
                return true;
            });
        }

        var prototypes = new Dictionary<(string DefId, int Rotation), Tile>();
        bool supportWorkSpotCompatible = support.Transform.IsWorkSpotCompatible;
        foreach (PlanEntry entry in nearbyEntries)
        {
            Tile planned = PlannedEntryPrototype(entry, prototypes);
            if (planned == null) continue;
            foreach (FunctionalClearanceCell clearance in
                     FunctionalClearanceCells(planned, entry.Position))
            {
                if (!supportFootprint.Contains(clearance.Position)
                        || clearance.Kind == FunctionalClearanceKind.WorkSpot
                            && supportWorkSpotCompatible) continue;
                return true;
            }
        }
        return false;
    }

    private bool ConstructionSupportFunctionalClearanceConflictsWithPlan(
        Tile support, int position)
    {
        foreach (FunctionalClearanceCell clearance in
                 FunctionalClearanceCells(support, position))
        {
            if (!planEntriesByFootprint.TryGetValue(clearance.Position,
                    out List<PlanEntry> planned)) continue;
            if (planned.Any(entry =>
                    PlannedEntryBlocksFunctionalClearance(entry,
                        clearance.Kind))) return true;
        }
        return false;
    }

    private bool PlannedEntryBlocksFunctionalClearance(PlanEntry entry,
        FunctionalClearanceKind kind)
    {
        if (entry?.Layer == WorldLayer.Floor) return false;
        if (kind != FunctionalClearanceKind.WorkSpot
                || entry.Layer != WorldLayer.Objects) return true;
        Def def = The.Defs.TryGet(entry.DefId);
        Tile prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
        if (prototype == null) return true;
        prototype.Transform.Rotation = (Facing.Type)entry.Rotation;
        return !prototype.Transform.IsWorkSpotCompatible;
    }

    private Tile PlannedEntryPrototype(PlanEntry entry,
        IDictionary<(string DefId, int Rotation), Tile> cache)
    {
        var key = (entry?.DefId ?? string.Empty, entry?.Rotation ?? 0);
        if (cache.TryGetValue(key, out Tile prototype)) return prototype;
        Def def = The.Defs.TryGet(entry?.DefId);
        prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
        if (prototype != null)
            prototype.Transform.Rotation = (Facing.Type)entry.Rotation;
        cache[key] = prototype;
        return prototype;
    }

    private bool TryFindConstructionSupportPowerConnector(Tile prototype,
        int destination, out int connectorPosition)
    {
        connectorPosition = 0;
        ElectricNodeComp sourceNode = prototype?.ENode;
        if (sourceNode == null) return true;

        ElectricNodeComp best = null;
        int bestDistance = int.MaxValue;
        foreach (ElectricNodeComp candidate in
                 S.Components.AllByType<ElectricNodeComp>())
        {
            if (!IsPoweredRelocationConnector(candidate, sourceNode,
                    destination, alreadyConnected: false)) continue;
            int distance = Pos.SqrDistance(destination, candidate.PosIdx);
            if (distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        if (best == null) return false;
        connectorPosition = best.PosIdx;
        return true;
    }

    private float ConstructionSupportHiveRangeSqr(Def hiveDef)
    {
        Tile prototype = EntityUtils.CreatePrototype<Tile>(hiveDef);
        return prototype != null
                && prototype.TryGetComponent(out NanobotHiveComp hive)
            ? hive.RangeSqr : 400f;
    }

    private bool IsConstructionSupportTile(Tile tile)
    {
        if (tile == null
                || !constructionSupportAdditions.TryGetValue(tile.Id,
                    out ConstructionSupportAddition addition)
                || !string.Equals(addition.DefId, tile.DefinitionId,
                    StringComparison.Ordinal)) return false;
        if (ConstructionSupportConflictsWithPlan(tile, tile.PosIdx))
            return false;
        // A later plan edit is allowed to reclaim this location. In that case
        // the helper becomes an ordinary clearing/reuse candidate instead of
        // being permanently protected from the new blueprint.
        return TileFootprint(tile, tile.PosIdx).All(position =>
            !planEntriesByFootprint.TryGetValue(position,
                out List<PlanEntry> planned)
            || planned.All(entry => entry.Layer == WorldLayer.Floor
                || entry.Layer == tile.Definition.LayerId
                    && string.Equals(entry.DefId, tile.DefinitionId,
                        StringComparison.Ordinal)
                    && entry.Position == tile.PosIdx));
    }

    private int PendingConstructionSupportPowerReservations(
        int connectorPosition)
    {
        int reserved = 0;
        foreach (int entityId in constructionSupportAdditions.Keys)
        {
            Tile tile = S.FindEntity<Tile>(entityId);
            if (tile?.IsActive == true && !tile.IsConstructed
                    && tile.ENode?.TryConnectPosAfterBuild == connectorPosition)
                reserved++;
        }
        return reserved;
    }

    private void ApplyConstructionSupportGoalPriorities(long ticks)
    {
        foreach (int entityId in constructionSupportAdditions.Keys.ToArray())
        {
            Tile tile = S.FindEntity<Tile>(entityId);
            if (tile?.IsActive != true || tile.IsConstructed
                    || tile.Goal == null) continue;
            if (Continuous)
            {
                if (tile.Goal.GoalPriority <= 0)
                    tile.Goal.SetPriority(ticks, 3);
            }
            else if (tile.Goal.Agent == null && tile.Goal.GoalPriority != 0)
                tile.Goal.SetPriority(ticks, 0);
        }
    }

    private void PruneConstructionSupportAdditions()
    {
        foreach (int entityId in constructionSupportAdditions.Keys.ToArray())
        {
            ConstructionSupportAddition addition =
                constructionSupportAdditions[entityId];
            Tile tile = S.FindEntity<Tile>(entityId);
            bool retained = tile?.IsActive == true
                && string.Equals(tile.DefinitionId, addition.DefId,
                    StringComparison.Ordinal)
                && (tile.IsConstructed || tile.Goal != null);
            if (retained)
            {
                addition.Position = tile.PosIdx;
                continue;
            }
            constructionSupportAdditions.Remove(entityId);
            // A cancelled optional helper remains a player choice. It is only
            // reconsidered when Continuous construction is stopped and started.
            constructionSupportEvaluatedRooms.Add(addition.RoomId);
        }
    }

    private void ResetConstructionSupportEvaluation()
    {
        constructionSupportEvaluatedRooms.Clear();
        constructionSupportRetryAfter.Clear();
    }

    private void ReleaseConstructionSupportOwnership()
    {
        constructionSupportAdditions.Clear();
        constructionSupportEvaluatedRooms.Clear();
        constructionSupportPlans.Clear();
        constructionSupportRetryAfter.Clear();
    }

    private void ReleaseConstructionSupportOwnership(int roomId)
    {
        foreach (int entityId in constructionSupportAdditions
                     .Where(item => item.Value.RoomId == roomId)
                     .Select(item => item.Key).ToArray())
            constructionSupportAdditions.Remove(entityId);
        constructionSupportEvaluatedRooms.Remove(roomId);
        constructionSupportPlans.Remove(roomId);
        constructionSupportRetryAfter.Remove(roomId);
    }

    private void InvalidateConstructionSupportPlan(
        IEnumerable<int> roomIds = null)
    {
        if (roomIds == null)
        {
            constructionSupportPlans.Clear();
            constructionSupportRetryAfter.Clear();
            return;
        }
        foreach (int roomId in roomIds.Distinct())
        {
            constructionSupportPlans.Remove(roomId);
            constructionSupportEvaluatedRooms.Remove(roomId);
            constructionSupportRetryAfter.Remove(roomId);
        }
    }

    private void SaveConstructionSupportState(ComponentData data)
    {
        PruneConstructionSupportAdditions();
        data.SetString("ConstructionSupportAdditions", string.Join(";",
            constructionSupportAdditions.Select(item => item.Key + ","
                + item.Value.RoomId + "," + item.Value.Position + ","
                + item.Value.DefId)));
        data.SetString("ConstructionSupportEvaluatedRooms", string.Join(",",
            constructionSupportEvaluatedRooms.OrderBy(id => id)));
    }

    private void LoadConstructionSupportState(ComponentData data)
    {
        constructionSupportAdditions.Clear();
        constructionSupportEvaluatedRooms.Clear();
        constructionSupportPlans.Clear();
        constructionSupportRetryAfter.Clear();
        string encoded = data.GetString("ConstructionSupportAdditions",
            string.Empty);
        foreach (string item in encoded.Split(new[] { ';' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = item.Split(',');
            if (fields.Length != 4
                    || !int.TryParse(fields[0], out int entityId)
                    || !int.TryParse(fields[1], out int roomId)
                    || !int.TryParse(fields[2], out int position)
                    || string.IsNullOrWhiteSpace(fields[3])) continue;
            constructionSupportAdditions[entityId] =
                new ConstructionSupportAddition
                {
                    RoomId = roomId,
                    Position = position,
                    DefId = fields[3]
                };
        }
        string evaluated = data.GetString(
            "ConstructionSupportEvaluatedRooms", string.Empty);
        foreach (string item in evaluated.Split(new[] { ',' },
                     StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(item, out int roomId))
                constructionSupportEvaluatedRooms.Add(roomId);
    }

    private sealed class ConstructionSupportAddition
    {
        internal int RoomId;
        internal int Position;
        internal string DefId;
    }

    private sealed class ConstructionSupportRoomPlan
    {
        internal bool IsExterior;
        internal int[] FloorPositions = Array.Empty<int>();
        internal HashSet<int> FloorPositionSet = new();
        internal int[] TargetPositions = Array.Empty<int>();
    }

    private sealed class SupportPositionCandidate
    {
        internal int Position;
        internal int Distance;
    }
}
