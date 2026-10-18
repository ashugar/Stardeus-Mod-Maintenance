using System;
using System.Collections.Generic;
using System.Linq;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Utils;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private readonly Dictionary<int, MaterialWaitState> materialWaits = new();
    private readonly Dictionary<MatType, MatDeficitEventHolder>
        optionalAutoFactoryDeficits = new();
    private bool? autoFactoryAssemblyLoaded;
    private int materialWaitRoomId;
    private int optionalAutoFactoryDemandAnchorId;

    private EquipmentConstructionPreview BuildPausedRoomMaterialPreview(int roomId,
        ref EquipmentStageState state)
    {
        if (!IsGamePaused) return null;
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == roomId);
        if (room == null) return null;

        if (materialWaits.TryGetValue(roomId, out MaterialWaitState wait))
        {
            PlanEntry[] waiting = wait.Entries
                .Where(planIndex.Contains)
                .Where(entry =>
                {
                    Tile existing = ExistingAt(entry);
                    return existing?.IsConstructed != true
                        || !string.Equals(existing.DefinitionId, entry.DefId,
                            StringComparison.Ordinal);
                })
                .ToArray();
            var retained = new EquipmentConstructionPreview
            {
                RoomId = room.Id,
                RoomName = room.Name,
                PhaseName = wait.PhaseName
            };
            retained.Entries.AddRange(waiting);
            if (waiting.Length > 0)
            {
                retained.Materials.AddRange(BuildMaterialRequirements(waiting));
            }
            else
            {
                retained.Materials.AddRange(wait.Materials
                    .Where(item => item?.Type != null)
                    .Select(item => new MaterialRequirement
                    {
                        Type = item.Type,
                        Cost = item.Cost,
                        RequiredToStart = item.RequiredToStart,
                        Available = S.Sys.Inventory.AvailableCount(item.Type)
                    }));
            }
            RefreshMaterialAvailability(retained.Materials);
            return retained;
        }

        state ??= BuildEquipmentStageState();
        if (!state.IsStructureFinished(roomId)
                || state.HasUnplannedBuildings(roomId)) return null;
        PlanEntry[] stageEntries = NextEquipmentStageEntries(state, roomId);
        if (stageEntries.Length == 0) return null;

        Dictionary<string, List<Tile>> availableSources =
            BuildAvailableEquipmentSources(stageEntries, state.Occupancy);
        var construction = new List<PlanEntry>();
        foreach (PlanEntry entry in stageEntries)
        {
            if (ExistingAt(entry) != null
                    || ProtectedLocationFor(entry.DefId)
                        != ProtectedBuildingLocation.None) continue;
            Tile reusable = FindAvailableBuildingForPlan(entry, availableSources);
            if (reusable != null)
            {
                availableSources[entry.DefId].Remove(reusable);
                continue;
            }
            construction.Add(entry);
        }
        if (construction.Count == 0) return null;

        var preview = new EquipmentConstructionPreview
        {
            RoomId = room.Id,
            RoomName = room.Name,
            PhaseName = EquipmentPhaseName(EquipmentPhase(construction[0]))
        };
        preview.Entries.AddRange(construction);
        preview.Materials.AddRange(BuildMaterialRequirements(construction));
        return preview;
    }

    private bool SetRoomMaterialWait(PlanRoom room, string phaseName,
        int preferredPosition, IEnumerable<MaterialRequirement> requirements,
        IEnumerable<PlanEntry> waitingEntries,
        bool recheckAfterActiveWork = false)
    {
        if (room == null) return false;
        MaterialRequirement[] materials = requirements?
            .Where(item => item?.Type != null && item.Cost > 0)
            .Select(item => new MaterialRequirement
            {
                Type = item.Type,
                Cost = item.Cost,
                RequiredToStart = item.RequiredToStart,
                Available = item.Available
            })
            .ToArray() ?? Array.Empty<MaterialRequirement>();
        var wait = new MaterialWaitState
        {
            RoomId = room.Id,
            RoomName = room.Name,
            PhaseName = phaseName,
            Stage = ConstructionStageForMaterialPhase(phaseName),
            PreferredPosition = preferredPosition,
            RecheckAfterActiveWork = recheckAfterActiveWork,
            NextRecheckTick = G.Ticks + MaterialWaitRecheckIntervalTicks
        };
        wait.Materials.AddRange(materials);
        if (waitingEntries != null)
            wait.Entries.AddRange(waitingEntries.Where(entry => entry != null));
        if (wait.Entries.Count > 0)
            RefreshMaterialWaitSnapshot(wait,
                SnapshotOutstandingPlanMaterialCommitments());
        else
            RefreshMaterialAvailability(wait.Materials);
        if (!wait.Materials.Any(item => item.Missing > 0))
        {
            ClearRoomMaterialWait(room.Id);
            return false;
        }
        materialWaits[room.Id] = wait;
        continuousRoomId = 0;
        return RefreshOptionalAutoFactoryMaterialDemand();
    }

    private void RefreshMaterialWaits(EquipmentStageState stageState,
        long ticks)
    {
        Dictionary<MatType, int> commitments = null;
        foreach (KeyValuePair<int, MaterialWaitState> pair in
                 materialWaits.ToArray())
        {
            int roomId = pair.Key;
            bool stillIncomplete = rooms.Any(room => room.Id == roomId)
                && RoomNeedsWork(roomId, stageState);
            bool sameStage = stillIncomplete
                && ConstructionStageForRoom(roomId, stageState)
                    == pair.Value.Stage;
            if (!sameStage)
            {
                ClearRoomMaterialWait(roomId);
                continue;
            }
            bool hasActiveWork = RoomHasTrackedPlanWork(roomId);
            bool recheckDue = !hasActiveWork
                || ticks >= pair.Value.NextRecheckTick;
            if (!recheckDue) continue;

            commitments ??= SnapshotOutstandingPlanMaterialCommitments();
            RefreshMaterialWaitSnapshot(pair.Value, commitments);
            pair.Value.NextRecheckTick = ticks
                + MaterialWaitRecheckIntervalTicks;
            bool materialsReady = MaterialWaitCanResume(pair.Value);
            if (stillIncomplete && materialsReady)
            {
                ClearRoomMaterialWait(roomId);
                continue;
            }
            if (stillIncomplete && hasActiveWork)
                continue;
            if (stillIncomplete && pair.Value.RecheckAfterActiveWork)
            {
                ClearRoomMaterialWait(roomId);
                continue;
            }
            if (stillIncomplete && !materialsReady) continue;
            ClearRoomMaterialWait(roomId);
        }
    }

    private bool TryHoldForCompletedMaterialRotation(
        EquipmentStageState stageState)
    {
        if (materialWaitRoomId == 0) return false;
        if (!materialWaits.TryGetValue(materialWaitRoomId,
                out MaterialWaitState wait)
            || !RoomNeedsWork(materialWaitRoomId, stageState))
        {
            materialWaitRoomId = 0;
            return false;
        }

        bool sameOrEarlierStageReady = rooms.Any(room =>
            room.Id != wait.RoomId
            && !stagingBlockedRooms.Contains(room.Id)
            && !materialWaits.ContainsKey(room.Id)
            && !researchWaits.ContainsKey(room.Id)
            && RoomNeedsWork(room.Id, stageState)
            && ConstructionStageForRoom(room.Id, stageState) <= wait.Stage);
        if (sameOrEarlierStageReady)
        {
            materialWaitRoomId = 0;
            continuousRoomId = 0;
            return false;
        }

        continuousRoomId = materialWaitRoomId;
        ShowMaterialWaitPreview(wait);
        bool signaled = RefreshOptionalAutoFactoryMaterialDemand();
        lastStatus = "Continuous construction completed a full pass without "
            + "finding more work it can start. Waiting on " + wait.RoomName
            + " " + wait.PhaseName + ": " + MaterialWaitSummary(wait)
            + "." + AutoFactorySignalSuffix(signaled);
        return true;
    }

    private bool BeginMaterialRotationWait(EquipmentStageState stageState)
    {
        PlanRoom first = rooms
            .Where(room => materialWaits.ContainsKey(room.Id)
                && RoomNeedsWork(room.Id, stageState))
            .OrderBy(room => materialWaits[room.Id].Stage)
            .ThenBy(room => room.Order)
            .ThenBy(room => room.Id)
            .FirstOrDefault();
        if (first == null) return false;

        materialWaitRoomId = first.Id;
        continuousRoomId = first.Id;
        MaterialWaitState wait = materialWaits[first.Id];
        ShowMaterialWaitPreview(wait);
        bool signaled = RefreshOptionalAutoFactoryMaterialDemand();
        lastStatus = "Continuous construction completed a full pass without "
            + "finding more work it can start. Waiting on " + wait.RoomName
            + " " + wait.PhaseName + ": " + MaterialWaitSummary(wait)
            + "." + AutoFactorySignalSuffix(signaled);
        return true;
    }

    private string MaterialWaitSummary(MaterialWaitState wait)
    {
        bool hasActiveWork = RoomHasTrackedPlanWork(wait.RoomId);
        string[] missingDescriptions = wait.Materials
            .Where(item => MaterialRequirementStillMissing(wait, item))
            .OrderBy(item => item.Type.NameT)
            .Select(item =>
            {
                int available = item.Available;
                int missingCount = item.Missing;
                return item.Type.NameT + " missing " + missingCount + " (need "
                    + item.RequiredToStart + ", have " + available + ")";
            }).ToArray();
        if (missingDescriptions.Length > 0)
            return string.Join("; ", missingDescriptions);
        return hasActiveWork
            ? "previously queued work is still active; the next material batch will be recalculated when it finishes"
            : "the pending batch will be retried on the next construction pass";
    }

    private void RefreshMaterialWaitSnapshot(MaterialWaitState wait,
        IReadOnlyDictionary<MatType, int> commitments)
    {
        PlanEntry[] pending = wait.Entries
            .Where(planIndex.Contains)
            .Where(entry =>
            {
                Tile existing = ExistingAt(entry);
                return existing?.IsConstructed != true
                    || !string.Equals(existing.DefinitionId, entry.DefId,
                        StringComparison.Ordinal);
            })
            .ToArray();
        wait.Entries.Clear();
        wait.Entries.AddRange(pending);
        wait.Materials.Clear();
        wait.Materials.AddRange(BuildMaterialRequirements(pending));
        RefreshMaterialAvailability(wait.Materials, commitments);
    }

    private void ShowMaterialWaitPreview(MaterialWaitState wait)
    {
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == wait.RoomId);
        if (room == null || wait.Entries.Count == 0)
        {
            ClearContinuousConstructionPreview();
            return;
        }
        SetContinuousConstructionPreview(room, wait.PhaseName,
            wait.Entries, wait.Materials);
    }

    private string MaterialRequirementsSummary(
        IEnumerable<MaterialRequirement> requirements)
    {
        MaterialRequirement[] missing = requirements?
            .Where(item => item?.Type != null)
            .Select(item => new MaterialRequirement
            {
                Type = item.Type,
                Cost = item.Cost,
                RequiredToStart = item.RequiredToStart,
                Available = item.Available
            })
            .ToArray() ?? Array.Empty<MaterialRequirement>();
        RefreshMaterialAvailability(missing);
        missing = missing
            .Where(item => item.Missing > 0)
            .OrderBy(item => item.Type.NameT)
            .ToArray();
        if (missing.Length == 0) return "additional materials";
        return string.Join("; ", missing.Select(item => item.Type.NameT
            + " missing " + item.Missing + " (need "
            + item.RequiredToStart + ", have " + item.Available + ")"));
    }

    private void ClearRoomMaterialWait(int roomId)
    {
        materialWaits.Remove(roomId);
        if (materialWaitRoomId == roomId) materialWaitRoomId = 0;
    }

    private void ClearAllMaterialWaits()
    {
        materialWaits.Clear();
        materialWaitRoomId = 0;
        ClearOptionalAutoFactoryMaterialDemand();
    }

    // Auto Factory consumes Core's native construction-deficit signal. Keeping
    // this bridge on Core types avoids a required assembly reference and lets
    // Planning Overhaul run unchanged when Auto Factory is absent.
    private bool RefreshOptionalAutoFactoryMaterialDemand()
    {
        if (!Continuous || materialWaits.Count == 0
            || !IsAutoFactoryAssemblyLoaded())
        {
            ClearOptionalAutoFactoryMaterialDemand();
            return false;
        }

        MaterialWaitState[] activeWaits = materialWaits.Values
            .OrderBy(wait => rooms.FirstOrDefault(room => room.Id == wait.RoomId)
                ?.Order ?? int.MaxValue)
            .ThenBy(wait => wait.RoomId)
            .ToArray();
        MatType[] missing = activeWaits
            .SelectMany(wait => wait.Materials
                .Where(item => MaterialRequirementStillMissing(wait, item)))
            .Select(item => item.Type)
            .Distinct()
            .ToArray();
        if (missing.Length == 0)
        {
            ClearOptionalAutoFactoryMaterialDemand();
            return false;
        }

        MaterialWaitState anchorWait = activeWaits.First(wait =>
            wait.Materials.Any(item => MaterialRequirementStillMissing(
                wait, item)));
        Tile anchor = FindOptionalMaterialDemandAnchor(
            anchorWait.PreferredPosition);
        if (anchor == null)
        {
            ClearOptionalAutoFactoryMaterialDemand();
            return false;
        }

        // Recreate holders if their map anchor changed or became inactive. A
        // holder's entity is fixed by Core when it is constructed.
        if (optionalAutoFactoryDemandAnchorId != anchor.Id
            || optionalAutoFactoryDeficits.Values.Any(holder =>
                holder?.Entity?.IsActive != true))
        {
            ClearOptionalAutoFactoryMaterialDemand();
            optionalAutoFactoryDemandAnchorId = anchor.Id;
        }

        var wanted = new HashSet<MatType>(missing);
        foreach (MatType material in optionalAutoFactoryDeficits.Keys
                     .Where(material => !wanted.Contains(material)).ToArray())
        {
            optionalAutoFactoryDeficits[material].Clear();
            optionalAutoFactoryDeficits.Remove(material);
        }

        // One persistent holder per material keeps production active without
        // multiplying the same batch demand on every room or scheduler pass.
        foreach (MatType material in missing)
        {
            if (optionalAutoFactoryDeficits.ContainsKey(material)) continue;
            var owner = new PlanningMaterialDemandOwner(anchor);
            var holder = new MatDeficitEventHolder(anchor, owner,
                T.JobTypeConstruction);
            optionalAutoFactoryDeficits[material] = holder;
            holder.Notify(material);
        }
        return optionalAutoFactoryDeficits.Count > 0;
    }

    private bool MaterialRequirementStillMissing(MaterialWaitState wait,
        MaterialRequirement requirement)
    {
        if (requirement?.Type == null) return false;
        return requirement.Missing > 0;
    }

    private bool MaterialWaitCanResume(MaterialWaitState wait)
    {
        Dictionary<MatType, int> available = wait.Materials
            .Where(item => item?.Type != null)
            .GroupBy(item => item.Type)
            .ToDictionary(group => group.Key,
                group => group.Min(item => item.Available));
        foreach (PlanEntry entry in wait.Entries)
        {
            if (!planIndex.Contains(entry)) return true;
            Tile existing = ExistingAt(entry);
            if (existing?.IsConstructed == true
                    && string.Equals(existing.DefinitionId, entry.DefId,
                        StringComparison.Ordinal))
                return true;

            Dictionary<MatType, int> cost = ConstructionCosts(new[] { entry });
            if (cost.All(item => available.TryGetValue(item.Key,
                        out int count)
                    && count >= item.Value + ((item.Value + 9) / 10)))
                return true;
        }

        // Old or empty runtime wait data falls back to its aggregate gate.
        return wait.Entries.Count == 0
            || wait.Materials.All(item => item.Missing == 0);
    }

    private static string AutoFactorySignalSuffix(bool signaled)
    {
        return signaled
            ? " Auto Factory has the active Lacking materials request."
            : string.Empty;
    }

    private bool RoomHasTrackedPlanWork(int roomId)
    {
        return planConstructionRooms.Values.Any(value => value == roomId)
            || planDeconstructionRooms.Values.Any(value => value == roomId)
            || relocationTargets.Values.Any(value => value.RoomId == roomId);
    }

    private bool IsAutoFactoryAssemblyLoaded()
    {
        if (autoFactoryAssemblyLoaded.HasValue)
            return autoFactoryAssemblyLoaded.Value;
        autoFactoryAssemblyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => string.Equals(assembly?.GetName()?.Name,
                "AutoFactory", StringComparison.OrdinalIgnoreCase));
        return autoFactoryAssemblyLoaded.Value;
    }

    private Tile FindOptionalMaterialDemandAnchor(int preferredPosition)
    {
        if (preferredPosition > 0 && preferredPosition < S.GridSize)
        {
            int[] layers = { WorldLayer.Floor, WorldLayer.Walls,
                WorldLayer.Objects };
            foreach (int layer in layers)
            {
                Tile tile = EntityUtils.ParentObj(
                    EntityUtils.GetTileInLayer(preferredPosition, layer));
                if (tile?.IsActive == true) return tile;
            }
        }

        return S.Components.FindFirstMatching<ShipComputerComp>(computer =>
            computer?.Tile?.IsActive == true
            && computer.Tile.IsConstructed)?.Tile;
    }

    private void ClearOptionalAutoFactoryMaterialDemand()
    {
        foreach (MatDeficitEventHolder holder in
                 optionalAutoFactoryDeficits.Values.ToArray())
            holder?.Clear();
        optionalAutoFactoryDeficits.Clear();
        optionalAutoFactoryDemandAnchorId = 0;
    }

    private sealed class MaterialWaitState
    {
        internal int RoomId;
        internal string RoomName;
        internal string PhaseName;
        internal int Stage;
        internal int PreferredPosition;
        internal bool RecheckAfterActiveWork;
        internal long NextRecheckTick;
        internal readonly List<MaterialRequirement> Materials = new();
        internal readonly List<PlanEntry> Entries = new();
    }

    private sealed class PlanningMaterialDemandOwner : IWithEntity
    {
        public Entity Entity { get; }

        internal PlanningMaterialDemandOwner(Entity entity)
        {
            Entity = entity;
        }
    }
}
