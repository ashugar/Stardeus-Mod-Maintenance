using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Inventory;
using Game.Systems.QuantumStorage;
using Game.Systems.QuantumStorage.Relays;

static partial class NanoAdvisoryProgram
{
    const BindingFlags FocusFlags=BindingFlags.Static|BindingFlags.NonPublic;
    static int poolClocks;
    static long poolTicks;
    static void FocusSet(string field,object value)=>typeof(RelayQueryFocus).GetField(field,FocusFlags).SetValue(null,value);
    sealed class Episode
    {
        internal bool Result;
        internal Exception Error;
        internal readonly List<string> Calls=new();
        internal int Active,Walks,ProviderCalls,Clocks;
        internal long Hashes;
        internal RelayQueryFocus.Snapshot Detail;
    }
    static Episode PoolEpisode(bool detail,int scenario,long ordinal=0,bool strict=false,bool throwClock=false)
    {
        G.Ticks=500;RelayQueryFocus.ClearQuery();FocusSet("state",null);RelayQueryFocus.Configure();
        poolClocks=0;poolTicks=100;
        FocusSet("clock",(Func<long>)(()=>{poolClocks++;if(throwClock)throw new InvalidOperationException("fixture observer");return ++poolTicks;}));
        FocusSet("allocatedBytes",null);FocusSet("allocationStatus","fixture_unavailable");
        var w=new World();var a=w.Relay(scenario==1?0:10);Item b=null;
        if(scenario==1)b=w.Relay(0,1);
        if(scenario==8)b=w.Relay(0,1);
        w.Build();Enable(w);
        if(scenario==2)a.Node.Tile.ENode.IsPowered=false; // mapped endpoint, no event notification
        if(scenario==3){a.Node.Tile.ENode.IsPowered=false;w.Build();} // already excluded endpoint
        if(scenario==4)w.Sys.MaterialReady(false);
        if(scenario==5)w.Sys.Online(false);
        if(scenario==10||scenario==11){a.Storage.Entity.Components=new object[]{a.Storage};if(scenario==10)a.Storage.QuantumPool=new QuantumStorageNetwork();}
        if(scenario==13)w.State.Relays=null;
        if(scenario==14)((Dictionary<MatStorageComp,StorageRelayNodeComp>)typeof(StorageRelaySys).GetField("nodes",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(w.Sys)).Remove(a.Storage);
        var e=new Episode();var sentinel=NativePoolSentinel;bool nested=false,changed=false;
        StorageRelayNodeComp.ReadTrace=name=>e.Calls.Add("node:"+name);
        StorageRelayNodeComp.OnGetter=name=>
        {
            if(scenario==6&&name=="IsUsable")throw sentinel;
            if(scenario==15&&!nested&&name=="IsUsable"){nested=true;e.Calls.Add("nested-active:start");w.Enough(1);e.Calls.Add("nested-active:end");}
            if(scenario==8&&!changed&&name=="IsUsable") {changed=true;w.Sys.InvalidateTopology();}
        };
        a.Storage.Entity.OnRead=()=>
        {
            e.Calls.Add("component:a");
            if(scenario==12)RelayCore.LocalDepth=1;
            if(scenario==7&&!nested)
            {
                nested=true;e.Calls.Add("nested:start");
                w.Enough(1);e.Calls.Add("nested:end");
            }
        };
        if(b!=null)b.Storage.Entity.OnRead=()=>e.Calls.Add("component:b");
        var original=a.Storage.Provider;
        a.Storage.Provider=type=>{e.Calls.Add("provider:a");int amount=original(type);if(scenario==9)w.Sys.InvalidateTopology();return amount;};
        if(b!=null){var second=b.Storage.Provider;b.Storage.Provider=type=>{e.Calls.Add("provider:b");return second(type);};}
        InventorySys.OnEligibility=storage=>e.Calls.Add("eligibility:"+(ReferenceEquals(storage,a.Storage)?"a":"b"));
        int activeStart=StorageRelaySys.ActiveCalls;long hashStart=MatStorageComp.HashCalls;
        try
        {
            if(detail){RelayQueryFocus.BeginQuery(ordinal);RelayQueryFocus.ArmEngine();}
            if(strict)e.Result=w.Enough(1);
            else using(var selection=new Selection(w))e.Result=w.Enough(1);
        }
        catch(Exception error){e.Error=error;}
        finally
        {
            if(detail)
            {
                e.Detail=RelayQueryFocus.EndQuery();
                if(e.Detail.Valid){e.Detail.Counts=(long[])e.Detail.Counts.Clone();e.Detail.Ticks=(long[])e.Detail.Ticks.Clone();e.Detail.TimedCounts=(long[])e.Detail.TimedCounts.Clone();}
            }
            e.Active=StorageRelaySys.ActiveCalls-activeStart;e.Walks=RelayQueryBaseline.Walks;e.Hashes=MatStorageComp.HashCalls-hashStart;
            e.ProviderCalls=a.Storage.ProviderCalls+(b?.Storage.ProviderCalls??0);e.Clocks=poolClocks;
            StorageRelayNodeComp.ReadTrace=null;StorageRelayNodeComp.OnGetter=null;InventorySys.OnEligibility=null;
            RelayQueryFocus.ClearQuery();RelayCore.LocalDepth=0;
        }
        return e;
    }
    static readonly Exception NativePoolSentinel=new("shared native exception identity");
    static Episode PoolParity(int scenario,long ordinal=0,bool strict=false,bool throwClock=false)
    {
        var ordinary=PoolEpisode(false,scenario,ordinal,strict,throwClock);var profiled=PoolEpisode(true,scenario,ordinal,strict,throwClock);
        Must(ordinary.Result==profiled.Result,"pool diagnostic retains native result scenario "+scenario);
        Must(ReferenceEquals(ordinary.Error,profiled.Error),"pool diagnostic retains native exception identity scenario "+scenario);
        Must(ordinary.Calls.SequenceEqual(profiled.Calls),"pool diagnostic retains exact native getter/provider order scenario "+scenario);
        Must(ordinary.Active==profiled.Active&&ordinary.Walks==profiled.Walks&&ordinary.Hashes==profiled.Hashes,"pool diagnostic adds no Active/topology/hash calls scenario "+scenario);
        Must(ordinary.ProviderCalls==profiled.ProviderCalls,"pool diagnostic adds no provider calls scenario "+scenario);
        Must(ordinary.Clocks==0&&profiled.Detail.Valid,"ordinary path has no profiling clocks and detail boundary is valid");
        Must(profiled.Clocks<=42,"bounded pool clocks across retry and nested calls");
        return profiled;
    }
    static void PoolDiagnosticChecks()
    {
        for(int category=0;category<3;category++)
        {
            var p=PoolParity(0,category);
            Must(p.Result&&p.Detail.Counts[12]==1&&p.Detail.Counts[14]==1&&p.Detail.Counts[19]==1,"one selected adapter and Relay-scope probe");
            Must(p.Detail.TimedCounts[10]==1&&p.Detail.TimedCounts[11]==1,"existing IsRelay and pool resolution measured once");
            Must(p.Detail.TimedCounts.Skip(12).Take(3).Sum()==1&&p.Detail.TimedCounts[12+category]==1,"exactly selected inner category measured");
            Must(p.Detail.Counts[20]==0&&p.Detail.Counts[21]==0,"steady mapped endpoint does not fabricate forced refresh");
            PoolParity(0,category,true);
        }
        var duplicate=PoolParity(1,1);
        Must(!duplicate.Result&&duplicate.Detail.Counts[22]==1&&duplicate.Detail.Counts[19]==1,"selected duplicate consumes a single scope probe without another provider");
        var offline=PoolParity(2,2);
        Must(!offline.Result&&offline.Detail.Counts[20]==1,"mapped offline path retains forced refresh");
        PoolParity(3,2);PoolParity(4,0);PoolParity(5,0);
        var native=PoolParity(6,2);Must(ReferenceEquals(native.Error,NativePoolSentinel),"throwing native Active keeps original exception");
        PoolParity(6,2,false,true);
        var nested=PoolParity(7,0);
        Must(nested.Detail.Counts[12]==1&&nested.Detail.Counts[19]==1,"nested component callback cannot steal adapter/scope budget");
        var activeNested=PoolParity(15,2);Must(activeNested.Detail.Counts[12]==1&&activeNested.Detail.Counts[19]==1&&activeNested.Detail.TimedCounts[14]==1,"nested native Active callback cannot steal consumed root scope budget");
        var retry=PoolParity(8,1);Must(retry.Detail.Counts[0]>1&&retry.Detail.Counts[19]==1,"generation retry preserves one probe across whole query");
        var capped=PoolParity(9,2);Must(!capped.Result&&capped.Detail.Counts[0]==3&&capped.Clocks==42&&capped.Detail.Counts[19]==1,"three native retries retain one probe and exact42clock cap");
        PoolParity(0,2,false,true);
        var quantum=PoolParity(10,2);Must(quantum.Detail.Counts[15]==1&&quantum.Detail.Counts[19]==0&&quantum.Detail.TimedCounts[11]==1,"Quantum branch classified from existing resolution without Relay scope probe");
        var local=PoolParity(11,2);Must(local.Detail.Counts[13]==1&&local.Detail.Counts[19]==0,"ordinary material branch stays ordinary");
        var guarded=PoolParity(12,2);Must(guarded.Detail.Counts[19]==1&&guarded.Detail.TimedCounts[14]==0,"local-depth guard preserves missing Active category");
        var absent=PoolParity(13,2);Must(absent.Detail.Counts[16]==1&&absent.Detail.Counts[19]==0,"missing Relay scope does not fabricate an inner probe");
        var missingNode=PoolParity(14,2);Must(!missingNode.Result&&missingNode.Detail.Counts[20]==1&&missingNode.Detail.Counts[21]==1&&missingNode.Detail.TimedCounts[14]==0,"missing mapped node refresh/refetch has no fabricated direct Active timing");
        Console.WriteLine("Actual adapter/MaterialReadScope pool probe parity passed: native Active/hash/provider/getter order retained; one-shot nested/retry and forced-refresh cases. Core/native clocks are substitutes.");
    }
}
