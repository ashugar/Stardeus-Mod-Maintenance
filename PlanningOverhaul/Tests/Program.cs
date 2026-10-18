using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Game.Constants;
using Game.Data;

namespace PlanningOverhaul;

internal static class Program
{
    private static int assertions;
    internal static void Check(bool result, string message)
    {
        assertions++;
        if (!result) throw new Exception(message);
    }

    private static void Main(string[] args)
    {
        CodecTests();
        PlanningOverhaulSys.StateTests();
        PlanningOverhaulSys.CandidateTests();
        StagingPolicyTests();
        IndexTests();
        LogTests();
        foreach (string blueprintPath in args)
        {
            string code = File.ReadAllText(blueprintPath);
            Check(BlueprintCodec.TryDecode(code, out var snapshot, out var error), error);
            Check(BlueprintCodec.TryDecode(BlueprintCodec.Encode(snapshot), out var roundTrip, out error), error);
            Check(BlueprintCodec.Encode(roundTrip) == BlueprintCodec.Encode(snapshot), "Real blueprint round trip");
#if BASELINE
            Check(BaselineBlueprintCodec.TryDecode(code, out var oldSnapshot, out error), error);
            Check(BlueprintCodec.Encode(snapshot) == BaselineBlueprintCodec.Encode(oldSnapshot), "Real blueprint baseline parity");
#endif
            Console.WriteLine($"Read-only blueprint check: {snapshot.Entries.Count} entries, {snapshot.Rooms.Count} rooms");
        }
        Console.WriteLine("PASS: " + assertions + " assertions. Core/gameplay acceptance remains in-game.");
    }

    private static PlanningBlueprintSnapshot Fixture(int count)
    {
        var snapshot = new PlanningBlueprintSnapshot
        { Name = "Ship | \n Test Ω", CurrentRoomId = 1, MapWidth = 512, MapHeight = 512 };
        for (int i = 1; i <= 73; i++)
            snapshot.Rooms.Add(new PlanRoom { Id = i, Order = i, Name = "Room " + i, IsExterior = i == 73 });
        for (int i = count; i > 0; i--)
        {
            var entry = new PlanEntry
            {
                RoomId = i % 73 + 1, Position = i, Layer = i % 3 + 1,
                Rotation = i % 4, DefId = "Building" + i % 28, IssuedByPlan = i % 2 == 0
            };
            if (i % 29 == 0)
            {
                entry.HasTransporterConfiguration = true;
                entry.TransporterLabel = "Link | \n Ω";
                entry.TransporterOutboundPosition = i + 1;
                entry.HasPlanterConfiguration = true;
                entry.PlanterPlantDefId = "Plant";
                entry.PlanterAutoHarvest = true;
                entry.InstalledUpgradeSlots.AddRange(new[] { "UpgradeA", null, "UpgradeB" });
                entry.Production = new ProductionPlan { TaskPriority = 5, IdleWarning = true, IgnoreUnconfigured = true };
                entry.Production.Orders.Add(new ProductionOrderPlan
                { Order = 2, CraftableId = "Gears", AmountWanted = 42, BatchSize = 7,
                    DemandType = 2, ColorId = "Purple", Paused = true, SuppressDeficitWarning = true });
            }
            snapshot.Entries.Add(entry);
        }
        snapshot.Staging[StagingAreaType.Indoor] = new HashSet<int> { 10, 100, 200 };
        return snapshot;
    }

    private static void CodecTests()
    {
        PlanningBlueprintSnapshot snapshot = Fixture(71786);
        // Warm the runtime before reporting synthetic CPU timings.
        BlueprintCodec.TryDecode(BlueprintCodec.Encode(Fixture(100)), out _, out _);
        var watch = Stopwatch.StartNew();
        string encoded = BlueprintCodec.Encode(snapshot);
        long encodeMs = watch.ElapsedMilliseconds;
        watch.Restart();
        Check(BlueprintCodec.TryDecode(encoded, out var decoded, out var error), error);
        long decodeMs = watch.ElapsedMilliseconds;
        Check(decoded.Entries.Count == snapshot.Entries.Count, "Large-plan entry count");
        Check(BlueprintCodec.Encode(decoded) == encoded, "Full metadata/staging/room round trip");
        Console.WriteLine($"Synthetic 71,786-entry codec: encode={encodeMs} ms, decode={decodeMs} ms");
#if BASELINE
        watch.Restart();
        string oldCode = BaselineBlueprintCodec.Encode(snapshot);
        long oldEncodeMs = watch.ElapsedMilliseconds;
        watch.Restart();
        Check(BaselineBlueprintCodec.TryDecode(oldCode, out var oldDecoded, out error), error);
        long oldDecodeMs = watch.ElapsedMilliseconds;
        Check(oldCode == encoded, "Byte-for-byte PO5 baseline compatibility");
        Check(BlueprintCodec.Encode(oldDecoded) == encoded, "Baseline decoded metadata parity");
        Console.WriteLine($"Baseline same fixture: encode={oldEncodeMs} ms, decode={oldDecodeMs} ms");
#endif
        foreach (string prefix in new[] { "PO1", "PO2", "PO3", "PO4", "PO5" })
        {
            string payload = "N|VGVzdA==\nC|1\nR|1|0|RXh0ZXJpb3I=\nE|1|2|0|3|1|QQ==";
            if (prefix != "PO1") payload += "\nM|10|10";
            Check(BlueprintCodec.TryDecode(Pack(prefix, payload), out decoded, out error), prefix + ": " + error);
            Check(decoded.Rooms[0].IsExterior, "Legacy Exterior migration");
        }
        string duplicate = "M|10|10\nE|1|2|0|3|0|QQ==\nT|1|2|3|3|Zmlyc3Q=\n"
            + "E|1|2|0|3|0|Qg==\nT|1|2|3|4|bGFzdA==";
        Check(BlueprintCodec.TryDecode(Pack("PO5", duplicate), out decoded, out error), error);
        Check(decoded.Entries[0].TransporterLabel == "first" && decoded.Entries[1].TransporterLabel == "last",
            "Metadata binds latest preceding duplicate, not future entry");
        foreach (string payload in new[] {
            "M|10|10\nT|1|2|3|4|QQ==", "M|10|10\nM|10|10",
            "M|10|10\nE|1|2|0|3|0|QQ==\nU|1|2|3|256|QQ==", "R|1|0|QQ==" })
            Check(!BlueprintCodec.TryDecode(Pack("PO5", payload), out _, out _), "Reject malformed metadata/map");
        string[] parts = encoded.Split(':');
        Check(!BlueprintCodec.TryDecode(parts[0] + ":0:" + parts[2], out _, out _), "Integrity check");
        Check(!BlueprintCodec.TryDecode("PO5:broken", out _, out _), "Bad envelope");
        Check(!BlueprintGeometry.TryAdapt(snapshot, 128, 128, _ => (1,1), out _, out _),
            "Reject larger-map content that does not fit the centered target map");
        Check(BlueprintGeometry.TryAdapt(snapshot, 600, 600, _ => (1,1), out var adapted, out error), error);
        Check(adapted.Entries.All(e => !e.IssuedByPlan), "Import cannot inherit job ownership");

        var centeredLargeMap = new PlanningBlueprintSnapshot
        { Name = "Centered ship", CurrentRoomId = 1, MapWidth = 448, MapHeight = 448 };
        centeredLargeMap.Rooms.Add(new PlanRoom { Id = 1, Order = 0, Name = "Room 1" });
        var centeredEntry = new PlanEntry
        {
            RoomId = 1, Position = 84 * 448 + 224, Rotation = 0,
            Layer = WorldLayer.Objects, DefId = "CenteredBuilding", IssuedByPlan = true,
            HasTransporterConfiguration = true,
            TransporterOutboundPosition = 300 * 448 + 224
        };
        centeredLargeMap.Entries.Add(centeredEntry);
        centeredLargeMap.Staging[StagingAreaType.Indoor] = new HashSet<int>
        { 224 * 448 + 224 };
        Check(BlueprintGeometry.TryAdapt(centeredLargeMap, 320, 320, _ => (4, 3),
            out var reduced, out error, 80), error);
        Check(reduced.Entries.Single().Position == 20 * 320 + 160,
            "Larger-map content retains its center-relative position on a smaller map");
        Check(reduced.Entries.Single().TransporterOutboundPosition == 236 * 320 + 160,
            "Larger-map Transporter target retains its center-relative position");
        Check(reduced.Staging[StagingAreaType.Indoor].SetEquals(new[] { 160 * 320 + 160 }),
            "Larger-map staging retains its center-relative position");
        Check(!reduced.Entries.Single().IssuedByPlan,
            "Reduced import cannot inherit job ownership");

        PlanningBlueprintSnapshot OneEntryAt(int x, int y)
        {
            var result = new PlanningBlueprintSnapshot
            { MapWidth = 448, MapHeight = 448, CurrentRoomId = 1 };
            result.Rooms.Add(new PlanRoom { Id = 1, Order = 0, Name = "Room 1" });
            result.Entries.Add(new PlanEntry
            {
                RoomId = 1, Position = x * 448 + y,
                Layer = WorldLayer.Objects, DefId = "Building"
            });
            return result;
        }
        Check(!BlueprintGeometry.TryAdapt(OneEntryAt(63, 224), 320, 320,
            _ => (1, 1), out _, out _, 80),
            "Reject a larger-map building outside the centered target crop");
        Check(!BlueprintGeometry.TryAdapt(OneEntryAt(382, 224), 320, 320,
            _ => (3, 1), out _, out _, 80),
            "Reject a rotated footprint crossing the centered target boundary");
        Check(!BlueprintGeometry.TryAdapt(OneEntryAt(100, 100), 320, 320,
            _ => (1, 1), out _, out _, 80),
            "Reject larger-map construction that would enter a target corner no-build zone");

        var stagedOutside = OneEntryAt(224, 224);
        stagedOutside.Staging[StagingAreaType.Outdoor] = new HashSet<int>
        { 400 * 448 + 224 };
        Check(!BlueprintGeometry.TryAdapt(stagedOutside, 320, 320,
            _ => (1, 1), out _, out _, 80),
            "Reject a larger-map staging cell outside the centered target crop");
        var stagedInCorner = OneEntryAt(224, 224);
        stagedInCorner.Staging[StagingAreaType.Outdoor] = new HashSet<int>
        { 100 * 448 + 100 };
        Check(!BlueprintGeometry.TryAdapt(stagedInCorner, 320, 320,
            _ => (1, 1), out _, out _, 80),
            "Reject larger-map staging in a target corner no-build zone");
        var linkedToCorner = OneEntryAt(224, 224);
        linkedToCorner.Entries[0].HasTransporterConfiguration = true;
        linkedToCorner.Entries[0].TransporterOutboundPosition = 100 * 448 + 100;
        Check(!BlueprintGeometry.TryAdapt(linkedToCorner, 320, 320,
            _ => (1, 1), out _, out _, 80),
            "Reject a larger-map Transporter target in a target corner no-build zone");

        var sameSizeCorner = new PlanningBlueprintSnapshot
        { MapWidth = 320, MapHeight = 320, CurrentRoomId = 1 };
        sameSizeCorner.Rooms.Add(new PlanRoom { Id = 1, Order = 0, Name = "Room 1" });
        sameSizeCorner.Entries.Add(new PlanEntry
        {
            RoomId = 1, Position = 10 * 320 + 10,
            Layer = WorldLayer.Objects, DefId = "ExistingLayout"
        });
        Check(BlueprintGeometry.TryAdapt(sameSizeCorner, 320, 320, _ => (1, 1),
            out _, out error, 80),
            "Same-size imports retain the established no-build-zone behavior: " + error);
    }

    private static string Pack(string prefix, string payload)
    {
        using var output = new MemoryStream();
        using (var stream = new DeflateStream(output, CompressionLevel.Optimal, true))
        { byte[] raw = Encoding.UTF8.GetBytes(payload); stream.Write(raw, 0, raw.Length); }
        byte[] compressed = output.ToArray();
        string hash = string.Concat(SHA256.HashData(compressed).Take(12).Select(b => b.ToString("x2")));
        return prefix + ":" + hash + ":" + Convert.ToBase64String(compressed);
    }

    private static void IndexTests()
    {
        var index = new PlanningOverhaulPlanIndex();
        var first = new PlanEntry { RoomId = 1, Position = 100, Layer = WorldLayer.Floor, DefId = "Floor" };
        var second = new PlanEntry { RoomId = 2, Position = 100, Layer = WorldLayer.Objects, DefId = "Hive", IssuedByPlan = true };
        int[] footprint = { 100, 101 };
        index.Add(first, footprint); index.Add(second, new[] { 100 }); index.Add(first, footprint);
        footprint[0] = 999;
        Check(index.FootprintFor(first)[0] == 100, "Owned immutable footprint copy");
        Check(index.IndexedRoomIds.Count() == 2 && index.StructuralEntries.Count == 1, "Indexed room membership");
        Check(index.EntriesForDefinition("Hive").Single() == second, "Definition isolation");
        int revision = index.Revision;
        index.SetIssued(first, true);
        Check(index.Revision == revision && index.IssuedEntries.Count == 2, "Issue flags do not change plan membership");
        index.ClearIssued();
        Check(!first.IssuedByPlan && !second.IssuedByPlan && index.IssuedEntries.Count == 0, "Clear issued parity");
        index.Remove(first, new[] { 100, 101 });
        Check(index.Revision > revision && !index.IndexedRoomIds.Contains(1), "Remove invalidates membership");
        index.Clear();
        Check(index.IndexedRoomIds.Count() == 0 && !index.Contains(second), "Clear resets indexes");
    }

    private static void StagingPolicyTests()
    {
        Check(PlanningOverhaulStagingPolicy.SelectType(PlacementConstraint.OnFloor,
                true, true) == StagingAreaType.Outdoor,
            "Must-be-outdoors OnFloor building uses Outdoor staging");
        Check(PlanningOverhaulStagingPolicy.SelectType(PlacementConstraint.OnFloor,
                false, true) == StagingAreaType.Indoor,
            "Must-be-indoors building uses Indoor staging");
        Check(PlanningOverhaulStagingPolicy.SelectType(PlacementConstraint.OnFloor,
                null, true) == StagingAreaType.Indoor,
            "Ordinary OnFloor building keeps Indoor staging");
        Check(PlanningOverhaulStagingPolicy.SelectType(PlacementConstraint.InSpace,
                null, false) == StagingAreaType.Outdoor,
            "InSpace building keeps Outdoor staging");
        Check(PlanningOverhaulStagingPolicy.SelectType(
                PlacementConstraint.InSpaceAttached, true, false)
                == StagingAreaType.OutdoorEdge,
            "Edge attachment remains authoritative");
        Check(PlanningOverhaulStagingPolicy.SelectType(PlacementConstraint.Anywhere,
                null, false) == StagingAreaType.Outdoor,
            "Anywhere building in space keeps Outdoor staging");
        Check(PlanningOverhaulStagingPolicy.SelectType(PlacementConstraint.Anywhere,
                null, true) == StagingAreaType.Indoor,
            "Anywhere building on floor keeps Indoor staging");

        Check(PlanningOverhaulStagingPolicy.OutdoorCellIsReady(false, false, true),
            "Exterior open space is ready Outdoor staging");
        Check(PlanningOverhaulStagingPolicy.OutdoorCellIsReady(true, true, true),
            "Completed exterior floor is ready Outdoor staging");
        Check(!PlanningOverhaulStagingPolicy.OutdoorCellIsReady(true, false, true),
            "Unfinished exterior floor is not ready Outdoor staging");
        Check(!PlanningOverhaulStagingPolicy.OutdoorCellIsReady(true, true, false),
            "Enclosed floor is not ready Outdoor staging");
    }

    private sealed class FailingWriter : StreamWriter
    {
        internal FailingWriter() : base(new MemoryStream()) { }
        public override void WriteLine(string value) => throw new IOException("simulated full disk");
        protected override void Dispose(bool disposing) => throw new IOException("simulated failed flush");
    }
    private static void LogTests()
    {
        FieldInfo writer = typeof(PlanningOverhaulLog).GetField("writer", BindingFlags.Static | BindingFlags.NonPublic);
        writer.SetValue(null, new FailingWriter());
        PlanningOverhaulLog.Info("failure probe");
        Check(writer.GetValue(null) == null, "Failed diagnostic write/dispose must detach writer without throwing");
        PlanningOverhaulLog.Shutdown();
    }
}

public sealed partial class PlanningOverhaulSys
{
    internal static void StateTests()
    {
        var sys = new PlanningOverhaulSys();
        sys.rooms.Add(new PlanRoom { Id = 1, Order = 0 });
        sys.rooms.Add(new PlanRoom { Id = 2, Order = 1, IsExterior = true });
        sys.rooms.Add(new PlanRoom { Id = 3, Order = 2 });
        sys.exterior.Add(2);
        for (int i = 1; i <= 71786; i++)
        {
            var e = new PlanEntry { RoomId = i % 4 + 1, Layer = i % 3 + 1, Position = i, Rotation = i % 4, DefId = "D" + i % 20 };
            sys.Add(e);
            if (i % 5 != 0) sys.existing[e] = new Tile { DefinitionId = e.DefId };
            if (i % 11 == 0) sys.unpowered.Add(e);
        }
        // Room 4 deliberately has entries but no room record, as older saves can.
        var state = sys.BuildEquipmentStageState();
        foreach (int room in sys.planIndex.IndexedRoomIds)
        {
            bool expected = sys.entries.Where(e => e.RoomId == room && sys.IsRoomStructuralEntry(e))
                .All(e => sys.IsEquipmentEntrySatisfied(e));
            Program.Check(state.IsStructureFinished(room) == expected, "Indexed structure parity");
        }
        var expectedPending = sys.entries.Where(sys.IsRoomPostFloorEntry).Where(e =>
            !sys.IsExteriorRoom(e.RoomId) && sys.EquipmentPhase(e) == 0
                ? !sys.IsPowerInfrastructureReady(e) : !sys.IsEquipmentEntrySatisfied(e)).ToArray();
        Program.Check(state.PendingEquipmentEntries.SequenceEqual(expectedPending), "Pending-entry order/eligibility parity");
        var cached = sys.PostFloorPlanEntries();
        Program.Check(ReferenceEquals(cached, sys.PostFloorPlanEntries()), "Reuse blueprint-only classification");
        PlanEntry changed = cached.First();
        sys.existing[changed] = new Tile { DefinitionId = changed.DefId };
        sys.unpowered.Remove(changed);
        Program.Check(!sys.BuildEquipmentStageState().PendingEquipmentEntries.Contains(changed), "Live completion must not be cached");
        var added = new PlanEntry { RoomId = 2, Position = 80000, Layer = WorldLayer.Walls, DefId = "Wall" };
        sys.Add(added);
        Program.Check(sys.PostFloorPlanEntries().Contains(added), "Exterior wall index invalidation");
        sys.exterior.Remove(2); sys.postFloorPlanRevision = -1;
        Program.Check(!sys.PostFloorPlanEntries().Contains(added), "Room type invalidation");
        sys.entries.Remove(added); sys.planIndex.Remove(added, new[] { added.Position });
        Program.Check(!sys.PostFloorPlanEntries().Contains(added), "Removed entry invalidation");
#if BASELINE
        string Signature(EquipmentStageState s) => string.Join("|", s.StructureFinished.OrderBy(x=>x.Key))
            + string.Join(",", s.PendingEquipmentEntries.Select(e=>e.Position))
            + string.Join(",", s.RoomsWaitingForPower.OrderBy(x=>x))
            + string.Join(",", s.RoomsWaitingForPowerSupport.OrderBy(x=>x))
            + string.Join(",", s.RoomsWaitingForAtmosphere.OrderBy(x=>x))
            + string.Join(",", s.RoomsWaitingForEquipment.OrderBy(x=>x));
        Program.Check(Signature(sys.BuildBaselineEquipmentStageState()) == Signature(sys.BuildEquipmentStageState()), "Old/new equipment-state parity");
        var watch = Stopwatch.StartNew();
        for (int i=0;i<20;i++) sys.BuildBaselineEquipmentStageState();
        long oldMs = watch.ElapsedMilliseconds; watch.Restart();
        for (int i=0;i<20;i++) sys.BuildEquipmentStageState();
        Console.WriteLine($"Synthetic state discovery, 20 passes (stub world): baseline={oldMs} ms, new={watch.ElapsedMilliseconds} ms");
#endif
    }

    internal static void CandidateTests()
    {
        var random = new Random(20102);
        for (int run = 0; run < 150; run++)
        {
            var sys = new PlanningOverhaulSys();
            int layer = run % 2 == 0 ? WorldLayer.Walls : WorldLayer.Objects;
            var source = new Tile { Id = 10, PosIdx = 5000, DefinitionId = "D", Definition = new Def { LayerId = layer } };
            for (int i=0; i<300; i++)
            {
                var e = new PlanEntry { RoomId = i % 3 + 1, Position = random.Next(1, 4000), Layer = layer, DefId = i%3==0 ? "Other" : "D" };
                sys.Add(e);
                if (i%4==0) sys.rejected.Add(e);
                if (i%7==0) sys.unready.Add(e);
                if (i%11==0) sys.phaseBlocked.Add(e);
                if (i%13==0) sys.unsupported.Add(e);
                if (i%17==0) sys.noPower.Add((source.Id, e.Position));
            }
            var reserved = new HashSet<int>(sys.entries.Take(15).Select(e=>e.Position));
            bool found = sys.TryFindDirectRelocationDestination(source, 2, reserved,
                out int destination, out var selected, requireEquipmentPhase: true);
            var expected = sys.entries.Where(e=>e.DefId=="D" && e.Position!=source.PosIdx
                    && !sys.unready.Contains(e) && !sys.phaseBlocked.Contains(e)
                    && (layer != WorldLayer.Walls || !sys.unsupported.Contains(e))
                    && !sys.noPower.Contains((source.Id, e.Position)))
                .OrderBy(e=>e.RoomId==2 ? 0 : 1)
                .ThenBy(e=>KL.Grid.Pos.SqrDistance(source.PosIdx, e.Position))
                .FirstOrDefault(e=>sys.CanPlaceRelocation(source,e.Position,0,reserved));
            Program.Check(found == (expected != null) && ReferenceEquals(expected, selected), "Stable room/distance/Core/reservation parity");
#if BASELINE
            bool oldFound = sys.TryFindBaselineDirectRelocationDestination(source, 2, reserved,
                out int oldDestination, out var oldSelected, requireEquipmentPhase: true);
            Program.Check(found == oldFound && destination == oldDestination && ReferenceEquals(selected, oldSelected), "Baseline destination parity");
#endif
        }
        var fast = new PlanningOverhaulSys();
        var original = new Tile { Id=1, PosIdx=1, DefinitionId="D", Definition=new Def { LayerId=WorldLayer.Objects } };
        for (int i=2; i<502; i++) fast.Add(new PlanEntry { RoomId=1, Position=i, Layer=WorldLayer.Objects, DefId="D" });
        fast.TryFindDirectRelocationDestination(original,1,new HashSet<int>(),out _,out _);
        Program.Check(fast.powerChecks == 1, "Best valid destination needs only one power probe");
        var sources = Enumerable.Range(2,500).Select(i=>new Tile { Id=i,PosIdx=i,DefinitionId="D",Reinstallable=new Reinstallable { CanOrderRelocationNow=true } }).ToList();
        fast.powerChecks=0;
        var destinationEntry = new PlanEntry { Position=1,DefId="D" };
        Tile chosen=fast.FindAvailableBuildingForPlan(destinationEntry,new Dictionary<string,List<Tile>> { ["D"]=sources });
        Program.Check(chosen==sources[0] && fast.powerChecks==1, "Reusable source early power probe");
        sources[0].Reinstallable.CanOrderRelocationNow=false;
        fast.noPower.Add((sources[1].Id,1));
        chosen=fast.FindAvailableBuildingForPlan(destinationEntry,new Dictionary<string,List<Tile>> { ["D"]=sources });
        Program.Check(chosen==sources[2], "Ready-now preference and unpowered fallback");
    }
}
