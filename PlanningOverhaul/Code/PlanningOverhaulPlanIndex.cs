using System;
using System.Collections.Generic;
using System.Linq;
using Game.Constants;
using KL.Grid;

namespace PlanningOverhaul;

/// <summary>
/// Derived, non-persistent views over the authoritative room and entry lists.
/// Plan mutations update this index once so UI, scheduling, configuration, and
/// visual code do not independently rediscover the same relationships.
/// </summary>
internal sealed class PlanningOverhaulPlanIndex
{
    private sealed class RoomIndex
    {
        internal readonly List<PlanEntry> Entries = new();
        internal readonly List<PlanEntry> Structural = new();
        internal readonly List<PlanEntry> Equipment = new();
        internal readonly List<PlanEntry> Floors = new();
        internal readonly List<PlanEntry> NonFloors = new();
        internal readonly Dictionary<int, int> FootprintCounts = new();
        internal readonly Dictionary<int, int> FloorFootprintCounts = new();
        internal int LabelPosition = Pos.Invalid;
        internal bool LabelPositionDirty = true;
    }

    private readonly Dictionary<int, RoomIndex> rooms = new();
    private readonly Dictionary<string, List<PlanEntry>> entriesByDefinition =
        new(StringComparer.Ordinal);
    // RebuildOverlay already computes every rotated footprint. Retain the
    // immutable result so automation does not recreate Tile prototypes while
    // evaluating the same large plan every tick.
    private readonly Dictionary<PlanEntry, int[]> footprints = new();
    private readonly HashSet<PlanEntry> membership = new();
    private readonly HashSet<PlanEntry> issuedEntries = new();
    private readonly List<PlanEntry> structuralEntries = new();
    private readonly List<PlanEntry> equipmentEntries = new();
    private readonly List<PlanEntry> configuredEntries = new();
    private readonly List<PlanEntry> productionEntries = new();
    private readonly List<PlanEntry> transporterEntries = new();
    private readonly List<PlanEntry> upgradeEntries = new();
    private readonly List<PlanEntry> planterEntries = new();
    private readonly Dictionary<int, PlanEntry> transporterByPosition = new();

    internal int Revision { get; private set; }
    // Enumerate during a synchronous inspection only; mutations invalidate
    // consumers' blueprint-only views through Revision.
    internal IEnumerable<int> IndexedRoomIds => rooms.Keys;
    internal IReadOnlyCollection<PlanEntry> IssuedEntries => issuedEntries;
    internal IReadOnlyList<PlanEntry> StructuralEntries => structuralEntries;
    internal IReadOnlyList<PlanEntry> EquipmentEntries => equipmentEntries;
    internal IReadOnlyList<PlanEntry> ConfiguredEntries => configuredEntries;
    internal IReadOnlyList<PlanEntry> ProductionEntries => productionEntries;
    internal IReadOnlyList<PlanEntry> TransporterEntries => transporterEntries;
    internal IReadOnlyList<PlanEntry> UpgradeEntries => upgradeEntries;
    internal IReadOnlyList<PlanEntry> PlanterEntries => planterEntries;

    internal void Clear()
    {
        rooms.Clear();
        entriesByDefinition.Clear();
        footprints.Clear();
        membership.Clear();
        issuedEntries.Clear();
        structuralEntries.Clear();
        equipmentEntries.Clear();
        configuredEntries.Clear();
        productionEntries.Clear();
        transporterEntries.Clear();
        upgradeEntries.Clear();
        planterEntries.Clear();
        transporterByPosition.Clear();
        Revision++;
    }

    internal void Add(PlanEntry entry, IReadOnlyList<int> footprint,
        int footprintCount = -1)
    {
        if (entry == null || !membership.Add(entry)) return;
        RoomIndex room = GetOrCreateRoom(entry.RoomId);
        room.Entries.Add(entry);
        if (entry.Layer == WorldLayer.Floor) room.Floors.Add(entry);
        else room.NonFloors.Add(entry);
        if (IsStructural(entry))
        {
            room.Structural.Add(entry);
            structuralEntries.Add(entry);
        }
        else
        {
            room.Equipment.Add(entry);
            equipmentEntries.Add(entry);
        }

        if (!entriesByDefinition.TryGetValue(entry.DefId ?? string.Empty,
                out List<PlanEntry> matching))
        {
            matching = new List<PlanEntry>();
            entriesByDefinition[entry.DefId ?? string.Empty] = matching;
        }
        matching.Add(entry);
        if (entry.IssuedByPlan) issuedEntries.Add(entry);
        AddConfigurationIndexes(entry);

        int count = footprintCount < 0 ? footprint?.Count ?? 0
            : Math.Min(footprintCount, footprint?.Count ?? 0);
        if (count > 0)
        {
            var indexedFootprint = new int[count];
            for (int i = 0; i < count; i++)
                indexedFootprint[i] = footprint[i];
            footprints[entry] = indexedFootprint;
        }
        else if (entry.Position > 0)
            footprints[entry] = new[] { entry.Position };
        else
            footprints[entry] = Array.Empty<int>();
        if (count == 0 && entry.Position > 0)
            AddPosition(room.FootprintCounts, entry.Position);
        else
            for (int i = 0; i < count; i++)
            {
                int position = footprint[i];
                if (position <= 0) continue;
                AddPosition(room.FootprintCounts, position);
                if (entry.Layer == WorldLayer.Floor)
                    AddPosition(room.FloorFootprintCounts, position);
            }
        if (count == 0 && entry.Position > 0 && entry.Layer == WorldLayer.Floor)
            AddPosition(room.FloorFootprintCounts, entry.Position);
        room.LabelPositionDirty = true;
        Revision++;
    }

    internal void Remove(PlanEntry entry, IReadOnlyList<int> footprint,
        int footprintCount = -1)
    {
        if (entry == null || !membership.Remove(entry)) return;
        if (rooms.TryGetValue(entry.RoomId, out RoomIndex room))
        {
            room.Entries.Remove(entry);
            if (entry.Layer == WorldLayer.Floor) room.Floors.Remove(entry);
            else room.NonFloors.Remove(entry);
            if (IsStructural(entry)) room.Structural.Remove(entry);
            else room.Equipment.Remove(entry);

            int count = footprintCount < 0 ? footprint?.Count ?? 0
                : Math.Min(footprintCount, footprint?.Count ?? 0);
            if (count == 0 && entry.Position > 0)
                RemovePosition(room.FootprintCounts, entry.Position);
            else
                for (int i = 0; i < count; i++)
                {
                    int position = footprint[i];
                    if (position <= 0) continue;
                    RemovePosition(room.FootprintCounts, position);
                    if (entry.Layer == WorldLayer.Floor)
                        RemovePosition(room.FloorFootprintCounts, position);
                }
            if (count == 0 && entry.Position > 0
                    && entry.Layer == WorldLayer.Floor)
                RemovePosition(room.FloorFootprintCounts, entry.Position);
            room.LabelPositionDirty = true;
            if (room.Entries.Count == 0 && room.FootprintCounts.Count == 0)
                rooms.Remove(entry.RoomId);
        }

        if (entriesByDefinition.TryGetValue(entry.DefId ?? string.Empty,
                out List<PlanEntry> matching))
        {
            matching.Remove(entry);
            if (matching.Count == 0)
                entriesByDefinition.Remove(entry.DefId ?? string.Empty);
        }
        issuedEntries.Remove(entry);
        footprints.Remove(entry);
        structuralEntries.Remove(entry);
        equipmentEntries.Remove(entry);
        configuredEntries.Remove(entry);
        productionEntries.Remove(entry);
        transporterEntries.Remove(entry);
        upgradeEntries.Remove(entry);
        planterEntries.Remove(entry);
        if (entry.HasTransporterConfiguration
                && transporterByPosition.TryGetValue(entry.Position,
                    out PlanEntry indexed)
                && ReferenceEquals(indexed, entry))
            transporterByPosition.Remove(entry.Position);
        Revision++;
    }

    internal void SetIssued(PlanEntry entry, bool issued)
    {
        if (entry == null) return;
        entry.IssuedByPlan = issued;
        if (!membership.Contains(entry)) return;
        if (issued) issuedEntries.Add(entry);
        else issuedEntries.Remove(entry);
    }

    internal void ClearIssued()
    {
        foreach (PlanEntry entry in issuedEntries) entry.IssuedByPlan = false;
        issuedEntries.Clear();
    }

    internal bool Contains(PlanEntry entry) => entry != null
        && membership.Contains(entry);

    internal int[] FootprintFor(PlanEntry entry) => entry != null
        && footprints.TryGetValue(entry, out int[] footprint)
            ? footprint : Array.Empty<int>();

    internal IReadOnlyList<PlanEntry> EntriesForRoom(int roomId) =>
        rooms.TryGetValue(roomId, out RoomIndex room)
            ? room.Entries : Array.Empty<PlanEntry>();

    internal IReadOnlyList<PlanEntry> StructuralForRoom(int roomId) =>
        rooms.TryGetValue(roomId, out RoomIndex room)
            ? room.Structural : Array.Empty<PlanEntry>();

    internal IReadOnlyList<PlanEntry> EquipmentForRoom(int roomId) =>
        rooms.TryGetValue(roomId, out RoomIndex room)
            ? room.Equipment : Array.Empty<PlanEntry>();

    internal IReadOnlyList<PlanEntry> FloorsForRoom(int roomId) =>
        rooms.TryGetValue(roomId, out RoomIndex room)
            ? room.Floors : Array.Empty<PlanEntry>();

    internal IReadOnlyList<PlanEntry> NonFloorsForRoom(int roomId) =>
        rooms.TryGetValue(roomId, out RoomIndex room)
            ? room.NonFloors : Array.Empty<PlanEntry>();

    internal IReadOnlyList<PlanEntry> EntriesForDefinition(string definitionId) =>
        entriesByDefinition.TryGetValue(definitionId ?? string.Empty,
            out List<PlanEntry> matching)
            ? matching : Array.Empty<PlanEntry>();

    internal int EntryCount(int roomId) => rooms.TryGetValue(roomId,
        out RoomIndex room) ? room.Entries.Count : 0;

    internal int StructuralCount(int roomId) => rooms.TryGetValue(roomId,
        out RoomIndex room) ? room.Structural.Count : 0;

    internal int EquipmentCount(int roomId) => rooms.TryGetValue(roomId,
        out RoomIndex room) ? room.Equipment.Count : 0;

    internal PlanEntry TransporterAt(int position) =>
        transporterByPosition.TryGetValue(position, out PlanEntry entry)
            ? entry : null;

    internal bool TryGetRoomLabelPosition(int roomId, out int position)
    {
        position = Pos.Invalid;
        if (!rooms.TryGetValue(roomId, out RoomIndex room)) return false;
        if (!room.LabelPositionDirty)
        {
            position = room.LabelPosition;
            return position != Pos.Invalid;
        }

        IReadOnlyDictionary<int, int> candidates =
            room.FloorFootprintCounts.Count > 0
                ? room.FloorFootprintCounts : room.FootprintCounts;
        if (candidates.Count == 0)
        {
            room.LabelPosition = Pos.Invalid;
            room.LabelPositionDirty = false;
            return false;
        }

        double centerX = 0d;
        double centerY = 0d;
        foreach (int candidate in candidates.Keys)
        {
            Pos.ToXY(candidate, out int x, out int y);
            centerX += x;
            centerY += y;
        }
        centerX /= candidates.Count;
        centerY /= candidates.Count;

        double bestDistance = double.MaxValue;
        int best = Pos.Invalid;
        foreach (int candidate in candidates.Keys)
        {
            Pos.ToXY(candidate, out int x, out int y);
            double dx = x - centerX;
            double dy = y - centerY;
            double distance = dx * dx + dy * dy;
            if (distance < bestDistance
                    || distance == bestDistance && candidate < best)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        room.LabelPosition = best;
        room.LabelPositionDirty = false;
        position = best;
        return best != Pos.Invalid;
    }

    private RoomIndex GetOrCreateRoom(int roomId)
    {
        if (!rooms.TryGetValue(roomId, out RoomIndex room))
        {
            room = new RoomIndex();
            rooms[roomId] = room;
        }
        return room;
    }

    private void AddConfigurationIndexes(PlanEntry entry)
    {
        bool hasUpgrades = entry.InstalledUpgradeSlots.Any(id =>
            !string.IsNullOrWhiteSpace(id));
        bool configured = entry.HasTransporterConfiguration
            || entry.HasPlanterConfiguration || entry.Production != null
            || hasUpgrades;
        if (configured) configuredEntries.Add(entry);
        if (entry.Production != null) productionEntries.Add(entry);
        if (entry.HasTransporterConfiguration)
        {
            transporterEntries.Add(entry);
            transporterByPosition[entry.Position] = entry;
        }
        if (hasUpgrades) upgradeEntries.Add(entry);
        if (entry.HasPlanterConfiguration) planterEntries.Add(entry);
    }

    private static bool IsStructural(PlanEntry entry) =>
        entry.Layer == WorldLayer.Floor || entry.Layer == WorldLayer.Walls;

    private static void AddPosition(IDictionary<int, int> counts, int position)
    {
        counts[position] = counts.TryGetValue(position, out int count)
            ? count + 1 : 1;
    }

    private static void RemovePosition(IDictionary<int, int> counts, int position)
    {
        if (!counts.TryGetValue(position, out int count)) return;
        if (count <= 1) counts.Remove(position);
        else counts[position] = count - 1;
    }
}
