// Minimal off-game boundary doubles. Tests link the real codec, plan index,
// equipment-state builder, and relocation search; Core itself is not emulated.
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Constants;
using Game.Data;

namespace Game.Constants
{
    public static class WorldLayer { public const int Floor = 1, Walls = 2, Objects = 3; }
    public enum PlacementConstraint { Anywhere, OnFloor, InSpace, InSpaceAttached, InSpaceAttachedChained, NearShipComputer }
    public sealed class MatType { }
    public static class Facing { public enum Type { North, East, South, West } }
}
namespace Game.Data
{
    public sealed class Def { public int LayerId; }
    public sealed class Reinstallable { public bool CanOrderRelocationNow; }
    public sealed class Tile
    {
        public int Id, PosIdx;
        public string DefinitionId;
        public Def Definition = new();
        public bool IsActive = true, IsConstructed = true;
        public Reinstallable Reinstallable = new();
    }
}
namespace Game.Utils
{
    public static class EntityUtils
    {
        public static readonly HashSet<(int, int)> Occupied = new();
        public static Tile ParentObjAt(int pos, int layer = WorldLayer.Objects) =>
            Occupied.Contains((pos, layer)) ? new Tile() : null;
    }
}
namespace KL.Utils { }
namespace KL.Grid
{
    public static class Pos
    {
        public const int Invalid = -1;
        public static void ToXY(int position, out int x, out int y)
        { x = position / 512; y = position % 512; }
        public static int SqrDistance(int a, int b)
        {
            ToXY(a, out int ax, out int ay); ToXY(b, out int bx, out int by);
            return (ax-bx)*(ax-bx)+(ay-by)*(ay-by);
        }
    }
}
namespace UnityEngine { public static class Application { public static string persistentDataPath = "unused-in-tests"; } }

namespace PlanningOverhaul
{
    internal sealed class PlanningOverhaulOccupancySnapshot
    {
        internal List<Tile> PlanRoomBuildings = new();
        internal int PlannedPositionCount;
        internal static PlanningOverhaulOccupancySnapshot Capture(object state, IEnumerable<int> positions)
            => new() { PlannedPositionCount = positions.Count() };
    }

    public sealed partial class PlanningOverhaulSys
    {
        private readonly object S = new();
        private readonly List<PlanRoom> rooms = new();
        private readonly List<PlanEntry> entries = new();
        private readonly PlanningOverhaulPlanIndex planIndex = new();
        private readonly Dictionary<int,int> plannedFloorRoomByPosition = new();
        private readonly HashSet<int> exterior = new();
        private readonly Dictionary<PlanEntry,Tile> existing = new();
        private readonly HashSet<PlanEntry> unpowered = new();
        private readonly HashSet<PlanEntry> rejected = new();
        private readonly HashSet<PlanEntry> unready = new();
        private readonly HashSet<PlanEntry> phaseBlocked = new();
        private readonly HashSet<PlanEntry> unsupported = new();
        private readonly HashSet<(int,int)> noPower = new();
        private int powerChecks;
        private float nextEquipmentStatePerfLogTime;
        private bool IsExteriorRoom(int roomId) => exterior.Contains(roomId);
        private bool IsRoomStructuralEntry(PlanEntry e) => IsExteriorRoom(e.RoomId)
            ? e.Layer == WorldLayer.Floor : e.Layer != WorldLayer.Objects;
        private bool IsRoomPostFloorEntry(PlanEntry e) => !IsRoomStructuralEntry(e);
        private IReadOnlyList<PlanEntry> RoomStructuralEntries(int room) => IsExteriorRoom(room)
            ? planIndex.FloorsForRoom(room) : planIndex.StructuralForRoom(room);
        private Tile ExistingAt(PlanEntry e) => existing.TryGetValue(e, out Tile tile) ? tile : null;
        private bool IsEquipmentEntrySatisfied(PlanEntry e) => ExistingAt(e)?.IsConstructed == true
            && ExistingAt(e).DefinitionId == e.DefId;
        private int EquipmentPhase(PlanEntry e) => e.Rotation % 4;
        private bool IsPowerInfrastructureReady(PlanEntry e) => IsEquipmentEntrySatisfied(e) && !unpowered.Contains(e);
        private bool TryPlanRoomFor(Tile tile, out int room) { room = 0; return false; }
        private bool OccupiesDesignatedStaging(Tile tile) => false;
        private bool MatchesPlannedTile(Tile tile) => false;
        private bool IsConstructionSupportTile(Tile tile) => false;
        private bool IsPlannerRemovalProtected(Tile tile) => false;
        private bool ProtectedBuildingBlocksPlan(Tile tile) => false;
        private PlanEntry[] ProtectedPlanEntriesNeedingWork(Tile tile, EquipmentStageState state) => Array.Empty<PlanEntry>();
        private void PruneConstructionSupportAdditions() { }
        private void ReportSlowOperation(string name, long ms, string detail, ref float next) { }
        private EquipmentConstructionPreview BuildPausedRoomMaterialPreview(int room, ref EquipmentStageState state) => null;
        private string DescribePausedRoomInspection(int room, PulsePreview preview, EquipmentConstructionPreview construction, EquipmentStageState state) => "";
        private bool DirectDestinationStructureIsReady(PlanEntry e) => !unready.Contains(e);
        private bool HasConstructedWallSupport(PlanEntry e) => !unsupported.Contains(e);
        private IEnumerable<int> EntryFootprint(PlanEntry e) => planIndex.FootprintFor(e);
        private bool DestinationAllowsEquipmentPhase(PlanEntry e, EquipmentStageState state) => !phaseBlocked.Contains(e);
        private bool TryPrepareRelocationPower(Tile source, int pos, out int connector)
        { powerChecks++; connector = 0; return !noPower.Contains((source.Id, pos)); }
        private bool CanPlaceRelocation(Tile source, int pos, Facing.Type facing, HashSet<int> reserved) =>
            !reserved.Contains(pos) && !entries.Any(e => e.Position == pos && rejected.Contains(e));
        private void Add(PlanEntry e)
        { entries.Add(e); planIndex.Add(e, new[] { e.Position }); }
    }
}
