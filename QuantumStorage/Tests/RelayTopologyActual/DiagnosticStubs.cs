// The production topology sources are linked; only diagnostic clock/sink selection is inert here.
namespace Game.Systems.QuantumStorage
{
    internal static class RelayQueryBaseline
    {
        internal static int Vertex=-1;
        internal static readonly long[] Begins=new long[7], Ends=new long[7];
        internal struct DetailSample { internal int Bucket; internal long Start; internal bool Entered; }
        internal struct TopologySample { internal bool Entered,Outer; internal long Id,Start; }
        internal static int SelectDetailVertex(int total)=>Vertex>=0&&Vertex<total?Vertex:-1;
        internal static DetailSample BeginDetail(int bucket) { Begins[bucket]++;return new DetailSample {Bucket=bucket,Entered=true}; }
        internal static void EndDetail(DetailSample token) { if(token.Entered)Ends[token.Bucket]++; }
        internal static TopologySample BeginTopology()=>default;
        internal static void EndTopology(TopologySample token) { }
        internal static void NoteReadScope(bool touched,bool activated,bool valid,bool advisory=false) { }
        internal static void NoteTopologyWalk() { }
        internal static void NoteAdvisoryRefresh(bool cached) { }
        internal static bool SamplingQuery=>false;
        internal static void NoteCandidateMode(bool candidatePresent,bool revalidation,bool currentWorld=false,bool decisionBound=false) { }
    }
}
