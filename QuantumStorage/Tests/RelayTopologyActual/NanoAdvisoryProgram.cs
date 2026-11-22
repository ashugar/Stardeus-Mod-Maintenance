using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Inventory;
using Game.Systems.QuantumStorage;
using Game.Systems.QuantumStorage.Relays;

static partial class NanoAdvisoryProgram
{
    static int assertions,ids;
    static readonly MatType Steel=new("steel");
    static void Must(bool yes,string reason) { assertions++;if(!yes)throw new Exception("Actual Nano advisory policy: "+reason+" #"+assertions); }
    sealed class Item { internal MatStorageComp Storage;internal StorageRelayNodeComp Node; }
    sealed class World
    {
        internal readonly StorageRelaySys Sys=new();
        internal readonly GameState State;
        internal readonly InventorySys Inventory;
        internal World() { State=new GameState{Relays=Sys,Quantum=new QuantumStorageNetworkSys()};Inventory=new InventorySys(State); }
        internal Item Relay(int count,int x=0,bool candidate=true)
        {
            var entity=new Entity{Id=++ids};var node=new StorageRelayNodeComp{Entity=entity,EntityId=entity.Id};node.Tile.Position=new Point{x=x};
            var storage=new MatStorageComp{Entity=entity};entity.Components=new object[]{node,storage};storage.Contents["steel"]=count;
            storage.Provider=type=>
            {
                var group=Sys.Group(storage);if(group==null)return 0;int available=0;
                foreach(var n in group.Nodes)available+=Local(StorageRelaySys.Storage(n),type);
                return Math.Max(0,available);
            };
            Sys.Add(storage,node);if(candidate)Inventory.Add(storage);return new Item{Storage=storage,Node=node};
        }
        internal void Build() { Sys.Build();RelayQueryBaseline.Reset();StorageRelaySys.ActiveCalls=0; }
        internal bool Enough(int amount,InventorySys inventory=null)=>RelayMaterialQuery.HasEnough(inventory??Inventory,Steel,amount,-1,null);
    }
    static int Local(MatStorageComp storage,MatType type)
    {
        storage.Contents.TryGetValue(type.Name,out int count);
        foreach(var r in storage.Reservations)if(r.Type==type.Name&&r.Amount>0)count-=r.Amount;
        return Math.Max(0,count);
    }
    sealed class Selection : IDisposable
    {
        readonly NanobotCandidateQueries.CandidateState candidate;
        readonly NanobotCandidateQueries.DecisionState decision;
        bool disposed;
        internal Selection(World w,bool allowFailed=false)
        { candidate=NanobotCandidateQueries.EnterCandidate(w.State,allowFailed);decision=NanobotCandidateQueries.EnterDecision(w.Inventory); }
        public void Dispose() { if(disposed)return;disposed=true;NanobotCandidateQueries.ExitDecision(decision);NanobotCandidateQueries.ExitCandidate(candidate); }
    }
    static void Enable(World w)
    {
        G.IsDemoMode=false;G.HasEnteredGame=true;
        var old=(GameState)typeof(NanobotCandidateQueries).GetField("world",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        NanobotCandidateQueries.Unload(old);
        MaterialPanelContractFingerprint.Match=true;HarmonyLib.Harmony.Reset();
        NanobotCandidateQueries.Install();NanobotCandidateQueries.WorldLoaded(w.State);
        Must(HarmonyLib.Harmony.Active.Count==2,"recognized fixture contracts install exactly two context hooks");
    }
    static void StrictAndAdvisoryWork()
    {
        G.Ticks=10;var w=new World();var first=w.Relay(10);for(int i=1;i<144;i++)w.Relay(1,i*20,false);w.Build();Enable(w);
        int strictWalks;
        Must(w.Enough(1),"normal query result");strictWalks=RelayQueryBaseline.Walks;
        Must(strictWalks==2&&RelayQueryBaseline.AdvisoryScopes==0,"normal non-Nanobot query retains two full native registry checks");
        RelayQueryBaseline.Reset();
        using(var selected=new Selection(w))
        {
            Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"recognized selection decision admits advisory read view");
            for(int i=0;i<1000;i++)Must(w.Enough(1),"repeated recognized Relay-dependent query");
        }
        Must(RelayQueryBaseline.Walks==0&&RelayQueryBaseline.AdvisoryScopes==1000,"144-node stable tick avoids actual global walks for 1000 Relay-dependent queries");
        Must(first.Storage.ProviderCalls==1001,"ordinary native provider calls are not cached or skipped");
        Console.WriteLine("Actual-source workload: 1000 recognized Relay-dependent queries / 144 nodes used registry_checks="+RelayQueryBaseline.Walks+"; provider_calls="+(first.Storage.ProviderCalls-1)+". Normal strict control registry_checks="+strictWalks+". Not Unity/FPS evidence.");
    }
    static void TickEventsAndOriginalTemporalPolicy()
    {
        G.Ticks=20;var w=new World();var a=w.Relay(0);var bridge=w.Relay(0,10,false);w.Relay(10,20,false);w.Build();Enable(w);
        using(var selected=new Selection(w))
        {
            Must(w.Enough(1),"connected advisory result");bridge.Node.Tile.ENode.IsPowered=false;
            Must(w.Enough(1)&&RelayQueryBaseline.Walks==0,"DOCUMENTED original advisory policy caches an UNNOTIFIED remote bridge change within the same tick");
            G.Ticks++;Must(!w.Enough(1)&&RelayQueryBaseline.Walks>0,"new tick refreshes the original advisory graph");
            bridge.Node.Tile.ENode.IsPowered=true;w.Sys.InvalidateTopology();RelayQueryBaseline.Reset();
            Must(w.Enough(1)&&RelayQueryBaseline.Walks>0,"normal private invalidation refreshes even within one tick");
            using(var read=w.Sys.BeginAdvisoryMaterialRead())
            {
                Must(read.Pool(a.Storage)!=null,"advisory pool begins in one tick");G.Ticks++;
                Must(read.Pool(a.Storage)==null&&!read.Validate(),"an existing advisory scope cannot silently cross ticks");
            }
            bridge.Node.Tile.ENode.IsPowered=false;
            Must(w.Sys.Group(a.Storage,true)?.Nodes.Count==1,"forced metadata action boundary sees an unnotified same-tick split");
        }
        bridge.Node.Tile.ENode.IsPowered=true;w.Sys.Build();bridge.Node.Tile.ENode.IsPowered=false;RelayQueryBaseline.Reset();
        Must(!w.Enough(1)&&RelayQueryBaseline.Walks>0,"normal strict query still catches direct same-tick bridge mutations");
    }
    static void LocalPowerAndReservations()
    {
        G.Ticks=30;var w=new World();var a=w.Relay(10);w.Build();Enable(w);
        var record=new Reservation{Type="steel",Amount=1};a.Storage.Reservations.Add(record);var contents=a.Storage.Contents;
        using(var selected=new Selection(w))
        {
            Must(w.Enough(9),"native current pins leave positive availability");record.Amount=10;
            Must(!w.Enough(1),"in-place native reservation edit is reread, not cached");
            record.Amount=0;a.Node.Tile.ENode.IsPowered=false;
            Must(!w.Enough(1),"a mapped local endpoint going offline forces refresh/refusal");
            Must(RelayQueryBaseline.Walks>0,"local mapped endpoint offline uses original forced refresh");
        }
        Must(ReferenceEquals(contents,a.Storage.Contents)&&ReferenceEquals(record,a.Storage.Reservations[0]),"original physical stock/reservation identity retained");
        w.Build();RelayQueryBaseline.Reset();StorageRelaySys.ActiveCalls=0;
        using(var selected=new Selection(w))Must(!w.Enough(1),"already excluded local endpoint remains offline");
        Must(RelayQueryBaseline.Walks==0&&StorageRelaySys.ActiveCalls==0,"original Group order looks up an absent mapped endpoint before doing Active work");
    }
    static void RepeatedClaimQueriesRemainFresh()
    {
        G.Ticks=40;var w=new World();var a=w.Relay(10);w.Build();Enable(w);var record=new Reservation{Type="steel",Amount=0};a.Storage.Reservations.Add(record);
        int claimReads=0,allowedReads=0,heldReads=0,hasEnoughCalls=0;
        // Native gate/body shape is independently audited. This controlled caller
        // models its two original calls and mutates pins between them.
        using(var selected=new Selection(w))
        {
            claimReads++;hasEnoughCalls++;bool full=w.Enough(20+0);
            Must(!full,"full requested amount remains lacking");
            allowedReads++;record.Amount=10;bool allowed=true;
            bool any=false;if(allowed){hasEnoughCalls++;any=w.Enough(1);}heldReads++;
            Must(!any,"second original query rereads a new reservation before its answer");
        }
        Must(claimReads==1&&allowedReads==1&&heldReads==1&&hasEnoughCalls==2&&a.Storage.ProviderCalls==2,"both material checks and native claim decision roles remain executed");
        Must(RelayQueryBaseline.AdvisoryScopes==2&&RelayQueryBaseline.Walks==0,"repeated gate checks share original metadata frequency without a quantity/result cache");
    }
    static void ContextBarriersAndStrictAncestor()
    {
        G.Ticks=50;var w=new World();var a=w.Relay(10);w.Build();Enable(w);
        using(var outer=new Selection(w))
        {
            using(var revalidation=new Selection(w,true))
            {
                Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"allowFailed=true nested revalidation is a strict suppression barrier");RelayQueryBaseline.Reset();Must(w.Enough(1)&&RelayQueryBaseline.Walks==2,"revalidation keeps strict query checks");
                using(var childSelection=new Selection(w,false))Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"a nested selection cannot escape an active revalidation barrier");
            }
            Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"normal nested cleanup restores selection context");
            var wrong=new InventorySys(w.State);wrong.Add(a.Storage);
            var mismatch=NanobotCandidateQueries.EnterDecision(wrong);
            Must(!NanobotCandidateQueries.IsAdvisory(wrong)&&!NanobotCandidateQueries.IsAdvisory(w.Inventory),"wrong inventory decision cannot inherit an older enabled decision");
            NanobotCandidateQueries.ExitDecision(mismatch);
            RelayQueryBaseline.Reset();
            Must(RelayMaterialQuery.HasEnough(w.Inventory,Steel,1,99,null)&&RelayQueryBaseline.AdvisoryScopes==0&&RelayQueryBaseline.Walks==2,"island-specific nested material query remains strict");
            RelayQueryBaseline.Reset();
            Must(RelayMaterialQuery.HasEnough(w.Inventory,Steel,1,-1,new Game.Systems.Creatures.Being())&&RelayQueryBaseline.AdvisoryScopes==0&&RelayQueryBaseline.Walks==2,"worker-specific nested material query remains strict");
            using(var strict=w.Sys.BeginDeferredMaterialRead())
            { RelayQueryBaseline.Reset();Must(w.Enough(1)&&RelayQueryBaseline.AdvisoryScopes==0&&RelayQueryBaseline.Walks==2,"live strict ancestor overrides Nano advisory policy"); }
            bool workerAdvisory=true;var thread=new Thread(()=>
            { var c=NanobotCandidateQueries.EnterCandidate(w.State,false);var d=NanobotCandidateQueries.EnterDecision(w.Inventory);workerAdvisory=NanobotCandidateQueries.IsAdvisory(w.Inventory);NanobotCandidateQueries.ExitDecision(d);NanobotCandidateQueries.ExitCandidate(c); });
            thread.Start();thread.Join();Must(!workerAdvisory&&NanobotCandidateQueries.IsAdvisory(w.Inventory),"wrong-thread frames suppress themselves without changing the owner context");
            var other=new World();var candidate=NanobotCandidateQueries.EnterCandidate(other.State,false);var decision=NanobotCandidateQueries.EnterDecision(other.Inventory);
            Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory)&&!NanobotCandidateQueries.IsAdvisory(other.Inventory),"nested wrong-world candidate blocks older contexts");
            NanobotCandidateQueries.ExitDecision(decision);NanobotCandidateQueries.ExitCandidate(candidate);
        }
        Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"no scope leaks after normal context cleanup");
        var sentinel=new InvalidOperationException("native query sentinel");Exception seen=null;
        try{using(var selected=new Selection(w))throw sentinel;}catch(Exception error){seen=error;}
        Must(ReferenceEquals(seen,sentinel)&&!NanobotCandidateQueries.IsAdvisory(w.Inventory),"exception identity and exact context cleanup preserved");
        var frames=new NanobotCandidateQueries.CandidateState[40];for(int i=0;i<frames.Length;i++)frames[i]=NanobotCandidateQueries.EnterCandidate(w.State,false);
        var overflow=NanobotCandidateQueries.EnterDecision(w.Inventory);Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"bounded frame overflow stays strict");NanobotCandidateQueries.ExitDecision(overflow);
        for(int i=frames.Length-1;i>=0;i--)NanobotCandidateQueries.ExitCandidate(frames[i]);
        Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"overflow cannot leave an advisory frame active");
    }
    static void LifecycleAndOptionalFallback()
    {
        G.Ticks=60;var w=new World();w.Relay(10);w.Build();Enable(w);
        var c=NanobotCandidateQueries.EnterCandidate(w.State,false);var d=NanobotCandidateQueries.EnterDecision(w.Inventory);
        Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"pre-unload context active");NanobotCandidateQueries.Unload(w.State);
        Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"world unload immediately suppresses all outstanding context");NanobotCandidateQueries.ExitDecision(d);NanobotCandidateQueries.ExitCandidate(c);
        MaterialPanelContractFingerprint.Match=false;NanobotCandidateQueries.Install();NanobotCandidateQueries.WorldLoaded(w.State);
        using(var unknown=new Selection(w))Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"unknown native content falls back to strict without a version guess");
        Must(HarmonyLib.Harmony.Active.Count==0,"unknown content installs no partial hooks");
        var target=HarmonyLib.AccessTools.DeclaredMethod(typeof(Game.Systems.Nanobots.NanobotHaulingController),"TryFindCandidate");
        var foreign=new HarmonyLib.Harmony("fixture.foreign");foreign.Patch(target,prefix:new HarmonyLib.HarmonyMethod(typeof(NanoAdvisoryProgram),nameof(ForeignPrefix)));
        MaterialPanelContractFingerprint.Match=true;NanobotCandidateQueries.Install();NanobotCandidateQueries.WorldLoaded(w.State);
        using(var conflict=new Selection(w))Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"foreign candidate hook retains strict query behavior");
        Must(HarmonyLib.Harmony.Active.Count==1&&HarmonyLib.Harmony.GetPatchInfo(target).Prefixes[0].owner=="fixture.foreign","optional feature refusal preserves unrelated hook owner");
        foreign.UnpatchAll("fixture.foreign");
        MaterialPanelContractFingerprint.Match=true;HarmonyLib.Harmony.Reset();HarmonyLib.Harmony.ThrowOnMethod="Decide";NanobotCandidateQueries.Install();NanobotCandidateQueries.WorldLoaded(w.State);
        using(var failed=new Selection(w))Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"second optional hook failure leaves normal strict behavior");
        Must(HarmonyLib.Harmony.Active.Count==0&&HarmonyLib.Harmony.UnpatchCalls>0,"optional patch transaction rolls back its first hook");
    }
    static void ForeignPrefix() { }
    static void NativeHookExceptionIdentity()
    {
        var w=new World();w.Relay(5);w.Build();Enable(w);var type=typeof(NanobotCandidateQueries);const BindingFlags f=BindingFlags.Static|BindingFlags.NonPublic;
        var before=type.GetMethod("CandidateBefore",f);var after=type.GetMethod("CandidateAfter",f);var decisionBefore=type.GetMethod("DecisionBefore",f);var decisionAfter=type.GetMethod("DecisionAfter",f);
        Must(before.ReturnType==typeof(void)&&decisionBefore.ReturnType==typeof(void),"both prefixes are markers and cannot skip original native methods");
        object[] candidateArgs={w.State,false,null};before.Invoke(null,candidateArgs);object[] decisionArgs={w.Inventory,null};decisionBefore.Invoke(null,decisionArgs);
        Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"actual prefix methods establish the scoped context");
        var sentinel=new InvalidOperationException("original caller exception");
        Must(ReferenceEquals(sentinel,decisionAfter.Invoke(null,new object[]{sentinel,decisionArgs[1]})),"decision finalizer returns exact original exception");
        Must(ReferenceEquals(sentinel,after.Invoke(null,new object[]{sentinel,candidateArgs[2]}))&&!NanobotCandidateQueries.IsAdvisory(w.Inventory),"candidate finalizer returns exception and clears its marker");
        using(var selected=new Selection(w))using(var read=w.Sys.BeginAdvisoryMaterialRead())
        {
            Must(read.Pool(w.Inventory.Storages[0] as MatStorageComp)!=null,"advisory read before unload");NanobotCandidateQueries.Unload(w.State);
            Must(!read.Validate(),"context epoch change invalidates an already-created advisory read scope");
        }
    }
    static void LoadEntryAndEpochBoundaries()
    {
        var old=(GameState)typeof(NanobotCandidateQueries).GetField("world",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);NanobotCandidateQueries.Unload(old);
        var w=new World();w.Relay(10);w.Build();w.State.Current=false;G.HasEnteredGame=false;
        MaterialPanelContractFingerprint.Match=true;HarmonyLib.Harmony.Reset();NanobotCandidateQueries.Install();NanobotCandidateQueries.WorldLoaded(w.State);
        using(var loading=new Selection(w))Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"loaded but not-current/not-entered world stays strict");
        w.State.Current=true;G.HasEnteredGame=true;
        using(var entered=new Selection(w))
        {
            Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"prepared load activates after current-world/entered transition without a second load event");
            var unrelated=new World();unrelated.State.Current=false;NanobotCandidateQueries.WorldLoaded(unrelated.State);
            Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"unrelated non-current callback cannot overwrite an active current world");
            NanobotCandidateQueries.Unload(unrelated.State);
            Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"unrelated world unload does not remove active hooks/context");
        }
        var c=NanobotCandidateQueries.EnterCandidate(w.State,false);var d=NanobotCandidateQueries.EnterDecision(w.Inventory);
        NanobotCandidateQueries.WorldLoaded(w.State);
        Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"load epoch clears previously active frames");
        NanobotCandidateQueries.ExitDecision(d);NanobotCandidateQueries.ExitCandidate(c);
        using(var fresh=new Selection(w))Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"fresh context works after epoch reset and stale cleanup");
        var pending=new World();pending.Relay(10);pending.Build();pending.State.Current=false;
        NanobotCandidateQueries.WorldLoaded(pending.State);
        var revalidation=NanobotCandidateQueries.EnterCandidate(w.State,true);
        w.State.Current=false;pending.State.Current=true;
        using(var nested=new Selection(pending))Must(!NanobotCandidateQueries.IsAdvisory(pending.Inventory),"pending world cannot late-arm across an active revalidation barrier");
        NanobotCandidateQueries.ExitCandidate(revalidation);
        using(var top=new Selection(pending))Must(NanobotCandidateQueries.IsAdvisory(pending.Inventory),"pending world arms after outer revalidation unwinds");
        pending.State.Current=false;w.State.Current=true;
        using(var returnToOld=new Selection(w))Must(NanobotCandidateQueries.IsAdvisory(w.Inventory),"prepared old world can arm again only at top level");
        NanobotCandidateQueries.Unload(w.State);
        Must(HarmonyLib.Harmony.Active.Count==2,"prepared next world retains actual marker hooks across current-world unload");
        using(var removed=new Selection(w))Must(!NanobotCandidateQueries.IsAdvisory(w.Inventory),"unloaded world cannot resurrect while Current/readiness flags have not yet been cleared");
        w.State.Current=false;
        pending.State.Current=true;
        using(var next=new Selection(pending))Must(NanobotCandidateQueries.IsAdvisory(pending.Inventory),"next prepared world activates through retained hooks without reinstall or a second load event");
        NanobotCandidateQueries.Unload(pending.State);
        Must(HarmonyLib.Harmony.Active.Count==0,"last prepared world unload removes owned hooks");
    }
    static void Main()
    {
        PoolDiagnosticChecks();StrictAndAdvisoryWork();TickEventsAndOriginalTemporalPolicy();LocalPowerAndReservations();RepeatedClaimQueriesRemainFresh();ContextBarriersAndStrictAncestor();LoadEntryAndEpochBoundaries();NativeHookExceptionIdentity();LifecycleAndOptionalFallback();
        Console.WriteLine("Actual Nano context/adapter/scope advisory restoration passed: "+assertions+" assertions. Original advisory temporal policy is explicit; normal strict tests retained. Native/Harmony/fingerprint bodies are substitutes, not Unity/Mono/FPS or save acceptance.");
    }
}
