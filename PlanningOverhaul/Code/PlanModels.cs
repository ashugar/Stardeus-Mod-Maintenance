using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.Data;
using UnityEngine;

namespace PlanningOverhaul;

internal sealed class PlanRoom
{
    internal int Id;
    internal int Order;
    internal string Name;
    internal bool IsExterior;
}

internal sealed class BuildCatalogCategory
{
    internal string Id;
    internal string Name;
    internal int Order;
    internal readonly List<Def> Buildings = new();
}

internal sealed class PlanEntry
{
    internal int RoomId;
    internal int Position;
    internal int Rotation;
    internal int Layer;
    internal string DefId;
    internal bool IssuedByPlan;
    internal bool HasTransporterConfiguration;
    internal string TransporterLabel;
    internal int TransporterOutboundPosition = -1;
    internal readonly List<string> InstalledUpgradeSlots = new();
    internal ProductionPlan Production;
    internal bool HasPlanterConfiguration;
    internal string PlanterPlantDefId;
    internal bool PlanterAutoHarvest;

    internal string Key => RoomId + ":" + Layer + ":" + Position;
}

internal sealed class ProductionPlan
{
    internal int TaskPriority;
    internal bool IdleWarning;
    internal bool IgnoreUnconfigured;
    internal readonly List<ProductionOrderPlan> Orders = new();
}

internal sealed class ProductionOrderPlan
{
    internal int Order;
    internal string CraftableId;
    internal int DemandType;
    internal int AmountWanted;
    internal int BatchSize = 1;
    internal string ColorId;
    internal bool Paused;
    internal bool SuppressDeficitWarning;
}

internal sealed class PlanningBlueprintSnapshot
{
    internal string Name = "Construction Plan";
    internal int CurrentRoomId;
    internal int MapWidth;
    internal int MapHeight;
    internal readonly List<PlanRoom> Rooms = new();
    internal readonly List<PlanEntry> Entries = new();
    internal readonly Dictionary<StagingAreaType, HashSet<int>> Staging = new();
}

internal enum StagingAreaType
{
    Indoor = 1,
    Outdoor = 2,
    OutdoorEdge = 3
}

internal sealed class MaterialRequirement
{
    internal Game.Constants.MatType Type;
    internal int Cost;
    internal int RequiredToStart;
    internal int Available;
    internal int Missing => Math.Max(0, RequiredToStart - Available);
}

internal sealed class EquipmentConstructionPreview
{
    internal int RoomId;
    internal string RoomName;
    internal string PhaseName;
    internal readonly List<PlanEntry> Entries = new();
    internal readonly List<MaterialRequirement> Materials = new();
}

internal sealed class PulsePreview
{
    internal PlanRoom Room;
    internal readonly List<PlanEntry> Pending = new();
    internal readonly List<PlanEntry> InProgressEntries = new();
    internal readonly List<PlanEntry> LockedEntries = new();
    internal readonly List<PlanEntry> BlockedEntries = new();
    internal readonly List<string> BlockedDetails = new();
    internal readonly List<MaterialRequirement> Materials = new();
    internal int StructuralTotal;
    internal int StructuralFinished;
    internal int InProgress;
    internal int Locked;
    internal int Blocked;
    internal int RelocationsReady;
    internal int RelocationsInProgress;
    internal int RelocationsWaiting;

    internal bool CanStart => Room != null && Pending.Count > 0
        && InProgress == 0 && Locked == 0 && Blocked == 0
        && Materials.All(item => item.Missing == 0);
}


internal static class BlueprintGeometry
{
    internal static bool ValidMapSize(int width, int height) => width > 0 && height > 0
        && (long)width * height <= int.MaxValue;

    internal static bool TryAdapt(PlanningBlueprintSnapshot source, int width, int height,
        Func<PlanEntry, (int Width, int Height)> footprintSize,
        out PlanningBlueprintSnapshot adapted, out string error,
        int targetCornerNoBuildMargin = 0)
    {
        adapted = null;
        error = null;
        int sourceWidth = source.MapWidth;
        int sourceHeight = source.MapHeight;
        if (sourceWidth == 0 && sourceHeight == 0)
        {
            error = "This older blueprint has no map dimensions and cannot be imported. "
                + "Recapture it with a current version of Planning Overhaul.";
            return false;
        }
        if (!ValidMapSize(sourceWidth, sourceHeight) || !ValidMapSize(width, height))
        {
            error = "Invalid source or current map dimensions.";
            return false;
        }
        // Core indexes columns using map height: x * height + y.
        // Preserve each tile's offset from the map center; never scale, wrap,
        // or clip. A larger source map is safe when its actual content fits
        // the centered target map and avoids the target's corner no-build zones.
        int dx = width / 2 - sourceWidth / 2;
        int dy = height / 2 - sourceHeight / 2;
        bool reducingMap = sourceWidth > width || sourceHeight > height;
        var result = new PlanningBlueprintSnapshot
        {
            Name = source.Name, CurrentRoomId = source.CurrentRoomId,
            MapWidth = width, MapHeight = height
        };
        result.Rooms.AddRange(source.Rooms.Select(room => new PlanRoom
        {
            Id = room.Id, Order = room.Order, Name = room.Name,
            IsExterior = room.IsExterior
        }));
        foreach (PlanEntry entry in source.Entries)
        {
            var size = footprintSize?.Invoke(entry) ?? (1, 1);
            if (!TryMapPosition(entry.Position, sourceWidth, sourceHeight, width, height,
                    dx, dy, size.Item1, size.Item2, out int position))
            {
                error = "The blueprint building “" + entry.DefId + "” at original tile index "
                    + entry.Position + " falls outside its original map or its centered footprint "
                    + "does not fit the current " + width + " × " + height
                    + " map. Nothing was clipped; the current plan was preserved.";
                return false;
            }
            int targetX = position / height;
            int targetY = position % height;
            if (reducingMap && IntersectsCornerNoBuildZone(targetX, targetY,
                    size.Item1, size.Item2, width, height, targetCornerNoBuildMargin))
            {
                error = "The blueprint building “" + entry.DefId + "” at original tile index "
                    + entry.Position + " would occupy a corner no-build zone on the current "
                    + width + " × " + height
                    + " map. Nothing was clipped; the current plan was preserved.";
                return false;
            }
            result.Entries.Add(new PlanEntry
            {
                RoomId = entry.RoomId, Position = position, Rotation = entry.Rotation,
                Layer = entry.Layer, DefId = entry.DefId, IssuedByPlan = false,
                HasTransporterConfiguration = entry.HasTransporterConfiguration,
                TransporterLabel = entry.TransporterLabel,
                Production = CloneProduction(entry.Production),
                HasPlanterConfiguration = entry.HasPlanterConfiguration,
                PlanterPlantDefId = entry.PlanterPlantDefId,
                PlanterAutoHarvest = entry.PlanterAutoHarvest
            });
            PlanEntry added = result.Entries[result.Entries.Count - 1];
            added.InstalledUpgradeSlots.AddRange(entry.InstalledUpgradeSlots);
            if (entry.TransporterOutboundPosition >= 0)
            {
                if (!TryMapPosition(entry.TransporterOutboundPosition, sourceWidth,
                        sourceHeight, width, height, dx, dy, 1, 1,
                        out int outboundPosition))
                {
                    error = "A saved Transporter link target falls outside the current map. "
                        + "The current plan was preserved.";
                    return false;
                }
                int outboundX = outboundPosition / height;
                int outboundY = outboundPosition % height;
                if (reducingMap && IntersectsCornerNoBuildZone(outboundX, outboundY,
                        1, 1, width, height, targetCornerNoBuildMargin))
                {
                    error = "A saved Transporter link target would occupy a corner no-build "
                        + "zone on the current map. Nothing was clipped; the current plan was preserved.";
                    return false;
                }
                added.TransporterOutboundPosition = outboundPosition;
            }
        }
        foreach (KeyValuePair<StagingAreaType, HashSet<int>> area in source.Staging)
        {
            var positions = new HashSet<int>();
            foreach (int original in area.Value)
            {
                if (!TryMapPosition(original, sourceWidth, sourceHeight, width, height,
                        dx, dy, 1, 1, out int position))
                {
                    error = "A blueprint staging cell falls outside its original map or does not "
                        + "fit the current map. The current plan was preserved.";
                    return false;
                }
                int targetX = position / height;
                int targetY = position % height;
                if (reducingMap && IntersectsCornerNoBuildZone(targetX, targetY,
                        1, 1, width, height, targetCornerNoBuildMargin))
                {
                    error = "A blueprint staging cell would occupy a corner no-build zone "
                        + "on the current map. Nothing was clipped; the current plan was preserved.";
                    return false;
                }
                positions.Add(position);
            }
            result.Staging[area.Key] = positions;
        }
        adapted = result;
        return true;
    }

    private static bool IntersectsCornerNoBuildZone(int x, int y,
        int footprintWidth, int footprintHeight, int width, int height, int margin)
    {
        if (margin <= 0) return false;
        long right = (long)x + footprintWidth;
        long top = (long)y + footprintHeight;
        bool touchesLeftOrRight = x < margin || right > width - margin;
        bool touchesBottomOrTop = y < margin || top > height - margin;
        return touchesLeftOrRight && touchesBottomOrTop;
    }

    private static ProductionPlan CloneProduction(ProductionPlan source)
    {
        if (source == null) return null;
        var result = new ProductionPlan
        {
            TaskPriority = source.TaskPriority,
            IdleWarning = source.IdleWarning,
            IgnoreUnconfigured = source.IgnoreUnconfigured
        };
        result.Orders.AddRange(source.Orders.Select(order => new ProductionOrderPlan
        {
            Order = order.Order,
            CraftableId = order.CraftableId,
            DemandType = order.DemandType,
            AmountWanted = order.AmountWanted,
            BatchSize = order.BatchSize,
            ColorId = order.ColorId,
            Paused = order.Paused,
            SuppressDeficitWarning = order.SuppressDeficitWarning
        }));
        return result;
    }

    private static bool TryMapPosition(int original, int sourceWidth, int sourceHeight,
        int width, int height, int dx, int dy, int footprintWidth, int footprintHeight,
        out int position)
    {
        position = 0;
        if (original < 0 || (long)original >= (long)sourceWidth * sourceHeight
                || footprintWidth <= 0 || footprintHeight <= 0) return false;
        int x = original / sourceHeight;
        int y = original % sourceHeight;
        if ((long)x + footprintWidth > sourceWidth || (long)y + footprintHeight > sourceHeight)
            return false;
        long targetX = (long)x + dx;
        long targetY = (long)y + dy;
        if (targetX < 0 || targetY < 0 || targetX + footprintWidth > width
                || targetY + footprintHeight > height) return false;
        position = (int)(targetX * height + targetY);
        return true;
    }
}

internal static class BlueprintRepository
{
    private static string DirectoryPath => Path.Combine(
        Application.persistentDataPath, "PlanningOverhaul", "Blueprints");

    internal static string Save(string requestedName, string code)
    {
        Directory.CreateDirectory(DirectoryPath);
        string name = SafeName(requestedName);
        string path = Path.Combine(DirectoryPath, name + ".pob");
        File.WriteAllText(path, code, new UTF8Encoding(false));
        return name;
    }

    internal static string Load(string name)
    {
        return File.ReadAllText(Path.Combine(DirectoryPath, SafeName(name) + ".pob"), Encoding.UTF8);
    }

    internal static bool Delete(string name) => DeleteFile(DirectoryPath, name);

    private static bool DeleteFile(string directory, string name)
    {
        // Use the exact listed name, without Save's renaming or truncation.
        // A deletion target must be a single file inside the blueprint folder.
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new ArgumentException("Select a saved blueprint to delete.", nameof(name));
        string path = Path.Combine(directory, name + ".pob");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    internal static IReadOnlyList<string> List()
    {
        if (!Directory.Exists(DirectoryPath)) return Array.Empty<string>();
        return Directory.GetFiles(DirectoryPath, "*.pob")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string SafeName(string value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "Construction Plan" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.Length > 80 ? name.Substring(0, 80) : name;
    }
}
