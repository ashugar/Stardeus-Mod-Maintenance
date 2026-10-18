using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.Constants;
using Game.Systems.Nanobots;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Optional attribution-only probes for Nanobot extensions owned by other
    /// installed mods. Targets are resolved by name so Optimization Mod does
    /// not gain a compile-time or load-time dependency on either mod.
    /// </summary>
    internal static class ExternalNanobotExtensionDiagnostics {
        internal const int ExpectedProbeCount = 16;

        private const int SpeedBucketCount = 5;
        private static readonly long FourMsTicks =
            (long) (Stopwatch.Frequency * 0.004d);
        private static readonly long EightMsTicks =
            (long) (Stopwatch.Frequency * 0.008d);
        private static readonly long SixteenMsTicks =
            (long) (Stopwatch.Frequency * 0.016d);
        private static readonly long ThirtyThreeMsTicks =
            (long) (Stopwatch.Frequency * 0.033d);
        private static readonly FieldInfo WorkingHivesField =
            AccessTools.Field(typeof(NanobotSys), "workingHives");
        private static readonly Dictionary<MethodBase, ProbeTarget> Targets =
            new Dictionary<MethodBase, ProbeTarget>();
        private static readonly List<OwnerProfile> Owners =
            new List<OwnerProfile>();
        private static readonly ProbeSpec[] ProbeSpecs = {
            new ProbeSpec("OnNanobotTick", true, false),
            new ProbeSpec("ValidateActiveOps", false, true),
            new ProbeSpec("MaintainActiveOps", false, true),
            new ProbeSpec("RefreshTrackedGoals", false, true),
            new ProbeSpec("FindBestCandidate", false, true),
            new ProbeSpec("BuildCandidate", false, false),
            new ProbeSpec("Start", false, true),
            new ProbeSpec("OnTickProgress", false, false)
        };

        private static long sessionStartedAt;
        private static int installedProbeCount;

        internal static int Install(Harmony harmony) {
            Reset();
            sessionStartedAt = Stopwatch.GetTimestamp();
            installedProbeCount += InstallOwner(
                harmony,
                "Quantum Storage",
                "Game.Systems.QuantumStorage.QuantumNanobotObjectHauling");
            installedProbeCount += InstallOwner(
                harmony,
                "Part Restoration Unit",
                "Game.Systems.PartRestoration.PartRestorationNanobotHauling");
            return installedProbeCount;
        }

        private static int InstallOwner(
                Harmony harmony, string label, string typeName) {
            Type ownerType = AccessTools.TypeByName(typeName);
            var owner = new OwnerProfile(label, typeName, ownerType);
            Owners.Add(owner);
            if (ownerType == null) {
                D.Warn("Optimization Mod external Nanobot diagnostic skipped "
                    + "{0}; its live type is not loaded.", label);
                return 0;
            }

            owner.TrackedGoalsField = AccessTools.Field(
                ownerType, "TrackedGoals");
            owner.ActiveOpsField = AccessTools.Field(ownerType, "ActiveOps");
            int installed = 0;
            for (int i = 0; i < ProbeSpecs.Length; i++) {
                ProbeSpec spec = ProbeSpecs[i];
                try {
                    MethodInfo original = AccessTools.DeclaredMethod(
                        ownerType, spec.MethodName);
                    if (original == null) {
                        D.Err("Optimization Mod external Nanobot diagnostic "
                            + "target is unavailable: {0}.{1}.", typeName,
                            spec.MethodName);
                        continue;
                    }

                    var target = new ProbeTarget(owner, spec.MethodName,
                        spec.IsOnTick, spec.IsDirectOnTickChild);
                    Targets[original] = target;
                    owner.Targets.Add(target);
                    MethodInfo prefix = AccessTools.DeclaredMethod(
                        typeof(ExternalNanobotExtensionDiagnostics),
                        nameof(Begin));
                    MethodInfo postfix = AccessTools.DeclaredMethod(
                        typeof(ExternalNanobotExtensionDiagnostics),
                        spec.IsOnTick ? nameof(EndOnTick) : nameof(End));
                    harmony.Patch(original,
                        prefix: new HarmonyMethod(prefix) {
                            priority = HarmonyLib.Priority.First
                        },
                        postfix: new HarmonyMethod(postfix) {
                            priority = HarmonyLib.Priority.Last
                        });
                    installed++;
                } catch (Exception ex) {
                    D.Err("Optimization Mod failed to install external "
                        + "Nanobot diagnostic for {0}.{1}: {2}", typeName,
                        spec.MethodName, ex);
                }
            }
            return installed;
        }

        private static void Begin(out long __state) {
            __state = Stopwatch.GetTimestamp();
        }

        private static void End(
                MethodBase __originalMethod, long __state) {
            Record(__originalMethod, __state);
        }

        private static void EndOnTick(
                MethodBase __originalMethod,
                NanobotSys __0,
                long __state) {
            ProbeTarget target = Record(__originalMethod, __state);
            target?.Owner.RecordWorkload(__0);
        }

        private static ProbeTarget Record(
                MethodBase originalMethod, long startedAt) {
            if (startedAt <= 0 || originalMethod == null
                    || !Targets.TryGetValue(
                        originalMethod, out ProbeTarget target)) {
                return null;
            }

            long elapsed = Stopwatch.GetTimestamp() - startedAt;
            int speed = (int) G.ClockSpeed;
            if (speed < 0 || speed >= SpeedBucketCount) { speed = 0; }
            TimingStats stats = target.BySpeed[speed];
            stats.Calls++;
            stats.TotalTicks += elapsed;
            if (elapsed > stats.MaxTicks) { stats.MaxTicks = elapsed; }
            if (elapsed >= FourMsTicks) { stats.OverFourMs++; }
            if (elapsed >= EightMsTicks) { stats.OverEightMs++; }
            if (elapsed >= SixteenMsTicks) { stats.OverSixteenMs++; }
            if (elapsed >= ThirtyThreeMsTicks) {
                stats.OverThirtyThreeMs++;
            }
            return target;
        }

        internal static void LogSummary() {
            long elapsed = sessionStartedAt == 0
                ? 0L
                : Stopwatch.GetTimestamp() - sessionStartedAt;
            D.Warn("Optimization Mod external Nanobot attribution summary: "
                + "{0:0.0}s captured, {1}/{2} timing probes installed. "
                + "Sub-method timings are nested and are not additive.",
                ToSeconds(elapsed), installedProbeCount,
                ExpectedProbeCount);

            for (int i = 0; i < Owners.Count; i++) {
                OwnerProfile owner = Owners[i];
                if (owner.OwnerType == null) {
                    continue;
                }

                ProbeTarget outer = owner.Find("OnNanobotTick");
                long outerCalls = outer?.TotalCalls ?? 0L;
                long outerTicks = outer?.TotalTicks ?? 0L;
                long directChildTicks = 0L;
                for (int j = 0; j < owner.Targets.Count; j++) {
                    ProbeTarget target = owner.Targets[j];
                    if (target.IsDirectOnTickChild) {
                        directChildTicks += target.TotalTicks;
                    }
                }
                long residualTicks = Math.Max(
                    0L, outerTicks - directChildTicks);
                D.Warn($"Optimization Mod external Nanobot workload "
                    + $"{owner.Label}: {outerCalls} OnNanobotTick calls; "
                    + $"working Hives avg/max {owner.AverageHives:0.###}/"
                    + $"{owner.MaxHives}, tracked goals avg/max "
                    + $"{owner.AverageTrackedGoals:0.###}/"
                    + $"{owner.MaxTrackedGoals}, active operations avg/max "
                    + $"{owner.AverageActiveOps:0.###}/{owner.MaxActiveOps}, "
                    + $"workload read failures {owner.WorkloadReadFailures}; "
                    + $"direct-child time "
                    + $"{ToMilliseconds(directChildTicks):0.###} ms, outer "
                    + $"residual estimate "
                    + $"{ToMilliseconds(residualTicks):0.###} ms.");

                for (int j = 0; j < owner.Targets.Count; j++) {
                    LogTarget(owner.Targets[j], outerCalls);
                }
            }
        }

        private static void LogTarget(
                ProbeTarget target, long outerCalls) {
            long calls = target.TotalCalls;
            if (calls == 0) { return; }
            long totalTicks = target.TotalTicks;
            long maxTicks = 0L;
            long overFour = 0L;
            long overEight = 0L;
            long overSixteen = 0L;
            long overThirtyThree = 0L;
            var speedParts = new List<string>();
            for (int i = 0; i < target.BySpeed.Length; i++) {
                TimingStats stats = target.BySpeed[i];
                if (stats.MaxTicks > maxTicks) { maxTicks = stats.MaxTicks; }
                overFour += stats.OverFourMs;
                overEight += stats.OverEightMs;
                overSixteen += stats.OverSixteenMs;
                overThirtyThree += stats.OverThirtyThreeMs;
                if (stats.Calls == 0) { continue; }
                speedParts.Add($"{(ClockSpeed) i}: {stats.Calls} calls, "
                    + $"{ToMilliseconds(stats.TotalTicks) / stats.Calls:0.###} "
                    + $"ms avg, {ToMilliseconds(stats.MaxTicks):0.###} ms max");
            }

            double callsPerOuter = outerCalls == 0
                ? 0d
                : calls / (double) outerCalls;
            D.Warn($"Optimization Mod external Nanobot timing "
                + $"{target.Owner.Label}.{target.MethodName}: {calls} calls "
                + $"({callsPerOuter:0.###}/outer tick), "
                + $"{ToMilliseconds(totalTicks):0.###} ms total, "
                + $"{ToMilliseconds(totalTicks) / calls:0.###} ms avg, "
                + $"{ToMilliseconds(maxTicks):0.###} ms max, >=4 ms "
                + $"{overFour}, >=8 ms {overEight}, >=16 ms "
                + $"{overSixteen}, >=33 ms {overThirtyThree}; "
                + string.Join(" | ", speedParts));
        }

        private static int ReadCount(FieldInfo field, object instance) {
            if (field == null) { return -1; }
            object value = field.GetValue(instance);
            if (value is ICollection collection) {
                return collection.Count;
            }
            PropertyInfo countProperty = value == null
                ? null
                : AccessTools.Property(value.GetType(), "Count");
            return countProperty?.GetValue(value, null) is int count
                ? count
                : -1;
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static double ToSeconds(long ticks) {
            return ticks / (double) Stopwatch.Frequency;
        }

        internal static void Reset() {
            Targets.Clear();
            Owners.Clear();
            sessionStartedAt = 0L;
            installedProbeCount = 0;
        }

        private sealed class ProbeSpec {
            internal readonly string MethodName;
            internal readonly bool IsOnTick;
            internal readonly bool IsDirectOnTickChild;

            internal ProbeSpec(
                    string methodName,
                    bool isOnTick,
                    bool isDirectOnTickChild) {
                MethodName = methodName;
                IsOnTick = isOnTick;
                IsDirectOnTickChild = isDirectOnTickChild;
            }
        }

        private sealed class OwnerProfile {
            internal readonly string Label;
            internal readonly string TypeName;
            internal readonly Type OwnerType;
            internal readonly List<ProbeTarget> Targets =
                new List<ProbeTarget>();
            internal FieldInfo TrackedGoalsField;
            internal FieldInfo ActiveOpsField;
            internal long WorkloadSamples;
            internal long TotalHives;
            internal long TotalTrackedGoals;
            internal long TotalActiveOps;
            internal int MaxHives;
            internal int MaxTrackedGoals;
            internal int MaxActiveOps;
            internal long WorkloadReadFailures;

            internal OwnerProfile(
                    string label, string typeName, Type ownerType) {
                Label = label;
                TypeName = typeName;
                OwnerType = ownerType;
            }

            internal double AverageHives => WorkloadSamples == 0
                ? 0d
                : TotalHives / (double) WorkloadSamples;

            internal double AverageTrackedGoals => WorkloadSamples == 0
                ? 0d
                : TotalTrackedGoals / (double) WorkloadSamples;

            internal double AverageActiveOps => WorkloadSamples == 0
                ? 0d
                : TotalActiveOps / (double) WorkloadSamples;

            internal void RecordWorkload(NanobotSys nanobotSystem) {
                try {
                    int hives = ReadCount(WorkingHivesField, nanobotSystem);
                    int trackedGoals = ReadCount(TrackedGoalsField, null);
                    int activeOps = ReadCount(ActiveOpsField, null);
                    if (hives < 0 || trackedGoals < 0 || activeOps < 0) {
                        WorkloadReadFailures++;
                        return;
                    }
                    WorkloadSamples++;
                    TotalHives += hives;
                    TotalTrackedGoals += trackedGoals;
                    TotalActiveOps += activeOps;
                    if (hives > MaxHives) { MaxHives = hives; }
                    if (trackedGoals > MaxTrackedGoals) {
                        MaxTrackedGoals = trackedGoals;
                    }
                    if (activeOps > MaxActiveOps) {
                        MaxActiveOps = activeOps;
                    }
                } catch {
                    WorkloadReadFailures++;
                }
            }

            internal ProbeTarget Find(string methodName) {
                for (int i = 0; i < Targets.Count; i++) {
                    if (Targets[i].MethodName == methodName) {
                        return Targets[i];
                    }
                }
                return null;
            }
        }

        private sealed class ProbeTarget {
            internal readonly OwnerProfile Owner;
            internal readonly string MethodName;
            internal readonly bool IsOnTick;
            internal readonly bool IsDirectOnTickChild;
            internal readonly TimingStats[] BySpeed = {
                new TimingStats(),
                new TimingStats(),
                new TimingStats(),
                new TimingStats(),
                new TimingStats()
            };

            internal ProbeTarget(
                    OwnerProfile owner,
                    string methodName,
                    bool isOnTick,
                    bool isDirectOnTickChild) {
                Owner = owner;
                MethodName = methodName;
                IsOnTick = isOnTick;
                IsDirectOnTickChild = isDirectOnTickChild;
            }

            internal long TotalCalls {
                get {
                    long total = 0L;
                    for (int i = 0; i < BySpeed.Length; i++) {
                        total += BySpeed[i].Calls;
                    }
                    return total;
                }
            }

            internal long TotalTicks {
                get {
                    long total = 0L;
                    for (int i = 0; i < BySpeed.Length; i++) {
                        total += BySpeed[i].TotalTicks;
                    }
                    return total;
                }
            }
        }

        private sealed class TimingStats {
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long OverFourMs;
            internal long OverEightMs;
            internal long OverSixteenMs;
            internal long OverThirtyThreeMs;
        }
    }
}
