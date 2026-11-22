using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlanningOverhaul;

/// <summary>
/// Keeps imported and legacy-save room identities outside Planning Overhaul's
/// reserved zero value. Native plans create positive IDs, but older/external
/// blueprint producers could serialize zero or negative IDs.
/// </summary>
internal static class PlanningOverhaulRoomIdMigration
{
    internal static bool TryNormalize(PlanningBlueprintSnapshot snapshot,
        out string error)
    {
        try
        {
            Normalize(snapshot);
            error = null;
            return true;
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static Dictionary<int, int> Normalize(
        PlanningBlueprintSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

        var duplicate = snapshot.Rooms.GroupBy(room => room.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidDataException("The blueprint contains duplicate room ID "
                + duplicate.Key + ".");

        int maximumPositiveId = snapshot.Rooms.Select(room => room.Id)
            .Concat(snapshot.Entries.Select(entry => entry.RoomId))
            .Where(id => id > 0).DefaultIfEmpty(0).Max();
        int nextId = maximumPositiveId;
        var remap = new Dictionary<int, int>();

        void Reserve(int roomId)
        {
            if (roomId > 0 || remap.ContainsKey(roomId)) return;
            if (nextId == int.MaxValue)
                throw new InvalidDataException(
                    "The blueprint has no positive room ID available for migration.");
            remap[roomId] = ++nextId;
        }

        foreach (PlanRoom room in snapshot.Rooms.OrderBy(room => room.Order)
                     .ThenBy(room => room.Id))
            Reserve(room.Id);
        foreach (PlanEntry entry in snapshot.Entries)
            Reserve(entry.RoomId);
        Reserve(snapshot.CurrentRoomId);

        foreach (PlanRoom room in snapshot.Rooms)
            room.Id = Remap(room.Id, remap);
        foreach (PlanEntry entry in snapshot.Entries)
            entry.RoomId = Remap(entry.RoomId, remap);
        snapshot.CurrentRoomId = Remap(snapshot.CurrentRoomId, remap);
        return remap;
    }

    internal static int Remap(int roomId,
        IReadOnlyDictionary<int, int> remap, bool preserveZeroSentinel = false)
    {
        if (preserveZeroSentinel && roomId == 0) return 0;
        return remap != null && remap.TryGetValue(roomId, out int migrated)
            ? migrated : roomId;
    }
}
