using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Game.Systems.QuantumStorage;

static class AdvisoryMetricTests
{
    static int assertions;
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.NonPublic,Static=BindingFlags.Static|BindingFlags.NonPublic;
    static void Must(bool yes,string reason) { assertions++;if(!yes)throw new Exception("Advisory metric fixture: "+reason); }
    static long Read(object value,string name)=>(long)value.GetType().GetField(name,Instance).GetValue(value);
    static void Set(string name,object value)=>typeof(RelayQueryBaseline).GetField(name,Static).SetValue(null,value);
    static void CountersAreNotScanAliases()
    {
        var lines=new List<string>();var r=new RelayQueryRecorder(1000,lines.Add);r.Start(0,true);r.Frame(20000,true);
        int epoch=r.Epoch;
        r.CompleteSample(20001,1,epoch,10,3,2,false,touchedScopes:1,activatedScopes:1,registryChecks:0,advisoryScopes:1,advisoryRefreshes:1,advisoryCacheHits:1,candidateModes:1,currentWorldModes:1,decisionModes:1);
        r.CompleteSample(20002,1,epoch,20,10,2,false,touchedScopes:1,activatedScopes:1,registryChecks:7,candidateModes:1,revalidationModes:1,currentWorldModes:1,decisionModes:1);
        r.CompleteSample(20003,1,epoch,30,12,2,false,detail:true,registryChecks:99,advisoryScopes:99,advisoryRefreshes:99,advisoryCacheHits:99,candidateModes:99,revalidationModes:99,currentWorldModes:99,decisionModes:99);
        r.CompleteSample(20004,1,epoch,30,12,2,false,caller:true,registryChecks:99,advisoryScopes:99,advisoryRefreshes:99,advisoryCacheHits:99,candidateModes:99,revalidationModes:99,currentWorldModes:99,decisionModes:99);
        r.Frame(30000,true);
        string primary=lines.FindLast(x=>x.Contains("state=periodic"));
        string metrics=lines.FindLast(x=>x.Contains("state=attribution"));
        Must(primary.Contains("interval_sampled_mod_queries=2")&&primary.Contains("interval_outer_topology_blocks=4"),"outer block count remains separate from real registry work");
        Must(metrics.Contains("coarse_registry_check_calls=7"),"actual registry calls aggregate only coarse observations");
        Must(metrics.Contains("coarse_advisory_queries=1")&&metrics.Contains("coarse_advisory_scopes=1"),"advisory query and scope coverage explicit");
        Must(metrics.Contains("coarse_advisory_refreshes=1")&&metrics.Contains("coarse_advisory_cache_hits=1"),"cached refresh can perform no registry call");
        Must(metrics.Contains("coarse_candidate_queries=2")&&metrics.Contains("coarse_revalidation_queries=1"),"selection and revalidation modes are measured independently");
        Must(metrics.Contains("coarse_current_world_queries=2")&&metrics.Contains("coarse_decision_context_queries=2"),"current-world and decision binding coverage is explicit");
        Must(!metrics.Contains("=99"),"detail and caller cohorts do not contaminate coarse work counters");
        Must(lines[0].Contains("nanobot_read_view="),"capture announces advisory availability separately from current activation");
        foreach(string line in lines)Must(line.Length<=1800,"expanded metrics remain bounded");
    }
    static void RealBaselineInstrumentation()
    {
        RelayQueryBaseline.Unload();
        var lines=new List<string>();var r=new RelayQueryRecorder(Stopwatch.Frequency,lines.Add);
        long now=Stopwatch.GetTimestamp();r.Start(now-21*Stopwatch.Frequency,true);r.Frame(now,true);
        Set("recorder",r);Set("ownerThread",Thread.CurrentThread.ManagedThreadId);Set("samplingEnabled",true);Set("sampleActive",false);
        RelayQueryBaseline.QuerySample sample=default;
        for(int i=0;i<64;i++)if(RelayQueryBaseline.TryBeginQuery(out var chosen))sample=chosen;
        RelayQueryBaseline.NoteReadScope(true,true,true,true);
        RelayQueryBaseline.NoteCandidateMode(true,false,true,true);
        for(int i=0;i<3;i++)RelayQueryBaseline.NoteTopologyWalk();
        RelayQueryBaseline.NoteAdvisoryRefresh(true);
        var outer=RelayQueryBaseline.BeginTopology();RelayQueryBaseline.EndTopology(outer);RelayQueryBaseline.EndQuery(sample,false);
        Must(Read(r,"intervalRegistryChecks")==3&&Read(r,"intervalAdvisoryQueries")==1,"real baseline passes exact registry and advisory observations");
        Must(Read(r,"intervalAdvisoryScopes")==1&&Read(r,"intervalAdvisoryCacheHits")==1,"real scope and cache counters passed");
        Must(Read(r,"intervalCandidateQueries")==1&&Read(r,"intervalCurrentWorldQueries")==1&&Read(r,"intervalDecisionQueries")==1&&Read(r,"intervalRevalidationQueries")==0,"actual baseline records selected context mode and world/decision coverage");
        Must(Read(r,"validations")==1,"actual wrapper block count independent from three registry calls");
        RelayQueryBaseline.NoteReadScope(true,true,true,true);RelayQueryBaseline.NoteTopologyWalk();RelayQueryBaseline.NoteAdvisoryRefresh(false);
        Must(Read(r,"intervalRegistryChecks")==3&&Read(r,"intervalAdvisoryScopes")==1,"ordinary and retired observation performs no work counting");
        RelayQueryBaseline.Unload();Set("recorder",null);Set("configured",false);Set("loaded",false);Set("captureStarted",false);
    }
    internal static int Run() { assertions=0;CountersAreNotScanAliases();RealBaselineInstrumentation();return assertions; }
}
