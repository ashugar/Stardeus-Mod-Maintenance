using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game.CodeGen;
using Game.Components;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Items;
using Game.Systems.Nanobots;
using Game.Tools;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Owns narrowly scoped, fail-open Core performance patches. The mod does
    /// not serialize state and does not alter or delete save records.
    /// </summary>
    public sealed class OptimizationModSys : GameSystem {
        public const string SysId = "OptimizationModSys";
        private const string HarmonyId = "jleas.stardeus.optimizationmod";
        private Harmony harmony;
        private bool diagnosticsEnabled;
        private bool coreProfilerDiagnosticsEnabled;
        private bool wholeFrameDiagnosticsEnabled;
        private bool externalNanobotDiagnosticsEnabled;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        [SelfInit]
        private static void Register() {
            GameSystems.Register(SysId, () => new OptimizationModSys());
        }

        protected override void OnInitialize() {
            ActiveHaulQueryPatch.Reset();
            EquipmentLookupIndex.Reset();
            NanobotDirtyGoalIndex.Reset();
            StressPerformanceDiagnostics.Reset();
            NanobotPhaseDiagnostics.Reset();
            DirtyIndexPerformanceDiagnostics.Reset();
            FramePerformanceDiagnostics.Reset();
            WholeFrameHierarchyDiagnostics.Reset();
            SchedulerPerformanceDiagnostics.Reset();
            AiPhasePerformanceDiagnostics.Reset();
            AiActionPerformanceDiagnostics.Reset();
            FocusedAiActionDiagnostics.Reset();
            AreaRegionPerformanceDiagnostics.Reset();
            PassabilityPerformanceDiagnostics.Reset();
            ExternalNanobotExtensionDiagnostics.Reset();
            TrapFloorIndexedDispatcher.Reset();
            NanobotTooltipCorrection.Reset();
            OptimizationDiagnosticsSettings.Load();
            diagnosticsEnabled = OptimizationDiagnosticsSettings
                .DiagnosticsEnabled;
            coreProfilerDiagnosticsEnabled = OptimizationDiagnosticsSettings
                .CoreProfilerSuiteEnabled;
            wholeFrameDiagnosticsEnabled = OptimizationDiagnosticsSettings
                .WholeFrameProfilerEnabled;
            externalNanobotDiagnosticsEnabled =
                OptimizationDiagnosticsSettings
                    .ExternalNanobotProfilerEnabled;
            DirtyIndexPerformanceDiagnostics.Configure(
                coreProfilerDiagnosticsEnabled);
            harmony = new Harmony(HarmonyId);
            NanobotDirtyGoalIndex.Initialize(S);

            int installed = 0;
            installed += PatchPrefix(
                typeof(ObjsSys),
                "FindHaulableUnstoredObjs",
                new[] { typeof(ObjData[]), typeof(Func<ObjData, bool>) },
                typeof(ActiveHaulQueryPatch),
                nameof(ActiveHaulQueryPatch.Prefix));
            installed += PatchPrefix(
                typeof(ObjsSys),
                "BestAvailableEquipment",
                new[] {
                    typeof(InventoryItemSlot),
                    typeof(HashSet<uint>),
                    typeof(HashSet<int>)
                },
                typeof(BestAvailableEquipmentPatch),
                nameof(BestAvailableEquipmentPatch.Prefix));
            installed += PatchPrefix(
                typeof(ObjsSys),
                "FindCompatibleEquipment",
                new[] {
                    typeof(AIAgentComp),
                    typeof(int),
                    typeof(ObjData[]),
                    typeof(HashSet<uint>),
                    typeof(HashSet<int>)
                },
                typeof(FindCompatibleEquipmentPatch),
                nameof(FindCompatibleEquipmentPatch.Prefix));
            installed += PatchPrefix(
                typeof(ObjsSys),
                "FindCompatibleEquipmentUI",
                new[] { typeof(AIAgentComp), typeof(int), typeof(List<ObjData>) },
                typeof(FindCompatibleEquipmentUiPatch),
                nameof(FindCompatibleEquipmentUiPatch.Prefix));
            installed += PatchPrefix(
                typeof(ObjsSys),
                "FindStoredObjData",
                new[] {
                    typeof(int),
                    typeof(ObjData[]),
                    typeof(string),
                    typeof(ObjData.SortMode)
                },
                typeof(StoredObjectLookupPatch),
                nameof(StoredObjectLookupPatch.Prefix));
            installed += PatchPostfix(
                typeof(ObjsSys),
                "MakePersistent",
                new[] { typeof(ObjData) },
                typeof(PersistentObjectAddedPatch),
                nameof(PersistentObjectAddedPatch.Postfix));
            installed += PatchPostfix(
                typeof(ObjsSys),
                "Deactivate",
                new[] { typeof(ObjData), typeof(ObjState) },
                typeof(PersistentObjectRemovedPatch),
                nameof(PersistentObjectRemovedPatch.Postfix));
            int nanobotDirtyIndexPatches = 0;
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(AISys),
                "OnAddGoal",
                new[] { typeof(AIGoal) },
                typeof(NanobotGoalAddedToIndexPatch),
                nameof(NanobotGoalAddedToIndexPatch.Postfix));
            nanobotDirtyIndexPatches += PatchTranspiler(
                typeof(NanobotSys),
                "OnTick",
                new[] { typeof(long) },
                typeof(NanobotDirtyGoalIndex),
                nameof(NanobotDirtyGoalIndex.Transpiler),
                Priority.High);
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(NanobotSys),
                "OnTick",
                new[] { typeof(long) },
                typeof(NanobotDirtyIndexTickPatch),
                nameof(NanobotDirtyIndexTickPatch.Postfix),
                Priority.High);
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(AIGoal),
                nameof(AIGoal.ChangeStateTo),
                new[] {
                    typeof(AIGoalState), typeof(long), typeof(bool),
                    typeof(string)
                },
                typeof(NanobotGoalStateChangedPatch),
                nameof(NanobotGoalStateChangedPatch.Postfix));
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(AIGoal),
                nameof(AIGoal.NanobotCompatible),
                Type.EmptyTypes,
                typeof(NanobotCompatibilityChangedPatch),
                nameof(NanobotCompatibilityChangedPatch.Postfix));
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(ConstructableComp),
                nameof(ConstructableComp.ConfigureFloorWave),
                new[] { typeof(bool), typeof(int) },
                typeof(NanobotConstructionGateChangedPatch),
                nameof(NanobotConstructionGateChangedPatch.Postfix));
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(ConstructableComp),
                nameof(ConstructableComp.ConfigureFloorDeconstructWave),
                new[] { typeof(bool), typeof(int) },
                typeof(NanobotConstructionGateChangedPatch),
                nameof(NanobotConstructionGateChangedPatch.Postfix));
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(ConstructableComp),
                nameof(ConstructableComp.ClearFloorDeconstructWave),
                Type.EmptyTypes,
                typeof(NanobotConstructionGateChangedPatch),
                nameof(NanobotConstructionGateChangedPatch.Postfix));
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(NanobotSys),
                "OnRegisterHive",
                new[] { typeof(NanobotHiveComp), typeof(bool) },
                typeof(NanobotHiveRegistrationChangedPatch),
                nameof(NanobotHiveRegistrationChangedPatch.Postfix));
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(NanobotSys),
                "ToggleJob",
                new[] { typeof(int), typeof(bool) },
                typeof(NanobotJobTogglePatch),
                nameof(NanobotJobTogglePatch.Postfix));
            nanobotDirtyIndexPatches += PatchPostfix(
                typeof(AISys),
                "RemoveConstructionGoal",
                new[] { typeof(AIGoal) },
                typeof(NanobotGoalRemovedFromIndexPatch),
                nameof(NanobotGoalRemovedFromIndexPatch.Postfix));

            int trapFloorDispatcherPatches = 0;
            trapFloorDispatcherPatches += PatchPostfix(
                typeof(TrapFloorComp),
                nameof(TrapFloorComp.OnLateReady),
                new[] { typeof(bool) },
                typeof(TrapFloorLateReadyDispatcherPatch),
                nameof(TrapFloorLateReadyDispatcherPatch.Postfix));
            trapFloorDispatcherPatches += PatchPostfix(
                typeof(TrapFloorComp),
                nameof(TrapFloorComp.OnRemove),
                Type.EmptyTypes,
                typeof(TrapFloorRemoveDispatcherPatch),
                nameof(TrapFloorRemoveDispatcherPatch.Postfix));
            trapFloorDispatcherPatches += PatchPostfix(
                typeof(TrapFloorComp),
                nameof(TrapFloorComp.UpdatePosIdx),
                new[] { typeof(int), typeof(int), typeof(int) },
                typeof(TrapFloorPositionDispatcherPatch),
                nameof(TrapFloorPositionDispatcherPatch.Postfix));
            TrapFloorIndexedDispatcher.Initialize(
                S, coreProfilerDiagnosticsEnabled);

            int nanobotTooltipPatches = PatchTranspiler(
                typeof(FreeSelectGoalHandler),
                "LoadGoalTooltip",
                new[] { typeof(AIGoal), typeof(bool), typeof(StringBuilder) },
                typeof(NanobotTooltipCorrection),
                nameof(NanobotTooltipCorrection.Transpiler),
                Priority.High);

            int diagnosticProbes = 0;
            int nanobotPhaseDiagnostics = 0;
            int frameDiagnostics = 0;
            int wholeFrameDiagnostics = 0;
            int schedulerDiagnostics = 0;
            int aiPhaseDiagnostics = 0;
            int aiActionDiagnostics = 0;
            int focusedAiDiagnostics = 0;
            int areaRegionDiagnostics = 0;
            int passabilityDiagnostics = 0;
            int externalNanobotDiagnostics = 0;
            if (coreProfilerDiagnosticsEnabled) {
                diagnosticProbes = StressPerformanceDiagnostics
                    .Install(harmony);
                nanobotPhaseDiagnostics =
                    NanobotPhaseDiagnostics.Install(harmony);
                frameDiagnostics = FramePerformanceDiagnostics
                    .Install(harmony);
                wholeFrameDiagnostics = wholeFrameDiagnosticsEnabled
                    ? WholeFrameHierarchyDiagnostics.Install(harmony)
                    : 0;
                schedulerDiagnostics =
                    SchedulerPerformanceDiagnostics.Install(harmony);
                aiPhaseDiagnostics =
                    AiPhasePerformanceDiagnostics.Install(harmony);
                aiActionDiagnostics =
                    AiActionPerformanceDiagnostics.Install(harmony);
                focusedAiDiagnostics =
                    FocusedAiActionDiagnostics.Install(harmony);
                areaRegionDiagnostics =
                    AreaRegionPerformanceDiagnostics.Install(harmony);
                passabilityDiagnostics =
                    PassabilityPerformanceDiagnostics.Install(harmony);
            }
            if (externalNanobotDiagnosticsEnabled) {
                externalNanobotDiagnostics =
                    ExternalNanobotExtensionDiagnostics.Install(harmony);
            }

            var version = typeof(OptimizationModSys)
                .Assembly.GetName().Version.ToString(3);
            bool queryPatchesHealthy = installed == 7;
            bool nanobotIndexHealthy = nanobotDirtyIndexPatches == 11
                && NanobotDirtyGoalIndex.IsTranspilerActive
                && !NanobotDirtyGoalIndex.IsFailOpen;
            bool trapDispatcherHealthy = trapFloorDispatcherPatches == 3
                && !TrapFloorIndexedDispatcher.IsFailOpen;
            bool nanobotTooltipHealthy = nanobotTooltipPatches == 1
                && NanobotTooltipCorrection.IsTranspilerActive;

            if (!queryPatchesHealthy) {
                D.Err("Optimization Mod {0} initialized with {1}/7 optimization "
                    + "patches. Unmatched paths remain on vanilla behavior.",
                    version, installed);
            }
            if (!nanobotIndexHealthy) {
                D.Err("Optimization Mod Nanobot dirty index is not fully active "
                    + "({0}/11 hooks, transpiler active: {1}, fail-open: {2}). "
                    + "Core's complete colony-goal view remains available.",
                    nanobotDirtyIndexPatches,
                    NanobotDirtyGoalIndex.IsTranspilerActive,
                    NanobotDirtyGoalIndex.IsFailOpen);
            }
            if (!trapDispatcherHealthy) {
                D.Err("Optimization Mod Trap Floor indexed dispatcher is not "
                    + "fully available ({0}/3 lifecycle hooks, fail-open: "
                    + "{1}). Core's per-trap listeners remain active.",
                    trapFloorDispatcherPatches,
                    TrapFloorIndexedDispatcher.IsFailOpen);
            }
            if (!nanobotTooltipHealthy) {
                D.Err("Optimization Mod Nanobot tooltip correction is not "
                    + "active ({0}/1 hooks, transpiler active: {1}). Core's "
                    + "vanilla tooltip wording remains active.",
                    nanobotTooltipPatches,
                    NanobotTooltipCorrection.IsTranspilerActive);
            }

            if (diagnosticsEnabled) {
                if (!coreProfilerDiagnosticsEnabled) {
                    D.Warn("Optimization Mod retained Core profiler suite "
                        + "remains disabled within this diagnostic session.");
                } else if (diagnosticProbes
                        == StressPerformanceDiagnostics.ExpectedProbeCount) {
                    D.Warn("Optimization Mod stress diagnostics armed with all "
                        + "{0} high-level timing probes.", diagnosticProbes);
                } else {
                    D.Err("Optimization Mod stress diagnostics armed with "
                        + "{0}/{1} high-level timing probes.", diagnosticProbes,
                        StressPerformanceDiagnostics.ExpectedProbeCount);
                }
                if (coreProfilerDiagnosticsEnabled
                        && nanobotPhaseDiagnostics == 1
                        && NanobotPhaseDiagnostics.IsTranspilerActive) {
                    D.Warn("Optimization Mod Nanobot phase diagnostics armed.");
                } else if (coreProfilerDiagnosticsEnabled) {
                    D.Err("Optimization Mod Nanobot phase diagnostics are not "
                        + "active; Core Nanobot behavior remains unchanged.");
                }
                if (coreProfilerDiagnosticsEnabled && frameDiagnostics
                        == FramePerformanceDiagnostics.ExpectedProbeCount) {
                    D.Warn("Optimization Mod frame and panning diagnostics "
                        + "armed with all {0} aggregate timing probes.",
                        frameDiagnostics);
                } else if (coreProfilerDiagnosticsEnabled) {
                    D.Err("Optimization Mod frame and panning diagnostics "
                        + "armed with {0}/{1} aggregate timing probes.",
                        frameDiagnostics,
                        FramePerformanceDiagnostics.ExpectedProbeCount);
                }
                if (!wholeFrameDiagnosticsEnabled) {
                    D.Warn("Optimization Mod broad whole-frame profiler remains "
                        + "disabled within this diagnostic session.");
                } else if (wholeFrameDiagnostics > 0
                        && !WholeFrameHierarchyDiagnostics.IsFailOpen) {
                    D.Warn("Optimization Mod whole-frame profiler armed with "
                        + "{0} entry points.", wholeFrameDiagnostics);
                } else {
                    D.Err("Optimization Mod whole-frame profiler is not fully "
                        + "active ({0} entry points, fail-open: {1}).",
                        wholeFrameDiagnostics,
                        WholeFrameHierarchyDiagnostics.IsFailOpen);
                }
                if (coreProfilerDiagnosticsEnabled && schedulerDiagnostics
                        == SchedulerPerformanceDiagnostics.ExpectedHookCount
                        && SchedulerPerformanceDiagnostics.IsInstalled
                        && !SchedulerPerformanceDiagnostics.IsFailOpen) {
                    D.Warn("Optimization Mod scheduler diagnostics armed with "
                        + "all {0} hooks.", schedulerDiagnostics);
                } else if (coreProfilerDiagnosticsEnabled) {
                    D.Err("Optimization Mod scheduler diagnostics armed with "
                        + "{0}/{1} hooks (installed: {2}, fail-open: {3}).",
                        schedulerDiagnostics,
                        SchedulerPerformanceDiagnostics.ExpectedHookCount,
                        SchedulerPerformanceDiagnostics.IsInstalled,
                        SchedulerPerformanceDiagnostics.IsFailOpen);
                }
                if (coreProfilerDiagnosticsEnabled && aiPhaseDiagnostics
                        == AiPhasePerformanceDiagnostics.ExpectedHookCount
                        && !AiPhasePerformanceDiagnostics.IsFailOpen) {
                    D.Warn("Optimization Mod AI phase diagnostics armed with "
                        + "all {0} hooks.", aiPhaseDiagnostics);
                } else if (coreProfilerDiagnosticsEnabled) {
                    D.Err("Optimization Mod AI phase diagnostics armed with "
                        + "{0}/{1} hooks (fail-open: {2}). Core AI behavior "
                        + "remains unchanged.", aiPhaseDiagnostics,
                        AiPhasePerformanceDiagnostics.ExpectedHookCount,
                        AiPhasePerformanceDiagnostics.IsFailOpen);
                }
                if (coreProfilerDiagnosticsEnabled && aiActionDiagnostics > 0
                        && aiActionDiagnostics == AiActionPerformanceDiagnostics
                            .DiscoveredImplementationCount
                        && AiActionPerformanceDiagnostics.InstallFailures == 0
                        && !AiActionPerformanceDiagnostics.IsFailOpen) {
                    D.Warn("Optimization Mod AI action diagnostics armed "
                        + "across all {0} discovered action implementations.",
                        aiActionDiagnostics);
                } else if (coreProfilerDiagnosticsEnabled) {
                    D.Err("Optimization Mod AI action diagnostics armed across "
                        + "{0}/{1} discovered action implementations "
                        + "(install failures: {2}, fail-open: {3}). AI "
                        + "behavior remains unchanged.", aiActionDiagnostics,
                        AiActionPerformanceDiagnostics
                            .DiscoveredImplementationCount,
                        AiActionPerformanceDiagnostics.InstallFailures,
                        AiActionPerformanceDiagnostics.IsFailOpen);
                }
                if (coreProfilerDiagnosticsEnabled && focusedAiDiagnostics
                        == FocusedAiActionDiagnostics.ExpectedHookCount
                        && !FocusedAiActionDiagnostics.IsFailOpen) {
                    D.Warn("Optimization Mod focused AI action diagnostics "
                        + "armed with all {0} hooks.", focusedAiDiagnostics);
                } else if (coreProfilerDiagnosticsEnabled) {
                    D.Err("Optimization Mod focused AI action diagnostics "
                        + "armed with {0}/{1} hooks (fail-open: {2}). AI "
                        + "behavior remains unchanged.", focusedAiDiagnostics,
                        FocusedAiActionDiagnostics.ExpectedHookCount,
                        FocusedAiActionDiagnostics.IsFailOpen);
                }
                if (coreProfilerDiagnosticsEnabled && areaRegionDiagnostics
                        == AreaRegionPerformanceDiagnostics.ExpectedHookCount
                        && !AreaRegionPerformanceDiagnostics.IsFailOpen) {
                    D.Warn("Optimization Mod Area/Region diagnostics armed with "
                        + "all {0} hooks.", areaRegionDiagnostics);
                } else if (coreProfilerDiagnosticsEnabled) {
                    D.Err("Optimization Mod Area/Region diagnostics armed with "
                        + "{0}/{1} hooks (fail-open: {2}).",
                        areaRegionDiagnostics,
                        AreaRegionPerformanceDiagnostics.ExpectedHookCount,
                        AreaRegionPerformanceDiagnostics.IsFailOpen);
                }
                if (coreProfilerDiagnosticsEnabled && passabilityDiagnostics
                        == PassabilityPerformanceDiagnostics.ExpectedHookCount
                        && !PassabilityPerformanceDiagnostics.IsFailOpen) {
                    D.Warn("Optimization Mod passability diagnostics armed with "
                        + "all {0} hooks.", passabilityDiagnostics);
                } else if (coreProfilerDiagnosticsEnabled) {
                    D.Err("Optimization Mod passability diagnostics armed with "
                        + "{0}/{1} hooks (fail-open: {2}).",
                        passabilityDiagnostics,
                        PassabilityPerformanceDiagnostics.ExpectedHookCount,
                        PassabilityPerformanceDiagnostics.IsFailOpen);
                }
                if (!externalNanobotDiagnosticsEnabled) {
                    D.Warn("Optimization Mod external Nanobot extension "
                        + "profiler remains disabled within this diagnostic "
                        + "session.");
                } else if (externalNanobotDiagnostics
                        == ExternalNanobotExtensionDiagnostics
                            .ExpectedProbeCount) {
                    D.Warn("Optimization Mod external Nanobot extension "
                        + "profiler armed with all {0} timing probes.",
                        externalNanobotDiagnostics);
                } else {
                    D.Err("Optimization Mod external Nanobot extension "
                        + "profiler armed with {0}/{1} timing probes. Missing "
                        + "targets remain behaviorally untouched.",
                        externalNanobotDiagnostics,
                        ExternalNanobotExtensionDiagnostics
                            .ExpectedProbeCount);
                }
            }

            if (queryPatchesHealthy && nanobotIndexHealthy
                    && trapDispatcherHealthy && nanobotTooltipHealthy) {
                D.Warn("Optimization Mod {0} initialized successfully; "
                    + "diagnostic probes are {1}.", version,
                    diagnosticsEnabled ? "enabled" : "disabled");
            }
        }

        private int PatchPrefix(
                Type targetType,
                string targetName,
                Type[] parameterTypes,
                Type patchType,
                string patchName) {
            try {
                MethodInfo original = AccessTools.DeclaredMethod(
                    targetType, targetName, parameterTypes);
                MethodInfo prefix = AccessTools.DeclaredMethod(patchType, patchName);
                if (original == null || prefix == null) {
                    D.Err("Optimization Mod skipped unavailable patch: {0}.{1}",
                        targetType.FullName, targetName);
                    return 0;
                }

                var harmonyMethod = new HarmonyMethod(prefix) {
                    // Run after ordinary prefixes. This lets compatibility or
                    // behavior mods inspect the vanilla call first.
                    priority = Priority.Low
                };
                harmony.Patch(original, prefix: harmonyMethod);
                return 1;
            } catch (Exception ex) {
                D.Err("Optimization Mod failed to patch {0}.{1}: {2}",
                    targetType.FullName, targetName, ex);
                return 0;
            }
        }

        private int PatchPostfix(
                Type targetType,
                string targetName,
                Type[] parameterTypes,
                Type patchType,
                string patchName,
                int priority = Priority.Low) {
            try {
                MethodInfo original = AccessTools.DeclaredMethod(
                    targetType, targetName, parameterTypes);
                MethodInfo postfix = AccessTools.DeclaredMethod(patchType, patchName);
                if (original == null || postfix == null) {
                    D.Err("Optimization Mod skipped unavailable lifecycle patch: {0}.{1}",
                        targetType.FullName, targetName);
                    return 0;
                }

                var harmonyMethod = new HarmonyMethod(postfix) {
                    priority = priority
                };
                harmony.Patch(original, postfix: harmonyMethod);
                return 1;
            } catch (Exception ex) {
                D.Err("Optimization Mod failed to patch lifecycle {0}.{1}: {2}",
                    targetType.FullName, targetName, ex);
                return 0;
            }
        }

        private int PatchTranspiler(
                Type targetType,
                string targetName,
                Type[] parameterTypes,
                Type patchType,
                string patchName,
                int priority = Priority.Low) {
            try {
                MethodInfo original = AccessTools.DeclaredMethod(
                    targetType, targetName, parameterTypes);
                MethodInfo transpiler = AccessTools.DeclaredMethod(
                    patchType, patchName);
                if (original == null || transpiler == null) {
                    D.Err("Optimization Mod skipped unavailable transpiler: {0}.{1}",
                        targetType.FullName, targetName);
                    return 0;
                }

                var harmonyMethod = new HarmonyMethod(transpiler) {
                    priority = priority
                };
                harmony.Patch(original, transpiler: harmonyMethod);
                return 1;
            } catch (Exception ex) {
                D.Err("Optimization Mod failed to transpile {0}.{1}: {2}",
                    targetType.FullName, targetName, ex);
                return 0;
            }
        }

        public override void Unload() {
            if (coreProfilerDiagnosticsEnabled) {
                StressPerformanceDiagnostics.LogSummary();
                NanobotPhaseDiagnostics.LogSummary();
                DirtyIndexPerformanceDiagnostics.LogSummary();
                FramePerformanceDiagnostics.LogSummary();
                if (wholeFrameDiagnosticsEnabled) {
                    WholeFrameHierarchyDiagnostics.LogSummary();
                }
                SchedulerPerformanceDiagnostics.LogSummary();
                AiPhasePerformanceDiagnostics.LogSummary();
                AiActionPerformanceDiagnostics.LogSummary();
                FocusedAiActionDiagnostics.LogSummary();
                AreaRegionPerformanceDiagnostics.LogSummary();
                PassabilityPerformanceDiagnostics.LogSummary();
                NanobotDirtyGoalIndex.LogSummary();
                TrapFloorIndexedDispatcher.LogSummary();
            }
            if (externalNanobotDiagnosticsEnabled) {
                ExternalNanobotExtensionDiagnostics.LogSummary();
            }
            TrapFloorIndexedDispatcher.Shutdown();
            StressPerformanceDiagnostics.Reset();
            NanobotPhaseDiagnostics.Reset();
            DirtyIndexPerformanceDiagnostics.Reset();
            FramePerformanceDiagnostics.Reset();
            WholeFrameHierarchyDiagnostics.Reset();
            SchedulerPerformanceDiagnostics.Reset();
            AiPhasePerformanceDiagnostics.Reset();
            AiActionPerformanceDiagnostics.Reset();
            FocusedAiActionDiagnostics.Reset();
            AreaRegionPerformanceDiagnostics.Reset();
            PassabilityPerformanceDiagnostics.Reset();
            ExternalNanobotExtensionDiagnostics.Reset();
            NanobotDirtyGoalIndex.Reset();
            ActiveHaulQueryPatch.Reset();
            EquipmentLookupIndex.Reset();
            TrapFloorIndexedDispatcher.Reset();
            NanobotTooltipCorrection.Reset();
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            diagnosticsEnabled = false;
            coreProfilerDiagnosticsEnabled = false;
            wholeFrameDiagnosticsEnabled = false;
            externalNanobotDiagnosticsEnabled = false;
        }
    }
}
