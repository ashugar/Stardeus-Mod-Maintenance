using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Game.Constants;
using Game.Systems.Nanobots;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Temporary coarse-grained timing for the five phases inside Core's
    /// NanobotSys.OnTick. The injected calls sit outside the per-goal,
    /// per-work-item, and per-hive loops so the diagnostic does not multiply
    /// its own overhead by the size of a large save.
    /// </summary>
    internal static class NanobotPhaseDiagnostics {
        private const int PhaseCount = 5;
        private const int HivePreparation = 0;
        private const int CandidateScan = 1;
        private const int RangeBuildAndJob = 2;
        private const int HitResolution = 3;
        private const int WorkDispatch = 4;

        private static readonly string[] PhaseNames = {
            "HivePreparation",
            "CandidateScan",
            "RangeBuildAndJob",
            "HitResolution",
            "WorkDispatch"
        };
        private static readonly PhaseStats[] Phases = {
            new PhaseStats(),
            new PhaseStats(),
            new PhaseStats(),
            new PhaseStats(),
            new PhaseStats()
        };
        private static readonly FieldInfo HubsField = AccessTools.Field(
            typeof(NanobotSys), "hubs");
        private static readonly FieldInfo WorkingHivesField = AccessTools.Field(
            typeof(NanobotSys), "workingHives");
        private static readonly FieldInfo WorkItemsField = AccessTools.Field(
            typeof(NanobotSys), "nanobotWorkItems");
        private static readonly FieldInfo HaulingControllerField =
            AccessTools.Field(typeof(NanobotSys), "haulingController");

        [ThreadStatic] private static bool tickActive;
        [ThreadStatic] private static int activePhase;
        [ThreadStatic] private static long phaseStartedAt;

        private static FieldInfo activeOpsField;
        private static bool countCaptureFailed;
        private static long snapshots;
        private static long totalHubs;
        private static long totalWorkingHives;
        private static long totalWorkItems;
        private static long totalActiveHaulOps;
        private static int maxHubs;
        private static int maxWorkingHives;
        private static int maxWorkItems;
        private static int maxActiveHaulOps;

        internal static bool IsTranspilerActive { get; private set; }
        internal static string LastTranspilerMatch { get; private set; }

        internal static int Install(Harmony harmony) {
            try {
                MethodInfo original = AccessTools.DeclaredMethod(
                    typeof(NanobotSys), "OnTick", new[] { typeof(long) });
                MethodInfo prefix = AccessTools.DeclaredMethod(
                    typeof(NanobotPhaseDiagnostics), nameof(BeginTick));
                MethodInfo postfix = AccessTools.DeclaredMethod(
                    typeof(NanobotPhaseDiagnostics), nameof(EndTick));
                MethodInfo transpiler = AccessTools.DeclaredMethod(
                    typeof(NanobotPhaseDiagnostics), nameof(Transpiler));
                if (original == null || prefix == null || postfix == null
                        || transpiler == null) {
                    D.Err("Optimization Mod Nanobot phase diagnostic target "
                        + "is unavailable; Core Nanobot behavior is unchanged.");
                    return 0;
                }

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix) {
                        priority = HarmonyLib.Priority.First
                    },
                    postfix: new HarmonyMethod(postfix) {
                        priority = HarmonyLib.Priority.Last
                    },
                    transpiler: new HarmonyMethod(transpiler) {
                        priority = HarmonyLib.Priority.Low
                    });
                return IsTranspilerActive ? 1 : 0;
            } catch (Exception ex) {
                D.Err("Optimization Mod failed to install Nanobot phase "
                    + "diagnostics: {0}", ex);
                return 0;
            }
        }

        private static void BeginTick() {
            if (!IsTranspilerActive) { return; }
            tickActive = true;
            activePhase = -1;
            phaseStartedAt = 0;
            StartPhase(HivePreparation);
        }

        private static void EndTick() {
            if (!tickActive) { return; }
            FinishActivePhase();
            tickActive = false;
        }

        private static void BeginCandidateScan() {
            TransitionTo(CandidateScan);
        }

        private static void FinishCandidateScan(NanobotSys instance) {
            if (!tickActive) { return; }
            FinishActivePhase();
            CaptureCounts(instance);
        }

        private static void BeginRangeBuildAndJob() {
            if (!tickActive) { return; }
            StartPhase(RangeBuildAndJob);
        }

        private static void BeginHitResolution() {
            if (!tickActive) { return; }
            FinishActivePhase();
            StartPhase(HitResolution);
        }

        private static void BeginWorkDispatch() {
            TransitionTo(WorkDispatch);
        }

        private static void TransitionTo(int phase) {
            if (!tickActive) { return; }
            FinishActivePhase();
            StartPhase(phase);
        }

        private static void StartPhase(int phase) {
            activePhase = phase;
            phaseStartedAt = Stopwatch.GetTimestamp();
        }

        private static void FinishActivePhase() {
            if (activePhase < 0 || activePhase >= PhaseCount
                    || phaseStartedAt <= 0) {
                activePhase = -1;
                phaseStartedAt = 0;
                return;
            }

            long elapsed = Stopwatch.GetTimestamp() - phaseStartedAt;
            int speed = (int) G.ClockSpeed;
            if (speed < 0 || speed >= 5) { speed = 0; }
            Phases[activePhase].Record(speed, elapsed);
            activePhase = -1;
            phaseStartedAt = 0;
        }

        private static void CaptureCounts(NanobotSys instance) {
            if (instance == null || countCaptureFailed) { return; }
            try {
                int hubs = CountOf(HubsField?.GetValue(instance));
                int workingHives = CountOf(
                    WorkingHivesField?.GetValue(instance));
                int workItems = CountOf(WorkItemsField?.GetValue(instance));
                object controller = HaulingControllerField?.GetValue(instance);
                if (controller != null && activeOpsField == null) {
                    activeOpsField = AccessTools.Field(
                        controller.GetType(), "activeOps");
                }
                int activeHaulOps = CountOf(
                    activeOpsField?.GetValue(controller));

                snapshots++;
                totalHubs += hubs;
                totalWorkingHives += workingHives;
                totalWorkItems += workItems;
                totalActiveHaulOps += activeHaulOps;
                if (hubs > maxHubs) { maxHubs = hubs; }
                if (workingHives > maxWorkingHives) {
                    maxWorkingHives = workingHives;
                }
                if (workItems > maxWorkItems) { maxWorkItems = workItems; }
                if (activeHaulOps > maxActiveHaulOps) {
                    maxActiveHaulOps = activeHaulOps;
                }
            } catch (Exception ex) {
                countCaptureFailed = true;
                D.Err("Optimization Mod stopped Nanobot count capture after "
                    + "an unexpected Core layout change: {0}", ex);
            }
        }

        private static int CountOf(object value) {
            return value is ICollection collection ? collection.Count : 0;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions) {
            var original = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in instructions) {
                original.Add(new CodeInstruction(instruction));
            }
            var patched = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in original) {
                patched.Add(new CodeInstruction(instruction));
            }

            FieldInfo workItems = AccessTools.Field(
                typeof(NanobotSys), "nanobotWorkItems");
            FieldInfo closestHits = AccessTools.Field(
                typeof(NanobotSys), "closestHits");
            MethodInfo beginCandidate = AccessTools.DeclaredMethod(
                typeof(NanobotPhaseDiagnostics), nameof(BeginCandidateScan));
            MethodInfo finishCandidate = AccessTools.DeclaredMethod(
                typeof(NanobotPhaseDiagnostics), nameof(FinishCandidateScan));
            MethodInfo beginRange = AccessTools.DeclaredMethod(
                typeof(NanobotPhaseDiagnostics), nameof(BeginRangeBuildAndJob));
            MethodInfo beginHitResolution = AccessTools.DeclaredMethod(
                typeof(NanobotPhaseDiagnostics), nameof(BeginHitResolution));
            MethodInfo beginWork = AccessTools.DeclaredMethod(
                typeof(NanobotPhaseDiagnostics), nameof(BeginWorkDispatch));

            int candidateStart = FindFieldCall(
                patched, workItems, "Clear", 0);
            int candidateEnd = FindFieldCall(
                patched, workItems, "get_Count", candidateStart + 1);
            int rangeConstructor = FindGenericConstructor(
                patched, "Unity.Collections.NativeArray`1", candidateEnd + 1);
            int rangeStart = FindAddressLoadIndexBefore(
                patched, rangeConstructor, 10);
            int hitResolutionStart = FindFieldCall(
                patched, closestHits, "Clear", rangeStart + 1);
            int queueDispose = FindCall(
                patched, "Unity.Collections.NativeQueue`1", "Dispose",
                hitResolutionStart + 1);

            LastTranspilerMatch = $"candidateStart={candidateStart}, "
                + $"candidateEnd={candidateEnd}, rangeStart={rangeStart}, "
                + $"rangeConstructor={rangeConstructor}, "
                + $"hitResolutionStart={hitResolutionStart}, "
                + $"queueDispose={queueDispose}";

            if (candidateStart < 0 || candidateEnd < 0 || rangeStart < 0
                    || hitResolutionStart < 0 || queueDispose < 0
                    || beginCandidate == null || finishCandidate == null
                    || beginRange == null || beginHitResolution == null
                    || beginWork == null) {
                TryLogError("Optimization Mod Nanobot phase transpiler did not "
                    + "match Core OnTick (" + LastTranspilerMatch + "); phase "
                    + "timing is disabled and vanilla Nanobot behavior is "
                    + "unchanged.");
                IsTranspilerActive = false;
                return original;
            }

            // Insert from the end of the method toward the beginning so the
            // anchors above remain valid. Labels move to the first injected
            // instruction so branches cannot bypass a timing boundary.
            InsertBefore(patched, queueDispose + 1, new[] {
                new CodeInstruction(OpCodes.Call, beginWork)
            });
            InsertBefore(patched, hitResolutionStart, new[] {
                new CodeInstruction(OpCodes.Call, beginHitResolution)
            });
            InsertBefore(patched, rangeStart, new[] {
                new CodeInstruction(OpCodes.Call, beginRange)
            });
            InsertBefore(patched, candidateEnd, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, finishCandidate)
            });
            InsertBefore(patched, candidateStart, new[] {
                new CodeInstruction(OpCodes.Call, beginCandidate)
            });

            bool firstActivation = !IsTranspilerActive;
            IsTranspilerActive = true;
            if (firstActivation) {
                D.Warn("Optimization Mod installed coarse Nanobot phase "
                    + "diagnostics without per-item probes.");
            }
            return patched;
        }

        private static void TryLogError(string message) {
            try {
                D.Err(message);
            } catch {
                // Unity logging is unavailable in headless static verifiers.
                // Runtime patching still reports the mismatch normally.
            }
        }

        private static int FindFieldCall(
                List<CodeInstruction> instructions,
                FieldInfo field,
                string methodName,
                int start) {
            if (field == null) { return -1; }
            for (int i = Math.Max(0, start); i + 1 < instructions.Count; i++) {
                if (instructions[i].opcode == OpCodes.Ldfld
                        && Equals(instructions[i].operand, field)
                        && IsCallNamed(instructions[i + 1], methodName)) {
                    return i;
                }
            }
            return -1;
        }

        private static int FindGenericConstructor(
                List<CodeInstruction> instructions,
                string genericTypeName,
                int start) {
            for (int i = Math.Max(0, start); i < instructions.Count; i++) {
                if ((instructions[i].opcode != OpCodes.Newobj
                            && instructions[i].opcode != OpCodes.Call)
                        || !(instructions[i].operand is ConstructorInfo ctor)
                        || ctor.DeclaringType == null
                        || !ctor.DeclaringType.IsGenericType) {
                    continue;
                }
                if (ctor.DeclaringType.GetGenericTypeDefinition().FullName
                        == genericTypeName) {
                    return i;
                }
            }
            return -1;
        }

        private static int FindCall(
                List<CodeInstruction> instructions,
                string genericTypeName,
                string methodName,
                int start) {
            for (int i = Math.Max(0, start); i < instructions.Count; i++) {
                if (!(instructions[i].operand is MethodInfo method)
                        || method.Name != methodName
                        || method.DeclaringType == null
                        || !method.DeclaringType.IsGenericType) {
                    continue;
                }
                if (method.DeclaringType.GetGenericTypeDefinition().FullName
                        == genericTypeName) {
                    return i;
                }
            }
            return -1;
        }

        private static int FindAddressLoadIndexBefore(
                List<CodeInstruction> instructions,
                int callIndex,
                int searchDistance) {
            if (callIndex < 0) { return -1; }
            int from = Math.Max(0, callIndex - searchDistance);
            for (int i = callIndex - 1; i >= from; i--) {
                OpCode opcode = instructions[i].opcode;
                if (opcode == OpCodes.Ldloca || opcode == OpCodes.Ldloca_S) {
                    return i;
                }
            }
            return -1;
        }

        private static bool IsCallNamed(
                CodeInstruction instruction, string methodName) {
            return instruction.operand is MethodInfo method
                && method.Name == methodName;
        }

        private static void InsertBefore(
                List<CodeInstruction> instructions,
                int index,
                IList<CodeInstruction> injected) {
            if (index < 0 || index >= instructions.Count
                    || injected == null || injected.Count == 0) {
                return;
            }
            CodeInstruction anchor = instructions[index];
            injected[0].labels.AddRange(anchor.labels);
            injected[0].blocks.AddRange(anchor.blocks);
            anchor.labels.Clear();
            anchor.blocks.Clear();
            instructions.InsertRange(index, injected);
        }

        internal static void LogSummary() {
            if (!IsTranspilerActive) { return; }
            if (snapshots > 0) {
                D.Warn($"Optimization Mod Nanobot workload summary: "
                    + $"{snapshots} tick snapshots; hubs "
                    + $"{totalHubs / (double) snapshots:0.0} avg/{maxHubs} "
                    + $"max, working hives "
                    + $"{totalWorkingHives / (double) snapshots:0.0} "
                    + $"avg/{maxWorkingHives} max, work items "
                    + $"{totalWorkItems / (double) snapshots:0.0} "
                    + $"avg/{maxWorkItems} max, active haul ops "
                    + $"{totalActiveHaulOps / (double) snapshots:0.0} "
                    + $"avg/{maxActiveHaulOps} max.");
            }
            for (int i = 0; i < Phases.Length; i++) {
                Phases[i].Log(PhaseNames[i]);
            }
        }

        internal static void Reset() {
            IsTranspilerActive = false;
            LastTranspilerMatch = null;
            tickActive = false;
            activePhase = -1;
            phaseStartedAt = 0;
            activeOpsField = null;
            countCaptureFailed = false;
            snapshots = 0;
            totalHubs = 0;
            totalWorkingHives = 0;
            totalWorkItems = 0;
            totalActiveHaulOps = 0;
            maxHubs = 0;
            maxWorkingHives = 0;
            maxWorkItems = 0;
            maxActiveHaulOps = 0;
            for (int i = 0; i < Phases.Length; i++) {
                Phases[i].Reset();
            }
        }

        private sealed class PhaseStats {
            private readonly TimingStats[] bySpeed = {
                new TimingStats(),
                new TimingStats(),
                new TimingStats(),
                new TimingStats(),
                new TimingStats()
            };

            internal void Record(int speed, long elapsed) {
                TimingStats stats = bySpeed[speed];
                stats.Calls++;
                stats.TotalTicks += elapsed;
                if (elapsed > stats.MaxTicks) { stats.MaxTicks = elapsed; }
            }

            internal void Log(string name) {
                long calls = 0;
                long total = 0;
                long max = 0;
                var speedParts = new List<string>();
                for (int i = 0; i < bySpeed.Length; i++) {
                    TimingStats stats = bySpeed[i];
                    if (stats.Calls == 0) { continue; }
                    calls += stats.Calls;
                    total += stats.TotalTicks;
                    if (stats.MaxTicks > max) { max = stats.MaxTicks; }
                    double speedMs = ToMilliseconds(stats.TotalTicks);
                    speedParts.Add($"{(ClockSpeed) i}: {stats.Calls} calls, "
                        + $"{speedMs / stats.Calls:0.###} ms avg, "
                        + $"{ToMilliseconds(stats.MaxTicks):0.###} ms max");
                }
                if (calls == 0) { return; }
                double totalMs = ToMilliseconds(total);
                D.Warn($"Optimization Mod Nanobot phase {name}: {calls} "
                    + $"calls, {totalMs:0.###} ms total, "
                    + $"{totalMs / calls:0.###} ms avg, "
                    + $"{ToMilliseconds(max):0.###} ms max; "
                    + string.Join(" | ", speedParts));
            }

            internal void Reset() {
                for (int i = 0; i < bySpeed.Length; i++) {
                    bySpeed[i].Reset();
                }
            }
        }

        private sealed class TimingStats {
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;

            internal void Reset() {
                Calls = 0;
                TotalTicks = 0;
                MaxTicks = 0;
            }
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }
    }
}
