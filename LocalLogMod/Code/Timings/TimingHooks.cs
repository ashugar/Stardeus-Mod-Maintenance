using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.Systems.AI;
using Game.Systems.Crafting;
using Game.Systems.Electricity;
using Game.Systems.Creatures;
using Game.Systems.Items;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.Timings {
    [HarmonyPatch]
    internal static class TimingHooks {
        private static readonly Dictionary<string, Sample> Samples = new Dictionary<string, Sample>();

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets() {
            foreach (Type type in new[] { typeof(AISys), typeof(CraftingSys), typeof(ElectricitySys),
                    typeof(BeingsSys), typeof(ObjsSys) }) {
                MethodInfo method = AccessTools.Method(type, "OnTick", new[] { typeof(long) });
                if (method != null) { yield return method; }
            }
        }

        [HarmonyPrefix]
        private static void Start(out long __state) {
            __state = Stopwatch.GetTimestamp();
        }

        [HarmonyPostfix]
        private static void Stop(MethodBase __originalMethod, long __state) {
            SessionLog.Guard("timings", () => {
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                string name = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
                if (!Samples.TryGetValue(name, out Sample sample)) { sample = new Sample(); Samples.Add(name, sample); }
                sample.Count++;
                sample.Total += ms;
                if (ms > sample.Max) { sample.Max = ms; }
                if (ms >= SessionLog.TimingWarningMs) {
                    SessionLog.Write("Timings", "slow system=" + name + " ms=" + ms.ToString("0.###")
                        + " threshold=" + SessionLog.TimingWarningMs);
                }
                if (sample.Count >= 300) {
                    double average = sample.Total / sample.Count;
                    SessionLog.Write("Timings", "window system=" + name + " calls=" + sample.Count
                        + " avgMs=" + average.ToString("0.###") + " maxMs=" + sample.Max.ToString("0.###")
                        + " investigate=" + (average >= SessionLog.TimingWarningMs / 2.0 || sample.Max >= SessionLog.TimingWarningMs));
                    sample.Count = 0;
                    sample.Total = 0;
                    sample.Max = 0;
                }
            });
        }

        private sealed class Sample {
            internal int Count;
            internal double Total;
            internal double Max;
        }

        internal static void FlushSummary() {
            if (!SessionLog.Timings) { return; }
            foreach (var entry in Samples) {
                Sample sample = entry.Value;
                if (sample.Count == 0) { continue; }
                double average = sample.Total / sample.Count;
                SessionLog.Write("Timings", "final_window system=" + entry.Key + " calls=" + sample.Count
                    + " avgMs=" + average.ToString("0.###") + " maxMs=" + sample.Max.ToString("0.###")
                    + " investigate=" + (average >= SessionLog.TimingWarningMs / 2.0
                        || sample.Max >= SessionLog.TimingWarningMs));
                sample.Count = 0;
                sample.Total = 0;
                sample.Max = 0;
            }
        }
    }
}
