using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Systems.QuantumStorage;

internal static class Program
{
    private static int assertions,clockCalls;
    private static long ticks;
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    private static void Must(bool value,string name){assertions++;if(!value)throw new Exception("Focused diagnostic fixture: "+name+" #"+assertions);}
    private static void Set(string name,object value)=>typeof(RelayQueryFocus).GetField(name,Static).SetValue(null,value);
    private static long Clock(){clockCalls++;return ++ticks;}
    private static void Reset()
    {
        RelayQueryFocus.ClearQuery();Set("state",null);RelayQueryFocus.Configure();Set("clock",(Func<long>)Clock);Set("allocatedBytes",null);Set("allocationStatus","fixture_unavailable");clockCalls=0;ticks=100;
    }
    private sealed class Model
    {
        internal readonly List<string> Calls=new List<string>();
        internal readonly List<Item> Items=new List<Item>();
        internal int Attempts,Ends,Validations,InvalidFor,CountReads;
        internal bool BeginValid=true;
        internal string ThrowAt;
        internal Exception Sentinel=new Exception("fixture native exception");
        internal Action<Model,Item> Callback;
        internal void Hit(string step){Calls.Add(step);if(ThrowAt==step)throw Sentinel;}
    }
    private sealed class Item
    {
        internal int Id,Amount;
        internal object Pool;
        internal bool Eligible=true;
    }
    private struct Source:IMaterialQuerySource<Item>
    {
        internal Model Model;
        public bool BeginAttempt(){Model.Attempts++;Model.Hit("begin:"+Model.Attempts);return Model.BeginValid;}
        public int Count{get{Model.CountReads++;Model.Hit("count:"+Model.Items.Count);return Model.Items.Count;}}
        public Item Candidate(int index){Model.Hit("candidate:"+index);return Model.Items[index];}
        public bool IsEligible(Item item){Model.Hit("eligible:"+item.Id);return item.Eligible;}
        public object PoolKey(Item item){Model.Hit("pool:"+item.Id);return item.Pool;}
        public int Available(Item item){Model.Hit("available:"+item.Id);Model.Callback?.Invoke(Model,item);return item.Amount;}
        public bool ValidateAttempt(){Model.Validations++;Model.Hit("validate:"+Model.Validations);return Model.Validations>Model.InvalidFor;}
        public void EndAttempt(){Model.Ends++;Model.Hit("end:"+Model.Ends);}
    }
    private static Model Build(int count=8)
    {
        var m=new Model();var pool=new object();
        for(int i=0;i<count;i++)m.Items.Add(new Item{Id=i,Amount=i%3==0?0:1,Pool=i<3?pool:new object(),Eligible=i!=1});
        return m;
    }
    private static bool Query(Model model,int amount){var source=new Source{Model=model};return MaterialQueryEngine.HasEnough<Item,Source>(ref source,amount);}
    private static (bool Result,Exception Error,RelayQueryFocus.Snapshot Snapshot) Run(Model m,int amount,bool detail,long ordinal=4)
    {
        bool result=false;Exception error=null;RelayQueryFocus.Snapshot sample=default;
        if(detail){RelayQueryFocus.BeginQuery(ordinal);RelayQueryFocus.ArmEngine();}
        try{result=Query(m,amount);}catch(Exception ex){error=ex;}
        finally{if(detail){sample=RelayQueryFocus.EndQuery();if(sample.Valid){sample.Counts=(long[])sample.Counts.Clone();sample.Ticks=(long[])sample.Ticks.Clone();sample.TimedCounts=(long[])sample.TimedCounts.Clone();}}}
        return (result,error,sample);
    }
    private static void Compare(Func<Model> factory,int amount,long ordinal=4)
    {
        Reset();var a=factory();var b=factory();var sentinel=new Exception("native original sentinel");a.Sentinel=b.Sentinel=sentinel;
        var ordinary=Run(a,amount,false);Must(clockCalls==0,"ordinary engine performs no diagnostic clocks");
        var detail=Run(b,amount,true,ordinal);
        Must(ordinary.Result==detail.Result,"result parity");Must(ReferenceEquals(ordinary.Error,detail.Error),"exception identity parity");
        Must(a.Calls.SequenceEqual(b.Calls),"exact source callback and Count read order parity");Must(a.Ends==b.Ends,"native cleanup parity");
        Must(detail.Snapshot.Valid,"selected profile valid");Must(!RelayQueryFocus.TryEnterEngine(),"query completion clears selected context");
    }
    private static void Main()
    {
        EngineEquivalence();KeyCallbackEquivalence();CountsAndWitness();NestedAndMutatingProviders();ObserverFailure();AllocationBoundaries();AllocationCalibration();IntervalContracts();PoolIntervalContracts();
        Console.WriteLine("Focused production-engine profiling fixture passed: "+assertions+" assertions. Actual engine/helper linked; scripted provider/clock/allocation controls are substitutes, not Unity/Mono/FPS or save acceptance.");
    }
    private static void EngineEquivalence()
    {
        foreach(int count in new[]{0,1,2,8,1000})foreach(int amount in new[]{-1,0,1,2,10000})foreach(int invalid in new[]{0,1,3})
            Compare(()=>{var m=Build(count);m.InvalidFor=invalid;return m;},amount);
        Compare(()=>{var m=Build();m.BeginValid=false;return m;},1);
        foreach(string at in new[]{"begin:1","count:8","candidate:4","eligible:4","pool:4","available:4","validate:1","end:1"})
            Compare(()=>{var m=Build();m.ThrowAt=at;return m;},100);
        // Native count mutation during a provider callback is visible in both exact call traces.
        Compare(()=>{var m=Build();m.Callback=(w,item)=>{if(item.Id==0)w.Items.RemoveAt(w.Items.Count-1);};return m;},100);
        Compare(()=>{var m=Build();m.Callback=(w,item)=>{if(item.Id==0)w.Items.Add(new Item{Id=99,Amount=4,Pool=new object()});};return m;},100);
    }
    private sealed class TrackedKey
    {
        internal Model Owner;
        internal int Id;
        public override int GetHashCode(){Owner.Hit("hash:"+Id);return Id;}
        public override bool Equals(object value){Owner.Hit("equals:"+Id);return value is TrackedKey other&&other.Id==Id;}
    }
    private static Model TrackedModel(string throwAt=null)
    {
        var m=Build();var shared=new TrackedKey{Owner=m,Id=0};
        for(int i=0;i<m.Items.Count;i++)m.Items[i].Pool=i<3?shared:new TrackedKey{Owner=m,Id=i};
        m.ThrowAt=throwAt;return m;
    }
    private static void KeyCallbackEquivalence()
    {
        Compare(()=>TrackedModel(),100);
        Compare(()=>TrackedModel("hash:4"),100);
        Compare(()=>TrackedModel("equals:0"),100,2);
    }
    private static void CountsAndWitness()
    {
        Reset();var r=Run(Build(),100,true,4);var c=r.Snapshot.Counts;var n=r.Snapshot.TimedCounts;
        Must(c[0]==1&&c[1]==8&&c[2]==7&&c[3]==7&&c[4]==1,"attempt/candidate/eligibility/dedup exact counters");
        Must(c[5]==6&&c[6]==3&&c[7]==0&&c[8]==1&&c[9]==1&&c[10]==0&&c[11]==9,"provider/retry/witness/count-read exact counters");
        Must(n.Take(10).All(x=>x==1)&&n.Skip(10).All(x=>x==0),"setup/begin/validate/cleanup plus split key and dedup witness stages each timed once");Must(clockCalls==20,"one-attempt fixed ten timing-pair budget");
        Reset();r=Run(Build(10000),100000,true,9999);Must(clockCalls==20&&r.Snapshot.Counts[1]==10000,"ten-thousand candidates do not multiply diagnostic clocks");
        Reset();r=Run(Build(),1,true,7);Must(r.Result&&r.Snapshot.Counts[8]==0&&r.Snapshot.TimedCounts.Skip(4).Take(4).All(x=>x==0),"early native success preserves missing witness");
        Reset();r=Run(Build(),100,true,1);Must(r.Snapshot.Counts[8]==1&&r.Snapshot.Counts[9]==0&&r.Snapshot.TimedCounts[4]==1&&r.Snapshot.TimedCounts[5]==1&&r.Snapshot.TimedCounts[6]==0&&r.Snapshot.TimedCounts[7]==0,"ineligible witness does not force key or provider");
        Reset();r=Run(Build(),100,true,2);Must(r.Snapshot.Counts[9]==0&&r.Snapshot.TimedCounts[6]==1&&r.Snapshot.TimedCounts[7]==0,"duplicate witness does not force provider");
        Reset();r=Run(Build(0),1,true);Must(r.Snapshot.Counts[8]==0&&r.Snapshot.TimedCounts.Skip(4).Take(4).All(x=>x==0),"empty domain has no fabricated witness");
        Reset();var retry=Build();retry.InvalidFor=3;r=Run(retry,100,true,4);
        Must(!r.Result&&r.Snapshot.Counts[0]==3&&r.Snapshot.Counts[1]==24&&r.Snapshot.Counts[7]==3,"retry cap and counters preserved");
        Must(r.Snapshot.Counts[8]==1&&r.Snapshot.TimedCounts[7]==1&&clockCalls==36,"single query-wide witness across three attempts");
    }
    private static void NestedAndMutatingProviders()
    {
        Reset();RelayQueryFocus.BeginQuery(4);var constructor=Build();Must(!Query(constructor,100)&&clockCalls==0,"constructor callback before explicit arm remains ordinary");RelayQueryFocus.ArmEngine();var root=Build();Must(!Query(root,100),"matching root query runs after constructor callback");var owned=RelayQueryFocus.EndQuery();Must(owned.Counts[1]==8&&owned.Counts[0]==1,"constructor callback cannot steal root counters");
        Reset();var m=Build();int innerCalls=0;
        m.Callback=(w,item)=>{if(item.Id!=4)return;var inner=Build(4);inner.Items[3].Amount=2;Must(Query(inner,1),"nested native provider result");innerCalls++;w.Items[7].Amount=10;};
        var r=Run(m,12,true,4);Must(r.Result&&innerCalls==1,"mutable downstream provider quantities stay fresh");
        Must(r.Snapshot.Counts[1]==8&&r.Snapshot.Counts[5]==6&&r.Snapshot.Counts[0]==1,"nested engine does not double-own root counters");
        Must(r.Snapshot.TimedCounts[7]==1&&clockCalls==20,"nested callback remains inside one inclusive provider timing");
        Compare(()=>{var w=Build();w.Callback=(model,item)=>{if(item.Id==4){var inner=Build(4);inner.Items[3].Amount=2;Query(inner,1);model.Items[7].Amount=10;}};return w;},12);
    }
    private static void ObserverFailure()
    {
        Reset();Set("clock",(Func<long>)(()=>throw new InvalidOperationException("observer private text")));var m=Build();var r=Run(m,100,true,4);
        Must(r.Error==null&&!r.Result&&m.Ends==1,"throwing observer clock cannot change gameplay result or cleanup");Must(r.Snapshot.Counts[10]>0&&r.Snapshot.Ticks.All(x=>x==0),"throwing observer times are unavailable, not fabricated");
        Reset();Set("clock",(Func<long>)(()=>throw new InvalidOperationException("observer private text")));m=Build();m.ThrowAt="available:4";r=Run(m,100,true,4);Must(ReferenceEquals(r.Error,m.Sentinel)&&m.Ends==1,"observer failure preserves exact native provider exception");
        Reset();Set("clock",(Func<long>)(()=>throw new InvalidOperationException("observer private text")));m=Build();m.ThrowAt="end:1";r=Run(m,100,true,4);Must(ReferenceEquals(r.Error,m.Sentinel),"observer cleanup failure preserves exact native cleanup exception");
        Reset();long reverse=100;Set("clock",(Func<long>)(()=>--reverse));r=Run(Build(),100,true,4);Must(r.Error==null&&r.Snapshot.Counts[10]>0&&r.Snapshot.Ticks.All(x=>x==0),"nonmonotonic clock fails observation only");
        Reset();RelayQueryFocus.BeginQuery(4);RelayQueryFocus.ClearQuery();Must(!RelayQueryFocus.EndQuery().Valid&&!RelayQueryFocus.TryEnterEngine(),"retired detail cannot be reported");
    }
    private static void AllocationBoundaries()
    {
        Reset();var r=Run(Build(),100,true);Must(!r.Snapshot.AllocationAvailable,"unsupported allocation counter remains unavailable");
        Reset();int reads=0;Set("allocatedBytes",(Func<long>)(()=>++reads==1?100:500));Set("allocationStatus","fixture_available");r=Run(Build(),100,true);Must(r.Snapshot.AllocationAvailable&&r.Snapshot.AllocatedBytes==400&&reads==2,"allocation reads exactly two root inclusive boundaries");
        Reset();reads=0;Set("allocatedBytes",(Func<long>)(()=>++reads==1?100:99));r=Run(Build(),100,true);Must(!r.Snapshot.AllocationAvailable&&RelayQueryFocus.AllocationStatus=="invalid_delta","nonmonotonic allocation invalidates observation");
        Reset();Set("allocatedBytes",(Func<long>)(()=>throw new Exception("allocation probe")));r=Run(Build(),100,true);Must(r.Error==null&&!r.Snapshot.AllocationAvailable&&RelayQueryFocus.AllocationStatus=="failed","throwing allocation probe leaves native result");
    }
    private static bool Probe(Func<long> read)=>(bool)typeof(RelayQueryFocus).GetMethod("ProbeAllocation",Static).Invoke(null,new object[]{read});
    private static void AllocationCalibration()
    {
        Reset();Must(!Probe(null)&&RelayQueryFocus.AllocationStatus=="unavailable","missing allocation API unavailable");
        Must(!Probe(()=>0)&&RelayQueryFocus.AllocationStatus=="unresponsive","stubbed zero allocation counter unavailable");
        Must(!Probe(()=>50)&&RelayQueryFocus.AllocationStatus=="unresponsive","constant positive allocation counter unavailable");
        Must(!Probe(()=>throw new InvalidOperationException("private observer"))&&RelayQueryFocus.AllocationStatus=="unavailable","throwing calibration fails closed");
        int call=0;Must(!Probe(()=>++call==3?99:100)&&RelayQueryFocus.AllocationStatus=="unresponsive","nonmonotonic calibration unavailable");
        call=0;Must(Probe(()=>++call==3?512:100)&&RelayQueryFocus.AllocationStatus=="available","responsive allocation calibration accepted");
        Must(!Probe(()=>0),"later failed calibration clears previously available delegate");
        var r=Run(Build(),100,true);Must(!r.Snapshot.AllocationAvailable,"failed reprobe cannot leak stale available allocation API");
    }
    private static RelayQueryFocus.Snapshot PoolIntervalWitness(int outcome,int kind,bool active)
    {
        var sample=new RelayQueryFocus.Snapshot{Valid=true,PoolOutcome=outcome,PoolInnerKind=kind,Counts=new long[RelayQueryFocus.CountSize],Ticks=new long[RelayQueryFocus.StageSize],TimedCounts=new long[RelayQueryFocus.StageSize]};
        sample.Counts[8]=sample.Counts[12]=sample.Counts[14]=sample.Counts[19]=1;
        sample.Ticks[6]=11;sample.TimedCounts[6]=1;
        sample.Ticks[12+kind]=7;sample.TimedCounts[12+kind]=1;
        if(!active){sample.Ticks[14]=sample.TimedCounts[14]=0;}
        if(outcome==2)sample.Counts[22]=1;
        return sample;
    }
    private static void PoolIntervalContracts()
    {
        var interval=new RelayQueryFocus.Interval(1000);
        interval.Add(PoolIntervalWitness(2,1,false),20);interval.Add(PoolIntervalWitness(1,2,true),30);
        Must(interval.CountsLine.Contains("duplicate_witnesses=1")&&interval.CountsLine.Contains("unique_witnesses=1"),"duplicate/unique key outcomes remain separate");
        Must(interval.CountsLine.Contains("inner_lookup_requested=1")&&interval.CountsLine.Contains("inner_active_requested=1")&&interval.CountsLine.Contains("inner_activation_requested=0"),"one rotating category request uses actual Scope probe denominator");
        Must(interval.TimingLine.Contains("duplicate_key_n=1")&&interval.TimingLine.Contains("duplicate_active_n=0")&&interval.TimingLine.Contains("unique_key_n=1")&&interval.TimingLine.Contains("unique_active_n=1"),"missing Active timing is not divided by duplicate witness count");
        interval.Reset();Must(interval.CountsLine.Contains("inner_lookup_requested=0")&&interval.TimingLine.Contains("duplicate_active_n=0")&&interval.TimingLine.Contains("unique_active_n=0"),"pool category and actual timing denominators reset");
        var large=new RelayQueryFocus.Snapshot{Valid=true,Counts=Enumerable.Repeat(long.MaxValue/100,RelayQueryFocus.CountSize).ToArray(),Ticks=Enumerable.Repeat(long.MaxValue/100,RelayQueryFocus.StageSize).ToArray(),TimedCounts=Enumerable.Repeat(long.MaxValue/100,RelayQueryFocus.StageSize).ToArray()};
        interval.Add(large,long.MaxValue/100);
        Must(interval.CountsLine.Length<=1725&&interval.TimingLine.Length<=1725,"large primitive counters and times leave header room under1800-character raw cap");
    }
    private static void IntervalContracts()
    {
        Reset();var interval=new RelayQueryFocus.Interval(1000);interval.Add(default,10);Must(interval.CountsLine.Contains("focus_samples=0"),"inactive snapshot ignored");
        var r=Run(Build(),100,true,4);interval.Add(r.Snapshot,20);Must(interval.CountsLine.Contains("focus_samples=1")&&interval.CountsLine.Contains("provider_calls=6")&&interval.CountsLine.Contains("allocated_bytes=unavailable"),"focused summary counts and unavailable allocation explicit");
        Must(interval.TimingLine.Contains("focus_query_ms=20.000")&&interval.TimingLine.Contains("provider_n=1")&&interval.TimingLine.Contains("category_times_overlap_topology=true"),"inclusive witness timing labels distinguish topology overlap");
        Must(interval.CountsLine.Length<=1800&&interval.TimingLine.Length<=1800,"focused output fits bounded lines");
        interval.Reset();Must(interval.CountsLine.Contains("focus_samples=0")&&interval.CountsLine.Contains("candidates=0")&&interval.TimingLine.Contains("focus_query_ms=0.000"),"interval reset does not leak prior samples");
    }
}
