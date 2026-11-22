using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Game.Components;
using Game.Data;
using Game.Systems.QuantumStorage.Relays;

static class Program
{
    private delegate bool Check(StorageRelaySys sys,out bool readable);
    private static readonly Check Matches=(Check)typeof(StorageRelaySys).GetMethod("QueryTopologyMatches",BindingFlags.NonPublic|BindingFlags.Instance).CreateDelegate(typeof(Check));
    private static readonly Func<StorageRelaySys,bool> Validate=(Func<StorageRelaySys,bool>)typeof(StorageRelaySys).GetMethod("ValidateQueryTopology",BindingFlags.NonPublic|BindingFlags.Instance).CreateDelegate(typeof(Func<StorageRelaySys,bool>));
    private static void Capture(StorageRelaySys sys)=>sys.Build();
    private static int assertions,id;
    private static void Must(bool value,string reason="invariant") { assertions++;if(!value)throw new Exception("Actual topology test failed: "+reason+" #"+assertions); }
    private sealed class Item
    {
        internal StorageRelayNodeComp Node;
        internal MatStorageComp Mat;
        internal ObjStorageComp Obj;
    }
    private static Item Material(StorageRelaySys sys,int x=0,int y=0,string network="m",int range=12)
    {
        var e=new Entity {Id=++id};var n=new StorageRelayNodeComp {Entity=e,EntityId=e.Id,NetworkId=network,Range=range};n.Tile.Position=new Point{x=x,y=y};
        var s=new MatStorageComp {Entity=e};e.Components=new object[]{n,s,new object(),new object()};sys.Add(s,n);return new Item{Node=n,Mat=s};
    }
    private static Item Object(StorageRelaySys sys,int x=0,int y=0,string network="m")
    {
        var e=new Entity {Id=++id};var n=new StorageRelayNodeComp {Entity=e,EntityId=e.Id,NetworkId=network};n.Tile.Position=new Point{x=x,y=y};
        var s=new ObjStorageComp {Entity=e};e.Components=new object[]{n,s,new object(),new object()};sys.Add(s);return new Item{Node=n,Obj=s};
    }
    private static List<StorageRelayNodeComp> OracleNodes(StorageRelaySys s,bool objects=true)=>s.MaterialNodes.Concat(objects?s.ObjectNodes.Select(o=>o.Entity.GetComponent<StorageRelayNodeComp>()):Enumerable.Empty<StorageRelayNodeComp>()).ToList();
    private static bool OracleActive(StorageRelayNodeComp n)=>n!=null&&n.Tile!=null&&n.Tile.IsActive&&n.Tile.IsConstructed&&
        n.Tile.Reinstallable?.IsMidRelocation!=true&&n.Tile.ENode!=null&&n.Tile.ENode.IsPowered&&!n.Tile.ENode.HasDeficit&&n.Usable&&n.Healthy;
    private static HashSet<StorageRelayNodeComp> OracleReach(StorageRelaySys sys,StorageRelayNodeComp root,bool objects=true)
    {
        var found=new HashSet<StorageRelayNodeComp>();if(!OracleActive(root))return found;
        var all=OracleNodes(sys,objects);if(!all.Contains(root))return found;
        var todo=new Queue<StorageRelayNodeComp>();found.Add(root);todo.Enqueue(root);
        bool ObjectKind(StorageRelayNodeComp n)=>n.Entity.Components!=null&&n.Entity.Components.Any(c=>c is ObjStorageComp);
        while(todo.Count>0)
        {
            var a=todo.Dequeue();foreach(var b in all)
            {
                if(found.Contains(b)||!OracleActive(b)||a.NetworkId!=b.NetworkId||ObjectKind(a)!=ObjectKind(b))continue;
                long dx=(int)a.Tile.Position.x-(int)b.Tile.Position.x,dy=(int)a.Tile.Position.y-(int)b.Tile.Position.y;long r=Math.Min(a.Range,b.Range);
                if(dx*dx+dy*dy<=r*r){found.Add(b);todo.Enqueue(b);}
            }
        }
        return found;
    }
    private static void VerifyMap(StorageRelaySys sys,IEnumerable<Item> items,bool objects=true)
    {
        Must(Validate(sys),"validate readable current world");Must(Matches(sys,out var readable)&&readable,"fresh snapshot matches");
        using(var scope=sys.BeginMaterialRead())
        {
            Must(scope.Valid,"scope valid");foreach(var i in items.Where(i=>i.Mat!=null))
            {
                var actual=scope.Pool(i.Mat);var expected=OracleReach(sys,i.Node,objects);
                bool kindObject=i.Node.Entity.Components.Any(c=>c is ObjStorageComp);
                if(expected.Count==0||kindObject){Must(actual==null,"no eligible material pool");continue;}
                Must(actual!=null,"material pool exists");Must(new HashSet<StorageRelayNodeComp>(actual.Nodes).SetEquals(expected),"independent BFS group parity");
            }
            Must(scope.Validate(),"final boundary fresh");
        }
    }
    private static void MutationChecks()
    {
        var sys=new StorageRelaySys();var a=Material(sys,0);var bridge=Material(sys,10);var c=Material(sys,20);var obj=Object(sys,4);var items=new[]{a,bridge,c,obj};sys.Build();VerifyMap(sys,items);
        var stock=a.Mat.Contents;stock["steel"]=40;var res=new Reservation{Type="steel",Amount=10};a.Mat.Reservations.Add(res);
        var changes=new Action[]{
            ()=>bridge.Node.Tile.ENode.IsPowered=false,()=>bridge.Node.Tile.ENode.IsPowered=true,
            ()=>bridge.Node.Tile.ENode.HasDeficit=true,()=>bridge.Node.Tile.ENode.HasDeficit=false,
            ()=>bridge.Node.Tile.IsActive=false,()=>bridge.Node.Tile.IsActive=true,
            ()=>bridge.Node.Tile.IsConstructed=false,()=>bridge.Node.Tile.IsConstructed=true,
            ()=>bridge.Node.Tile.Reinstallable.IsMidRelocation=true,()=>bridge.Node.Tile.Reinstallable.IsMidRelocation=false,
            ()=>bridge.Node.Usable=false,()=>bridge.Node.Usable=true,()=>bridge.Node.Healthy=false,()=>bridge.Node.Healthy=true,
            ()=>bridge.Node.Tile.Position=new Point{x=100,y=0},()=>bridge.Node.Tile.Position=new Point{x=10,y=0},
            ()=>bridge.Node.NetworkId="other",()=>bridge.Node.NetworkId="m",()=>bridge.Node.Range=1,()=>bridge.Node.Range=12,
            ()=>bridge.Node.EntityId+=10000,()=>bridge.Node.EntityId-=10000
        };
        foreach(var change in changes)
        {
            change();Must(!Matches(sys,out var readable)&&readable,"same-tick public mutation detected");VerifyMap(sys,items);
            Must(ReferenceEquals(stock,a.Mat.Contents)&&stock["steel"]==40&&ReferenceEquals(res,a.Mat.Reservations[0])&&res.Amount==10,"query preserves physical records");
        }
        // Public component array mutation in place cannot hide a kind change.
        var components=bridge.Node.Entity.Components;components[2]=new ObjStorageComp{Entity=bridge.Node.Entity};
        Must(!Matches(sys,out var kindReadable)&&kindReadable,"in-place kind change");VerifyMap(sys,items);
        components[2]=new object();Must(!Matches(sys,out _) ,"in-place kind restore");VerifyMap(sys,items);
        // Native object node rediscovery must not become a cached node reference.
        var replacement=new StorageRelayNodeComp{Entity=obj.Node.Entity,EntityId=obj.Node.EntityId,Tile=obj.Node.Tile,NetworkId="m"};
        obj.Node.Entity.Components[0]=replacement;obj.Node=replacement;
        Must(!Matches(sys,out var objectReadable)&&objectReadable,"same-array object node replacement");VerifyMap(sys,items);
        sys.Ready(false);Must(!Matches(sys,out _),"object readiness transition");VerifyMap(sys,items,false);
        sys.Ready(true);VerifyMap(sys,items);
        int calls=bridge.Node.Entity.LookupCalls;Must(Matches(sys,out _));Must(bridge.Node.Entity.LookupCalls>calls,"native kind getter retained");
    }
    private static void MembershipChecks()
    {
        var sys=new StorageRelaySys();var a=Material(sys);var b=Material(sys,10);sys.Build();
        sys.Remove(new MatStorageComp());Must(!Matches(sys,out var absentReadable)&&absentReadable,"remove-absent still invalidates audited writer revision");VerifyMap(sys,new[]{a,b});
        var originalGroup=sys.GroupFor(a.Mat);
        sys.Add(a.Mat,a.Node);Must(!Matches(sys,out var readable)&&readable,"same-key overwrite touches revision");VerifyMap(sys,new[]{a,b});
        Must(ReferenceEquals(originalGroup,sys.GroupFor(a.Mat)),"redundant Attach refreshes membership stamp without replacing physical group state");
        var old=a.Node;var changed=new StorageRelayNodeComp{Entity=old.Entity,EntityId=old.EntityId,Tile=old.Tile};a.Node=changed;a.Mat.Entity.Components[0]=changed;sys.Add(a.Mat,changed);
        old.Tile=null;Must(!Matches(sys,out readable)&&readable,"same-count node replacement does not read stale tombstone");VerifyMap(sys,new[]{a,b});
        sys.Remove(b.Mat);var c=Material(sys,10);b.Node.Tile=null;Must(!Matches(sys,out readable)&&readable,"same-count remove/add identity swap");VerifyMap(sys,new[]{a,c});
        var invalid=Material(sys,30);invalid.Node.Tile=null;int before=StorageRelaySys.Refreshes;using(var scope=sys.BeginMaterialRead()){Must(!scope.Valid,"unreadable new registry refuses");}
        Must(StorageRelaySys.Refreshes==before,"unreadable does not enter rebuild");sys.Remove(invalid.Mat);VerifyMap(sys,new[]{a,c});
        var o=Object(sys,2);VerifyMap(sys,new[]{a,c,o});sys.Remove(o.Obj);var p=Object(sys,3);Must(!Matches(sys,out readable)&&readable,"object registry same count replacement");VerifyMap(sys,new[]{a,c,p});
        var oldObjectEntity=p.Obj.Entity;var newEntity=new Entity{Id=oldObjectEntity.Id};var newNode=new StorageRelayNodeComp{Entity=newEntity,EntityId=newEntity.Id,Tile=p.Node.Tile};
        var newObject=new ObjStorageComp{Entity=newEntity};newEntity.Components=new object[]{newNode,newObject};sys.Add(newObject);p.Obj.Entity=null;p.Obj=newObject;p.Node=newNode;
        Must(!Matches(sys,out readable)&&readable,"object same-id overwrite does not read old entity");VerifyMap(sys,new[]{a,c,p});
        sys.Clear();Must(!Matches(sys,out _),"unload resets snapshot");VerifyMap(sys,Array.Empty<Item>());
    }
    private static void ScopeChecks()
    {
        var sys=new StorageRelaySys();var a=Material(sys);var b=Material(sys,10);sys.Build();
        using(var outer=sys.BeginMaterialRead())
        {
            Must(outer.Valid&&outer.Pool(a.Mat)!=null);using(var inner=sys.BeginMaterialRead()){Must(inner.Valid&&inner.Validate());}Must(outer.Validate(),"nested scope restores outer");
            b.Node.Tile.ENode.HasDeficit=true;Must(!outer.Validate(),"final boundary discards direct public change");
        }
        VerifyMap(sys,new[]{a,b});b.Node.Tile.ENode.HasDeficit=false;VerifyMap(sys,new[]{a,b});
        bool once=false;var replacement=new StorageRelayNodeComp{Entity=a.Node.Entity,EntityId=a.Node.EntityId,Tile=a.Node.Tile};
        a.Node.Entity.OnRead=()=>{if(!once){once=true;sys.Add(a.Mat,replacement);}};
        Must(!Matches(sys,out var readable)&&readable,"reentrant private membership mutation invalidates traversal");a.Node.Entity.OnRead=null;a.Node=replacement;a.Mat.Entity.Components[0]=replacement;VerifyMap(sys,new[]{a,b});
        var sentinel=new Exception("fixture sentinel");Exception seen=null;try{using(var s=sys.BeginMaterialRead()){throw sentinel;}}catch(Exception e){seen=e;}
        Must(ReferenceEquals(sentinel,seen),"exception identity unchanged");
        var active=typeof(StorageRelaySys).GetField("materialRead",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);Must(active==null,"exception scope cleanup");
        RelayCore.LocalDepth=1;Must(sys.BeginMaterialRead()==null,"local operations do not pool");RelayCore.LocalDepth=0;
        sys.SetRebuilding(true);using(var s=sys.BeginMaterialRead()){Must(!s.Valid,"rebuilding scope refuses");}sys.SetRebuilding(false);
        using(var s=sys.BeginMaterialRead()){sys.Online(false);Must(!s.Validate(),"lifecycle change invalidates");}sys.Online(true);
    }
    private static void RandomWorlds()
    {
        var random=new Random(941);for(int world=0;world<150;world++)
        {
            var sys=new StorageRelaySys();var items=new List<Item>();
            for(int n=0;n<18;n++){var i=n%5==0?Object(sys,random.Next(-20,21),random.Next(-20,21),"g"+random.Next(3)):Material(sys,random.Next(-20,21),random.Next(-20,21),"g"+random.Next(3),random.Next(1,13));items.Add(i);}
            sys.Build();VerifyMap(sys,items);
            for(int step=0;step<8;step++)
            {
                var selected=items[random.Next(items.Count)];switch(step%4){case 0:selected.Node.Tile.ENode.HasDeficit=!selected.Node.Tile.ENode.HasDeficit;break;case 1:selected.Node.Tile.Position=new Point{x=random.Next(-20,21),y=random.Next(-20,21)};break;case 2:selected.Node.NetworkId="g"+random.Next(3);break;default:selected.Node.Range=random.Next(1,13);break;}
                VerifyMap(sys,items);
            }
        }
    }
    private static void ReentrantCaptureChecks()
    {
        var sys=new StorageRelaySys();var a=Material(sys);var b=Material(sys,10);var o=Object(sys,2);sys.Build();
        bool once=false;o.Node.Entity.OnRead=()=>{if(!once){once=true;a.Node.Range++;Capture(sys);}};
        Must(!Matches(sys,out var readable)&&readable,"reentrant capture invalidates old object traversal");
        o.Node.Entity.OnRead=null;Must(Matches(sys,out readable)&&readable,"inner publication survives outer traversal");
        once=false;a.Node.Entity.OnRead=()=>{if(!once){once=true;a.Node.Range++;Capture(sys);}};
        Capture(sys);a.Node.Entity.OnRead=null;Must(Matches(sys,out readable)&&readable,"inner publication survives outer capture");
        once=false;a.Node.Entity.OnRead=()=>{if(!once){once=true;sys.Remove(b.Mat);b.Node.Tile=null;sys.Build();}};
        Must(!Matches(sys,out readable)&&readable,"reentrant shrink does not dereference cleared/stale tail");
        a.Node.Entity.OnRead=null;VerifyMap(sys,new[]{a,o});
        // Reset during capture must not let the old capture republish retained references.
        once=false; a.Node.Range++; int clearReads=0; a.Node.Entity.OnRead=()=>{if(++clearReads==2&&!once){once=true;sys.Clear();}};Capture(sys);a.Node.Entity.OnRead=null;
        Must(!Matches(sys,out _),"reset invalidates old capture");VerifyMap(sys,Array.Empty<Item>());
    }
    private static void TransactionChecks()
    {
        // The earliest vertex is a bridge; mutate it from a later native getter.
        var sys=new StorageRelaySys();var bridge=Material(sys,10);var left=Material(sys,0);var right=Material(sys,20);var items=new[]{bridge,left,right};sys.Build();
        typeof(StorageRelaySys).GetMethod("ResetQueryTopology",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(sys,null); bool once=false;right.Node.Entity.OnRead=()=>{if(!once){once=true;bridge.Node.Tile.ENode.IsPowered=false;}};
        Must(!sys.TryBuild(),"public mutation after earlier build fact must abort actual transaction");
        right.Node.Entity.OnRead=null;Must(!Matches(sys,out _),"failed build must not bless old graph with new facts");VerifyMap(sys,items);
        bridge.Node.Tile.ENode.IsPowered=true;VerifyMap(sys,items);

        typeof(StorageRelaySys).GetMethod("ResetQueryTopology",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(sys,null); once=false;right.Node.Entity.OnRead=()=>{if(!once){once=true;sys.Remove(bridge.Mat);}};
        Must(!sys.TryBuild(),"membership mutation during actual vertex construction aborts");right.Node.Entity.OnRead=null;VerifyMap(sys,new[]{left,right});
        sys.Add(bridge.Mat,bridge.Node);VerifyMap(sys,items);

        once=false;StorageRelaySys.OnRefresh=_=>{if(!once){once=true;bridge.Node.Tile.ENode.HasDeficit=true;}};
        bridge.Node.Range=11;
        try { Must(!sys.TryBuild(),"public mutation during actual group refresh aborts before publication"); }
        finally { StorageRelaySys.OnRefresh=null; }
        VerifyMap(sys,items);bridge.Node.Tile.ENode.HasDeficit=false;VerifyMap(sys,items);

        once=false;bool blocked=false;right.Node.Entity.OnRead=()=>{if(!once){once=true;blocked=sys.Group(left.Mat)==null;}};
        try { Must(sys.TryBuild(),"read callback can be refused without corrupting actual transaction"); }
        finally { right.Node.Entity.OnRead=null; }
        Must(blocked,"Group during native build getter refuses recursive/partial graph");VerifyMap(sys,items);

        var sentinel=new Exception("refresh fixture sentinel");Exception observed=null;bridge.Node.Range=10;
        StorageRelaySys.OnRefresh=_=>throw sentinel;
        try { try {sys.TryBuild();}catch(Exception e){observed=e;} }
        finally {StorageRelaySys.OnRefresh=null;}
        Must(ReferenceEquals(observed,sentinel),"actual build preserves original callback exception");
        Must(!(bool)typeof(StorageRelaySys).GetField("rebuilding",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(sys),"throwing transaction releases building guard");
        VerifyMap(sys,items);

        // Normal notifications must run after publication and outside building,
        // even while the caller has an older generation-bound read scope.
        bool sawDisplay=false;
        using(var outer=sys.BeginMaterialRead())
        {
            bridge.Node.Tile.ENode.IsPowered=false;
            QuantumStorageReflection.OnDisplay=s=>
            {
                if(!ReferenceEquals(s,left.Mat))return;
                sawDisplay=true;var current=sys.Group(left.Mat);
                Must(current!=null&&new HashSet<StorageRelayNodeComp>(current.Nodes).SetEquals(new[]{left.Node}),"native display sees committed current map with old outer scope");
            };
            try { Must(!outer.Validate(),"old outer scope discarded after callback-safe rebuild"); }
            finally { QuantumStorageReflection.OnDisplay=null; }
        }
        Must(sawDisplay,"actual transaction emitted normal notification");VerifyMap(sys,items);

        // A post-publication native notification may remove a registration;
        // captured targets must not enumerate a live dictionary or bless it.
        once=false;QuantumStorageReflection.OnDisplay=_=>{if(!once){once=true;sys.Remove(right.Mat);}};
        bridge.Node.Tile.ENode.IsPowered=true;
        try { Must(!sys.TryBuild(),"notification membership change refuses stale final map"); }
        finally { QuantumStorageReflection.OnDisplay=null; }
        VerifyMap(sys,new[]{bridge,left});
    }
    private static void UnchangedFastPathChecks()
    {
        var sys=new StorageRelaySys();var a=Material(sys,0);var b=Material(sys,10);var o=Object(sys,0);sys.Build();
        int refreshes=StorageRelaySys.Refreshes; StorageRelaySys.ActiveCalls=0;
        int aReads=a.Node.Entity.LookupCalls,bReads=b.Node.Entity.LookupCalls,oReads=o.Node.Entity.LookupCalls;
        bool display=false;QuantumStorageReflection.OnDisplay=_=>display=true;
        try {Must(sys.TryBuild(),"unchanged forced update succeeds");}finally {QuantumStorageReflection.OnDisplay=null;}
        Must(a.Node.Entity.LookupCalls-aReads==2&&b.Node.Entity.LookupCalls-bReads==2&&o.Node.Entity.LookupCalls-oReads==4,"unchanged force performs two full native getter passes including object rediscovery");
        Must(StorageRelaySys.ActiveCalls==6,"unchanged force performs exactly two full Active passes"); Must(StorageRelaySys.Refreshes==refreshes&&!display,"unchanged force performs no group refresh or UI notification");
        bool once=false,blocked=false;
        b.Node.Entity.OnRead=()=>{if(!once){once=true;blocked=sys.Group(a.Mat)==null&&sys.ObjectGroup(o.Obj)==null&&!sys.ConnectedObjects(o.Obj.EntityId,o.Obj);a.Node.Tile.ENode.IsPowered=false;}};
        try {Must(sys.TryBuild(),"late first-pass public mutation rebuilds safely");}finally {b.Node.Entity.OnRead=null;}
        Must(once&&blocked,"fast getter callbacks refuse all recursive group access");
        Must(sys.Group(a.Mat)==null,"second pass detects earlier-node mutation rather than blessing old graph");VerifyMap(sys,new[]{a,b,o});
        typeof(StorageRelaySys).GetMethod("ResetQueryTopology",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(sys,null);
        Must(!Matches(sys,out _),"not-ready snapshot cannot qualify fast path");Must(sys.TryBuild()&&Matches(sys,out _),"missing snapshot rebuilds coherently");
        o.Obj.Entity=null;Must(!sys.TryBuild(),"unreadable registry refuses fast and build paths");
    }
    private static void AllocationCheck()
    {
        var sys=new StorageRelaySys();for(int i=0;i<128;i++)Material(sys,i);for(int i=0;i<16;i++)Object(sys,i);sys.Build();
        for(int i=0;i<1000;i++)if(!Matches(sys,out _))throw new Exception("warm match");
        long before=GC.GetAllocatedBytesForCurrentThread();bool valid=true;var watch=Stopwatch.StartNew();
        // Stopwatch allocation is deliberately excluded from the measured hot loop.
        MatStorageComp.HashCalls=ObjStorageComp.HashCalls=0;
        before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++)valid&=Matches(sys,out _);long allocated=GC.GetAllocatedBytesForCurrentThread()-before;watch.Stop();
        Must(valid&&allocated==0,"warmed actual predicate allocation-free");
        Must(MatStorageComp.HashCalls==0&&ObjStorageComp.HashCalls==0,"steady actual predicate performs no registry/snapshot key hashing");
        Console.WriteLine("Actual predicate desktop CLR: 10000 checks, nodes=144, allocated_bytes="+allocated+", key_hash_calls="+(MatStorageComp.HashCalls+ObjStorageComp.HashCalls)+", elapsed_ms="+watch.Elapsed.TotalMilliseconds.ToString("F3")+". Not Unity Mono/FPS evidence.");
        // Historical pre-fast-path graph updater allocated; unchanged update now avoids
        // immutable transaction inputs. Changed builds still allocate; only claim the
        // warmed predicate's zero-allocation claim.
        bool builds=true;before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<100;i++)builds&=sys.TryBuild();
        long buildBytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Must(builds&&buildBytes==0,"forced unchanged updates remain coherent and allocation-free"); Must(MatStorageComp.HashCalls==0&&ObjStorageComp.HashCalls==0,"unchanged metadata updates perform no key hashing");
        Console.WriteLine("Actual metadata transaction desktop CLR: 100 forced unchanged updates, allocated_bytes="+buildBytes+", mean_bytes_per_update="+(buildBytes/100.0).ToString("F1")+". Unchanged fast path; changed builds still allocate. Not Unity Mono/FPS evidence.");
    }
    private static void ObjectCallerChecks()
    {
        var sys=new StorageRelaySys();var a=Object(sys,0,0,"o");var bridge=Object(sys,10,0,"o");var c=Object(sys,20,0,"o");var material=Material(sys,0,0,"o");sys.Build();
        Must(sys.ObjectGroup(a.Obj)!=null&&sys.ConnectedObjects(a.Obj.EntityId,c.Obj),"actual native object callers use connected map");
        Must(sys.ObjectGroup(a.Obj).Nodes.Count==3&&sys.Group(material.Mat).Nodes.Count==1,"material/object kinds never share stock groups");
        RelayObjectCore.LocalDepth=1;Must(sys.ObjectGroup(a.Obj)==null,"object local scope retains refusal");RelayObjectCore.LocalDepth=0;
        bridge.Node.Tile.ENode.IsPowered=false;Must(!sys.ConnectedObjects(a.Obj.EntityId,c.Obj),"object force caller rebuilds same-tick bridge split");
        Must(sys.ObjectGroup(bridge.Obj)==null,"offline object endpoint remains inaccessible");
        bridge.Node.Tile.ENode.IsPowered=true;Must(sys.ConnectedObjects(a.Obj.EntityId,c.Obj),"object force caller sees same-tick merge");
        bool once=false;StorageRelaySys.OnRefresh=_=>{if(!once){once=true;sys.Remove(bridge.Obj);}};a.Node.Range=11;
        try {Must(!sys.ConnectedObjects(a.Obj.EntityId,c.Obj),"object force caller refuses aborted metadata build");}
        finally {StorageRelaySys.OnRefresh=null;}
        Must(!sys.ConnectedObjects(a.Obj.EntityId,c.Obj),"object split remains true after stable retry");
        sys.Ready(false);Must(sys.ObjectGroup(a.Obj)==null&&!sys.ConnectedObjects(a.Obj.EntityId,c.Obj),"objects-ready refusal preserved");sys.Ready(true);
        VerifyMap(sys,new[]{a,c,material});
    }
    public static void Main()
    {
        MutationChecks();MembershipChecks();ScopeChecks();ReentrantCaptureChecks();TransactionChecks();ObjectCallerChecks();UnchangedFastPathChecks();RandomWorlds();AllocationCheck();
        Console.WriteLine("Actual production topology query code passed: "+assertions+" assertions. Native field/getter and lifecycle substitutes are inert; Unity/Core integration and FPS remain separate checks.");
    }
}
