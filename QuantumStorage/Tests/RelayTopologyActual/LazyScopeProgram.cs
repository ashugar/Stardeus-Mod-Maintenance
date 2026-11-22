using System;
using System.Collections.Generic;
using System.Linq;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Inventory;
using Game.Systems.QuantumStorage;
using Game.Systems.QuantumStorage.Relays;

static class LazyScopeProgram
{
    private static int assertions,ids;
    private static readonly MatType Steel=new("steel");
    private static void Must(bool condition,string reason) { assertions++;if(!condition)throw new Exception("Actual lazy scope: "+reason+" #"+assertions); }
    private sealed class Item { internal MatStorageComp Storage; internal StorageRelayNodeComp Node; }
    private sealed class World
    {
        internal readonly StorageRelaySys Sys=new();
        internal readonly GameState State;
        internal readonly InventorySys Inventory;
        internal readonly List<Item> Items=new();
        internal World() { State=new GameState{Relays=Sys,Quantum=new QuantumStorageNetworkSys()};Inventory=new InventorySys(State); }
        internal Item Relay(int stock=0,int x=0,string network="m",bool inInventory=true)
        {
            var entity=new Entity{Id=++ids};var node=new StorageRelayNodeComp{Entity=entity,EntityId=entity.Id,NetworkId=network};node.Tile.Position=new Point{x=x};
            var storage=new MatStorageComp{Entity=entity};entity.Components=new object[]{node,storage};storage.Contents["steel"]=stock;
            storage.Provider=type=>Read(storage,type);Sys.Add(storage,node);if(inInventory)Inventory.Add(storage);
            var item=new Item{Node=node,Storage=storage};Items.Add(item);return item;
        }
        internal MatStorageComp Ordinary(int stock=0,InventorySys inventory=null)
        {
            var entity=new Entity{Id=++ids};var storage=new MatStorageComp{Entity=entity};entity.Components=new object[]{storage};storage.Contents["steel"]=stock;
            storage.Provider=type=>Physical(storage,type);(inventory??Inventory).Add(storage);return storage;
        }
        internal int Read(MatStorageComp storage,MatType type)
        {
            if(RelayCore.LocalDepth>0||!StorageRelaySys.IsRelay(storage))return Physical(storage,type);
            var group=Sys.Group(storage);if(group==null)return 0;
            int total=0;foreach(var node in group.Nodes)total+=Physical(StorageRelaySys.Storage(node),type);return Math.Max(0,total);
        }
        internal void Build() { Sys.Build();RelayQueryBaseline.Reset();StorageRelaySys.ActiveCalls=0;foreach(var i in Items)i.Node.Entity.LookupCalls=0; }
        internal bool Enough(int amount,InventorySys inventory=null)=>RelayMaterialQuery.HasEnough(inventory??Inventory,Steel,amount,-1,null);
    }
    private static int Physical(MatStorageComp storage,MatType type)
    {
        storage.Contents.TryGetValue(type.Name,out int amount);foreach(var r in storage.Reservations)if(r.Type==type.Name&&r.Amount>0)amount-=r.Amount;return Math.Max(0,amount);
    }
    private static void ColdQueries()
    {
        var w=new World();var ordinary=w.Ordinary(10);var relay=w.Relay(20);w.Build();
        relay.Node.NetworkId="public mutation unrelated to this answer";
        Must(w.Enough(5),"ordinary prefix alone can answer");Must(RelayQueryBaseline.Boundaries==0&&StorageRelaySys.ActiveCalls==0,"cold adapter does not scan registry");
        Must(ordinary.ProviderCalls==1&&relay.Storage.ProviderCalls==0,"native provider order/count retains early success");
        Must(RelayQueryBaseline.ColdScopes==1&&RelayQueryBaseline.UsedScopes==0,"cold scope observed separately");
        ordinary.Contents["steel"]=0;relay.Storage.Forbidden=true;RelayQueryBaseline.Reset();
        Must(!w.Enough(0),"native zero-amount request still needs positive availability");Must(RelayQueryBaseline.Boundaries==0,"ineligible Relay never depends on grouping");
        relay.Storage.Forbidden=false;relay.Storage.StorageType=StorageType.StoreOnly;
        Must(!w.Enough(1)&&RelayQueryBaseline.Boundaries==0,"store-only Relay remains excluded without scan");
        var empty=new World();empty.Relay(40,inInventory:false);empty.Build();Must(!empty.Enough(1)&&RelayQueryBaseline.Boundaries==0,"empty candidate domain skips scans");
        var before=ordinary.Contents;for(int i=0;i<1000;i++)Must(!w.Enough(1),"repeated cold answer");
        Must(RelayQueryBaseline.Boundaries==0&&ReferenceEquals(before,ordinary.Contents),"repeated no-dependency queries retain zero scans and physical records");
        bool added=false;int providers=ordinary.ProviderCalls;ordinary.Provider=type=>
        { if(!added){added=true;w.Relay(0,400,inInventory:false);}return 10; };
        Must(w.Enough(1),"cold membership change can succeed after stable retry");
        Must(ordinary.ProviderCalls==providers+2&&RelayQueryBaseline.Boundaries==0,"cold constructor generation guard retries without unnecessary global scans");
    }
    private static void PromotedQueries()
    {
        var w=new World();var a=w.Relay(0);var bridge=w.Relay(0,10,inInventory:false);var c=w.Relay(10,20,inInventory:false);w.Build();
        var stock=c.Storage.Contents;var reservation=new Reservation{Type="steel",Amount=3};c.Storage.Reservations.Add(reservation);
        Must(w.Enough(7),"actual adapter reaches remote stock through original provider");Must(RelayQueryBaseline.Boundaries==2,"direct dependency keeps initial and final global checks");
        Must(RelayQueryBaseline.UsedScopes==1,"promoted read recorded");reservation.Amount=9;RelayQueryBaseline.Reset();
        Must(!w.Enough(2),"in-place native reservation edit remains fresh");Must(RelayQueryBaseline.Boundaries==2,"unsatisfied promoted query keeps both boundaries");
        Must(ReferenceEquals(stock,c.Storage.Contents)&&ReferenceEquals(reservation,c.Storage.Reservations[0]),"original stock and reservation records retained");
        bridge.Node.Tile.ENode.IsPowered=false;
        Must(!w.Enough(1),"same-tick outside bridge split affects current read");
        bridge.Node.Tile.ENode.IsPowered=true;
        Must(w.Enough(1),"same-tick bridge restore permits stable retry");
        w.Sys.Ready(false);Must(w.Enough(1),"object-readiness transition cannot hide material stock after retry");w.Sys.Ready(true);
        Must(w.Enough(1),"readiness restore remains coherent");
    }
    private static void EarlierOrdinaryContribution()
    {
        var w=new World();var a=w.Relay(10,0,"apart");var b=w.Relay(0,2,"m");w.Build();
        // In-place public component and network edits do not advance the private registry generation.
        a.Storage.Entity.Components=new object[]{a.Storage};bool once=false;
        a.Storage.Provider=type=>
        {
            int current=w.Read(a.Storage,type);
            if(!once){once=true;a.Node.NetworkId="m";a.Storage.Entity.Components=new object[]{a.Node,a.Storage};}
            return current;
        };
        int displayReads=0;QuantumStorageReflection.OnDisplay=storage=>
        {
            displayReads++;var committed=w.Sys.Group(storage);
            Must(committed!=null&&committed.Nodes.Count==2,"post-publication native display callback sees current committed merged map during lazy repair");
        };
        try {Must(!w.Enough(15),"activation rebuild cannot bless earlier physical contribution and count it again in merged pool");}
        finally {QuantumStorageReflection.OnDisplay=null;}
        Must(displayReads==2,"changed lazy activation keeps native display notifications");
        Must(a.Storage.ProviderCalls>1,"changed activation forces retry from original constructor stamp");
        Must(a.Storage.Contents["steel"]==10&&b.Storage.Contents["steel"]==0,"retry does not move stock");
    }
    private static void NestedDependency()
    {
        var w=new World();var a=w.Relay(0,inInventory:false);var bridge=w.Relay(0,10,inInventory:false);w.Relay(10,20,inInventory:false);w.Build();
        var nested=new InventorySys(w.State);nested.Add(a.Storage);bool once=false;var outer=w.Ordinary(0);
        outer.Provider=type=>
        {
            bool result=w.Enough(1,nested);
            if(!once){once=true;bridge.Node.Tile.ENode.IsPowered=false;}
            return result?10:0;
        };
        Must(!w.Enough(1),"nested Relay provider marks every ancestor so later public split invalidates ordinary outer result");
        Must(outer.ProviderCalls>1&&RelayQueryBaseline.UsedScopes>=3,"outer query retries instead of accepting stale nested value");
    }
    private static void IndirectAndOffline()
    {
        var w=new World();var a=w.Relay(4,inInventory:false);w.Build();var ordinary=w.Ordinary(0);
        ordinary.Provider=type=>w.Sys.Group(a.Storage)==null?0:4;
        Must(w.Enough(4)&&RelayQueryBaseline.Boundaries==2,"ordinary provider indirect Group activates ambient scope");
        RelayQueryBaseline.Reset();w.Sys.Online(false);
        using(var scope=w.Sys.BeginDeferredMaterialRead())
        {
            Must(w.Sys.Group(a.Storage)==null,"offline native group still null");Must(scope.Validate(),"stable offline read validates zero");
        }
        Must(RelayQueryBaseline.UsedScopes==1&&RelayQueryBaseline.Boundaries>=1,"offline attempted access remains a dependency");w.Sys.Online(true);
        var missing=new MatStorageComp{Entity=new Entity{Id=++ids}};missing.Entity.Components=new object[]{missing};RelayQueryBaseline.Reset();
        using(var scope=w.Sys.BeginDeferredMaterialRead())
        {
            Must(scope.Pool(missing)==null,"missing pool remains null");Must(scope.Validate(),"stable missing pool validates");
        }
        Must(RelayQueryBaseline.Boundaries==2&&RelayQueryBaseline.UsedScopes==1,"null pool answer is checked at both boundaries");
        using(var scope=w.Sys.BeginDeferredMaterialRead()) { w.Sys.Online(false);Must(!scope.Validate(),"cold constructor operational stamp preserved"); }w.Sys.Online(true);
        using(var scope=w.Sys.BeginDeferredMaterialRead()) { w.Sys.MaterialReady(false);Must(!scope.Validate(),"cold constructor readiness stamp preserved"); }w.Sys.MaterialReady(true);
    }
    private static void ForcedAndLocal()
    {
        var w=new World();var a=w.Relay(0);w.Relay(10,10,inInventory:false);w.Build();
        using(var scope=w.Sys.BeginDeferredMaterialRead())
        {
            w.Sys.InvalidateTopology();Must(scope.Pool(a.Storage)==null,"read attempt invalidated by membership generation");
            StorageRelaySys.ActiveCalls=0;Must(w.Sys.Group(a.Storage,true)!=null,"invalid ambient query cannot block forced native action access");
            Must(StorageRelaySys.ActiveCalls>0&&!scope.Validate(),"forced action checks fresh topology and invalid query stays invalid");
        }
        w.Build();RelayCore.LocalDepth++;
        try { Must(!w.Enough(1),"explicit local query cannot use remote stock");Must(RelayQueryBaseline.Boundaries==0,"local native semantics do not promote global read scope"); }
        finally {RelayCore.LocalDepth--;}
    }
    private static (ObjStorageComp storage,StorageRelayNodeComp node) AddObject(World w,int x)
    {
        var entity=new Entity{Id=++ids};var node=new StorageRelayNodeComp{Entity=entity,EntityId=entity.Id,NetworkId="objects"};node.Tile.Position=new Point{x=x};
        var storage=new ObjStorageComp{Entity=entity};entity.Components=new object[]{node,storage};w.Sys.Add(storage);return(storage,node);
    }
    private static void IndirectObjectDependencies()
    {
        var w=new World();var a=AddObject(w,0);var bridge=AddObject(w,10);var c=AddObject(w,20);w.Build();
        var ordinary=w.Ordinary(0);bool once=false;
        ordinary.Provider=type=>
        {
            bool connected=w.Sys.ObjectGroup(a.storage)?.Nodes.Count==3;
            if(!once){once=true;bridge.node.Tile.ENode.IsPowered=false;}
            return connected?10:0;
        };
        Must(!w.Enough(1),"indirect ordinary provider object grouping marks its enclosing material scope");
        Must(ordinary.ProviderCalls>1,"object public-state change forces material outer retry");
        bridge.node.Tile.ENode.IsPowered=true;w.Build();once=false;
        ordinary.Provider=type=>
        {
            bool connected=w.Sys.ConnectedObjects(a.storage.EntityId,c.storage);
            if(!once){once=true;bridge.node.Tile.ENode.IsPowered=false;}
            return connected?10:0;
        };
        Must(!w.Enough(1),"forced object connectivity read also marks enclosing material scope");
        Must(ordinary.ProviderCalls>2,"forced indirect object read cannot leave cold outer answer unchecked");
        bridge.node.Tile.ENode.IsPowered=true;w.Build();bridge.node.Tile.ENode.IsPowered=false;int firstCount=-1;
        ordinary.Provider=type=>
        {
            var group=w.Sys.ObjectGroup(a.storage);
            if(firstCount<0)firstCount=group?.Nodes.Count??0;
            bridge.node.Tile.ENode.IsPowered=true;
            return group?.Nodes.Count==3?10:0;
        };
        Must(w.Enough(1),"restored object bridge can succeed only after stable retry");
        Must(firstCount==1,"unforced object dependency promotes before observing stale same-tick map, even when provider restores facts before final check");
    }
    private static void ReentrantAndExceptions()
    {
        var w=new World();var a=w.Relay(5);w.Build();bool inside=false;int nested=0;
        a.Node.Entity.OnRead=()=>
        {
            if(inside)return;inside=true;nested++;
            try
            {
                Must(w.Sys.Group(a.Storage)==null,"activation reentry cannot expose stale group fallback");
                using(var inner=w.Sys.BeginDeferredMaterialRead())Must(inner.Pool(a.Storage)==null,"nested activation refuses instead of recurring");
            }
            finally {inside=false;}
        };
        try {using(var scope=w.Sys.BeginDeferredMaterialRead()){scope.Pool(a.Storage);scope.Validate();}}
        finally {a.Node.Entity.OnRead=null;}
        Must(nested>0&&nested<20,"activation callback remains bounded");
        w.Build();var sentinel=new InvalidOperationException("original native getter");Exception seen=null;
        try {using(var scope=w.Sys.BeginDeferredMaterialRead()){a.Node.Entity.OnRead=()=>throw sentinel;scope.Pool(a.Storage);}}
        catch(Exception error){seen=error;}finally{a.Node.Entity.OnRead=null;}
        Must(ReferenceEquals(seen,sentinel),"original getter exception identity propagates");
        Must(RelayQueryBaseline.Depth==0,"topology timing wrapper cleaned after native throw");
        w.Build();using(var scope=w.Sys.BeginDeferredMaterialRead()){Must(scope.Pool(a.Storage)!=null&&scope.Validate(),"fresh scope works after exception cleanup");}
        var outer=w.Sys.BeginDeferredMaterialRead();var innerScope=w.Sys.BeginDeferredMaterialRead();outer.Dispose();
        Must(innerScope.Pool(a.Storage)!=null&&innerScope.Validate(),"out-of-order disposed ancestor cannot poison live child");innerScope.Dispose();
        var other=new World();other.Relay(5);other.Build();RelayQueryBaseline.Reset();
        using(var ownerScope=w.Sys.BeginDeferredMaterialRead())using(var unrelated=other.Sys.BeginDeferredMaterialRead())
        {
            Must(w.Sys.Group(a.Storage)!=null,"different owner scope does not hide earlier matching owner");int calls=RelayQueryBaseline.Boundaries;
            Must(unrelated.Validate()&&RelayQueryBaseline.Boundaries==calls,"unrelated owner remains cold");Must(ownerScope.Validate(),"matching ancestor gets final boundary");
        }
    }
    private static void ColdScaleProof()
    {
        var w=new World();w.Ordinary(5);for(int i=0;i<144;i++)w.Relay(1,i*20,inInventory:false);w.Build();
        for(int i=0;i<1000;i++)Must(w.Enough(1),"large registry ordinary answer");
        Must(RelayQueryBaseline.Boundaries==0&&StorageRelaySys.ActiveCalls==0,"144-node registry untouched by 1000 cold adapter reads");
        for(int i=0;i<10;i++)using(var eager=w.Sys.BeginMaterialRead())Must(eager.Validate(),"unchanged eager scope control");
        Must(RelayQueryBaseline.Boundaries==20&&StorageRelaySys.ActiveCalls==2880,"existing eager API retains both complete native passes");
        Console.WriteLine("Actual-source operation counts: 1000 ordinary-prefix adapter reads / 144 registered nodes = 0 topology boundaries, 0 Active reads; 10 eager API controls = 20 boundaries, 2880 Active reads. No Unity/FPS timing inference.");
    }
    static void Main()
    {
        ColdQueries();PromotedQueries();EarlierOrdinaryContribution();NestedDependency();IndirectAndOffline();ForcedAndLocal();IndirectObjectDependencies();ReentrantAndExceptions();ColdScaleProof();
        Console.WriteLine("Actual production lazy adapter/engine/topology passed: "+assertions+" assertions. Cold reads have zero global walks; Relay-dependent reads retain initial/final checks. Core providers/lifecycle/clock are substitutes; not Unity, save safety or FPS evidence.");
    }
}
