using System;
using System.Reflection;
using System.Threading;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Inventory;
using Game.Systems.Nanobots;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.QuantumStorage.Relays
{
    // Marks synchronous planning reads only. Neither hook suppresses native code,
    // changes a verdict, caches a quantity, or extends into an active operation.
    internal static class NanobotCandidateQueries
    {
        internal const string Owner = "QuantumStorage.NanobotAdvisoryRead";
        private const int DepthLimit = 32;
        private const string CandidateHash = "2B7D8E97CEAE851FEB8933AD24199E8217DF7AC2B9665B1A2C01B64B93E75655";
        private const string DecisionHash = "86E7E2E97F5D529C23F576B8227C5B31634A68092CC1165785129EBCE86451CD";
        private static Harmony harmony;
        private static MethodInfo candidateTarget, decisionTarget;
        private static bool installed, compatible, cleanupUnconfirmed, prepared, preparationOverflow;
        private static readonly WeakReference[] preparedWorlds = new WeakReference[16];
        private static GameState world;
        private static int ownerThread, epoch, reports;
        [ThreadStatic] private static Frame candidate, decision;
        [ThreadStatic] private static int serial;

        internal struct Frame
        {
            internal GameState World;
            internal InventorySys Inventory;
            internal int Serial, ParentSerial, Depth, Epoch;
            internal bool Enabled, Revalidation;
        }
        internal struct CandidateState { internal Frame Previous; internal int Serial, Epoch; }
        internal struct DecisionState { internal Frame Previous; internal int Serial, Epoch; }
        internal static int Epoch => epoch;
        internal static string Status => cleanupUnconfirmed ? "strict-cleanup-unconfirmed" : preparationOverflow ? "strict-world-overflow" : !installed ? "strict-contract" :
            !compatible ? "strict-hooks" : world == null ? prepared ? "waiting-current" : "waiting-load" : "active";

        internal static void Install()
        {
            if (installed || cleanupUnconfirmed || preparationOverflow) return;
            try
            {
                var assembly = typeof(NanobotSys).Assembly;
                var controller = assembly.GetType("Game.Systems.Nanobots.NanobotHaulingController", false);
                var result = assembly.GetType("Game.Systems.Nanobots.NanobotMaterialHaulCandidate", false);
                candidateTarget = controller == null || result == null ? null : AccessTools.DeclaredMethod(controller,
                    "TryFindCandidate", new[] { typeof(AIGoal), typeof(bool), result.MakeByRefType() });
                decisionTarget = AccessTools.DeclaredMethod(typeof(MatClaimGate), "Decide",
                    new[] { typeof(InventorySys), typeof(IMatRequester), typeof(MatRequest), typeof(int), typeof(long) });
                var state = controller?.GetField("S", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (!ReviewedContracts(candidateTarget, decisionTarget, state)) throw new InvalidOperationException();
                if (!CompatibleHooks(candidateTarget, null, null) || !CompatibleHooks(decisionTarget, null, null))
                    throw new InvalidOperationException();
                harmony = new Harmony(Owner);
                harmony.Patch(candidateTarget,
                    prefix: new HarmonyMethod(typeof(NanobotCandidateQueries), nameof(CandidateBefore)) { priority = HarmonyLib.Priority.First },
                    finalizer: new HarmonyMethod(typeof(NanobotCandidateQueries), nameof(CandidateAfter)) { priority = HarmonyLib.Priority.Last });
                harmony.Patch(decisionTarget,
                    prefix: new HarmonyMethod(typeof(NanobotCandidateQueries), nameof(DecisionBefore)) { priority = HarmonyLib.Priority.First },
                    finalizer: new HarmonyMethod(typeof(NanobotCandidateQueries), nameof(DecisionAfter)) { priority = HarmonyLib.Priority.Last });
                installed = compatible = true;
            }
            catch (Exception)
            {
                RemoveHooks();
                Report("Quantum Storage: Nanobot advisory read optimization unavailable; strict Relay query validation retained.");
            }
        }

        internal static bool ReviewedContracts(MethodInfo candidateMethod, MethodInfo decisionMethod, FieldInfo state)
        {
            if (candidateMethod == null || candidateMethod.IsStatic || candidateMethod.IsGenericMethod || candidateMethod.ReturnType != typeof(bool) ||
                decisionMethod == null || !decisionMethod.IsStatic || decisionMethod.IsGenericMethod || decisionMethod.ReturnType != typeof(MatClaimVerdict) ||
                state == null || state.Name != "S" || !state.IsPrivate || state.IsStatic || state.FieldType != typeof(GameState) || state.DeclaringType != candidateMethod.DeclaringType) return false;
            var first = candidateMethod.GetParameters();
            var second = decisionMethod.GetParameters();
            if (candidateMethod.Name != "TryFindCandidate" || candidateMethod.DeclaringType.FullName != "Game.Systems.Nanobots.NanobotHaulingController" ||
                first.Length != 3 || first[0].ParameterType != typeof(AIGoal) || first[1].ParameterType != typeof(bool) ||
                !first[2].ParameterType.IsByRef || first[2].ParameterType.GetElementType().FullName != "Game.Systems.Nanobots.NanobotMaterialHaulCandidate" ||
                decisionMethod.Name != "Decide" || decisionMethod.DeclaringType != typeof(MatClaimGate) || second.Length != 5 ||
                second[0].ParameterType != typeof(InventorySys) || second[1].ParameterType != typeof(IMatRequester) ||
                second[2].ParameterType != typeof(MatRequest) || second[3].ParameterType != typeof(int) || second[4].ParameterType != typeof(long)) return false;
            return string.Equals(MaterialPanelContractFingerprint.Hash(candidateMethod), CandidateHash, StringComparison.Ordinal) &&
                string.Equals(MaterialPanelContractFingerprint.Hash(decisionMethod), DecisionHash, StringComparison.Ordinal);
        }

        // This is a setup/load compatibility census, not a hot-path patch census.
        // Later foreign patch-flow changes are not assumed to be runtime-audited.
        private static bool CompatibleHooks(MethodBase target, string before, string after)
        {
            if (target == null) return false;
            var patches = Harmony.GetPatchInfo(target);
            if (patches == null) return before == null;
            if (patches.Postfixes.Count != 0 || patches.Transpilers.Count != 0) return false;
            if (before == null) return patches.Prefixes.Count == 0 && patches.Finalizers.Count == 0;
            if (patches.Prefixes.Count != 1 || patches.Finalizers.Count != 1) return false;
            return patches.Prefixes[0].owner == Owner && patches.Finalizers[0].owner == Owner &&
                patches.Prefixes[0].PatchMethod == AccessTools.DeclaredMethod(typeof(NanobotCandidateQueries), before) &&
                patches.Finalizers[0].PatchMethod == AccessTools.DeclaredMethod(typeof(NanobotCandidateQueries), after);
        }

        internal static void WorldLoaded(GameState state)
        {
            try
            {
                if (state == null || G.IsDemoMode) return;
                if (world != null && GameState.IsCurrent(world) && Thread.CurrentThread.ManagedThreadId != ownerThread) return;
                StorageRelaySys.Get(state)?.PrepareAdvisoryReadWorld();
                if (!PrepareWorld(state)) return;
                // A background world's AfterLoad must not disarm a live save.
                if (world != null && !ReferenceEquals(world, state) && GameState.IsCurrent(world)) return;
                Install();
                AdvanceEpoch(); world = null; ownerThread = 0;
                if (!installed || cleanupUnconfirmed) return;
                compatible = CompatibleHooks(candidateTarget, nameof(CandidateBefore), nameof(CandidateAfter)) &&
                    CompatibleHooks(decisionTarget, nameof(DecisionBefore), nameof(DecisionAfter));
                if (!compatible)
                {
                    Report("Quantum Storage: unreviewed Nanobot claim hooks retain strict Relay query validation.");
                    return;
                }
                prepared = true;
                if (GameState.IsCurrent(state)) ArmWorld(state);
            }
            catch (Exception) { AdvanceEpoch(); world = null; ownerThread = 0; compatible = false; }
        }

        internal static void Unload(GameState state)
        {
            bool ownsWorld = ReferenceEquals(world, state);
            ForgetWorld(state);
            if (ownsWorld) { AdvanceEpoch(); world = null; ownerThread = 0; }
            // Another saved world may already have completed AfterLoad but not
            // yet become current. Keep passive hooks available for its late arm.
            prepared = HasPreparedWorlds();
            if (!prepared && world == null) RemoveHooks();
        }
        private static bool PrepareWorld(GameState state)
        {
            int free = -1;
            for (int i = 0; i < preparedWorlds.Length; i++)
            {
                var value = preparedWorlds[i]?.Target as GameState;
                if (ReferenceEquals(value, state)) { prepared = true; return !preparationOverflow; }
                if (value == null && free < 0) free = i;
            }
            if (free >= 0)
            { preparedWorlds[free] = new WeakReference(state); prepared = true; return !preparationOverflow; }
            preparationOverflow = true; compatible = false;
            AdvanceEpoch(); world = null; ownerThread = 0;
            Report("Quantum Storage: too many pending world contexts; strict Relay query validation retained.");
            return false;
        }
        private static void ForgetWorld(GameState state)
        {
            for (int i = 0; i < preparedWorlds.Length; i++)
                if (ReferenceEquals(preparedWorlds[i]?.Target, state)) preparedWorlds[i] = null;
        }
        private static bool HasPreparedWorlds()
        {
            for (int i = 0; i < preparedWorlds.Length; i++) if (preparedWorlds[i]?.Target is GameState) return true;
            return false;
        }
        private static bool IsPrepared(GameState state)
        {
            for (int i = 0; i < preparedWorlds.Length; i++) if (ReferenceEquals(preparedWorlds[i]?.Target, state)) return true;
            return false;
        }
        private static void RemoveHooks()
        {
            try { harmony?.UnpatchAll(Owner); harmony = null; installed = compatible = false; }
            catch (Exception) { cleanupUnconfirmed = true; installed = compatible = false; }
        }
        private static void AdvanceEpoch()
        {
            unchecked { epoch++; if (epoch == 0) epoch++; }
            candidate = decision = default;
        }
        private static int NextSerial() { unchecked { serial++; if (serial == 0) serial++; } return serial; }
        private static void ArmWorld(GameState state)
        {
            AdvanceEpoch(); world = state; ownerThread = Thread.CurrentThread.ManagedThreadId;
        }
        private static bool WorldIsUsable() => installed && compatible && !cleanupUnconfirmed && world != null &&
            Thread.CurrentThread.ManagedThreadId == ownerThread && !G.IsDemoMode && G.HasEnteredGame && GameState.IsCurrent(world);

        internal static CandidateState EnterCandidate(GameState state, bool allowFailed)
        {
            // AfterLoad can precede IsCurrent/HasEnteredGame. Re-arm once the
            // prepared world becomes live, without rehashing or patch census.
            if (candidate.Depth == 0 && decision.Depth == 0 && !ReferenceEquals(world, state) && installed && compatible &&
                !cleanupUnconfirmed && !preparationOverflow && state != null &&
                !G.IsDemoMode && G.HasEnteredGame && GameState.IsCurrent(state) &&
                IsPrepared(state) &&
                StorageRelaySys.Get(state)?.CanPrepareAdvisoryReadWorld == true) ArmWorld(state);
            var previous = candidate;
            int id = NextSerial();
            candidate = new Frame { World = state, Serial = id, Depth = previous.Depth + 1, Epoch = epoch,
                Revalidation = allowFailed || previous.Revalidation, Enabled = !allowFailed && previous.Depth < DepthLimit &&
                    (previous.Depth == 0 || previous.Enabled) &&
                    WorldIsUsable() && ReferenceEquals(state, world) };
            return new CandidateState { Previous = previous, Serial = id, Epoch = epoch };
        }
        internal static void ExitCandidate(CandidateState state)
        {
            if (state.Serial == 0) return;
            candidate = state.Epoch == epoch && candidate.Serial == state.Serial ? state.Previous : default;
        }
        internal static DecisionState EnterDecision(InventorySys inventory)
        {
            var previous = decision;
            int id = NextSerial();
            decision = new Frame { Inventory = inventory, World = inventory?.S, Serial = id, ParentSerial = candidate.Serial,
                Depth = previous.Depth + 1, Epoch = epoch, Enabled = previous.Depth < DepthLimit && candidate.Enabled &&
                    (previous.Depth == 0 || previous.Enabled) &&
                    candidate.Epoch == epoch && WorldIsUsable() && inventory != null && ReferenceEquals(inventory.S, candidate.World) &&
                    ReferenceEquals(inventory, world.Sys?.Inventory) };
            return new DecisionState { Previous = previous, Serial = id, Epoch = epoch };
        }
        internal static void ExitDecision(DecisionState state)
        {
            if (state.Serial == 0) return;
            decision = state.Epoch == epoch && decision.Serial == state.Serial ? state.Previous : default;
        }
        internal static bool IsAdvisory(InventorySys inventory)
        {
#if QUANTUM_RELAY_QUERY_BASELINE
            if (RelayQueryBaseline.SamplingQuery)
                RelayQueryBaseline.NoteCandidateMode(candidate.Serial != 0, candidate.Revalidation,
                    GameState.IsCurrent(inventory?.S), decision.Serial != 0 && decision.Enabled && decision.Epoch == epoch &&
                    decision.ParentSerial == candidate.Serial && ReferenceEquals(decision.Inventory, inventory));
#endif
            return CanUseAdvisory(inventory);
        }
        internal static bool CanUseAdvisory(InventorySys inventory) => candidate.Enabled && decision.Enabled &&
            candidate.Epoch == epoch && decision.Epoch == epoch && decision.ParentSerial == candidate.Serial &&
            ReferenceEquals(decision.Inventory, inventory) && ReferenceEquals(decision.World, world) && WorldIsUsable() &&
            ReferenceEquals(inventory, world.Sys?.Inventory);

        private static void CandidateBefore(GameState ___S, bool __1, out CandidateState __state)
        {
            __state = default;
            try { __state = EnterCandidate(___S, __1); }
            catch (Exception) { candidate = default; }
        }
        private static Exception CandidateAfter(Exception __exception, CandidateState __state)
        {
            try { ExitCandidate(__state); } catch (Exception) { candidate = default; }
            return __exception;
        }
        private static void DecisionBefore(InventorySys __0, out DecisionState __state)
        {
            __state = default;
            try { __state = EnterDecision(__0); }
            catch (Exception) { decision = default; }
        }
        private static Exception DecisionAfter(Exception __exception, DecisionState __state)
        {
            try { ExitDecision(__state); } catch (Exception) { decision = default; }
            return __exception;
        }
        private static void Report(string value) { if (reports++ >= 6) return; try { D.Warn(value); } catch (Exception) { } }
    }
}
