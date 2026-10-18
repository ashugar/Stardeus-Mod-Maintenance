using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Areas;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Optional aggregate observations of Core's indexed colony-goal auction.
    /// No scheduling inputs, outputs, goal fields, or exceptions are changed.
    /// This group has its own Harmony owner and installs all-or-nothing.
    /// </summary>
    internal static class SchedulerPerformanceDiagnostics {
        internal const int ExpectedHookCount = 7;
        private const int PriorityCount = 6;
        private const long SnapshotIntervalTicks = 120;
        private static readonly long[] Indexed = new long[PriorityCount];
        private static readonly long[] BidAttempts = new long[PriorityCount];
        private static readonly long[] BidPreparations = new long[PriorityCount];
        private static readonly long[] Winners = new long[PriorityCount];
        private static readonly long[] Assigned = new long[PriorityCount];
        private static readonly long[] Completed = new long[PriorityCount];
        private static readonly long[] Failed = new long[PriorityCount];
        private static readonly long[] Cancelled = new long[PriorityCount];
        private static readonly long[] Interrupted = new long[PriorityCount];
        private static readonly long[] Errors = new long[PriorityCount];
        private static readonly long[] BoostedAdmissions = new long[PriorityCount];
        private static readonly long[] SnapshotGoalTotals = new long[PriorityCount];
        private static readonly long[] SnapshotBiddingTotals = new long[PriorityCount];
        private static readonly long[] OldestBiddingAgeTicks = new long[PriorityCount];
        private static readonly int[] LatestGoals = new int[PriorityCount];
        private static readonly int[] LatestBidding = new int[PriorityCount];
        private static readonly int[] LatestInProgress = new int[PriorityCount];
        private static readonly int[] LatestFailed = new int[PriorityCount];
        private static readonly int[] LatestOther = new int[PriorityCount];
        private static readonly int[] MaxBidding = new int[PriorityCount];
        private static Harmony groupOwner;
        private static AIAuctionIndex currentIndex;
        private static AIAuction currentAuction;
        private static bool currentTickActive;
        private static long aiTicks, abortedTicks, snapshots, lastSnapshotTick;
        private static long auctionRuns, abortedAuctions, indexedAtRunTotal;
        private static long inspectionsTotal, noWinnerRuns, agentsWithoutColonyWinnerTotal;
        private static int maxIndexedAtRun, maxInspections, maxAuctionAgents;
        private static int maxAgentsWithoutColonyWinner;
        private static bool runtimeFailureLogged;
        internal static bool IsInstalled { get; private set; }
        internal static bool IsFailOpen { get; private set; }

        internal sealed class HookSpec {
            internal Type Owner, Result, Patch;
            internal string Name;
            internal Type[] Parameters;
            internal bool Prefix, Postfix, Finalizer;
        }

        // Full signatures document the reviewed rc332 auction boundaries.
        internal static HookSpec[] RequiredHooks() {
            return new[] {
                Spec(typeof(AISys), "OnTick", new[] { typeof(long) },
                    typeof(void), typeof(SchedulerAiTickPatch), true, true, true),
                Spec(typeof(AIAuctionIndex), "Add",
                    new[] { typeof(AIGoal), typeof(int), typeof(long) },
                    typeof(void), typeof(SchedulerIndexAdmissionPatch), true, true),
                Spec(typeof(AIAuction), "Run",
                    new[] { typeof(GameState), typeof(long), typeof(AIAuctionIndex) },
                    typeof(void), typeof(SchedulerAuctionPatch), true, true, true),
                Spec(typeof(AIAuction), "CollectCandidates",
                    new[] { typeof(GameState), typeof(long), typeof(AIAuctionIndex),
                        typeof(bool), typeof(int).MakeByRefType() },
                    typeof(void), typeof(SchedulerCandidateCollectionPatch), false, true),
                Spec(typeof(AIAuction), "TryBid",
                    new[] { typeof(AIGoal), typeof(AIAgentComp), typeof(AreaSys),
                        typeof(bool), typeof(long), typeof(AIPlan).MakeByRefType(),
                        typeof(int).MakeByRefType() },
                    typeof(bool), typeof(SchedulerBidAttemptPatch), true, true),
                Spec(typeof(AIAgentComp), nameof(AIAgentComp.SetGoal),
                    new[] { typeof(AIGoal), typeof(AIPlan), typeof(long), typeof(bool) },
                    typeof(bool), typeof(SchedulerAssignmentPatch), false, true),
                Spec(typeof(AIGoal), nameof(AIGoal.ChangeStateTo),
                    new[] { typeof(AIGoalState), typeof(long), typeof(bool), typeof(string) },
                    typeof(void), typeof(SchedulerGoalStatePatch), true, true)
            };
        }

        private static HookSpec Spec(Type owner, string name, Type[] parameters,
                Type result, Type patch, bool prefix, bool postfix,
                bool finalizer = false) {
            return new HookSpec { Owner = owner, Name = name, Parameters = parameters,
                Result = result, Patch = patch, Prefix = prefix, Postfix = postfix,
                Finalizer = finalizer };
        }

        internal static int Install(Harmony harmony) {
            return InstallGroup(harmony, RequiredHooks());
        }

        private static int InstallGroup(Harmony harmony, HookSpec[] specs) {
            Shutdown();
            try {
                if (harmony == null) { throw new ArgumentNullException(nameof(harmony)); }
                if (specs == null || specs.Length != ExpectedHookCount) {
                    throw new ArgumentException("Incomplete scheduler diagnostic hook group.");
                }
                var targets = new MethodInfo[specs.Length];
                // Resolve every target and callback before installing the first hook.
                for (int i = 0; i < specs.Length; i++) {
                    HookSpec spec = specs[i];
                    targets[i] = AccessTools.DeclaredMethod(spec.Owner, spec.Name, spec.Parameters);
                    if (targets[i] == null || targets[i].ReturnType != spec.Result
                            || targets[i].IsStatic
                            || (spec.Prefix && Hook(spec.Patch, "Prefix") == null)
                            || (spec.Postfix && Hook(spec.Patch, "Postfix") == null)
                            || (spec.Finalizer && Hook(spec.Patch, "Finalizer") == null)) {
                        throw new MissingMethodException(spec.Owner.FullName, spec.Name);
                    }
                }
                groupOwner = new Harmony(harmony.Id + ".schedulerdiagnostics");
                for (int i = 0; i < specs.Length; i++) {
                    HookSpec spec = specs[i];
                    groupOwner.Patch(targets[i],
                        prefix: spec.Prefix ? Hook(spec.Patch, "Prefix") : null,
                        postfix: spec.Postfix ? Hook(spec.Patch, "Postfix") : null,
                        finalizer: spec.Finalizer ? Hook(spec.Patch, "Finalizer") : null);
                }
                IsFailOpen = false;
                IsInstalled = true;
                return specs.Length;
            } catch (Exception ex) {
                Shutdown();
                IsFailOpen = true;
                TryLogError("Optimization Mod scheduler diagnostics were not installed; "
                    + "the complete diagnostic group was rolled back. " + ex);
                return 0;
            }
        }

        private static HarmonyMethod Hook(Type patch, string name) {
            MethodInfo method = AccessTools.DeclaredMethod(patch, name, null);
            return method == null ? null : new HarmonyMethod(method) { priority = Priority.Low };
        }

        private static void Shutdown() {
            IsInstalled = false;
            CloseTick();
            if (groupOwner == null) { return; }
            try {
                groupOwner.UnpatchAll(groupOwner.Id);
                groupOwner = null;
            } catch (Exception ex) {
                // Callbacks remain inert through IsInstalled if detach fails.
                // Keep the owner so a later cleanup can retry.
                TryLogError("Optimization Mod scheduler diagnostic cleanup failed: " + ex);
            }
        }

        internal static void BeginTick(AIAuctionIndex index) {
            if (!IsInstalled || IsFailOpen) { return; }
            currentIndex = index;
            currentAuction = null;
            currentTickActive = true;
        }

        internal static void EndTick(AISys ai, long ticks) {
            if (!IsInstalled || IsFailOpen || !currentTickActive) { return; }
            try {
                aiTicks++;
                if (snapshots == 0 || ticks - lastSnapshotTick >= SnapshotIntervalTicks) {
                    TakeSnapshot(ai, ticks);
                    lastSnapshotTick = ticks;
                }
            } catch (Exception ex) { FailOpen(ex); }
            finally { CloseTick(); }
        }

        internal static void AbortTick() {
            if (IsInstalled && currentTickActive) { abortedTicks++; }
            CloseTick();
        }

        private static void CloseTick() {
            currentTickActive = false;
            currentAuction = null;
            currentIndex = null;
        }

        internal static int AdmissionStart(AIAuctionIndex index) {
            return IsInstalled && !IsFailOpen && currentTickActive && index != null
                && ReferenceEquals(index, currentIndex) ? index.Count : -1;
        }

        internal static void RecordAdmission(AIAuctionIndex index, AIGoal goal,
                long ticks, int before) {
            if (before < 0 || AdmissionStart(index) < 0
                    || goal?.Type != AIGoalType.Colony) { return; }
            try {
                // Add refuses invalid job indexes: count only actual insertions.
                if (index.Count != before + 1) { return; }
                int priority = PriorityOf(goal);
                Indexed[priority]++;
                if (goal.BoostedUntil > ticks) { BoostedAdmissions[priority]++; }
            } catch (Exception ex) { FailOpen(ex); }
        }

        internal static void BeginAuction(AIAuction auction, AIAuctionIndex index) {
            if (!IsInstalled || IsFailOpen || !currentTickActive || auction == null
                    || index == null || !ReferenceEquals(index, currentIndex)) { return; }
            try {
                currentAuction = auction;
                indexedAtRunTotal += index.Count;
                maxIndexedAtRun = Math.Max(maxIndexedAtRun, index.Count);
                maxAuctionAgents = Math.Max(maxAuctionAgents, auction.Agents.Count);
            } catch (Exception ex) { FailOpen(ex); }
        }

        private static bool Observing(AIAuction auction) {
            return IsInstalled && !IsFailOpen && currentTickActive
                && currentAuction != null && ReferenceEquals(auction, currentAuction);
        }

        internal static void RecordInspections(AIAuction auction, int inspections) {
            if (!Observing(auction)) { return; }
            inspectionsTotal += inspections;
            maxInspections = Math.Max(maxInspections, inspections);
        }

        internal static void RecordBid(AIAuction auction, AIGoal goal,
                bool preparation, bool succeeded) {
            if (!Observing(auction) || goal?.Type != AIGoalType.Colony) { return; }
            try {
                int priority = PriorityOf(goal);
                if (!preparation) { BidAttempts[priority]++; }
                else if (succeeded) { BidPreparations[priority]++; }
            } catch (Exception ex) { FailOpen(ex); }
        }

        internal static void EndAuction(AIAuction auction) {
            if (!Observing(auction)) { return; }
            try {
                auctionRuns++;
                int winnerCount = auction.GoalsWithBids.Count;
                foreach (AIGoal goal in auction.GoalsWithBids) {
                    if (goal?.Type == AIGoalType.Colony) { Winners[PriorityOf(goal)]++; }
                }
                if (winnerCount == 0) { noWinnerRuns++; }
                int remaining = auction.Agents.Count;
                agentsWithoutColonyWinnerTotal += remaining;
                maxAgentsWithoutColonyWinner = Math.Max(maxAgentsWithoutColonyWinner, remaining);
            } catch (Exception ex) { FailOpen(ex); }
            finally { currentAuction = null; }
        }

        internal static void AbortAuction(AIAuction auction) {
            if (!Observing(auction)) { return; }
            abortedAuctions++;
            currentAuction = null;
        }

        internal static void RecordAssignment(AIGoal goal, bool assigned) {
            if (!IsInstalled || IsFailOpen || !assigned
                    || goal?.Type != AIGoalType.Colony) { return; }
            try { Assigned[PriorityOf(goal)]++; }
            catch (Exception ex) { FailOpen(ex); }
        }

        internal static void RecordStateChange(AIGoal goal,
                AIGoalState oldState, AIGoalState newState) {
            if (!IsInstalled || IsFailOpen || goal?.Type != AIGoalType.Colony
                    || oldState == newState) { return; }
            try {
                int priority = PriorityOf(goal);
                switch (newState) {
                    case AIGoalState.Completed: Completed[priority]++; break;
                    case AIGoalState.Failed: Failed[priority]++; break;
                    case AIGoalState.Cancelled: Cancelled[priority]++; break;
                    case AIGoalState.Interrupted: Interrupted[priority]++; break;
                    case AIGoalState.Error:
                    case AIGoalState.ErrorLooping: Errors[priority]++; break;
                }
            } catch (Exception ex) { FailOpen(ex); }
        }

        private static void TakeSnapshot(AISys ai, long ticks) {
            foreach (int[] values in new[] { LatestGoals, LatestBidding,
                    LatestInProgress, LatestFailed, LatestOther }) {
                Array.Clear(values, 0, values.Length);
            }
            foreach (KeyValuePair<uint, AIGoal> entry in ai.ColonyGoals) {
                AIGoal goal = entry.Value;
                if (goal == null) { continue; }
                int priority = PriorityOf(goal);
                LatestGoals[priority]++;
                switch (goal.State) {
                    case AIGoalState.Bidding:
                        LatestBidding[priority]++;
                        OldestBiddingAgeTicks[priority] = Math.Max(
                            OldestBiddingAgeTicks[priority], Math.Max(0, ticks - goal.StateStartTick));
                        break;
                    case AIGoalState.InProgress: LatestInProgress[priority]++; break;
                    case AIGoalState.Failed: LatestFailed[priority]++; break;
                    default: LatestOther[priority]++; break;
                }
            }
            snapshots++;
            for (int p = 0; p < PriorityCount; p++) {
                SnapshotGoalTotals[p] += LatestGoals[p];
                SnapshotBiddingTotals[p] += LatestBidding[p];
                MaxBidding[p] = Math.Max(MaxBidding[p], LatestBidding[p]);
            }
        }

        internal static void LogSummary() {
            try {
                D.Warn($"Optimization Mod indexed scheduler diagnostic summary: "
                    + $"AI ticks={aiTicks}, aborted ticks={abortedTicks}, snapshots={snapshots}, "
                    + $"auction runs={auctionRuns}, aborted auctions={abortedAuctions}, "
                    + $"indexed goals at run total/max={indexedAtRunTotal}/{maxIndexedAtRun}, "
                    + $"candidate inspections total/max={inspectionsTotal}/{maxInspections}, "
                    + $"no-winner runs={noWinnerRuns}, max starting auction agents={maxAuctionAgents}, "
                    + $"agents without colony winner total/max={agentsWithoutColonyWinnerTotal}/"
                    + $"{maxAgentsWithoutColonyWinner}, fail-open={IsFailOpen}. "
                    + "Inspections may revisit a goal for different workers. TryBid successes "
                    + "are prepared plans before region-path measurement and final resolution. "
                    + "Agents without a colony winner may receive personal work; they are not an idle count.");
                for (int p = PriorityCount - 1; p >= 0; p--) {
                    double avgGoals = snapshots > 0 ? SnapshotGoalTotals[p] / (double)snapshots : 0d;
                    double avgBidding = snapshots > 0 ? SnapshotBiddingTotals[p] / (double)snapshots : 0d;
                    D.Warn($"Optimization Mod scheduler priority p{p}: "
                        + $"latest goals={LatestGoals[p]}, bidding={LatestBidding[p]}, "
                        + $"in-progress={LatestInProgress[p]}, failed={LatestFailed[p]}, other={LatestOther[p]}; "
                        + $"snapshots goals avg={avgGoals:0.0}, bidding avg/max={avgBidding:0.0}/{MaxBidding[p]}, "
                        + $"oldest bidding age={OldestBiddingAgeTicks[p]} ticks; "
                        + $"indexed admissions={Indexed[p]}, bid attempts={BidAttempts[p]}, "
                        + $"TryBid successes={BidPreparations[p]}, winners={Winners[p]}, assigned={Assigned[p]}, "
                        + $"completed={Completed[p]}, failed transitions={Failed[p]}, cancelled={Cancelled[p]}, "
                        + $"interrupted={Interrupted[p]}, errors={Errors[p]}, boosted admissions={BoostedAdmissions[p]}.");
                }
            } catch (Exception ex) {
                TryLogError("Optimization Mod scheduler diagnostic summary failed: " + ex);
            }
        }

        internal static void Reset() {
            Shutdown();
            foreach (long[] values in new[] { Indexed, BidAttempts, BidPreparations,
                    Winners, Assigned, Completed, Failed, Cancelled, Interrupted,
                    Errors, BoostedAdmissions, SnapshotGoalTotals, SnapshotBiddingTotals,
                    OldestBiddingAgeTicks }) { Array.Clear(values, 0, values.Length); }
            foreach (int[] values in new[] { LatestGoals, LatestBidding,
                    LatestInProgress, LatestFailed, LatestOther, MaxBidding }) {
                Array.Clear(values, 0, values.Length);
            }
            aiTicks = abortedTicks = snapshots = lastSnapshotTick = 0;
            auctionRuns = abortedAuctions = indexedAtRunTotal = inspectionsTotal = 0;
            noWinnerRuns = agentsWithoutColonyWinnerTotal = 0;
            maxIndexedAtRun = maxInspections = maxAuctionAgents = maxAgentsWithoutColonyWinner = 0;
            runtimeFailureLogged = false;
            IsFailOpen = false;
        }

        private static int PriorityOf(AIGoal goal) {
            return Math.Max(0, Math.Min(PriorityCount - 1, (int)goal.GoalPriority));
        }

        private static void FailOpen(Exception ex) {
            IsFailOpen = true;
            CloseTick();
            if (runtimeFailureLogged) { return; }
            runtimeFailureLogged = true;
            TryLogError("Optimization Mod scheduler diagnostics failed open; "
                + "Core scheduling remains unchanged. " + ex);
        }

        private static void TryLogError(string message) {
            try { D.Err(message); }
            catch { /* Unity logging is unavailable in isolated static verifiers. */ }
        }
    }

    internal static class SchedulerAiTickPatch {
        internal static void Prefix(AIAuctionIndex ___auctionIndex) {
            SchedulerPerformanceDiagnostics.BeginTick(___auctionIndex);
        }
        internal static void Postfix(AISys __instance, long __0) {
            SchedulerPerformanceDiagnostics.EndTick(__instance, __0);
        }
        internal static void Finalizer(Exception __exception) {
            if (__exception != null) { SchedulerPerformanceDiagnostics.AbortTick(); }
        }
    }

    internal static class SchedulerIndexAdmissionPatch {
        internal static void Prefix(AIAuctionIndex __instance, out int __state) {
            __state = SchedulerPerformanceDiagnostics.AdmissionStart(__instance);
        }
        internal static void Postfix(AIAuctionIndex __instance, AIGoal __0, long __2, int __state) {
            SchedulerPerformanceDiagnostics.RecordAdmission(__instance, __0, __2, __state);
        }
    }

    internal static class SchedulerAuctionPatch {
        internal static void Prefix(AIAuction __instance, AIAuctionIndex __2) {
            SchedulerPerformanceDiagnostics.BeginAuction(__instance, __2);
        }
        internal static void Postfix(AIAuction __instance) {
            SchedulerPerformanceDiagnostics.EndAuction(__instance);
        }
        internal static void Finalizer(AIAuction __instance, Exception __exception) {
            if (__exception != null) { SchedulerPerformanceDiagnostics.AbortAuction(__instance); }
        }
    }

    internal static class SchedulerCandidateCollectionPatch {
        internal static void Postfix(AIAuction __instance, int __4) {
            SchedulerPerformanceDiagnostics.RecordInspections(__instance, __4);
        }
    }

    internal static class SchedulerBidAttemptPatch {
        internal static void Prefix(AIAuction __instance, AIGoal __0) {
            SchedulerPerformanceDiagnostics.RecordBid(__instance, __0, false, false);
        }
        internal static void Postfix(AIAuction __instance, AIGoal __0, bool __result) {
            SchedulerPerformanceDiagnostics.RecordBid(__instance, __0, true, __result);
        }
    }

    internal static class SchedulerAssignmentPatch {
        internal static void Postfix(AIGoal __0, bool __result) {
            SchedulerPerformanceDiagnostics.RecordAssignment(__0, __result);
        }
    }

    internal static class SchedulerGoalStatePatch {
        internal static void Prefix(AIGoal __instance, out AIGoalState __state) {
            __state = __instance?.State ?? AIGoalState.Uninitialized;
        }
        internal static void Postfix(AIGoal __instance, AIGoalState __0, AIGoalState __state) {
            SchedulerPerformanceDiagnostics.RecordStateChange(__instance, __state, __0);
        }
    }
}
