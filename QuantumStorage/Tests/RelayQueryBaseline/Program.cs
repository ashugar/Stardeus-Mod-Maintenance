using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Game.Systems.QuantumStorage;

class Program
{
    static int assertions;
    // Legacy positional assertions inspect primary interval lines; dedicated tests inspect all diagnostic lines.
    static void Store(List<string> lines,string line) { if(!line.Contains("state=attribution")&&!line.Contains("state=native_detail")&&!line.Contains("state=focused_")&&!line.Contains("state=frame_evidence")&&!line.Contains("state=caller_"))lines.Add(line); }
    static readonly BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Static;
    static void Must(bool value) { assertions++; if(!value) throw new Exception("Isolation recorder contract failed at assertion "+assertions); }
    static long Read(RelayQueryRecorder r,string name)=>(long)typeof(RelayQueryRecorder).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(r);
    static void Field(string name,object value)=>typeof(RelayQueryBaseline).GetField(name,Flags).SetValue(null,value);
    static T Field<T>(string name)=>(T)typeof(RelayQueryBaseline).GetField(name,Flags).GetValue(null);
    static void Advance(RelayQueryRecorder r,ref long now,long target) { while(now<target) { now=Math.Min(target,now+10);r.Frame(now,true); } }
    static void Reset()
    {
        RelayQueryBaseline.Unload();
        foreach(var n in new[]{"configured","loaded","captureStarted","enabled","samplingEnabled","sampleActive"})Field(n,false);
        foreach(var n in new[]{"stages","ownerThread","topologyDepth"})Field(n,0);
        foreach(var n in new[]{"loadAnchor","sampleId","topologyTicks","topologyCalls"})Field(n,0L);
        Field("recorder",null);Field("ticker",null);Game.The.ModLoader=null;Game.Constants.G.IsDemoMode=Game.Constants.G.HasEnteredGame=false;
        KL.Utils.D.Lines.Clear();
    }
    static RelayQueryBaseline.QuerySample Sample()
    {
        RelayQueryBaseline.QuerySample token=default;
        for(int i=0;i<64;i++) { bool selected=RelayQueryBaseline.TryBeginQuery(out var next); if(i==63) {Must(selected); token=next;}else Must(!selected); }
        return token;
    }
    static void Main()
    {
        var lines=new List<string>();var r=new RelayQueryRecorder(1000,line=>Store(lines,line));
        r.Start(0,false);Must(!r.Running && !r.SelectQuery() && lines.Count==0);
        r.Start(0,true,"active",true);Must(r.Running && !r.Sampling && r.Phase==0);
        long now=0;Advance(r,ref now,10000);Must(lines[1].Contains("mode=frame_only") && lines[1].Contains("query_counts=not_observed"));
        for(int i=0;i<100;i++)Must(!r.SelectQuery());Must(Read(r,"entries")==0);
        Advance(r,ref now,20000);Must(r.Phase==1 && r.Sampling && lines[2].Contains("phase=0 mode=frame_only next_phase=1"));
        int epoch=r.Epoch;for(int i=1;i<=64;i++)Must(r.SelectQuery()==(i==64));
        Must(r.CanStartSample(20001));r.CompleteSample(20006,1,epoch,5,3,2,false);
        Advance(r,ref now,30000);Must(lines[3].Contains("interval_sampled_mod_queries=1") && lines[3].Contains("interval_query_ms=5.000") && lines[3].Contains("interval_topology_ms=3.000") && lines[3].Contains("interval_outer_topology_blocks=2"));
        Advance(r,ref now,40000);Must(!r.Sampling && r.Phase==2);Advance(r,ref now,120000);Must(r.Spent && !r.Running && lines[^1].Contains("state=deadline phase=5 mode=sample next_phase=6"));
        int count=lines.Count;r.Start(120001,true);r.Frame(130000,true);Must(lines.Count==count && r.Spent);
        foreach(var line in lines)Must(line.Length<=1800 && !line.Contains("\\") && !line.Contains("\n"));

        var mixed=new List<string>();var m=new RelayQueryRecorder(1000,line=>Store(mixed,line));m.Start(0,true);m.Frame(45000,false);
        Must(m.Phase==2 && mixed[1].Contains("phase=0 mode=frame_only next_phase=2") && mixed[1].Contains("mixed_unattributable") && mixed[1].Contains("unfocused_frames=1"));
        var straddle=new List<string>();var s=new RelayQueryRecorder(1000,line=>Store(straddle,line));s.Start(0,true);s.Frame(20000,true);epoch=s.Epoch;
        for(int i=0;i<64;i++)s.SelectQuery();Must(s.CanStartSample(39999));s.CompleteSample(40001,1,epoch,2,1,1,false);Must(Read(s,"samples")==0 && Read(s,"discarded")==1);
        s.Frame(40001,true);s.CompleteSample(40002,1,epoch,2,1,1,false);Must(Read(s,"discarded")==2);
        var held=new RelayQueryRecorder(1000,_=>{});held.Start(0,true);held.Frame(20000,true);for(int i=0;i<64;i++)held.SelectQuery();Must(!held.CanStartSample(40001) && !held.Sampling);long observed=Read(held,"entries");for(int i=0;i<100;i++)Must(!held.SelectQuery());Must(Read(held,"entries")==observed);held.Frame(60000,true);Must(held.Sampling && held.Phase==3);
        var clamp=new RelayQueryRecorder(1000,_=>{});clamp.Start(0,true);clamp.Frame(20000,true);clamp.CompleteSample(20001,1,clamp.Epoch,5,99,2,true);Must(Read(clamp,"samples")==0 && Read(clamp,"discarded")==1 && Read(clamp,"topologyTicks")==0);
        var capped=new List<string>();var c=new RelayQueryRecorder(1000,line=>Store(capped,line));c.Start(0,true);c.Frame(20000,true);for(int i=0;i<1000000;i++)c.SelectQuery();Must(!c.SelectQuery() && !c.Sampling);c.Frame(21000,true);Must(c.Spent && capped[^1].Contains("state=entry_cap"));
        var bad=new RelayQueryRecorder(1000,_=>throw new Exception("private sink text"));bad.Start(0,true);Must(bad.Spent && !bad.Running);
        Must(lines.Count<=80 && capped.Count<=80);

        Reset();RelayQueryBaseline.Configure();RelayQueryBaseline.Unload();Must(KL.Utils.D.Lines.Count==0 && Field<RelayQueryRecorder>("recorder")==null);
        var marker=Path.GetTempFileName();
        try
        {
            Game.The.ModLoader=new Game.Loader {Mod=new Game.Mod {Marker=marker}};
            RelayQueryBaseline.Configure();var first=Field<RelayQueryRecorder>("recorder");Must(first!=null && !first.Running);
            Game.Constants.G.IsDemoMode=true;RelayQueryBaseline.AfterLoad();Must(!first.Running);
            RelayQueryBaseline.Unload();Must(Field<RelayQueryRecorder>("recorder")==null);
            RelayQueryBaseline.Configure();var second=Field<RelayQueryRecorder>("recorder");Must(second!=null && !ReferenceEquals(first,second));
            Game.Constants.G.IsDemoMode=false;RelayQueryBaseline.AfterLoad();Must(!second.Running);
            Game.Constants.G.HasEnteredGame=true;RelayQueryBaseline.Tick();Must(second.Running && !Field<bool>("samplingEnabled"));
            for(int i=0;i<100;i++)Must(!RelayQueryBaseline.TryBeginQuery(out _));Must(Read(second,"entries")==0);
            // Move the model's anchor into phase1; no game clock or gameplay is run.
            long anchor=Stopwatch.GetTimestamp()-Stopwatch.Frequency*21;
            typeof(RelayQueryRecorder).GetField("start",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(second,anchor);
            RelayQueryBaseline.Tick();Must(Field<bool>("samplingEnabled"));
            bool wrongThread=true;var worker=new Thread(()=>{for(int i=0;i<64;i++)wrongThread&=!RelayQueryBaseline.TryBeginQuery(out _);});worker.Start();worker.Join();Must(wrongThread && Read(second,"entries")==0);
            var token=Sample();Must(!RelayQueryBaseline.TryBeginQuery(out _));Must(Read(second,"entries")==64);
            var top=RelayQueryBaseline.BeginTopology();var inner=RelayQueryBaseline.BeginTopology();Must(top.Entered && top.Outer && inner.Entered && !inner.Outer);
            RelayQueryBaseline.EndTopology(inner);RelayQueryBaseline.EndTopology(top);RelayQueryBaseline.EndQuery(default,false);Must(Field<bool>("sampleActive"));RelayQueryBaseline.EndQuery(token,false);
            Must(!Field<bool>("sampleActive") && Read(second,"samples")==1 && Read(second,"validations")==1 && Read(second,"topologyTicks")<=Read(second,"queryTicks"));
            token=Sample();var sentinel=new Exception("private gameplay text");Exception seen=null;
            try {try {throw sentinel;}finally {RelayQueryBaseline.EndQuery(token,true);}}catch(Exception ex){seen=ex;}
            Must(ReferenceEquals(seen,sentinel) && Read(second,"failures")==1 && Read(second,"samples")==2);
            token=Sample();top=RelayQueryBaseline.BeginTopology();RelayQueryBaseline.EndQuery(token,false);RelayQueryBaseline.EndTopology(top);Must(!Field<bool>("sampleActive") && Field<int>("topologyDepth")==0 && Read(second,"discarded")==1);
            token=Sample();top=RelayQueryBaseline.BeginTopology();top.Start=Stopwatch.GetTimestamp()+Stopwatch.Frequency*60;
            RelayQueryBaseline.EndTopology(top);RelayQueryBaseline.EndQuery(token,false);
            Must(!Field<bool>("sampleActive") && Read(second,"discarded")==2 && Read(second,"samples")==2);
            RelayQueryBaseline.Unload();RelayQueryBaseline.Configure();Must(second.Spent && ReferenceEquals(second,Field<RelayQueryRecorder>("recorder")));
            Must(!string.Join("",KL.Utils.D.Lines).Contains("private gameplay text"));

            Reset();Game.The.ModLoader=new Game.Loader {Mod=new Game.Mod {Marker=marker}};RelayQueryBaseline.Configure();RelayQueryBaseline.AfterLoad();Field("loadAnchor",Stopwatch.GetTimestamp()-Stopwatch.Frequency*130);Game.Constants.G.HasEnteredGame=true;RelayQueryBaseline.Tick();
            Must(Field<RelayQueryRecorder>("recorder").Spent && !Field<bool>("samplingEnabled") && string.Join("",KL.Utils.D.Lines).Contains("expired_before_entry"));
            Reset();Game.The.ModLoader=new Game.Loader {Mod=new Game.Mod {Marker=marker}};for(int i=0;i<20;i++){RelayQueryBaseline.Configure();RelayQueryBaseline.Unload();}Must(KL.Utils.D.Lines.Count<=8);
        }
        finally {File.Delete(marker);Reset();}
        assertions+=AttributionTests.Run();
        assertions+=CallerTraceTests.Run();
        assertions+=AdvisoryMetricTests.Run();
        assertions+=FocusedRecorderTests.Run();
        Console.WriteLine("Isolation recorder contracts passed: "+assertions+" assertions; passive phases, sampling/phase epochs, mixed gaps, cap/sink/deadline, lifecycle/entry/owner thread, nested topology and exception cleanup. No Harmony or game runtime exercised.");
    }
}
