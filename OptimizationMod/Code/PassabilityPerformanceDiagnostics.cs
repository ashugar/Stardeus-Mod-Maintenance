using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Areas;
using Game.Systems.Path;
using Game.Systems.Reg;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using Unity.Collections;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Temporary aggregate-only diagnostics for Core's passability refresh
    /// pipeline. These hooks observe signal sources, queue coalescing, actual
    /// PathNode deltas, and batch cost. They never suppress a signal, alter a
    /// PathNode, defer a path job, or change Region invalidation.
    /// </summary>
    internal static class PassabilityPerformanceDiagnostics {
        internal const int ExpectedHookCount = 11;
        private const int MaxRetainedBatches = 8;
        private const int RootOutcomeCount = 4;

        private enum SignalSource {
            DirectOrMod = 0,
            ConstructionFinish = 1,
            FloorVent = 2,
            UpdateSection = 3,
            EntityTriggerChange = 4,
            PermissionDeny = 5,
            InstantRelocation = 6,
            ReinstallOverride = 7
        }

        private enum RootOutcomeKind {
            Unchanged = 0,
            TraversalOnly = 1,
            ConnectivityOrArea = 2,
            Unknown = 3
        }

        private struct NodeSnapshot {
            internal bool Tracked;
            internal bool IsRoot;
            internal PathNodeType Type;
            internal NavPermission Deny;
            internal float Difficulty;
            internal int TeleportsTo;
        }

        private struct RootOutcome {
            internal bool Available;
            internal int PosIdx;
            internal SignalSource Source;
            internal RootOutcomeKind Kind;
            internal int TouchedPositions;
            internal int Unchanged;
            internal int ConnectivityChanged;
            internal int TraversalOnlyChanged;
            internal bool AreaMismatch;
            internal bool TouchesDoor;
            internal bool TeleporterChanged;
        }

        private struct RegionEligibilityState {
            internal bool Tracked;
            internal bool WasDirty;
            internal RootOutcome Outcome;
        }

        private sealed class BatchRecord {
            internal long Offset;
            internal long Tick;
            internal int Frame;
            internal int Speed;
            internal int QueueCount;
            internal int RootUpdates;
            internal int RecursiveUpdates;
            internal int Unchanged;
            internal int ConnectivityChanged;
            internal int TraversalOnlyChanged;
            internal bool ForcedPathCompletion;
            internal long Elapsed;
        }

        private static readonly string[] SourceNames = {
            "direct/mod/unattributed",
            "construction finish",
            "floor vent",
            "GameState.UpdateSection",
            "EntityUtils.TriggerChange",
            "permission deny",
            "instant relocation",
            "reinstall override"
        };

        private static readonly string[] RootOutcomeNames = {
            "unchanged tree",
            "traversal-only tree",
            "connectivity or Area change",
            "unknown or external caller"
        };

        private static readonly long[] SourceSignals =
            new long[SourceNames.Length];
        private static readonly long[] SourceNewQueueEntries =
            new long[SourceNames.Length];
        private static readonly long[] SourceCoalescedSignals =
            new long[SourceNames.Length];
        private static readonly List<BatchRecord> WorstBatches =
            new List<BatchRecord>(MaxRetainedBatches);
        private static readonly Dictionary<int, SignalSource>
            PendingSources = new Dictionary<int, SignalSource>();
        private static readonly List<int> CurrentRootPositions =
            new List<int>(128);
        private static readonly long[] RegionCallsByOutcome =
            new long[RootOutcomeCount];
        private static readonly long[] RegionNewDirtyByOutcome =
            new long[RootOutcomeCount];
        private static readonly long[] RegionAlreadyDirtyByOutcome =
            new long[RootOutcomeCount];
        private static readonly long[] RegionLocalOrNoopByOutcome =
            new long[RootOutcomeCount];
        private static readonly long[,] RegionCallsBySourceAndOutcome =
            new long[8, RootOutcomeCount];
        private static readonly long[,] RegionNewDirtyBySourceAndOutcome =
            new long[8, RootOutcomeCount];

        private static readonly long FourMsTicks =
            (long) (Stopwatch.Frequency * 0.004);
        private static readonly long EightMsTicks =
            (long) (Stopwatch.Frequency * 0.008);
        private static readonly long SixteenMsTicks =
            (long) (Stopwatch.Frequency * 0.016);
        private static readonly long ThirtyThreeMsTicks =
            (long) (Stopwatch.Frequency * 0.033);

        private static long sessionStartedAt;
        private static long signals;
        private static long newQueueEntries;
        private static long coalescedSignals;
        private static long batches;
        private static long queuedPositions;
        private static int maxQueueCount;
        private static long forcedPathCompletionBatches;
        private static long nodeUpdates;
        private static long rootNodeUpdates;
        private static long recursiveNodeUpdates;
        private static long unchangedNodes;
        private static long connectivityChangedNodes;
        private static long traversalOnlyChangedNodes;
        private static long typeChanges;
        private static long denyChanges;
        private static long difficultyChanges;
        private static long teleporterChanges;
        private static long batchElapsedTotal;
        private static long batchElapsedMax;
        private static long batchesOverFourMs;
        private static long batchesOverEightMs;
        private static long batchesOverSixteenMs;
        private static long batchesOverThirtyThreeMs;
        private static long regionEligibilityCalls;
        private static long regionEligibilityMatchedRoots;
        private static long regionEligibilityNewDirty;
        private static long regionEligibilityAlreadyDirty;
        private static long regionEligibilityLocalOrNoop;
        private static long rootsWithAreaMismatch;
        private static long rootsTouchingDoors;
        private static long rootsWithTeleporterChange;

        private static SignalSource activeSource;
        private static bool batchActive;
        private static bool currentBatchForcedPathCompletion;
        private static int currentBatchQueueCount;
        private static int currentBatchRootUpdates;
        private static int currentBatchRecursiveUpdates;
        private static int currentBatchUnchanged;
        private static int currentBatchConnectivityChanged;
        private static int currentBatchTraversalOnlyChanged;
        private static int nodeUpdateDepth;
        private static SignalSource currentRootSource;
        private static int currentRootUnchanged;
        private static int currentRootConnectivityChanged;
        private static int currentRootTraversalOnlyChanged;
        private static bool currentRootHadUntrackedNode;
        private static bool currentRootTeleporterChanged;
        private static RootOutcome pendingRootOutcome;
        private static bool failureLogged;

        internal static bool IsFailOpen { get; private set; }

        internal static int Install(Harmony harmony) {
            Reset();
            sessionStartedAt = Stopwatch.GetTimestamp();
            int installed = 0;
            installed += Patch(harmony, typeof(PathSys),
                "OnUpdatePassability", new[] { typeof(int) },
                nameof(RecordSignal), null);
            installed += Patch(harmony, typeof(PathSys),
                "UpdatePassability", Type.EmptyTypes,
                nameof(BeginBatch), nameof(EndBatch));
            installed += Patch(harmony, typeof(PathNode),
                nameof(PathNode.Update), new[] {
                    typeof(NativeArray<PathNode>).MakeByRefType(),
                    typeof(int),
                    typeof(Dictionary<int, int>),
                    typeof(HashSet<int>)
                }, nameof(BeginNodeUpdate), nameof(EndNodeUpdate));
            installed += Patch(harmony, typeof(ConstructableComp),
                nameof(ConstructableComp.FinishConstruction),
                new[] { typeof(bool) }, nameof(BeginConstructionFinish),
                nameof(EndSource));
            installed += Patch(harmony, typeof(FloorVentComp),
                "UpdateVentGraphicAndPassability", Type.EmptyTypes,
                nameof(BeginFloorVent), nameof(EndSource));
            installed += Patch(harmony, typeof(GameState),
                nameof(GameState.UpdateSection), new[] { typeof(int) },
                nameof(BeginUpdateSection), nameof(EndSource));
            installed += Patch(harmony, typeof(EntityUtils),
                nameof(EntityUtils.TriggerChange),
                new[] { typeof(Tile), typeof(bool) },
                nameof(BeginEntityTriggerChange), nameof(EndSource));
            installed += Patch(harmony, typeof(PassabilityComp),
                nameof(PassabilityComp.UpdateDeny),
                new[] { typeof(NavPermission) },
                nameof(BeginPermissionDeny), nameof(EndSource));
            installed += Patch(harmony, typeof(TileTransformComp),
                "PlaceInstant", new[] {
                    typeof(GameState), typeof(int), typeof(Tile),
                    typeof(Action<Tile>)
                }, nameof(BeginInstantRelocation), nameof(EndSource));
            installed += Patch(harmony, typeof(ReinstallableComp),
                nameof(ReinstallableComp.SetSourceUnbuiltOverride),
                new[] { typeof(bool) }, nameof(BeginReinstallOverride),
                nameof(EndSource));
            installed += Patch(harmony, typeof(RegSys),
                nameof(RegSys.UpdateRegDenyAt), new[] { typeof(int) },
                nameof(BeginRegionEligibility),
                nameof(EndRegionEligibility));
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
                        typeof(PassabilityPerformanceDiagnostics), prefixName);
                MethodInfo postfix = string.IsNullOrEmpty(postfixName)
                    ? null
                    : AccessTools.DeclaredMethod(
                        typeof(PassabilityPerformanceDiagnostics), postfixName);
                if (original == null || prefixName != null && prefix == null
                        || postfixName != null && postfix == null) {
                    TryLogError("Optimization Mod passability diagnostic "
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
                TryLogError("Optimization Mod failed to install passability "
                    + "diagnostic for " + targetType.FullName + "."
                    + targetName + ": " + ex);
                return 0;
            }
        }

        private static void RecordSignal(
                int posIdx,
                HashSet<int> ___passabilityUpdateQueue) {
            if (IsFailOpen) { return; }
            try {
                int source = (int) activeSource;
                if (source < 0 || source >= SourceNames.Length) { source = 0; }
                signals++;
                SourceSignals[source]++;
                if (___passabilityUpdateQueue != null
                        && ___passabilityUpdateQueue.Contains(posIdx)) {
                    coalescedSignals++;
                    SourceCoalescedSignals[source]++;
                } else {
                    newQueueEntries++;
                    SourceNewQueueEntries[source]++;
                    PendingSources[posIdx] = (SignalSource) source;
                }
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void BeginBatch(
                HashSet<int> ___passabilityUpdateQueue,
                bool ___isJobRunning,
                out long __state) {
            __state = 0;
            if (IsFailOpen || ___passabilityUpdateQueue == null
                    || ___passabilityUpdateQueue.Count == 0) {
                return;
            }
            try {
                if (batchActive) {
                    throw new InvalidOperationException(
                        "Nested PathSys.UpdatePassability batch.");
                }
                batchActive = true;
                nodeUpdateDepth = 0;
                pendingRootOutcome = default(RootOutcome);
                CurrentRootPositions.Clear();
                currentBatchQueueCount = ___passabilityUpdateQueue.Count;
                currentBatchForcedPathCompletion = ___isJobRunning;
                currentBatchRootUpdates = 0;
                currentBatchRecursiveUpdates = 0;
                currentBatchUnchanged = 0;
                currentBatchConnectivityChanged = 0;
                currentBatchTraversalOnlyChanged = 0;
                batches++;
                queuedPositions += currentBatchQueueCount;
                if (currentBatchQueueCount > maxQueueCount) {
                    maxQueueCount = currentBatchQueueCount;
                }
                if (___isJobRunning) { forcedPathCompletionBatches++; }
                __state = Stopwatch.GetTimestamp();
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndBatch(long __state) {
            if (__state <= 0 || IsFailOpen) {
                batchActive = false;
                nodeUpdateDepth = 0;
                return;
            }
            try {
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - __state;
                batchElapsedTotal += elapsed;
                if (elapsed > batchElapsedMax) { batchElapsedMax = elapsed; }
                if (elapsed >= FourMsTicks) { batchesOverFourMs++; }
                if (elapsed >= EightMsTicks) { batchesOverEightMs++; }
                if (elapsed >= SixteenMsTicks) { batchesOverSixteenMs++; }
                if (elapsed >= ThirtyThreeMsTicks) {
                    batchesOverThirtyThreeMs++;
                }
                Retain(new BatchRecord {
                    Offset = now - sessionStartedAt,
                    Tick = G.Ticks,
                    Frame = GameTimer.frameCount,
                    Speed = CurrentSpeed(),
                    QueueCount = currentBatchQueueCount,
                    RootUpdates = currentBatchRootUpdates,
                    RecursiveUpdates = currentBatchRecursiveUpdates,
                    Unchanged = currentBatchUnchanged,
                    ConnectivityChanged = currentBatchConnectivityChanged,
                    TraversalOnlyChanged = currentBatchTraversalOnlyChanged,
                    ForcedPathCompletion = currentBatchForcedPathCompletion,
                    Elapsed = elapsed
                });
            } catch (Exception ex) {
                FailOpen(ex);
            } finally {
                batchActive = false;
                nodeUpdateDepth = 0;
                pendingRootOutcome = default(RootOutcome);
                PendingSources.Clear();
                CurrentRootPositions.Clear();
            }
        }

        private static void BeginNodeUpdate(
                ref NativeArray<PathNode> nodes,
                int posIdx,
                out NodeSnapshot __state) {
            __state = default(NodeSnapshot);
            if (IsFailOpen || !batchActive) { return; }
            try {
                bool isRoot = nodeUpdateDepth == 0;
                __state.IsRoot = isRoot;
                if (isRoot) {
                    CurrentRootPositions.Clear();
                    currentRootSource = PendingSources.TryGetValue(
                        posIdx, out SignalSource source)
                        ? source
                        : SignalSource.DirectOrMod;
                    currentRootUnchanged = 0;
                    currentRootConnectivityChanged = 0;
                    currentRootTraversalOnlyChanged = 0;
                    currentRootHadUntrackedNode = false;
                    currentRootTeleporterChanged = false;
                    pendingRootOutcome = default(RootOutcome);
                }
                nodeUpdateDepth++;
                nodeUpdates++;
                if (isRoot) {
                    rootNodeUpdates++;
                    currentBatchRootUpdates++;
                } else {
                    recursiveNodeUpdates++;
                    currentBatchRecursiveUpdates++;
                }
                if (!nodes.IsCreated || posIdx < 0 || posIdx >= nodes.Length) {
                    currentRootHadUntrackedNode = true;
                    return;
                }
                CurrentRootPositions.Add(posIdx);
                PathNode node = nodes[posIdx];
                __state = new NodeSnapshot {
                    Tracked = true,
                    IsRoot = isRoot,
                    Type = node.Type,
                    Deny = node.Deny,
                    Difficulty = node.Difficulty,
                    TeleportsTo = node.TeleportsTo
                };
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndNodeUpdate(
                ref NativeArray<PathNode> nodes,
                int posIdx,
                NodeSnapshot __state) {
            if (!batchActive) { return; }
            try {
                if (!__state.Tracked || !nodes.IsCreated
                        || posIdx < 0 || posIdx >= nodes.Length) {
                    currentRootHadUntrackedNode = true;
                    return;
                }
                PathNode node = nodes[posIdx];
                bool typeChanged = __state.Type != node.Type;
                bool denyChanged = __state.Deny != node.Deny;
                bool difficultyChanged =
                    !__state.Difficulty.Equals(node.Difficulty);
                bool teleporterChanged =
                    __state.TeleportsTo != node.TeleportsTo;
                if (typeChanged) { typeChanges++; }
                if (denyChanged) { denyChanges++; }
                if (difficultyChanged) { difficultyChanges++; }
                if (teleporterChanged) { teleporterChanges++; }
                if (teleporterChanged) {
                    currentRootTeleporterChanged = true;
                }

                if (!typeChanged && !denyChanged && !difficultyChanged
                        && !teleporterChanged) {
                    unchangedNodes++;
                    currentBatchUnchanged++;
                    currentRootUnchanged++;
                    return;
                }

                const PathNodeType ConnectivityMask =
                    PathNodeType.Passable | PathNodeType.Walkable
                    | PathNodeType.Door | PathNodeType.Teleporter
                    | PathNodeType.Wall;
                bool connectivityChanged = teleporterChanged
                    || ((__state.Type ^ node.Type) & ConnectivityMask) != 0;
                if (connectivityChanged) {
                    connectivityChangedNodes++;
                    currentBatchConnectivityChanged++;
                    currentRootConnectivityChanged++;
                } else {
                    traversalOnlyChangedNodes++;
                    currentBatchTraversalOnlyChanged++;
                    currentRootTraversalOnlyChanged++;
                }
            } catch (Exception ex) {
                FailOpen(ex);
            } finally {
                if (__state.IsRoot && nodeUpdateDepth > 0) {
                    CompleteRootOutcome(posIdx);
                }
                if (nodeUpdateDepth > 0) { nodeUpdateDepth--; }
            }
        }

        private static void CompleteRootOutcome(int posIdx) {
            bool areaMismatch = false;
            bool touchesDoor = false;
            bool areaKnown = true;
            try {
                AreaSys areas = A.S?.Sys?.Areas;
                RegSys regions = A.S?.Sys?.Path?.Reg;
                if (areas == null || regions == null
                        || !areas.AreaTypes.IsCreated
                        || !regions.PosRegIds.IsCreated
                        || !regions.RegTypes.IsCreated) {
                    areaKnown = false;
                } else {
                    for (int i = 0; i < CurrentRootPositions.Count; i++) {
                        int position = CurrentRootPositions[i];
                        if (position < 0
                                || position >= areas.AreaTypes.Length
                                || position >= regions.PosRegIds.Length) {
                            areaKnown = false;
                            continue;
                        }
                        AreaType currentArea = areas.AreaTypes[position];
                        int regionId = regions.PosRegIds[position];
                        if (regionId < 0
                                || regionId >= regions.RegTypes.Length) {
                            if (currentArea != AreaType.Space) {
                                areaMismatch = true;
                            }
                            continue;
                        }
                        AreaType regionArea = regions.RegTypes[regionId];
                        if (regionArea == AreaType.Door) {
                            touchesDoor = true;
                        }
                        if (currentArea != regionArea) {
                            areaMismatch = true;
                        }
                    }
                }
            } catch {
                areaKnown = false;
            }

            RootOutcomeKind kind;
            if (currentRootHadUntrackedNode || !areaKnown) {
                kind = RootOutcomeKind.Unknown;
            } else if (currentRootConnectivityChanged > 0 || areaMismatch) {
                kind = RootOutcomeKind.ConnectivityOrArea;
            } else if (currentRootTraversalOnlyChanged > 0) {
                kind = RootOutcomeKind.TraversalOnly;
            } else {
                kind = RootOutcomeKind.Unchanged;
            }
            if (areaMismatch) { rootsWithAreaMismatch++; }
            if (touchesDoor) { rootsTouchingDoors++; }
            if (currentRootTeleporterChanged) {
                rootsWithTeleporterChange++;
            }
            pendingRootOutcome = new RootOutcome {
                Available = true,
                PosIdx = posIdx,
                Source = currentRootSource,
                Kind = kind,
                TouchedPositions = CurrentRootPositions.Count,
                Unchanged = currentRootUnchanged,
                ConnectivityChanged = currentRootConnectivityChanged,
                TraversalOnlyChanged = currentRootTraversalOnlyChanged,
                AreaMismatch = areaMismatch,
                TouchesDoor = touchesDoor,
                TeleporterChanged = currentRootTeleporterChanged
            };
        }

        private static void BeginRegionEligibility(
                int posIdx,
                bool ___needsRecalc,
                out RegionEligibilityState __state) {
            __state = default(RegionEligibilityState);
            if (IsFailOpen || !batchActive) { return; }
            try {
                RootOutcome outcome = pendingRootOutcome.Available
                        && pendingRootOutcome.PosIdx == posIdx
                    ? pendingRootOutcome
                    : new RootOutcome {
                        Available = false,
                        PosIdx = posIdx,
                        Source = SignalSource.DirectOrMod,
                        Kind = RootOutcomeKind.Unknown
                    };
                pendingRootOutcome = default(RootOutcome);
                __state = new RegionEligibilityState {
                    Tracked = true,
                    WasDirty = ___needsRecalc,
                    Outcome = outcome
                };
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndRegionEligibility(
                bool ___needsRecalc,
                RegionEligibilityState __state) {
            if (IsFailOpen || !__state.Tracked) { return; }
            try {
                regionEligibilityCalls++;
                RootOutcome outcome = __state.Outcome;
                if (outcome.Available) { regionEligibilityMatchedRoots++; }
                int kind = (int) outcome.Kind;
                int source = (int) outcome.Source;
                if (kind < 0 || kind >= RootOutcomeCount) {
                    kind = (int) RootOutcomeKind.Unknown;
                }
                if (source < 0 || source >= SourceNames.Length) {
                    source = (int) SignalSource.DirectOrMod;
                }
                RegionCallsByOutcome[kind]++;
                RegionCallsBySourceAndOutcome[source, kind]++;
                if (!__state.WasDirty && ___needsRecalc) {
                    regionEligibilityNewDirty++;
                    RegionNewDirtyByOutcome[kind]++;
                    RegionNewDirtyBySourceAndOutcome[source, kind]++;
                } else if (__state.WasDirty) {
                    regionEligibilityAlreadyDirty++;
                    RegionAlreadyDirtyByOutcome[kind]++;
                } else {
                    regionEligibilityLocalOrNoop++;
                    RegionLocalOrNoopByOutcome[kind]++;
                }
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void BeginConstructionFinish(out int __state) {
            BeginSource(SignalSource.ConstructionFinish, out __state);
        }

        private static void BeginFloorVent(out int __state) {
            BeginSource(SignalSource.FloorVent, out __state);
        }

        private static void BeginUpdateSection(out int __state) {
            BeginSource(SignalSource.UpdateSection, out __state);
        }

        private static void BeginEntityTriggerChange(out int __state) {
            BeginSource(SignalSource.EntityTriggerChange, out __state);
        }

        private static void BeginPermissionDeny(out int __state) {
            BeginSource(SignalSource.PermissionDeny, out __state);
        }

        private static void BeginInstantRelocation(out int __state) {
            BeginSource(SignalSource.InstantRelocation, out __state);
        }

        private static void BeginReinstallOverride(out int __state) {
            BeginSource(SignalSource.ReinstallOverride, out __state);
        }

        private static void BeginSource(
                SignalSource source,
                out int __state) {
            __state = (int) activeSource;
            if (IsFailOpen) { return; }
            try {
                if (activeSource == SignalSource.DirectOrMod) {
                    activeSource = source;
                }
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void EndSource(int __state) {
            if (__state < 0 || __state >= SourceNames.Length) {
                activeSource = SignalSource.DirectOrMod;
            } else {
                activeSource = (SignalSource) __state;
            }
        }

        internal static void LogSummary() {
            try {
                double averageQueue = batches > 0
                    ? (double) queuedPositions / batches
                    : 0.0;
                double unchangedPercent = nodeUpdates > 0
                    ? 100.0 * unchangedNodes / nodeUpdates
                    : 0.0;
                double connectivityPercent = nodeUpdates > 0
                    ? 100.0 * connectivityChangedNodes / nodeUpdates
                    : 0.0;
                double traversalPercent = nodeUpdates > 0
                    ? 100.0 * traversalOnlyChangedNodes / nodeUpdates
                    : 0.0;
                double batchTotalMs = ToMilliseconds(batchElapsedTotal);
                double batchAverageMs = batches > 0
                    ? batchTotalMs / batches
                    : 0.0;
                D.Warn("Optimization Mod passability diagnostic summary: "
                    + $"signals={signals} ({newQueueEntries} new queue/"
                    + $"{coalescedSignals} same-position coalesced), batches="
                    + $"{batches} ({averageQueue:0.0} positions avg/"
                    + $"{maxQueueCount} max), forced path completions="
                    + $"{forcedPathCompletionBatches}, node updates="
                    + $"{nodeUpdates} ({rootNodeUpdates} root/"
                    + $"{recursiveNodeUpdates} recursive), unchanged="
                    + $"{unchangedNodes} ({unchangedPercent:0.00}%), "
                    + $"connectivity changed={connectivityChangedNodes} "
                    + $"({connectivityPercent:0.00}%), traversal-only changed="
                    + $"{traversalOnlyChangedNodes} "
                    + $"({traversalPercent:0.00}%), fail-open={IsFailOpen}.");
                D.Warn("Optimization Mod passability field changes: "
                    + $"type={typeChanges}, deny={denyChanges}, difficulty="
                    + $"{difficultyChanges}, teleporter={teleporterChanges}.");
                D.Warn("Optimization Mod passability batch timing: "
                    + $"{batches} calls, {batchTotalMs:0.###} ms total, "
                    + $"{batchAverageMs:0.###} ms avg, "
                    + $"{ToMilliseconds(batchElapsedMax):0.###} ms max, "
                    + $">=4 ms {batchesOverFourMs}, >=8 ms "
                    + $"{batchesOverEightMs}, >=16 ms "
                    + $"{batchesOverSixteenMs}, >=33 ms "
                    + $"{batchesOverThirtyThreeMs}.");
                for (int i = 0; i < SourceNames.Length; i++) {
                    if (SourceSignals[i] == 0) { continue; }
                    D.Warn("Optimization Mod passability source "
                        + $"{SourceNames[i]}: signals={SourceSignals[i]}, "
                        + $"new queue={SourceNewQueueEntries[i]}, coalesced="
                        + $"{SourceCoalescedSignals[i]}.");
                }
                D.Warn("Optimization Mod passability Region eligibility: "
                    + $"calls={regionEligibilityCalls}, matched root trees="
                    + $"{regionEligibilityMatchedRoots}, newly dirtied="
                    + $"{regionEligibilityNewDirty}, already dirty="
                    + $"{regionEligibilityAlreadyDirty}, local door/no-op="
                    + $"{regionEligibilityLocalOrNoop}, roots with Area "
                    + $"mismatch={rootsWithAreaMismatch}, roots touching "
                    + $"doors={rootsTouchingDoors}, roots with teleporter "
                    + $"change={rootsWithTeleporterChange}.");
                for (int kind = 0; kind < RootOutcomeCount; kind++) {
                    D.Warn("Optimization Mod passability Region outcome "
                        + $"{RootOutcomeNames[kind]}: calls="
                        + $"{RegionCallsByOutcome[kind]}, newly dirtied="
                        + $"{RegionNewDirtyByOutcome[kind]}, already dirty="
                        + $"{RegionAlreadyDirtyByOutcome[kind]}, local "
                        + $"door/no-op={RegionLocalOrNoopByOutcome[kind]}.");
                }
                for (int source = 0; source < SourceNames.Length; source++) {
                    long sourceCalls = 0;
                    long sourceNewDirty = 0;
                    for (int kind = 0; kind < RootOutcomeCount; kind++) {
                        sourceCalls +=
                            RegionCallsBySourceAndOutcome[source, kind];
                        sourceNewDirty +=
                            RegionNewDirtyBySourceAndOutcome[source, kind];
                    }
                    if (sourceCalls == 0) { continue; }
                    D.Warn("Optimization Mod passability Region source "
                        + $"{SourceNames[source]}: eligibility calls="
                        + $"{sourceCalls}, newly dirtied="
                        + $"{sourceNewDirty}.");
                }
                for (int i = 0; i < WorstBatches.Count; i++) {
                    BatchRecord record = WorstBatches[i];
                    D.Warn($"Optimization Mod passability batch #{i + 1}: "
                        + $"+{ToSeconds(record.Offset):0.000}s, tick="
                        + $"{record.Tick}, frame={record.Frame}, speed="
                        + $"{(ClockSpeed) record.Speed}, queue="
                        + $"{record.QueueCount}, root/recursive="
                        + $"{record.RootUpdates}/{record.RecursiveUpdates}, "
                        + $"unchanged/connectivity/traversal="
                        + $"{record.Unchanged}/"
                        + $"{record.ConnectivityChanged}/"
                        + $"{record.TraversalOnlyChanged}, forced path "
                        + $"completion={record.ForcedPathCompletion}, "
                        + $"{ToMilliseconds(record.Elapsed):0.###} ms.");
                }
            } catch (Exception ex) {
                TryLogError("Optimization Mod passability diagnostic summary "
                    + "failed: " + ex);
            }
        }

        internal static void Reset() {
            Array.Clear(SourceSignals, 0, SourceSignals.Length);
            Array.Clear(SourceNewQueueEntries, 0,
                SourceNewQueueEntries.Length);
            Array.Clear(SourceCoalescedSignals, 0,
                SourceCoalescedSignals.Length);
            Array.Clear(RegionCallsByOutcome, 0,
                RegionCallsByOutcome.Length);
            Array.Clear(RegionNewDirtyByOutcome, 0,
                RegionNewDirtyByOutcome.Length);
            Array.Clear(RegionAlreadyDirtyByOutcome, 0,
                RegionAlreadyDirtyByOutcome.Length);
            Array.Clear(RegionLocalOrNoopByOutcome, 0,
                RegionLocalOrNoopByOutcome.Length);
            Array.Clear(RegionCallsBySourceAndOutcome, 0,
                RegionCallsBySourceAndOutcome.Length);
            Array.Clear(RegionNewDirtyBySourceAndOutcome, 0,
                RegionNewDirtyBySourceAndOutcome.Length);
            PendingSources.Clear();
            CurrentRootPositions.Clear();
            WorstBatches.Clear();
            sessionStartedAt = 0;
            signals = 0;
            newQueueEntries = 0;
            coalescedSignals = 0;
            batches = 0;
            queuedPositions = 0;
            maxQueueCount = 0;
            forcedPathCompletionBatches = 0;
            nodeUpdates = 0;
            rootNodeUpdates = 0;
            recursiveNodeUpdates = 0;
            unchangedNodes = 0;
            connectivityChangedNodes = 0;
            traversalOnlyChangedNodes = 0;
            typeChanges = 0;
            denyChanges = 0;
            difficultyChanges = 0;
            teleporterChanges = 0;
            batchElapsedTotal = 0;
            batchElapsedMax = 0;
            batchesOverFourMs = 0;
            batchesOverEightMs = 0;
            batchesOverSixteenMs = 0;
            batchesOverThirtyThreeMs = 0;
            regionEligibilityCalls = 0;
            regionEligibilityMatchedRoots = 0;
            regionEligibilityNewDirty = 0;
            regionEligibilityAlreadyDirty = 0;
            regionEligibilityLocalOrNoop = 0;
            rootsWithAreaMismatch = 0;
            rootsTouchingDoors = 0;
            rootsWithTeleporterChange = 0;
            activeSource = SignalSource.DirectOrMod;
            batchActive = false;
            currentBatchForcedPathCompletion = false;
            currentBatchQueueCount = 0;
            currentBatchRootUpdates = 0;
            currentBatchRecursiveUpdates = 0;
            currentBatchUnchanged = 0;
            currentBatchConnectivityChanged = 0;
            currentBatchTraversalOnlyChanged = 0;
            nodeUpdateDepth = 0;
            currentRootSource = SignalSource.DirectOrMod;
            currentRootUnchanged = 0;
            currentRootConnectivityChanged = 0;
            currentRootTraversalOnlyChanged = 0;
            currentRootHadUntrackedNode = false;
            currentRootTeleporterChanged = false;
            pendingRootOutcome = default(RootOutcome);
            failureLogged = false;
            IsFailOpen = false;
        }

        private static void Retain(BatchRecord record) {
            int index = 0;
            while (index < WorstBatches.Count
                    && WorstBatches[index].Elapsed >= record.Elapsed) {
                index++;
            }
            if (index >= MaxRetainedBatches) { return; }
            WorstBatches.Insert(index, record);
            if (WorstBatches.Count > MaxRetainedBatches) {
                WorstBatches.RemoveAt(WorstBatches.Count - 1);
            }
        }

        private static int CurrentSpeed() {
            int speed = (int) G.ClockSpeed;
            return speed < 0 || speed > 4 ? 0 : speed;
        }

        private static void FailOpen(Exception ex) {
            IsFailOpen = true;
            activeSource = SignalSource.DirectOrMod;
            batchActive = false;
            nodeUpdateDepth = 0;
            pendingRootOutcome = default(RootOutcome);
            PendingSources.Clear();
            CurrentRootPositions.Clear();
            if (failureLogged) { return; }
            failureLogged = true;
            TryLogError("Optimization Mod passability diagnostics failed open; "
                + "Core passability, pathfinding, and Region behavior remain "
                + "unchanged. " + ex);
        }

        private static void TryLogError(string message) {
            try {
                D.Err(message);
            } catch {
            }
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static double ToSeconds(long ticks) {
            return (double) ticks / Stopwatch.Frequency;
        }
    }
}
