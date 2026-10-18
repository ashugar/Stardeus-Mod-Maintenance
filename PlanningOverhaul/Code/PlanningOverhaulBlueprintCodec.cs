using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PlanningOverhaul;

/// <summary>
/// Versioned, integrity-checked blueprint serialization. Kept independent of
/// live world state so save compatibility can be regression-tested off-game.
/// </summary>
internal static class BlueprintCodec
{
    private const string Prefix = "PO5";

    internal static string Encode(PlanningBlueprintSnapshot snapshot)
    {
        if (!BlueprintGeometry.ValidMapSize(snapshot.MapWidth, snapshot.MapHeight))
            throw new InvalidDataException("The blueprint needs its original map dimensions.");
        var lines = new List<string>
        {
            "N|" + B64(snapshot.Name ?? "Construction Plan"),
            "C|" + snapshot.CurrentRoomId,
            "M|" + snapshot.MapWidth + "|" + snapshot.MapHeight
        };
        lines.AddRange(snapshot.Rooms.OrderBy(room => room.Order).ThenBy(room => room.Id)
            .Select(room => "R|" + room.Id + "|" + room.Order + "|"
                + (room.IsExterior ? "1" : "0") + "|"
                + B64(room.Name ?? "Room")));
        // The wire format puts every building before its metadata. Sort once
        // and reuse that stable order for both sections (including duplicate keys).
        PlanEntry[] orderedEntries = snapshot.Entries.OrderBy(entry => entry.RoomId)
            .ThenBy(entry => entry.Layer).ThenBy(entry => entry.Position).ToArray();
        lines.AddRange(orderedEntries.Select(entry => "E|" + entry.RoomId + "|" + entry.Position + "|"
                + entry.Rotation + "|" + entry.Layer + "|"
                + (entry.IssuedByPlan ? "1" : "0") + "|" + B64(entry.DefId)));
        foreach (PlanEntry entry in orderedEntries)
        {
            if (entry.HasTransporterConfiguration)
                lines.Add("T|" + entry.RoomId + "|" + entry.Position + "|"
                    + entry.Layer + "|" + entry.TransporterOutboundPosition + "|"
                    + B64(entry.TransporterLabel));
            if (entry.HasPlanterConfiguration)
                lines.Add("G|" + entry.RoomId + "|" + entry.Position + "|"
                    + entry.Layer + "|" + (entry.PlanterAutoHarvest ? "1" : "0")
                    + "|" + B64(entry.PlanterPlantDefId));
            for (int slot = 0; slot < entry.InstalledUpgradeSlots.Count; slot++)
            {
                string upgradeId = entry.InstalledUpgradeSlots[slot];
                if (!string.IsNullOrWhiteSpace(upgradeId))
                    lines.Add("U|" + entry.RoomId + "|" + entry.Position + "|"
                        + entry.Layer + "|" + slot + "|" + B64(upgradeId));
            }
            if (entry.Production == null) continue;
            lines.Add("P|" + entry.RoomId + "|" + entry.Position + "|"
                + entry.Layer + "|" + entry.Production.TaskPriority + "|"
                + (entry.Production.IdleWarning ? "1" : "0") + "|"
                + (entry.Production.IgnoreUnconfigured ? "1" : "0"));
            foreach (ProductionOrderPlan order in entry.Production.Orders
                         .OrderBy(item => item.Order))
                lines.Add("O|" + entry.RoomId + "|" + entry.Position + "|"
                    + entry.Layer + "|" + order.Order + "|" + order.DemandType + "|"
                    + order.AmountWanted + "|" + order.BatchSize + "|"
                    + (order.Paused ? "1" : "0") + "|"
                    + (order.SuppressDeficitWarning ? "1" : "0") + "|"
                    + B64(order.CraftableId) + "|" + B64(order.ColorId));
        }
        foreach (KeyValuePair<StagingAreaType, HashSet<int>> area in snapshot.Staging
                     .OrderBy(item => (int)item.Key))
            lines.AddRange(area.Value.OrderBy(position => position)
                .Select(position => "S|" + (int)area.Key + "|" + position));

        byte[] raw = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var stream = new DeflateStream(output,
                       System.IO.Compression.CompressionLevel.Optimal, true))
                stream.Write(raw, 0, raw.Length);
            compressed = output.ToArray();
        }
        return Prefix + ":" + Hash(compressed) + ":" + Convert.ToBase64String(compressed);
    }

    internal static bool TryDecode(string code, out PlanningBlueprintSnapshot snapshot,
        out string error)
    {
        snapshot = null;
        error = null;
        try
        {
            string[] parts = (code ?? string.Empty).Trim().Split(':');
            if (parts.Length != 3 || (parts[0] != Prefix && parts[0] != "PO4"
                    && parts[0] != "PO3" && parts[0] != "PO2"
                    && parts[0] != "PO1"))
                throw new InvalidDataException("This is not a Planning Overhaul blueprint code.");
            byte[] compressed = Convert.FromBase64String(parts[2]);
            if (!string.Equals(parts[1], Hash(compressed), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The blueprint integrity check failed.");
            string payload;
            using (var input = new MemoryStream(compressed))
            using (var stream = new DeflateStream(input, CompressionMode.Decompress))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                payload = reader.ReadToEnd();

            var parsed = new PlanningBlueprintSnapshot();
            // Metadata refers to the latest preceding entry with this key.
            // Retain that legacy behavior without a reverse scan per setting.
            var entriesByKey = new Dictionary<(int Room, int Position, int Layer), PlanEntry>();
            bool hasMapSize = false;
            foreach (string line in payload.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.TrimEnd('\r').Split('|');
                if (fields[0] == "N" && fields.Length == 2)
                    parsed.Name = FromB64(fields[1]);
                else if (fields[0] == "C" && fields.Length == 2)
                    parsed.CurrentRoomId = int.Parse(fields[1]);
                else if (fields[0] == "M")
                {
                    if (hasMapSize || fields.Length != 3)
                        throw new InvalidDataException("Invalid blueprint map dimensions.");
                    parsed.MapWidth = int.Parse(fields[1]);
                    parsed.MapHeight = int.Parse(fields[2]);
                    if (!BlueprintGeometry.ValidMapSize(parsed.MapWidth, parsed.MapHeight))
                        throw new InvalidDataException("Invalid blueprint map dimensions.");
                    hasMapSize = true;
                }
                else if (fields[0] == "R" && fields.Length == 5)
                    parsed.Rooms.Add(new PlanRoom
                    {
                        Id = int.Parse(fields[1]),
                        Order = int.Parse(fields[2]),
                        IsExterior = fields[3] == "1",
                        Name = FromB64(fields[4])
                    });
                else if (fields[0] == "R" && fields.Length == 4)
                {
                    string roomName = FromB64(fields[3]);
                    parsed.Rooms.Add(new PlanRoom
                    {
                        Id = int.Parse(fields[1]),
                        Order = int.Parse(fields[2]),
                        Name = roomName,
                        IsExterior = string.Equals(roomName,
                            "Exterior", StringComparison.OrdinalIgnoreCase)
                    });
                }
                else if (fields[0] == "E" && fields.Length == 7)
                {
                    var entry = new PlanEntry
                    {
                        RoomId = int.Parse(fields[1]),
                        Position = int.Parse(fields[2]),
                        Rotation = int.Parse(fields[3]),
                        Layer = int.Parse(fields[4]),
                        IssuedByPlan = fields[5] == "1",
                        DefId = FromB64(fields[6])
                    };
                    parsed.Entries.Add(entry);
                    entriesByKey[(entry.RoomId, entry.Position, entry.Layer)] = entry;
                }
                else if (fields[0] == "T" && fields.Length == 6)
                {
                    PlanEntry entry = FindEntry(entriesByKey, fields);
                    entry.HasTransporterConfiguration = true;
                    entry.TransporterOutboundPosition = int.Parse(fields[4]);
                    entry.TransporterLabel = FromB64(fields[5]);
                }
                else if (fields[0] == "G" && fields.Length == 6)
                {
                    PlanEntry entry = FindEntry(entriesByKey, fields);
                    entry.HasPlanterConfiguration = true;
                    entry.PlanterAutoHarvest = fields[4] == "1";
                    entry.PlanterPlantDefId = FromB64(fields[5]);
                }
                else if (fields[0] == "U" && fields.Length == 6)
                {
                    PlanEntry entry = FindEntry(entriesByKey, fields);
                    int slot = int.Parse(fields[4]);
                    if (slot < 0 || slot > 255)
                        throw new InvalidDataException("Invalid installed upgrade slot.");
                    while (entry.InstalledUpgradeSlots.Count <= slot)
                        entry.InstalledUpgradeSlots.Add(null);
                    entry.InstalledUpgradeSlots[slot] = FromB64(fields[5]);
                }
                else if (fields[0] == "P" && fields.Length == 7)
                {
                    PlanEntry entry = FindEntry(entriesByKey, fields);
                    entry.Production = new ProductionPlan
                    {
                        TaskPriority = int.Parse(fields[4]),
                        IdleWarning = fields[5] == "1",
                        IgnoreUnconfigured = fields[6] == "1"
                    };
                }
                else if (fields[0] == "O" && fields.Length == 12)
                {
                    PlanEntry entry = FindEntry(entriesByKey, fields);
                    entry.Production ??= new ProductionPlan();
                    entry.Production.Orders.Add(new ProductionOrderPlan
                    {
                        Order = int.Parse(fields[4]),
                        DemandType = int.Parse(fields[5]),
                        AmountWanted = int.Parse(fields[6]),
                        BatchSize = Math.Max(1, int.Parse(fields[7])),
                        Paused = fields[8] == "1",
                        SuppressDeficitWarning = fields[9] == "1",
                        CraftableId = FromB64(fields[10]),
                        ColorId = FromB64(fields[11])
                    });
                }
                else if (fields[0] == "S" && fields.Length == 3
                         && Enum.TryParse(fields[1], out StagingAreaType type))
                {
                    if (!parsed.Staging.TryGetValue(type, out HashSet<int> positions))
                    {
                        positions = new HashSet<int>();
                        parsed.Staging[type] = positions;
                    }
                    positions.Add(int.Parse(fields[2]));
                }
            }
            if (parts[0] != "PO1" && !hasMapSize)
                throw new InvalidDataException("The blueprint is missing its original map dimensions.");
            if (parsed.Rooms.Count == 0)
                parsed.Rooms.Add(new PlanRoom { Id = 1, Order = 0, Name = "Room 1" });
            if (parsed.Rooms.All(room => room.Id != parsed.CurrentRoomId))
                parsed.CurrentRoomId = parsed.Rooms.OrderBy(room => room.Order).First().Id;
            snapshot = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static PlanEntry FindEntry(
        Dictionary<(int Room, int Position, int Layer), PlanEntry> entriesByKey,
        string[] fields)
    {
        int roomId = int.Parse(fields[1]);
        int position = int.Parse(fields[2]);
        int layer = int.Parse(fields[3]);
        if (!entriesByKey.TryGetValue((roomId, position, layer), out PlanEntry entry))
            throw new InvalidDataException("Blueprint metadata has no matching building entry.");
        return entry;
    }

    private static string B64(string value) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string FromB64(string value) => Encoding.UTF8.GetString(
        Convert.FromBase64String(value));

    private static string Hash(byte[] data)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(data);
        var result = new StringBuilder(24);
        for (int i = 0; i < 12; i++) result.Append(hash[i].ToString("x2"));
        return result.ToString();
    }
}
