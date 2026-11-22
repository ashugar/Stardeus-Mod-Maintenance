using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Game.Systems.QuantumStorage;

internal static class FocusedRecorderTests
{
    private static int assertions;
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic,Instance=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Must(bool value,string name){assertions++;if(!value)throw new Exception("Focused recorder fixture: "+name+" #"+assertions);}
    private static long Read(object value,string name)=>(long)value.GetType().GetField(name,Instance).GetValue(value);
    private static void Set(string name,object value)=>typeof(RelayQueryBaseline).GetField(name,Static).SetValue(null,value);
    private static void SetFocus(string name,object value)=>typeof(RelayQueryFocus).GetField(name,Static).SetValue(null,value);
    private static RelayQueryFocus.Snapshot Snapshot()
    {
        RelayQueryFocus.Configure();SetFocus("allocatedBytes",null);RelayQueryFocus.BeginQuery(0);RelayQueryFocus.ArmEngine();
        Must(RelayQueryFocus.TryEnterEngine(),"explicit root engine armed");RelayQueryFocus.Attempt();RelayQueryFocus.CountRead(1);Must(RelayQueryFocus.Visit(),"one candidate witness");RelayQueryFocus.Eligible(true);RelayQueryFocus.BeforePool();RelayQueryFocus.Pool(false);RelayQueryFocus.BeforeAvailable();RelayQueryFocus.Available(3);RelayQueryFocus.LeaveEngine();return RelayQueryFocus.EndQuery();
    }
    private static void CohortsAndReset()
    {
        var lines=new List<string>();var r=new RelayQueryRecorder(1000,lines.Add);r.Start(0,true);r.Frame(20000,true);int epoch=r.Epoch;
        var sample=Snapshot();r.CompleteSample(20001,1,epoch,10,2,1,false,detail:true,focus:sample);
        r.CompleteSample(20002,1,epoch,20,5,1,false,focus:sample);r.CompleteSample(20003,1,epoch,30,5,1,false,caller:true,focus:sample);
        r.Frame(30000,true);var counts=lines.Last(x=>x.Contains("state=focused_counts"));
        Must(counts.Contains("focus_samples=1")&&counts.Contains("provider_calls=1"),"only detail cohort enters focused totals");
        Must(Read(r,"samples")==1&&Read(r,"queryTicks")==20&&Read(r,"detailSamples")==1,"coarse and detail totals remain separate");
        Must(lines.Any(x=>x.Contains("state=focused_timing")&&x.Contains("focus_query_ms=10.000")),"focused elapsed uses matching detail total");
        Must(!lines.Any(x=>x.Contains("state=native_detail")),"focused schema intentionally replaces native vertex lines");
        r.Frame(40000,true);Must(lines.Last(x=>x.Contains("state=focused_counts")).Contains("focus_samples=0"),"periodic report resets focused counters");
        r.CompleteSample(40001,1,epoch,99,1,1,false,detail:true,focus:sample);r.Frame(50000,true);
        Must(Read(r,"discarded")==1&&lines.Last(x=>x.Contains("state=focused_counts")).Contains("focus_samples=0"),"phase-crossing detail discarded before aggregation");
        Must(lines[0].Contains("build=local-relay-pool-1")&&lines[0].Contains("per_item_counts=focused_outer_engine_only"),"capture identity and sparse scope explicit");
    }
    private static void BaselineRouting()
    {
        RelayQueryBaseline.Unload();var lines=new List<string>();var r=new RelayQueryRecorder(Stopwatch.Frequency,lines.Add);long now=Stopwatch.GetTimestamp();r.Start(now-21*Stopwatch.Frequency,true);r.Frame(now,true);
        Set("recorder",r);Set("ownerThread",Thread.CurrentThread.ManagedThreadId);Set("samplingEnabled",true);Set("sampleActive",false);RelayQueryFocus.Configure();
        RelayQueryBaseline.QuerySample detail=default;
        for(int i=1;i<=512;i++)
        {
            if(!RelayQueryBaseline.TryBeginQuery(out var q))continue;
            if(q.Detail){detail=q;break;}
            Must(!RelayQueryFocus.TryEnterEngine(),"coarse query has no focused root engine");RelayQueryBaseline.EndQuery(q,false);
        }
        Must(detail.Detail,"actual baseline selects detail at512");Must(!RelayQueryFocus.TryEnterEngine(),"detail source-constructor work cannot steal root engine before arm");
        RelayQueryFocus.ArmEngine();Must(RelayQueryFocus.TryEnterEngine(),"actual selected detail can arm matching root engine");RelayQueryFocus.Attempt();RelayQueryFocus.CountRead(1);RelayQueryFocus.Visit();RelayQueryFocus.BeforeAvailable();RelayQueryFocus.Available(1);RelayQueryFocus.LeaveEngine();
        RelayQueryBaseline.EndQuery(detail,false);Must(!RelayQueryFocus.TryEnterEngine(),"actual baseline completion clears focus");
        var interval=(RelayQueryFocus.Interval)typeof(RelayQueryRecorder).GetField("focused",Instance).GetValue(r);Must(interval.CountsLine.Contains("focus_samples=1")&&interval.CountsLine.Contains("provider_calls=1"),"actual baseline forwards matching focused snapshot");
        RelayQueryBaseline.Unload();Set("recorder",null);Set("configured",false);Set("loaded",false);Set("captureStarted",false);
    }
    private static void FullBudget()
    {
        var lines=new List<string>();var r=new RelayQueryRecorder(1000,lines.Add);r.Start(0,true);var callers=(RelayCallerTrace)typeof(RelayQueryRecorder).GetField("callers",Instance).GetValue(r);var add=typeof(RelayCallerTrace).GetMethod("Add",Instance);
        for(int i=0;i<8;i++)add.Invoke(callers,new object[]{new string('M',760)+i});
        for(long now=10000;now<=120000;now+=10000)r.Frame(now,true);
        Must(r.Spent&&lines.Any(x=>x.Contains("state=deadline")),"terminal deadline retained");Must(lines.Count(x=>x.Contains("state=caller_path"))==8&&lines.Any(x=>x.Contains("state=caller_summary")),"full caller budget survives focused reports");
        Must(lines.Count<=80&&lines.All(x=>x.Length<=1824),"full120-second focused output and eight paths stay within hard caps");
        Must(lines.Count(x=>x.Contains("state=focused_counts"))==12&&lines.Count(x=>x.Contains("state=focused_timing"))==12,"all12bounded focused report pairs delivered");
        Must(!string.Join("",lines).Contains("\\")&&!string.Join("",lines).Contains(".cs:"),"bounded output retains no local paths");
    }
    private static void FrameAllocation()
    {
        RelayQueryFocus.Configure();SetFocus("allocatedBytes",null);using(var f=new RelayFrameTiming()){f.Start();string text=f.Report();Must(text.Contains("owner_thread_allocated_bytes=unavailable")&&text.Contains("outside_query_cpu=not_attributed"),"unavailable allocation and unmeasured outside CPU explicit");}
        int reads=0;SetFocus("allocatedBytes",(Func<long>)(()=>100*++reads));SetFocus("allocationStatus","fixture_available");using(var f=new RelayFrameTiming()){f.Start();Must(reads==1,"frame allocation starts one boundary");for(int i=0;i<100;i++)f.Frame();Must(reads==1,"no allocation reads added per frame");Must(f.Report().Contains("owner_thread_allocated_bytes=100")&&reads==2,"report-only owner-thread allocation delta");}
        SetFocus("allocatedBytes",(Func<long>)(()=>-1));Must(!RelayQueryFocus.TryReadAllocation(out _)&&RelayQueryFocus.AllocationStatus=="invalid_value","negative allocation read unavailable");
        SetFocus("allocatedBytes",(Func<long>)(()=>throw new Exception("observer")));Must(!RelayQueryFocus.TryReadAllocation(out _)&&RelayQueryFocus.AllocationStatus=="failed","throwing allocation read unavailable");
    }
    internal static int Run(){assertions=0;CohortsAndReset();BaselineRouting();FullBudget();FrameAllocation();return assertions;}
}
