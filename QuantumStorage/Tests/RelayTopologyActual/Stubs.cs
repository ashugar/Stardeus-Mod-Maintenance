// Faithful field/getter shapes for isolated actual-query code. These do not run Unity/Core lifecycle.
using System;
using System.Collections.Generic;
using System.Linq;
namespace Game.Constants { public static class G { public static long Ticks; public static bool IsDemoMode,HasEnteredGame; } }
namespace Game.Data
{
    public struct Point { public float x, y; }
    public class Entity
    {
        public int Id;
        public object[] Components;
        public Action OnRead;
        public int LookupCalls;
        public T GetComponent<T>() where T:class
        {
            LookupCalls++; OnRead?.Invoke();
            if (Components != null) foreach(var c in Components) if(c is T result) return result;
            return null;
        }
        public bool TryGetComponent<T>(out T result) where T:class
        { result=GetComponent<T>(); return result!=null; }
    }
    public sealed class Tile
    {
        public Point Position;
        public bool IsActive=true, IsConstructed=true;
        public Game.Components.ReinstallableComp Reinstallable=new();
        public Game.Components.ElectricNodeComp ENode=new();
    }
}
namespace Game.Components
{
    public sealed class ReinstallableComp { public bool IsMidRelocation; }
    public sealed class ElectricNodeComp { public bool IsPowered=true, HasDeficit; }
    public sealed class StorageRelayNodeComp
    {
        public static Action<string> ReadTrace;
        public static Action<string> OnGetter;
        internal static void Trace(string name) { ReadTrace?.Invoke(name); OnGetter?.Invoke(name); }
        private Game.Data.Entity entity;
        private Game.Data.Tile tile=new();
        private int entityId, range=12;
        private string network="m";
        public Game.Data.Entity Entity { get { Trace("Entity"); return entity; } set=>entity=value; }
        public Game.Data.Tile Tile { get { Trace("Tile"); return tile; } set=>tile=value; }
        public int EntityId { get { Trace("EntityId"); return entityId; } set=>entityId=value; }
        public string NetworkId { get { Trace("NetworkId"); return network; } set=>network=value; }
        public int Range { get { Trace("Range"); return range; } set=>range=value; }
        public bool Usable=true, Healthy=true;
        public bool IsUsable { get { Trace("IsUsable"); return Usable && Healthy; } }
    }
    public sealed class Reservation { public string Type; public int Amount; }
    public sealed class MatStorageComp
#if LAZY_ADAPTER_FIXTURE
        : Game.Systems.Inventory.IMatStorage
#endif
    {
        public static long HashCalls;
        public override int GetHashCode() { HashCalls++; return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this); }
        public Game.Data.Entity Entity;
        public readonly Dictionary<string,int> Contents=new();
        public readonly List<Reservation> Reservations=new();
#if LAZY_ADAPTER_FIXTURE
        public Game.Systems.Inventory.StorageType StorageType { get; set; }
        public int OutPort { get; set; }
        public bool Forbidden;
        internal Game.Systems.QuantumStorage.QuantumStorageNetwork QuantumPool;
        public Func<Game.Constants.MatType,int> Provider;
        public int ProviderCalls;
        public int Available(Game.Constants.MatType type) { ProviderCalls++; return Provider?.Invoke(type) ?? 0; }
#endif
    }
    public sealed class ObjStorageComp
    {
        public static long HashCalls;
        public Game.Data.Entity Entity;
        public int EntityId=>Entity.Id;
        public override int GetHashCode() { HashCalls++; return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this); }
    }
}
namespace Game.Systems.QuantumStorage.Relays
{
    internal static class RelayCore { internal static int LocalDepth; }
    internal static class RelayObjectCore { internal static int LocalDepth; }
    internal sealed class RelayGroup { internal bool IsObject; internal readonly List<Game.Components.StorageRelayNodeComp> Nodes=new(); }
    internal static class QuantumStorageReflection { internal static Action<Game.Components.MatStorageComp> OnDisplay; internal static void RefreshDisplay(Game.Components.MatStorageComp s)=>OnDisplay?.Invoke(s); }
    public sealed partial class StorageRelaySys
    {
        private readonly Dictionary<Game.Components.MatStorageComp,Game.Components.StorageRelayNodeComp> nodes=new();
        private Dictionary<Game.Components.MatStorageComp,RelayGroup> groups=new();
        private readonly Dictionary<int,Game.Components.ObjStorageComp> objectNodes=new();
        private Dictionary<int,RelayGroup> objectGroups=new();
        private bool ready=true, rebuilding, objectsReady=true;
        internal bool Operational=true;
        internal static int Refreshes, ActiveCalls;
        private long lastTick=long.MinValue;
        private List<RelayVertex> lastVertices=new();
        internal static Action<RelayGroup> OnRefresh;
        private static void Refresh(RelayGroup group) { Refreshes++; OnRefresh?.Invoke(group); }
        private void RefreshObjects(RelayGroup group) { Refreshes++; OnRefresh?.Invoke(group); }
        private void RefreshObjectDisplays() { }
        internal static Game.Components.MatStorageComp Storage(Game.Components.StorageRelayNodeComp n)=>n.Entity.GetComponent<Game.Components.MatStorageComp>();

        internal IEnumerable<Game.Components.StorageRelayNodeComp> MaterialNodes=>nodes.Values;
        internal IEnumerable<Game.Components.ObjStorageComp> ObjectNodes=>objectNodes.Values;
        internal static bool Active(Game.Components.StorageRelayNodeComp n) { ActiveCalls++; return n?.Tile!=null && n.Tile.IsActive && n.Tile.IsConstructed &&
            n.Tile.Reinstallable?.IsMidRelocation!=true && n.Tile.ENode?.IsPowered==true && !n.Tile.ENode.HasDeficit && n.IsUsable; }
        internal void Add(Game.Components.MatStorageComp s,Game.Components.StorageRelayNodeComp n)
        { nodes[s]=n; AdvanceMembershipRevision(); InvalidateTopology(); }
        internal void Add(Game.Components.ObjStorageComp s)
        { objectNodes[s.Entity.Id]=s; AdvanceMembershipRevision(); InvalidateTopology(); }
        internal void Remove(Game.Components.MatStorageComp s)
        { nodes.Remove(s); groups.Remove(s); AdvanceMembershipRevision(); InvalidateTopology(); }
        internal void Remove(Game.Components.ObjStorageComp s)
        { objectNodes.Remove(s.Entity.Id); objectGroups.Remove(s.Entity.Id); AdvanceMembershipRevision(); InvalidateTopology(); }
        internal void Clear()
        {
            AdvanceMembershipRevision(); InvalidateTopology();
            nodes.Clear(); groups.Clear(); objectNodes.Clear(); objectGroups.Clear(); ResetQueryTopology();
        }
        internal void InvalidateTopology() { lastTick=long.MinValue; AdvanceQueryGeneration(); }
        internal void Ready(bool value) { objectsReady=value; AdvanceQueryGeneration(); }
        internal void Online(bool value) { Operational=value; }
        internal void SetRebuilding(bool value) { rebuilding=value; }
        internal void MaterialReady(bool value) { ready=value; }
        internal void Build()=>EnsureTopology(true);
        internal bool TryBuild()=>EnsureTopology(true);
        internal object GroupFor(Game.Components.MatStorageComp s)=>groups.TryGetValue(s,out var g)?g:null;
    }
}

#if !LAZY_ADAPTER_FIXTURE
namespace Game.Systems.QuantumStorage.Relays { internal static class NanobotCandidateQueries { internal static int Epoch=>0; } }
#endif
