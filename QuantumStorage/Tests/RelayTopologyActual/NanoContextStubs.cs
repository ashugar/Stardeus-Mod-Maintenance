// The actual production context is linked. Native method bodies, fingerprints
// and Harmony installation are controlled substitutes, not runtime validation.
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Data;
using Game.Systems.Inventory;
namespace Game.Systems.AI { public sealed class AIGoal { } }
namespace Game.Components
{
    public interface IMatRequester { }

}
namespace Game.Systems.Nanobots
{
    public sealed class NanobotSys { }
    internal sealed class NanobotMaterialHaulCandidate { }
    internal sealed class NanobotHaulingController
    {
        private GameState S;
        internal NanobotHaulingController(GameState state) { S=state; }
        private bool TryFindCandidate(Game.Systems.AI.AIGoal goal,bool allowFailed,out NanobotMaterialHaulCandidate candidate)
        { candidate=null;return false; }
    }
}
namespace Game.Systems.Inventory
{
    public enum MatClaimVerdict { Available,Trickle,Reserved,Lacking }
    public static class MatClaimGate
    {
        private static MatClaimVerdict Decide(InventorySys inventory,Game.Components.IMatRequester requester,Game.Data.MatRequest next,int priority,long ticks)
        { return MatClaimVerdict.Lacking; }
    }
}
namespace Game.Systems.QuantumStorage
{
    internal static class MaterialPanelContractFingerprint
    {
        internal static bool Match;
        internal static string Hash(MethodInfo method)
        {
            if(!Match)return "fixture_unreviewed_body";
            string key=method.Name=="TryFindCandidate"?"candidate":"decision";
            foreach(var field in typeof(Relays.NanobotCandidateQueries).GetFields(BindingFlags.Static|BindingFlags.NonPublic))
                if(field.IsLiteral&&field.FieldType==typeof(string)&&field.Name.IndexOf(key,StringComparison.OrdinalIgnoreCase)>=0&&
                    (field.Name.IndexOf("hash",StringComparison.OrdinalIgnoreCase)>=0||field.Name.IndexOf("fingerprint",StringComparison.OrdinalIgnoreCase)>=0))return (string)field.GetRawConstantValue();
            throw new InvalidOperationException("Fixture hash constant not found for "+key);
        }
    }
}
namespace KL.Utils { public static class D { public static readonly List<string> Lines=new();public static void Warn(string line)=>Lines.Add(line); } }
namespace HarmonyLib
{
    public sealed class HarmonyMethod
    {
        public int priority;
        public MethodInfo method;
        public HarmonyMethod(Type type,string name) { method=AccessTools.DeclaredMethod(type,name); }
        public HarmonyMethod(MethodInfo method) { this.method=method; }
    }
    public static class AccessTools
    {
        public static MethodInfo DeclaredMethod(Type type,string name,Type[] parameters=null)
        {
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly;
            return parameters==null?type.GetMethod(name,flags):type.GetMethod(name,flags,null,parameters,null);
        }
    }
    public sealed class Patch { public string owner;public MethodInfo PatchMethod; }
    public sealed class Patches
    {
        public readonly List<Patch> Prefixes=new(),Postfixes=new(),Transpilers=new(),Finalizers=new();
    }
    public static class Priority { public const int First=800,Last=0; }
    public enum HarmonyPatchType { All }
    public sealed class Harmony
    {
        public static int PatchCalls,UnpatchCalls,ThrowOnPatch;
        public static string ThrowOnMethod;
        public static bool ThrowOnUnpatch;
        public static readonly HashSet<MethodBase> Active=new();
        public static readonly Dictionary<MethodBase,Patches> Info=new();
        public readonly string Id;
        public Harmony(string id) { Id=id; }
        public MethodInfo Patch(MethodBase original,HarmonyMethod prefix=null,HarmonyMethod postfix=null,HarmonyMethod transpiler=null,HarmonyMethod finalizer=null)
        {
            PatchCalls++;
            if(PatchCalls==ThrowOnPatch||original.Name==ThrowOnMethod)throw new InvalidOperationException("inert patch failure");
            Active.Add(original);var info=GetPatchInfo(original)??new Patches();
            if(prefix!=null)info.Prefixes.Add(new Patch{owner=Id,PatchMethod=prefix.method});
            if(postfix!=null)info.Postfixes.Add(new Patch{owner=Id,PatchMethod=postfix.method});
            if(transpiler!=null)info.Transpilers.Add(new Patch{owner=Id,PatchMethod=transpiler.method});
            if(finalizer!=null)info.Finalizers.Add(new Patch{owner=Id,PatchMethod=finalizer.method});
            Info[original]=info;return null;
        }
        public static Patches GetPatchInfo(MethodBase target)=>Info.TryGetValue(target,out var info)?info:null;
        public void UnpatchAll(string id=null)
        {
            UnpatchCalls++;if(ThrowOnUnpatch)throw new InvalidOperationException("inert cleanup failure");
            var remove=new List<MethodBase>();foreach(var pair in Info)
            {
                pair.Value.Prefixes.RemoveAll(p=>p.owner==(id??Id));pair.Value.Postfixes.RemoveAll(p=>p.owner==(id??Id));
                pair.Value.Transpilers.RemoveAll(p=>p.owner==(id??Id));pair.Value.Finalizers.RemoveAll(p=>p.owner==(id??Id));
                if(pair.Value.Prefixes.Count+pair.Value.Postfixes.Count+pair.Value.Transpilers.Count+pair.Value.Finalizers.Count==0)remove.Add(pair.Key);
            }
            foreach(var key in remove){Info.Remove(key);Active.Remove(key);}
        }
        public static void Reset() { PatchCalls=UnpatchCalls=ThrowOnPatch=0;ThrowOnMethod=null;ThrowOnUnpatch=false;Active.Clear();Info.Clear(); }
    }
}

namespace Game.Data { public sealed class MatRequest { } }
