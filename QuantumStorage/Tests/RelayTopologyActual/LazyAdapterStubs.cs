// Actual adapter/engine/topology are linked. Core providers, policy and clocks are substitutes.
using System;
using System.Collections.Generic;
using Game.Components;
using Game.Data;
using Game.Constants;
namespace Game.Constants { public sealed class MatType { public readonly string Name; public MatType(string name)=>Name=name; } }
namespace Game.Systems.Creatures
{
    public sealed class Being { public readonly BeingData Data=new(); }
    public sealed class BeingData { public readonly BeingSafety Safety=new(); }
    public sealed class BeingSafety { public bool IgnoreDanger; public bool Safe=true; public bool IsActuallySafeAt(int port)=>Safe; }
}
namespace Game.Data
{
    public sealed class GameState
    {
        public Game.Systems.QuantumStorage.Relays.StorageRelaySys Relays;
        internal Game.Systems.QuantumStorage.QuantumStorageNetworkSys Quantum;
        public readonly StateSystems Sys=new();
        public bool Current=true;
        public static bool IsCurrent(GameState state)=>state?.Current==true;
    }
    public sealed class StateSystems { public readonly FixtureAreas Areas=new(); public Game.Systems.Inventory.InventorySys Inventory; }
    public sealed class FixtureAreas { public bool Reachable=true; public bool CanReachGroundedIslandToPos(int island,int port)=>Reachable; }
}
namespace Game.Systems.Inventory
{
    public enum StorageType { Normal, StoreOnly }
    public interface IMatStorage { StorageType StorageType { get; } int OutPort { get; } int Available(MatType type); }
    public sealed class FixturePiles
    {
        public readonly Dictionary<MatType,List<UnstoredMatComp>> Values=new();
        public List<UnstoredMatComp> Of(MatType type)=>Values.TryGetValue(type,out var list)?list:null;
    }
    public sealed class InventorySys
    {
        // Exact field contract exercised by the real reflection-backed adapter.
        private readonly List<IMatStorage> nonPileStorages=new();
        public readonly List<IMatStorage> Storages=new();
        public readonly FixturePiles Piles=new();
        public readonly GameState S;
        public static Action<IMatStorage> OnEligibility;
        public InventorySys(GameState state) { S=state;if(state.Sys.Inventory==null)state.Sys.Inventory=this; }
        public void Add(IMatStorage storage) { nonPileStorages.Add(storage); Storages.Add(storage); }
        public static bool IsForbiddenFor(IMatStorage storage,Game.Systems.Creatures.Being being)
        { OnEligibility?.Invoke(storage);return storage is MatStorageComp material&&material.Forbidden; }
    }
}
namespace Game.Components
{
    public sealed class UnstoredMatComp : Game.Systems.Inventory.IMatStorage
    {
        public Game.Systems.Inventory.StorageType StorageType { get; set; }
        public int OutPort { get; set; }
        public Func<MatType,int> Provider;
        public int Available(MatType type)=>Provider?.Invoke(type)??0;
    }
}
namespace Game.Systems.QuantumStorage
{
    internal sealed class QuantumStorageNetwork { }
    internal sealed class QuantumStorageNetworkSys
    {
        internal static QuantumStorageNetworkSys Get(GameState s)=>s.Quantum;
        internal bool TryGetNetwork(MatStorageComp s,out QuantumStorageNetwork network) { network=s.QuantumPool;return network!=null; }
    }
    internal static class RelayQueryBaseline
    {
        internal static int Boundaries,Depth,ColdScopes,UsedScopes,Walks,AdvisoryScopes,CachedRefreshes,FreshRefreshes,CandidateModes,RevalidationModes;
        internal struct QuerySample { }
        internal struct DetailSample { }
        internal struct TopologySample { internal bool Entered,Outer; }
        internal static bool TryBeginQuery(out QuerySample sample) { sample=default;return false; }
        internal static void EndQuery(QuerySample sample,bool failed) { }
        internal static TopologySample BeginTopology() { bool outer=Depth++==0;if(outer)Boundaries++;return new TopologySample{Entered=true,Outer=outer}; }
        internal static void EndTopology(TopologySample sample) { if(sample.Entered)Depth--; }
        internal static int SelectDetailVertex(int total)=>-1;
        internal static DetailSample BeginDetail(int bucket)=>default;
        internal static void EndDetail(DetailSample token) { }
        internal static void NoteReadScope(bool touched,bool activated,bool valid,bool advisory=false) { if(touched)UsedScopes++;else ColdScopes++;if(advisory)AdvisoryScopes++; }
        internal static void NoteTopologyWalk() { Walks++; }
        internal static void NoteAdvisoryRefresh(bool cached) { if(cached)CachedRefreshes++;else FreshRefreshes++; }
        internal static bool SamplingQuery=>false;
        internal static void NoteCandidateMode(bool candidatePresent,bool revalidation,bool currentWorld=false,bool decisionBound=false) { if(candidatePresent)CandidateModes++;if(revalidation)RevalidationModes++; }
        internal static void Reset() { Boundaries=Depth=ColdScopes=UsedScopes=Walks=AdvisoryScopes=CachedRefreshes=FreshRefreshes=CandidateModes=RevalidationModes=0; }
    }
}
namespace Game.Systems.QuantumStorage.Relays
{
#if !NANO_ADVISORY_FIXTURE
    internal static class NanobotCandidateQueries { internal static int Epoch=>0; internal static bool IsAdvisory(Game.Systems.Inventory.InventorySys inventory)=>false; internal static bool CanUseAdvisory(Game.Systems.Inventory.InventorySys inventory)=>false; }
#endif
    public sealed partial class StorageRelaySys
    {
        internal static StorageRelaySys Get(GameState s)=>s.Relays;
        internal static bool IsRelay(MatStorageComp storage)=>storage.Entity?.TryGetComponent<StorageRelayNodeComp>(out _)==true;
    }
}
