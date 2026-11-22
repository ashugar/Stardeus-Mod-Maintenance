using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Game.Systems.QuantumStorage;
using UnityEngine;

static class AttributionTests
{
    private static int assertions;
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic,Instance=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Must(bool condition,string reason) { assertions++;if(!condition)throw new Exception("Attribution diagnostic test: "+reason+" #"+assertions); }
    private static long Read(RelayQueryRecorder r,string name)=>(long)typeof(RelayQueryRecorder).GetField(name,Instance).GetValue(r);
    private static void Set(string name,object value)=>typeof(RelayQueryBaseline).GetField(name,Static).SetValue(null,value);
    private static T Get<T>(string name)=>(T)typeof(RelayQueryBaseline).GetField(name,Static).GetValue(null);
    private static void RecorderCohorts()
    {
        var lines=new List<string>();var r=new RelayQueryRecorder(1000,lines.Add);r.Start(0,true);
        for(int i=0;i<1000;i++)Must(!r.SelectQuery(),"passive phase observes no query entries");
        Must(Read(r,"entries")==0,"passive counters untouched");r.Frame(20000,true);
        int coarse=0,detail=0,caller=0;
        var ticks=new long[14];var counts=new long[14];ticks[0]=1;counts[0]=1;ticks[7]=2;counts[7]=1;
        for(int i=1;i<=4096;i++)
        {
            bool selected=r.SelectQuery();Must(selected==(i%64==0),"exact ordinal selection");
            if(!selected)continue;
            Must(r.SelectedCaller==(i%4096==0),"exact separate caller cohort");
            Must(r.SelectedDetail==(i%512==0&&i%4096!=0),"detail cohort excludes caller tracing");
            Must(r.CanStartSample(20001),"selected in same phase");
            if(r.SelectedCaller){caller++;r.CompleteSample(20002,1,r.Epoch,200,30,2,false,caller:true);}
            else if(r.SelectedDetail){detail++;r.CompleteSample(20002,1,r.Epoch,100,20,2,false,true,ticks,counts);}
            else {coarse++;r.CompleteSample(20002,1,r.Epoch,10,4,2,false);}
        }
        Must(coarse==56&&detail==7&&caller==1,"caller replaces one detailed sample, never coarse mean");
        Must(Read(r,"samples")==56&&Read(r,"queryTicks")==560&&Read(r,"topologyTicks")==224,"detailed costs excluded from coarse totals");
        Must(Read(r,"detailSamples")==7&&Read(r,"callerSamples")==1,"detail and caller cohorts counted separately");
        Must(Read(r,"entries")==4096,"frequency includes all eligible entries, not only successful timing samples");
        r.Frame(20010,true);for(int i=0;i<20;i++)r.SelectQuery();r.Frame(20020,true);r.Frame(20030,true);r.Frame(30000,true);
        var attribution=lines.FindLast(x=>x.Contains("state=attribution"));
        Must(attribution.Contains("entries_per_update_min=0")&&attribution.Contains("entries_per_update_max=4096"),"per-update extrema include zero-query updates");
        Must(attribution.Contains("eligible_mod_entries=4116"),"interval eligible frequency reported");
        // A discarded timing still contributed its entry to frequency.
        for(int i=0;i<44;i++)r.SelectQuery();Must(r.CanStartSample(39999),"straddle enters old phase");
        long entries=Read(r,"entries");r.CompleteSample(40001,1,r.Epoch,100,20,2,false,true,ticks,counts);
        Must(Read(r,"entries")==entries&&Read(r,"discarded")==1&&Read(r,"detailSamples")==7,"phase discard preserves frequency, excludes detail");
        foreach(var line in lines)Must(line.Length<=1800,"bounded line length");Must(lines.Count<=80,"bounded line count");
        var hot=new RelayQueryRecorder(1000,_=>{});hot.Start(0,true);hot.Frame(20000,true);
        for(int i=0;i<1000;i++)hot.SelectQuery();long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)hot.SelectQuery();long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Must(bytes==0,"warmed ordinal/frequency hotpath allocates zero bytes");
        Console.WriteLine("Recorder ordinal/frequency desktop CLR: 10000 entries, allocated_bytes="+bytes+"; no clocks in production ordinal selector. Not Mono/FPS evidence.");
    }
    private static void NativeFrameTelemetry()
    {
        FrameTimingManager.Reset();using(var f=new RelayFrameTiming())
        {
            f.Start();for(int i=0;i<20;i++)f.Frame();var report=f.Report();
            Must(FrameTimingManager.Captures==0&&FrameTimingManager.Reads==0&&report.Contains("engine_timing=disabled"),"disabled feature performs no native timing work");
        }
        FrameTimingManager.Reset();FrameTimingManager.Enabled=true;
        using(var f=new RelayFrameTiming())
        {
            f.Start();f.Frame();Must(f.Report().Contains("engine_missing=1"),"missing is not a zero CPU sample");
            FrameTimingManager.Count=1;FrameTimingManager.Latest=new FrameTiming{frameStartTimestamp=100,cpuFrameTime=10,cpuMainThreadFrameTime=7,cpuMainThreadPresentWaitTime=2,cpuRenderThreadFrameTime=3,gpuFrameTime=5};
            f.Frame();f.Frame();var report=f.Report();
            Must(report.Contains("engine_unique_samples=1")&&report.Contains("engine_duplicate=1"),"same delayed engine sample counted once");
            Must(report.Contains("engine_cpu_frame_ms_sum=10.000")&&report.Contains("engine_present_wait_ms_sum=2.000"),"CPU and wait evidence remain distinct");
            FrameTimingManager.Latest.frameStartTimestamp=99;f.Frame();report=f.Report();Must(report.Contains("engine_unique_samples=0"),"older timing timestamp rejected");
            FrameTimingManager.Latest.frameStartTimestamp=101;FrameTimingManager.Latest.cpuFrameTime=double.NaN;f.Frame();
            report=f.Report();Must(report.Contains("engine_unique_samples=0")&&report.Contains("engine_missing=1"),"invalid timing not included");
            FrameTimingManager.Latest.frameStartTimestamp=102;FrameTimingManager.Latest.cpuFrameTime=0;f.Frame();
            report=f.Report();Must(report.Contains("engine_unique_samples=0")&&report.Contains("engine_missing=1"),"zero CPU sample unavailable");
            FrameTimingManager.Latest.frameStartTimestamp=103;FrameTimingManager.Latest.cpuFrameTime=10;FrameTimingManager.Latest.gpuFrameTime=0;f.Frame();report=f.Report();
            Must(report.Contains("engine_unique_samples=1"),"valid CPU timing survives unavailable GPU channel");
            Must(report.Contains("engine_gpu_positive_samples=0")&&report.Contains("engine_gpu_ms_sum=unavailable"),"zero GPU timing not counted as covered GPU measurement");
            FrameTimingManager.Latest.frameStartTimestamp=104;f.Frame();long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<10000;i++){FrameTimingManager.Latest.frameStartTimestamp++;f.Frame();}
            long bytes=GC.GetAllocatedBytesForCurrentThread()-before;Must(bytes==0,"fixed buffer frame polling allocates zero warmed bytes");
            FrameTimingManager.Throw=true;f.Frame();report=f.Report();Must(report.Contains("engine_timing=failed"),"native exception disables channel only");
            int calls=FrameTimingManager.Captures;f.Frame();Must(FrameTimingManager.Captures==calls,"failed telemetry channel not retried every frame");
            Console.WriteLine("Native-frame telemetry substitute: 10000 polls, allocated_bytes="+bytes+". API/model coverage only, not actual Unity availability.");
        }
        FrameTimingManager.Reset();
        using(var f=new RelayFrameTiming())
        {
            f.Start();typeof(RelayFrameTiming).GetField("processTicks",Instance).SetValue(f,long.MaxValue);
            Must(f.Report().Contains("process_cpu=invalid_delta"),"invalid process CPU delta closes only process observer");
            Must(typeof(RelayFrameTiming).GetField("process",Instance).GetValue(f)==null,"invalid CPU observer resource released");
        }
        using(var f=new RelayFrameTiming())
        {
            f.Start();var held=(Process)typeof(RelayFrameTiming).GetField("process",Instance).GetValue(f);held.Dispose();typeof(RelayFrameTiming).GetField("process",Instance).SetValue(f,new Process());
            Must(f.Report().Contains("process_cpu=failed"),"unassociated process exception stays inside observer");
            Must(typeof(RelayFrameTiming).GetField("process",Instance).GetValue(f)==null,"failed process observer resource released");
            f.Dispose();int calls=FrameTimingManager.Captures;f.Frame();Must(FrameTimingManager.Captures==calls,"disposed frame observer remains retired");
        }
        FrameTimingManager.Reset();
    }
    private static void BaselineDetailBoundaries()
    {
        var marker=Path.GetTempFileName();
        try
        {
            RelayQueryBaseline.Unload();foreach(var name in new[]{"configured","loaded","captureStarted","enabled","samplingEnabled","sampleActive","detailActive"})Set(name,false);
            Set("recorder",null);Set("ticker",null);Set("stages",0);Set("ownerThread",0);
            Game.The.ModLoader=new Game.Loader{Mod=new Game.Mod{Marker=marker}};Game.Constants.G.HasEnteredGame=true;Game.Constants.G.IsDemoMode=false;
            RelayQueryBaseline.Configure();RelayQueryBaseline.AfterLoad();var r=Get<RelayQueryRecorder>("recorder");
            typeof(RelayQueryRecorder).GetField("start",Instance).SetValue(r,Stopwatch.GetTimestamp()-Stopwatch.Frequency*21);RelayQueryBaseline.Tick();
            RelayQueryBaseline.QuerySample detail=default;
            for(int i=1;i<=512;i++)
            {
                if(!RelayQueryBaseline.TryBeginQuery(out var q))continue;
                Must(q.Detail==(i==512),"baseline detail routing exact");
                if(!q.Detail){Must(RelayQueryBaseline.SelectDetailVertex(4)==-1,"coarse query has no native detail");RelayQueryBaseline.EndQuery(q,false);}
                else detail=q;
            }
            var outer=RelayQueryBaseline.BeginTopology();var inner=RelayQueryBaseline.BeginTopology();
            Must(RelayQueryBaseline.SelectDetailVertex(4)==-1,"nested topology cannot consume outer vertex");RelayQueryBaseline.EndTopology(inner);
            Must(RelayQueryBaseline.SelectDetailVertex(4)==-1,"focused pass retires outer native vertex clocking");
            Must(RelayQueryBaseline.SelectDetailVertex(4)==-1,"reentrant/Ensure matcher cannot select a second vertex");
            var bucket=RelayQueryBaseline.BeginDetail(0);RelayQueryBaseline.EndDetail(bucket);RelayQueryBaseline.EndTopology(outer);
            var second=RelayQueryBaseline.BeginTopology();Must(RelayQueryBaseline.SelectDetailVertex(4)==-1,"focused pass retires final native vertex clocking");
            bucket=RelayQueryBaseline.BeginDetail(0);RelayQueryBaseline.EndDetail(bucket);RelayQueryBaseline.EndTopology(second);RelayQueryBaseline.EndQuery(detail,false);
            Must(Read(r,"samples")==7&&Read(r,"detailSamples")==1,"coarse/detail model separation through real baseline");
            var counts=Get<long[]>("detailCounts");Must(counts[0]==1&&counts[7]==1,"first and later boundary bucket counts separated");
            Must(!Get<bool>("sampleActive")&&!Get<bool>("detailActive"),"detail scope cleanup");
            Must(RelayQueryBaseline.SelectDetailVertex(4)==-1&&!RelayQueryBaseline.BeginDetail(0).Entered,"retired detail cannot observe outside selected query");
        }
        finally {RelayQueryBaseline.Unload();Game.The.ModLoader=null;File.Delete(marker);}
    }
    internal static int Run() { assertions=0;RecorderCohorts();NativeFrameTelemetry();BaselineDetailBoundaries();return assertions; }
}
