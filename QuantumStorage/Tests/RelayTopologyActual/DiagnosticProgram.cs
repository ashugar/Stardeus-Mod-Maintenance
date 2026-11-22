using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Components;
using Game.Data;
using Game.Systems.QuantumStorage;
using Game.Systems.QuantumStorage.Relays;

static class DiagnosticProgram
{
    private delegate bool Check(StorageRelaySys sys,out bool readable);
    private static readonly Check Matches=(Check)typeof(StorageRelaySys).GetMethod("QueryTopologyMatches",BindingFlags.NonPublic|BindingFlags.Instance).CreateDelegate(typeof(Check));
    private static int assertions;
    private static void Must(bool value,string message) { assertions++;if(!value)throw new Exception(message+" #"+assertions); }
    private static (StorageRelaySys,StorageRelayNodeComp,Entity) World(bool objects)
    {
        StorageRelayNodeComp.ReadTrace=null;StorageRelayNodeComp.OnGetter=null;RelayQueryBaseline.Vertex=-1;
        var sys=new StorageRelaySys();var e=new Entity{Id=1};var n=new StorageRelayNodeComp{Entity=e,EntityId=1};
        if(objects) { var s=new ObjStorageComp{Entity=e};e.Components=new object[]{n,s};sys.Add(s); }
        else { var s=new MatStorageComp{Entity=e};e.Components=new object[]{n,s};sys.Add(s,n); }
        sys.Build();return(sys,n,e);
    }
    private static (bool result,bool readable,string trace,int lookups,int active,Exception error) Run(bool detail,bool objects,Action<StorageRelayNodeComp,Entity> mutate,Action<string,StorageRelayNodeComp> getter=null)
    {
        var (sys,n,e)=World(objects);mutate?.Invoke(n,e);
        var trace=new List<string>();StorageRelayNodeComp.ReadTrace=trace.Add;
        StorageRelayNodeComp.OnGetter=getter==null?null:name=>getter(name,n);
        e.OnRead=()=>trace.Add("GetComponent");e.LookupCalls=0;StorageRelaySys.ActiveCalls=0;
        RelayQueryBaseline.Vertex=detail?0:-1;
        bool result=false,readable=false;Exception error=null;
        try { result=Matches(sys,out readable); }catch(Exception ex){error=ex;}
        finally { StorageRelayNodeComp.ReadTrace=null;StorageRelayNodeComp.OnGetter=null; }
        return(result,readable,string.Join(",",trace),e.LookupCalls,StorageRelaySys.ActiveCalls,error);
    }
    private static void Parity(bool objects,Action<StorageRelayNodeComp,Entity> mutation,Action<string,StorageRelayNodeComp> getter=null)
    {
        var plain=Run(false,objects,mutation,getter);var detail=Run(true,objects,mutation,getter);
        Must(plain.result==detail.result&&plain.readable==detail.readable,"result/readability parity");
        Must(plain.trace==detail.trace,"native getter evaluation order/count parity");
        Must(plain.lookups==detail.lookups&&plain.active==detail.active,"lookup/Active count parity");
        Must(ReferenceEquals(plain.error,detail.error),"same original exception object");
    }
    static void Main()
    {
        Action<StorageRelayNodeComp,Entity>[] changes={null,(n,e)=>n.EntityId=2,(n,e)=>n.NetworkId="changed",(n,e)=>n.Range=1,(n,e)=>n.Tile.Position=new Point{x=1},(n,e)=>n.Tile.Position=new Point{y=1},
            (n,e)=>n.Tile.IsActive=false,(n,e)=>n.Tile.IsConstructed=false,(n,e)=>n.Tile.Reinstallable.IsMidRelocation=true,(n,e)=>n.Tile.ENode.IsPowered=false,
            (n,e)=>n.Tile.ENode.HasDeficit=true,(n,e)=>n.Usable=false,(n,e)=>n.Healthy=false,(n,e)=>n.Tile.ENode=null,(n,e)=>n.Tile=null,(n,e)=>e.Components=e.Components.Any(c=>c is ObjStorageComp)?new object[]{n}:new object[]{n,new ObjStorageComp{Entity=e}}};
        foreach(bool objects in new[]{false,true})
        {
            foreach(var change in changes)Parity(objects,change);
            // Getter side effects occur at their original positions, without rereads.
            Parity(objects,null,(name,n)=>{if(name=="Range")n.NetworkId="changed during read";});
            var sentinel=new Exception("sentinel preserved");
            foreach(var stop in new[]{"Tile","EntityId","NetworkId","Range","Entity","IsUsable"})
                Parity(objects,null,(name,n)=>{if(name==stop)throw sentinel;});
        }
        Must(RelayQueryBaseline.Begins.All(n=>n>0),"all seven real native detail categories exercised");
        Must(RelayQueryBaseline.Begins.Zip(RelayQueryBaseline.Ends,(a,b)=>a==b).All(x=>x),"every detail timer balanced on return/exception");
        var world=World(false);RelayQueryBaseline.Vertex=0;Matches(world.Item1,out _);
        long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++)Must(Matches(world.Item1,out _),"warm match");long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Must(bytes==0,"warmed detailed topology path allocation free with inert clock");
        Console.WriteLine("Actual production diagnostic topology parity passed: "+assertions+" assertions; warmed detail allocated_bytes="+bytes+". Actual topology sources, inert Core and diagnostic clock substitutes; not Unity/Mono or FPS evidence.");
    }
}
