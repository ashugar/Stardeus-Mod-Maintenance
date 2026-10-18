using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.CodeGen;
using Game.Constants;
using Game.Systems.Areas;
using Game.Systems.Reg;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Temporary aggregate-only diagnostics for Core's Area and navigational-
    /// Region topology rebuild path. The hooks observe existing lifecycle
    /// methods and never defer, suppress, or mutate a rebuild.
    /// </summary>
    internal static class AreaRegionPerformanceDiagnostics {
        internal const int ExpectedHookCount = 8;
        private const int SpeedCount = 5;
        private const int MaxRetainedEvents = 8;

        private static readonly long FourMsTicks =
            (long) (Stopwatch.Frequency * 0.004d);
        private static readonly long EightMsTicks =
            (long) (Stopwatch.Frequency * 0.008d);
        private static readonly long SixteenMsTicks =
            (long) (Stopwatch.Frequency * 0.016d);
        private static readonly long ThirtyThreeMsTicks =
            (long) (Stopwatch.Frequency * 0.033d);

        private static readonly FieldInfo PendingChangesField =
            AccessTools.Field(typeof(AreaSys), "pendingChanges");

        private static readonly PhaseStats AreaPending =
            new PhaseStats("Area.ProcessPendingChanges");
        private static readonly PhaseStats AreaIncrementalFinish =
            new PhaseStats("Area.FinishIncrementalUpdate");
        private static readonly PhaseStats AreaFullRecalculate =
            new PhaseStats("Area.FullRecalculate");
        private static readonly PhaseStats AreaCompletionFrame =
            new PhaseStats("Area.CompleteJobAndPostProcess");
        private static readonly PhaseStats AreaAfterFlood =
            new PhaseStats("Area.AfterFlood");
        private static readonly PhaseStats RegionRecalculate =
            new PhaseStats("Regions.Recalculate");

        private static readonly List<TopologyEvent> WorstAreaCompletions =
            new List<TopologyEvent>(MaxRetainedEvents);
        private static readonly List<TopologyEvent> WorstRegionRebuilds =
            new List<TopologyEvent>(MaxRetainedEvents);

        private static long sessionStartedAt;
        private static long areaPendingBatches;
        private static long areaPendingChanges;
        private static int maxAreaPendingChanges;
        private static long areaIncrementalFinishes;
        private static long areaFullRecalculations;
        private static long areaFullFromPending;
        private static long areaFullImmediate;
        private static long areaFullScheduled;
        private static long areaCompletionFrames;
        private static long areaAfterFloodCalls;

        private static long regionDirtySignals;
        private static long regionDirtyNewWindows;
        private static long regionDirtyCoalesced;
        private static long regionDirtyFromArea;
        private static long regionDirtyFromPassability;
        private static long regionDirtyOther;
        private static long regionDenyUpdates;
        private static long regionRebuilds;

        private static long previousRegionTick;
        private static int previousRegionFrame;
        private static long regionTickGapTotal;
        private static long regionTickGapMin;
        private static long regionTickGapMax;
        private static long regionFrameGapTotal;
        private static int regionFrameGapMin;
        private static int regionFrameGapMax;
        private static long regionIntervalSamples;

        private static bool insideAreaPending;
        private static bool insideAreaAfterFlood;
        private static bool insideRegionDenyUpdate;
        private static bool areaCompletionActive;
        private static long areaCompletionAfterFloodTicks;
        private static bool failureLogged;

        internal static bool IsFailOpen { get; private set; }

        internal static int Install(Harmony harmony) {
            Reset();
            sessionStartedAt = Stopwatch.GetTimestamp();
            int installed = 0;
            installed += Patch(harmony, typeof(AreaSys),
                "ProcessPendingChanges", Type.EmptyTypes,
                nameof(BeginAreaPending), nameof(EndAreaPending));
            installed += Patch(harmony, typeof(AreaSys),
                "FinishIncrementalUpdate", Type.EmptyTypes,
                nameof(BeginAreaIncrementalFinish),
                nameof(EndAreaIncrementalFinish));
            installed += Patch(harmony, typeof(AreaSys),
                "FullRecalculate", new[] { typeof(bool) },
                nameof(BeginAreaFullRecalculate),
                nameof(EndAreaFullRecalculate));
            installed += Patch(harmony, typeof(AreaSys), "EveryFrame",
                Type.EmptyTypes, nameof(BeginAreaCompletionFrame),
                nameof(EndAreaCompletionFrame));
            installed += Patch(harmony, typeof(AreaSys), "AfterFlood",
                Type.EmptyTypes, nameof(BeginAreaAfterFlood),
                nameof(EndAreaAfterFlood));
            installed += Patch(harmony, typeof(RegSys), "MarkRegsDirty",
                Type.EmptyTypes, nameof(RecordRegionDirty), null);
            installed += Patch(harmony, typeof(RegSys),
                nameof(RegSys.UpdateRegDenyAt), new[] { typeof(int) },
                nameof(BeginRegionDenyUpdate),
                nameof(EndRegionDenyUpdate));
            installed += Patch(harmony, typeof(RegSys),
                nameof(RegSys.Recalculate), Type.EmptyTypes,
                nameof(BeginRegionRecalculate),
                nameof(EndRegionRecalculate));
            return installed;
        }

        private static int Patch(
                Harmony harmony,
                Type targetType,
                string targetName,
                Type[] parameterTypes,
                string prefixName,
                string postfixName) {
            try {
                MethodInfo original = AccessTools.DeclaredMethod(
                    targetType, targetName, parameterTypes);
                MethodInfo prefix = string.IsNullOrEmpty(prefixName)
                    ? null
                    : AccessTools.DeclaredMethod(
                        typeof(AreaRegionPerformanceDiagnostics), prefixName);
                MethodInfo postfix = string.IsNullOrEmpty(postfixName)
                    ? null
                    : AccessTools.DeclaredMethod(
                        typeof(AreaRegionPerformanceDiagnostics), postfixName);
                if (original == null || prefixName != null && prefix == null
                        || postfixName != null && postfix == null) {
                    TryLogError("Optimization Mod Area/Region diagnostic "
                        + "target is unavailable: " + targetType.FullName
                        + "." + targetName + ".");
                    return 0;
                }
                harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(prefix) {
                        priority = HarmonyLib.Priority.First
                    },
                    postfix: postfix == null ? null : new HarmonyMethod(postfix) {
                        priority = HarmonyLib.Priority.Last
                    });
                return 1;
            } catch (Exception ex) {
                TryLogError("Optimization Mod failed to install Area/Region "
                    + "diagnostic for " + targetType.FullName + "."
                    + targetName + ": " + ex);
                return 0;
            }
        }

        private static void BeginAreaPending(
                AreaSys __instance, out long __state) {
            __state = 0;
            if (IsFailOpen) { return; }
            try {
                insideAreaPending = true;
                areaPendingBatches++;
                int count = PendingChangeCount(__instance);
                areaPendingChanges += count;
                if (count > maxAreaPendingChanges) {
                    maxAreaPendingChanges = count;
                }
                __state = Stopwatch.GetTimestamp();
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndAreaPending(long __state) {
            insideAreaPending = false;
            RecordPhase(AreaPending, __state);
        }

        private static void BeginAreaIncrementalFinish(out long __state) {
            __state = 0;
            if (IsFailOpen) { return; }
            try {
                areaIncrementalFinishes++;
                __state = Stopwatch.GetTimestamp();
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndAreaIncrementalFinish(long __state) {
            RecordPhase(AreaIncrementalFinish, __state);
        }

        private static void BeginAreaFullRecalculate(out long __state) {
            __state = 0;
            if (IsFailOpen) { return; }
            try {
                areaFullRecalculations++;
                if (insideAreaPending) { areaFullFromPending++; }
                if ((int) G.ClockSpeed > (int) ClockSpeed.Fast2
                        || !Ready.Gen) {
                    areaFullImmediate++;
                } else {
                    areaFullScheduled++;
                }
                __state = Stopwatch.GetTimestamp();
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndAreaFullRecalculate(long __state) {
            RecordPhase(AreaFullRecalculate, __state);
        }

        private static void BeginAreaCompletionFrame(
                bool ___isRecalcRunning, out long __state) {
            __state = 0;
            if (IsFailOpen || !___isRecalcRunning) { return; }
            try {
                areaCompletionFrames++;
                areaCompletionAfterFloodTicks = 0;
                areaCompletionActive = true;
                __state = Stopwatch.GetTimestamp();
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndAreaCompletionFrame(long __state) {
            if (__state <= 0 || IsFailOpen) { return; }
            try {
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - __state;
                AreaCompletionFrame.Record(CurrentSpeed(), elapsed);
                Retain(WorstAreaCompletions, new TopologyEvent {
                    Offset = now - sessionStartedAt,
                    Tick = G.Ticks,
                    Frame = GameTimer.frameCount,
                    Speed = CurrentSpeed(),
                    Elapsed = elapsed,
                    Nested = areaCompletionAfterFloodTicks
                });
                areaCompletionActive = false;
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void BeginAreaAfterFlood(out long __state) {
            __state = 0;
            if (IsFailOpen) { return; }
            try {
                areaAfterFloodCalls++;
                insideAreaAfterFlood = true;
                __state = Stopwatch.GetTimestamp();
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndAreaAfterFlood(long __state) {
            insideAreaAfterFlood = false;
            if (__state <= 0 || IsFailOpen) { return; }
            try {
                long elapsed = Stopwatch.GetTimestamp() - __state;
                AreaAfterFlood.Record(CurrentSpeed(), elapsed);
                if (areaCompletionActive) {
                    areaCompletionAfterFloodTicks += elapsed;
                }
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void RecordRegionDirty(bool ___needsRecalc) {
            if (IsFailOpen) { return; }
            try {
                regionDirtySignals++;
                if (___needsRecalc) {
                    regionDirtyCoalesced++;
                } else {
                    regionDirtyNewWindows++;
                }
                if (insideAreaAfterFlood) {
                    regionDirtyFromArea++;
                } else if (insideRegionDenyUpdate) {
                    regionDirtyFromPassability++;
                } else {
                    regionDirtyOther++;
                }
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void BeginRegionDenyUpdate(
                RegSys __instance, int posIdx, out bool __state) {
            __state = insideRegionDenyUpdate;
            if (IsFailOpen) { return; }
            try {
                insideRegionDenyUpdate = true;
                regionDenyUpdates++;
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndRegionDenyUpdate(bool __state) {
            insideRegionDenyUpdate = __state;
        }

        private static void BeginRegionRecalculate(out long __state) {
            __state = 0;
            if (IsFailOpen) { return; }
            try {
                long tick = G.Ticks;
                int frame = GameTimer.frameCount;
                regionRebuilds++;
                if (previousRegionTick > 0) {
                    long tickGap = Math.Max(0, tick - previousRegionTick);
                    int frameGap = Math.Max(0, frame - previousRegionFrame);
                    regionIntervalSamples++;
                    regionTickGapTotal += tickGap;
                    regionFrameGapTotal += frameGap;
                    if (regionTickGapMin == 0 || tickGap < regionTickGapMin) {
                        regionTickGapMin = tickGap;
                    }
                    if (tickGap > regionTickGapMax) {
                        regionTickGapMax = tickGap;
                    }
                    if (regionFrameGapMin == 0 || frameGap < regionFrameGapMin) {
                        regionFrameGapMin = frameGap;
                    }
                    if (frameGap > regionFrameGapMax) {
                        regionFrameGapMax = frameGap;
                    }
                }
                previousRegionTick = tick;
                previousRegionFrame = frame;
                __state = Stopwatch.GetTimestamp();
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndRegionRecalculate(long __state) {
            if (__state <= 0 || IsFailOpen) { return; }
            try {
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - __state;
                RegionRecalculate.Record(CurrentSpeed(), elapsed);
                Retain(WorstRegionRebuilds, new TopologyEvent {
                    Offset = now - sessionStartedAt,
                    Tick = G.Ticks,
                    Frame = GameTimer.frameCount,
                    Speed = CurrentSpeed(),
                    Elapsed = elapsed
                });
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static int PendingChangeCount(AreaSys area) {
            if (PendingChangesField?.GetValue(area) is ICollection collection) {
                return collection.Count;
            }
            return 0;
        }

        private static void RecordPhase(PhaseStats phase, long startedAt) {
            if (startedAt <= 0 || IsFailOpen) { return; }
            try {
                phase.Record(CurrentSpeed(),
                    Stopwatch.GetTimestamp() - startedAt);
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void Retain(
                List<TopologyEvent> records,
                TopologyEvent record) {
            int insertAt = 0;
            while (insertAt < records.Count
                    && records[insertAt].Elapsed >= record.Elapsed) {
                insertAt++;
            }
            if (insertAt >= MaxRetainedEvents) { return; }
            records.Insert(insertAt, record);
            if (records.Count > MaxRetainedEvents) {
                records.RemoveAt(records.Count - 1);
            }
        }

        internal static void LogSummary() {
            try {
                double pendingAverage = areaPendingBatches > 0
                    ? areaPendingChanges / (double) areaPendingBatches
                    : 0d;
                long areaFullOther = Math.Max(0,
                    areaFullRecalculations - areaFullFromPending);
                D.Warn($"Optimization Mod Area/Region diagnostic summary: "
                    + $"pending batches={areaPendingBatches} "
                    + $"({pendingAverage:0.0} changes avg/"
                    + $"{maxAreaPendingChanges} max), incremental finishes="
                    + $"{areaIncrementalFinishes}, full Area recalculations="
                    + $"{areaFullRecalculations} ({areaFullFromPending} from "
                    + $"pending processing/{areaFullOther} deferred, direct, "
                    + $"or load; {areaFullScheduled} scheduled/"
                    + $"{areaFullImmediate} immediate), active Area "
                    + $"completion frames={areaCompletionFrames}, "
                    + $"AfterFlood calls={areaAfterFloodCalls}, Region dirty "
                    + $"signals={regionDirtySignals} ({regionDirtyNewWindows} "
                    + $"new/{regionDirtyCoalesced} already dirty; sources "
                    + $"Area={regionDirtyFromArea}, passability="
                    + $"{regionDirtyFromPassability}, other="
                    + $"{regionDirtyOther}), Region rebuilds={regionRebuilds}, "
                    + $"fail-open={IsFailOpen}.");

                long nonDirtyingDenyUpdates = Math.Max(0,
                    regionDenyUpdates - regionDirtyFromPassability);
                D.Warn($"Optimization Mod Area/Region passability updates: "
                    + $"total={regionDenyUpdates}, dirtied Regions="
                    + $"{regionDirtyFromPassability}, door/no-op/invalid="
                    + $"{nonDirtyingDenyUpdates}.");

                if (regionIntervalSamples > 0) {
                    double averageTickGap = regionTickGapTotal
                        / (double) regionIntervalSamples;
                    double averageFrameGap = regionFrameGapTotal
                        / (double) regionIntervalSamples;
                    D.Warn($"Optimization Mod Region rebuild intervals: "
                        + $"{regionIntervalSamples} samples, ticks avg/min/max="
                        + $"{averageTickGap:0.0}/"
                        + $"{regionTickGapMin}/{regionTickGapMax}, frames "
                        + $"avg/min/max={averageFrameGap:0.0}/"
                        + $"{regionFrameGapMin}/{regionFrameGapMax}.");
                }

                AreaPending.Log();
                AreaIncrementalFinish.Log();
                AreaFullRecalculate.Log();
                AreaCompletionFrame.Log();
                AreaAfterFlood.Log();
                RegionRecalculate.Log();

                for (int i = 0; i < WorstAreaCompletions.Count; i++) {
                    TopologyEvent record = WorstAreaCompletions[i];
                    long outsidePost = Math.Max(
                        0, record.Elapsed - record.Nested);
                    D.Warn($"Optimization Mod Area completion #{i + 1}: "
                        + $"+{ToSeconds(record.Offset):0.000}s, tick="
                        + $"{record.Tick}, frame={record.Frame}, speed="
                        + $"{(ClockSpeed) record.Speed}, total="
                        + $"{ToMilliseconds(record.Elapsed):0.###} ms, "
                        + $"AfterFlood={ToMilliseconds(record.Nested):0.###} "
                        + $"ms, job wait/other="
                        + $"{ToMilliseconds(outsidePost):0.###} ms.");
                }
                for (int i = 0; i < WorstRegionRebuilds.Count; i++) {
                    TopologyEvent record = WorstRegionRebuilds[i];
                    D.Warn($"Optimization Mod Region rebuild #{i + 1}: "
                        + $"+{ToSeconds(record.Offset):0.000}s, tick="
                        + $"{record.Tick}, frame={record.Frame}, speed="
                        + $"{(ClockSpeed) record.Speed}, "
                        + $"{ToMilliseconds(record.Elapsed):0.###} ms.");
                }
            } catch (Exception ex) {
                TryLogError("Optimization Mod Area/Region diagnostic summary "
                    + "failed: " + ex);
            }
        }

        internal static void Reset() {
            AreaPending.Reset();
            AreaIncrementalFinish.Reset();
            AreaFullRecalculate.Reset();
            AreaCompletionFrame.Reset();
            AreaAfterFlood.Reset();
            RegionRecalculate.Reset();
            WorstAreaCompletions.Clear();
            WorstRegionRebuilds.Clear();
            sessionStartedAt = 0;
            areaPendingBatches = 0;
            areaPendingChanges = 0;
            maxAreaPendingChanges = 0;
            areaIncrementalFinishes = 0;
            areaFullRecalculations = 0;
            areaFullFromPending = 0;
            areaFullImmediate = 0;
            areaFullScheduled = 0;
            areaCompletionFrames = 0;
            areaAfterFloodCalls = 0;
            regionDirtySignals = 0;
            regionDirtyNewWindows = 0;
            regionDirtyCoalesced = 0;
            regionDirtyFromArea = 0;
            regionDirtyFromPassability = 0;
            regionDirtyOther = 0;
            regionDenyUpdates = 0;
            regionRebuilds = 0;
            previousRegionTick = 0;
            previousRegionFrame = 0;
            regionTickGapTotal = 0;
            regionTickGapMin = 0;
            regionTickGapMax = 0;
            regionFrameGapTotal = 0;
            regionFrameGapMin = 0;
            regionFrameGapMax = 0;
            regionIntervalSamples = 0;
            insideAreaPending = false;
            insideAreaAfterFlood = false;
            insideRegionDenyUpdate = false;
            areaCompletionActive = false;
            areaCompletionAfterFloodTicks = 0;
            failureLogged = false;
            IsFailOpen = false;
        }

        private static int CurrentSpeed() {
            int speed = (int) G.ClockSpeed;
            return speed >= 0 && speed < SpeedCount ? speed : 0;
        }

        private static void FailOpen(Exception ex) {
            IsFailOpen = true;
            insideAreaPending = false;
            insideAreaAfterFlood = false;
            insideRegionDenyUpdate = false;
            areaCompletionActive = false;
            if (failureLogged) { return; }
            failureLogged = true;
            TryLogError("Optimization Mod Area/Region diagnostics failed "
                + "open; Core topology rebuild behavior remains unchanged. "
                + ex);
        }

        private static void TryLogError(string message) {
            try {
                D.Err(message);
            } catch {
                // Unity logging is unavailable in headless static verifiers.
            }
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static double ToSeconds(long ticks) {
            return ticks / (double) Stopwatch.Frequency;
        }

        private sealed class PhaseStats {
            private readonly string name;
            private readonly TimingStats[] bySpeed = {
                new TimingStats(), new TimingStats(), new TimingStats(),
                new TimingStats(), new TimingStats()
            };

            internal PhaseStats(string name) {
                this.name = name;
            }

            internal void Record(int speed, long elapsed) {
                TimingStats stats = bySpeed[speed];
                stats.Calls++;
                stats.Total += elapsed;
                if (elapsed > stats.Max) { stats.Max = elapsed; }
                if (elapsed >= FourMsTicks) { stats.OverFourMs++; }
                if (elapsed >= EightMsTicks) { stats.OverEightMs++; }
                if (elapsed >= SixteenMsTicks) { stats.OverSixteenMs++; }
                if (elapsed >= ThirtyThreeMsTicks) {
                    stats.OverThirtyThreeMs++;
                }
            }

            internal void Log() {
                long calls = 0;
                long total = 0;
                long max = 0;
                long overFour = 0;
                long overEight = 0;
                long overSixteen = 0;
                long overThirtyThree = 0;
                var parts = new List<string>();
                for (int speed = 0; speed < SpeedCount; speed++) {
                    TimingStats stats = bySpeed[speed];
                    if (stats.Calls == 0) { continue; }
                    calls += stats.Calls;
                    total += stats.Total;
                    if (stats.Max > max) { max = stats.Max; }
                    overFour += stats.OverFourMs;
                    overEight += stats.OverEightMs;
                    overSixteen += stats.OverSixteenMs;
                    overThirtyThree += stats.OverThirtyThreeMs;
                    double speedMs = ToMilliseconds(stats.Total);
                    parts.Add($"{(ClockSpeed) speed}: {stats.Calls} calls, "
                        + $"{speedMs / stats.Calls:0.###} ms avg, "
                        + $"{ToMilliseconds(stats.Max):0.###} ms max");
                }
                if (calls == 0) { return; }
                double totalMs = ToMilliseconds(total);
                D.Warn($"Optimization Mod Area/Region phase {name}: "
                    + $"{calls} calls, {totalMs:0.###} ms total, "
                    + $"{totalMs / calls:0.###} ms avg, "
                    + $"{ToMilliseconds(max):0.###} ms max, >=4 ms "
                    + $"{overFour}, >=8 ms {overEight}, >=16 ms "
                    + $"{overSixteen}, >=33 ms {overThirtyThree}; "
                    + string.Join(" | ", parts));
            }

            internal void Reset() {
                for (int i = 0; i < SpeedCount; i++) {
                    bySpeed[i].Reset();
                }
            }
        }

        private sealed class TimingStats {
            internal long Calls;
            internal long Total;
            internal long Max;
            internal long OverFourMs;
            internal long OverEightMs;
            internal long OverSixteenMs;
            internal long OverThirtyThreeMs;

            internal void Reset() {
                Calls = 0;
                Total = 0;
                Max = 0;
                OverFourMs = 0;
                OverEightMs = 0;
                OverSixteenMs = 0;
                OverThirtyThreeMs = 0;
            }
        }

        private sealed class TopologyEvent {
            internal long Offset;
            internal long Tick;
            internal int Frame;
            internal int Speed;
            internal long Elapsed;
            internal long Nested;
        }
    }
}
