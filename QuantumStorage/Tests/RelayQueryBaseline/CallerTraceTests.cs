using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Systems.QuantumStorage;

static class CallerTraceTests
{
    private static int assertions;
    private const BindingFlags Instance=BindingFlags.Instance|BindingFlags.NonPublic,Static=BindingFlags.Static|BindingFlags.NonPublic;
    private static void Must(bool condition,string reason) { assertions++;if(!condition)throw new Exception("Caller trace fixture: "+reason+" #"+assertions); }
    private static T Read<T>(object value,string name)=>(T)value.GetType().GetField(name,Instance).GetValue(value);
    private static object Invoke(string name,params object[] args)=>typeof(RelayCallerTrace).GetMethod(name,Static).Invoke(null,args);
    private static void CaptureActual(RelayCallerTrace trace)=>trace.Capture();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Deep(RelayCallerTrace trace,int depth) { if(depth>0)Deep(trace,depth-1);else trace.Capture();GC.KeepAlive(depth); }
    private static void IdentityAndLimits()
    {
        var lines=new List<string>();var trace=new RelayCallerTrace(Stopwatch.Frequency,lines.Add);
        for(int i=0;i<100;i++)CaptureActual(trace);
        Must(Read<int>(trace,"attempts")==32&&Read<int>(trace,"captures")==32,"hard capture budget");
        trace.Report();int count=lines.Count;trace.Report();trace.Capture();
        Must(lines.Count==count&&Read<int>(trace,"attempts")==32,"report and retirement idempotent");
        Must(lines[0].Contains("scope=sparse_method_paths_not_caller_census")&&lines[0].Contains("attempts=32"),"scope and coverage labelled");
        Must(lines.Any(x=>x.Contains("CallerTraceTests::CaptureActual")||x.Contains("CallerTraceTests::IdentityAndLimits")),"bounded trace retains an actual external method");
        Must(lines.All(x=>!x.Contains(".cs:")&&!x.Contains("\\")&&!x.Contains("/")),"no filenames or path separators in method identities");
        Must(lines.Count<=9&&lines.All(x=>x.Length<1000),"at most one summary plus eight bounded path lines");
        Must((bool)Invoke("IsWrapper",typeof(RelayQueryRecorder).GetMethod("SelectQuery",Instance)),"known recorder wrapper excluded");
        Must(!(bool)Invoke("IsWrapper",typeof(CallerTraceTests).GetMethod("CaptureActual",Static)),"external caller retained");
        string safe=(string)Invoke("Safe","opaque/path\\name:with space",12);
        Must(safe.Length<=12&&!safe.Contains('/')&&!safe.Contains('\\')&&!safe.Contains(':'),"bounded metadata sanitization");
        Must((string)Invoke("Safe",null,10)=="","missing metadata can be formatted without data access");
        var dynamicMethod=new DynamicMethod("anonymous_wrapper",typeof(void),Type.EmptyTypes);
        string identity=(string)Invoke("Identity",dynamicMethod);Must(identity.Contains("dynamic::anonymous_wrapper"),"method without declaring type remains labelled");
        var overflowLines=new List<string>();var overflow=new RelayCallerTrace(1000,overflowLines.Add);
        var add=typeof(RelayCallerTrace).GetMethod("Add",Instance);
        for(int i=0;i<12;i++)add.Invoke(overflow,new object[]{"synthetic_method_"+i});
        add.Invoke(overflow,new object[]{"synthetic_method_0"});overflow.Report();
        Must(Read<int>(overflow,"used")==8&&Read<int>(overflow,"overflow")==4&&overflowLines.Count==9,"fixed caller buckets and explicit overflow");
        Must(overflowLines.Any(x=>x.Contains("hits=2")&&x.Contains("synthetic_method_0")),"existing bucket still counts after full capacity");
        var failing=new RelayCallerTrace(1000,_=>throw new InvalidOperationException("sink sentinel"));
        failing.Capture();failing.Report();failing.Report();Must(Read<bool>(failing,"failed"),"report sink error contained");
        var deepLines=new List<string>();var deep=new RelayCallerTrace(1000,deepLines.Add);Deep(deep,50);deep.Report();
        string deepPath=deepLines.Single(x=>x.StartsWith("state=caller_path"));
        Must(deepPath.Split('>').Length==7&&deepPath.Length<1000,"deep runtime stack is reduced to seven bounded external frames");
    }
    private static void BaselineRoutingAndScopeCounts()
    {
        var flags=BindingFlags.NonPublic|BindingFlags.Static;
        void Set(string name,object value)=>typeof(RelayQueryBaseline).GetField(name,flags).SetValue(null,value);
        T Get<T>(string name)=>(T)typeof(RelayQueryBaseline).GetField(name,flags).GetValue(null);
        RelayQueryBaseline.Unload();var lines=new List<string>();var r=new RelayQueryRecorder(Stopwatch.Frequency,lines.Add);
        long now=Stopwatch.GetTimestamp();r.Start(now-Stopwatch.Frequency*21,true);r.Frame(now,true);
        Set("recorder",r);Set("ownerThread",Thread.CurrentThread.ManagedThreadId);Set("samplingEnabled",true);Set("sampleActive",false);
        typeof(RelayQueryRecorder).GetField("entries",Instance).SetValue(r,4095L);
        Must(RelayQueryBaseline.TryBeginQuery(out var q)&&q.Caller&&!q.Detail,"caller selected independently through real baseline");
        RelayQueryBaseline.NoteReadScope(false,false,true);RelayQueryBaseline.NoteReadScope(true,true,false);
        var sentinel=new InvalidOperationException("original query sentinel");Exception seen=null;
        try {try {throw sentinel;}finally {RelayQueryBaseline.EndQuery(q,true);}}catch(Exception error){seen=error;}
        Must(ReferenceEquals(seen,sentinel),"caller observation cannot replace original query exception");
        Must(!Get<bool>("sampleActive")&&Read<long>(r,"callerSamples")==1&&Read<long>(r,"samples")==0&&Read<long>(r,"detailSamples")==0,"caller cohort excluded from both timing means and sample scope cleared");
        var callers=Read<RelayCallerTrace>(r,"callers");Must(Read<int>(callers,"attempts")==1,"only selected caller sampled");
        Must(Read<long>(r,"queryTicks")==0&&Read<long>(r,"topologyTicks")==0,"trace duration never enters coarse query/topology totals");
        for(int i=0;i<64;i++)if(RelayQueryBaseline.TryBeginQuery(out var coarse))
        {
            Must(!coarse.Caller&&!coarse.Detail,"next sparse sample remains coarse");
            RelayQueryBaseline.NoteReadScope(false,false,true);RelayQueryBaseline.EndQuery(coarse,false);
        }
        Must(Read<int>(callers,"attempts")==1&&Read<long>(r,"samples")==1,"ordinary coarse observation performs no stack walk");
        Must(Read<long>(r,"intervalColdQueries")==1&&Read<long>(r,"intervalColdScopes")==1,"cold scope coverage recorded for coarse query");
        r.Stop("fixture",Stopwatch.GetTimestamp());Set("samplingEnabled",false);
        for(int i=0;i<5000;i++)Must(!RelayQueryBaseline.TryBeginQuery(out _),"retired observer cannot walk stack");
        Must(Read<int>(callers,"attempts")==1,"retirement retains trace budget and performs no extra captures");
        var straddle=new RelayQueryRecorder(Stopwatch.Frequency,_=>{});now=Stopwatch.GetTimestamp();straddle.Start(now-Stopwatch.Frequency*21,true);straddle.Frame(now,true);
        Set("recorder",straddle);Set("samplingEnabled",true);typeof(RelayQueryRecorder).GetField("entries",Instance).SetValue(straddle,4095L);
        Must(RelayQueryBaseline.TryBeginQuery(out var crossing)&&crossing.Caller,"caller crossing sample selected");
        typeof(RelayQueryRecorder).GetField("start",Instance).SetValue(straddle,Stopwatch.GetTimestamp()-Stopwatch.Frequency*41);
        RelayQueryBaseline.EndQuery(crossing,false);
        Must(Read<long>(straddle,"discarded")==1&&Read<int>(Read<RelayCallerTrace>(straddle,"callers"),"attempts")==0,"phase-straddled caller sample is discarded without stack capture");
        RelayQueryBaseline.Unload();Set("recorder",null);Set("configured",false);Set("loaded",false);Set("captureStarted",false);
    }
    internal static int Run() { assertions=0;IdentityAndLimits();BaselineRoutingAndScopeCounts();return assertions; }
}
