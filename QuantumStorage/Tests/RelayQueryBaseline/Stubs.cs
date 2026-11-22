// Inert test substitutes. This project deliberately supplies no Harmony surface.
using System;
namespace Game.Constants { public static class G { public static bool IsDemoMode, HasEnteredGame; } }
namespace Game.Systems.QuantumStorage.Relays
{
    internal static class RelayMaterialSources { internal static string Status => "native"; }
    internal static class NanobotCandidateQueries { internal static string Status => "fixture_native_advisory"; }
}
namespace Game.Systems.QuantumStorage { internal static class RelayMaterialQuery { internal static bool FastCandidatesAvailable => true; } }
namespace KL.Utils { public static class D { public static readonly System.Collections.Generic.List<string> Lines = new System.Collections.Generic.List<string>(); public static void Warn(string line) { Lines.Add(line); } } }
namespace Game { public static class The { public static Loader ModLoader; } public class Loader { public Mod Mod; public Mod FindModById(string id)=>Mod; } public class Mod { public string Marker; public string ModFilePath(string file,bool a,bool b)=>Marker; } }
namespace UnityEngine
{
    public struct FrameTiming
    {
        public ulong frameStartTimestamp;
        public double cpuFrameTime,cpuMainThreadFrameTime,cpuMainThreadPresentWaitTime,cpuRenderThreadFrameTime,gpuFrameTime;
    }
    public static class FrameTimingManager
    {
        public static bool Enabled;
        public static bool Throw;
        public static uint Count;
        public static FrameTiming Latest;
        public static int Captures,Reads;
        public static bool IsFeatureEnabled()=>Enabled;
        public static void CaptureFrameTimings() { Captures++;if(Throw)throw new InvalidOperationException("native telemetry failed"); }
        public static uint GetLatestTimings(uint count,FrameTiming[] frames) { Reads++;frames[0]=Latest;return Count; }
        public static void Reset() { Enabled=Throw=false;Count=0;Latest=default;Captures=Reads=0; }
    }
    public static class Application { public static bool isFocused = true; }
    public enum HideFlags { HideAndDontSave }
    public class Object { public static void Destroy(Object obj) { } }
    public class GameObject : Object { public HideFlags hideFlags; public GameObject(string name) { } public T AddComponent<T>() where T:new()=>new T(); }
    public class MonoBehaviour : Object { public GameObject gameObject; }
}
