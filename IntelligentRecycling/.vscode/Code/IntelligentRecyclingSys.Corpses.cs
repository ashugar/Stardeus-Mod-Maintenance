using System;
using System.Collections.Generic;
using Game.CodeGen;
using Game.Components;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.IntelligentRecycling {
    public sealed partial class IntelligentRecyclingSys {
        internal const string CorpseMarker = "IntelligentRecycling.CorpseKind";
        private const string CorpseBeingMarker = "IntelligentRecycling.CorpseBeing";
        private const string CorpseGoalMarker = "IntelligentRecycling.CorpseGoal";
        internal const string CorpsePolicyDenied = "Intelligent Recycling corpse policy no longer permits disposal";
        private readonly Dictionary<uint, CorpseRecord> corpseGoals = new();
        private bool corpseSettingsLoaded;
        private bool corpseInitializationReady;
        private int corpseLoadAttemptedGameId = -1;
        private GameState corpseSafetyState;
        private bool corpseSafetySweepDone;

        private void OnCorpseSafetyAfterLoadState(GameState state) {
            corpseSafetyState?.Clock.OnTickEarly.RemoveListener(OnCorpseSafetyTick);
            corpseSafetyState = state;
            corpseSafetySweepDone = false;
            // Clock emits OnTickEarly before OnTick, where AISys executes.
            // Listener order within a signal is intentionally not assumed.
            state.Clock.OnTickEarly.AddListener(OnCorpseSafetyTick);
        }

        private void OnCorpseSafetyTick(long ticks) {
            if (corpseSafetySweepDone || corpseSafetyState == null) { return; }
            corpseSafetySweepDone = true;
            bool corpseReady = CorpseRuntimeReady(corpseSafetyState);
            bool selectionReady = SelectionRuntimeReady(corpseSafetyState);
            if (corpseReady && selectionReady) { return; }
            List<AIGoal> denied = null;
            foreach (AIGoal goal in corpseSafetyState.Sys.AI.ColonyGoals.Values) {
                if ((!corpseReady && (TryReadCorpseMarker(goal, out _, out _)
                            || IsRegisteredCorpseGoal(goal)))
                        || (!selectionReady && !IsNativeSelectionTakeover(goal)
                            && (HasSelectionMarker(goal)
                            || IsRegisteredEggGoal(goal)))) {
                    (denied ??= new List<AIGoal>()).Add(goal);
                }
            }
            if (denied == null) { return; }
            foreach (AIGoal goal in denied) {
                string reason = HasSelectionMarker(goal) || IsRegisteredEggGoal(goal)
                    ? SelectionPolicyDenied : CorpsePolicyDenied;
                AIGoal.Cancel(goal, ticks, reason, removeImmediately: false);
            }
        }

        private sealed class CorpseRecord {
            internal int GameId;
            internal uint BeingId;
            internal uint GoalId;
            internal CorpseDisposalKind Kind;
        }

        public bool RecycleBiologicalCorpses = false;
        public bool RecycleRoboticCorpses = false;
        public int BiologicalCorpseTasksQueued { get; private set; }
        public int RoboticCorpseTasksQueued { get; private set; }

        internal void SetBiologicalCorpses(bool value) {
            RecycleBiologicalCorpses = value;
            RequestScan();
            panel?.Refresh();
        }

        internal void SetRoboticCorpses(bool value) {
            RecycleRoboticCorpses = value;
            RequestScan();
            panel?.Refresh();
        }

        private bool CorpseRuntimeReady(GameState state) {
            return state != null && CorpseDisposalGuards.Ready && corpseInitializationReady
                && corpseSettingsLoaded
                && ReferenceEquals(state, loadedState)
                && loadedSettingsGameId == state.GameId;
        }

        internal static bool TryReadCorpseMarker(AIGoal goal,
                out CorpseDisposalKind kind, out uint beingId) {
            kind = default;
            beingId = 0;
            if (!HasGoalMarker(goal, CorpseMarker)) { return false; }
            goal.TryGetVar(CorpseMarker, out int raw);
            kind = (CorpseDisposalKind) raw;
            // Any namespaced marker remains guarded, including incomplete or
            // damaged persisted data. It must never fall back to manual work.
            goal.TryGetVar(CorpseBeingMarker, out beingId);
            return true;
        }

        private static bool HasGoalMarker(AIGoal goal, string key) {
            ComponentData vars = goal?.Vars;
            return vars != null && (vars.Ints?.ContainsKey(key) == true
                || vars.Strings?.ContainsKey(key) == true
                || vars.Bools?.ContainsKey(key) == true
                || vars.Longs?.ContainsKey(key) == true
                || vars.Floats?.ContainsKey(key) == true
                || vars.IntSets?.ContainsKey(key) == true
                || vars.StringSets?.ContainsKey(key) == true
                || vars.FloatSets?.ContainsKey(key) == true
                || vars.Mats?.ContainsKey(key) == true);
        }

        internal static bool IsCorpseWork(AIGoal goal) {
            return TryReadCorpseMarker(goal, out _, out _)
                || activeInstance?.IsRegisteredCorpseGoal(goal) == true;
        }

        private bool IsRegisteredCorpseGoal(AIGoal goal) {
            BeingData data = (goal?.Target as DisposableComp)?.Being?.Data;
            return data != null && ReferenceEquals(data.Being.Goal, goal)
                && corpseGoals.TryGetValue(data.Id, out CorpseRecord record)
                && CorpseDisposalRules.Matches(S.GameId, data.Id, goal.Id,
                    record.Kind, record.GameId, record.BeingId, record.GoalId, record.Kind);
        }

        private bool OwnsCorpseGoal(GameState state, BeingData data,
                AIGoal goal, out CorpseDisposalKind kind) {
            kind = default;
            return data != null && goal != null && !goal.IsByPlayer
                && ReferenceEquals(data.Being?.Goal, goal)
                && goal.Target is DisposableComp disposable
                && ReferenceEquals(disposable.Being?.Data, data)
                && TryReadCorpseMarker(goal, out kind, out uint markedBeing)
                && markedBeing == data.Id
                && goal.TryGetVar(CorpseGoalMarker, out uint markedGoal)
                && markedGoal == goal.Id
                && corpseGoals.TryGetValue(data.Id, out CorpseRecord record)
                && CorpseDisposalRules.Matches(state.GameId, data.Id, goal.Id,
                    kind, record.GameId, record.BeingId, record.GoalId, record.Kind);
        }

        private bool CorpsePartsRemain(Body body) {
            if (body?.Slots == null || body.Slots.Length == 0) {
                return true;
            }
            foreach (BodyPartSlot slot in body.Slots) {
                if (slot == null || slot.Type == null) { return true; }
                if (slot.IsPartRemoved || slot.InstalledDef == null) { continue; }
                if (slot.State == null) { return true; }
                bool welded = (slot.State.Flags & BodyPartStateFlags.Welded) != 0;
                bool mind = slot.HoldsMind || slot.InstalledDef.HoldsMind
                    || IsProtectedCategoryId(slot.Type.Id)
                    || IsProtectedCategoryId(slot.InstalledDef.SlotTypeId);
                if (CorpseDisposalRules.SlotBlocks(slot.IsPartRemoved,
                        true, (slot.Type.Flags & BodyPartFlags.Removable) != 0,
                        welded, mind)) {
                    return true;
                }
            }
            return body.HasRemovableParts;
        }

        private bool CanProcessCorpse(BeingData data, AIGoal ownGoal,
                AIAgentComp worker, out CorpseDisposalKind kind,
                out DisposableComp disposable) {
            kind = data?.Species?.IsBiological == true
                ? CorpseDisposalKind.Biological : CorpseDisposalKind.Robotic;
            disposable = null;
            Being being = data?.Being;
            if (data?.Species == null || data.Body == null || being == null
                    || !ReferenceEquals(being.S, loadedState)
                    || !being.TryGetComponent(out disposable)) {
                return false;
            }
            bool biological = kind == CorpseDisposalKind.Biological;
            bool processor = disposable.IsBiological == biological
                && disposable.SlotDesignation == (biological
                    ? SlotDesignationH.Disposer_Organic
                    : SlotDesignationH.Disposer_Robotic)
                && (biological ? BuildProcessorContext().BiologicalCount > 0
                    : BuildProcessorContext().RoboticCount > 0);
            bool owns = ownGoal != null && OwnsCorpseGoal(loadedState,
                data, ownGoal, out CorpseDisposalKind recordedKind)
                && recordedKind == kind;
            bool exactWorker = owns && worker != null
                && ReferenceEquals(ownGoal.Agent, worker)
                && ReferenceEquals(worker.Goal, ownGoal);
            bool foreignCarrier = being.IsCarriedByAny
                && !(exactWorker && being.NanobotCarrierHiveId == 0
                    && ReferenceEquals(being.Carrier, worker.Being));
            var reserved = worker?.BB?.ReservedSlot;
            bool foreignReservation = reserved != null
                && (!exactWorker || (reserved.ReservedBy != null
                        && reserved.ReservedBy != worker.Being)
                    || !(reserved.Parent is DisposerComp)
                    || ((DisposerComp) reserved.Parent).SlotDesignation
                        != disposable.SlotDesignation);
            if (being.TryGetComponent<StorableObjComp>(out var storable)
                    && storable.IsForbidden) { return false; }
            bool unavailable = being.IsRemoved
                || (data.StateFlags & (BeingState.Hidden | BeingState.Expedition
                    | BeingState.Stored | BeingState.InDevice)) != 0
                || (data.Body.Flags & BodyFlags.Missing) != 0;
            return CorpseDisposalRules.Allows(CorpseRuntimeReady(loadedState),
                Enabled, biological ? RecycleBiologicalCorpses : RecycleRoboticCorpses,
                data.HasStateFlag(BeingState.Dead), being.IsAlive, data.Body.IsAlive,
                data.Species.IdH == SpeciesIdH.Crawler, being.IsActive, unavailable,
                CorpsePartsRemain(data.Body), S.Query.HasPendingSurgeries.Ask(data, null),
                ownGoal == null ? being.Goal != null : !owns,
                foreignCarrier, foreignReservation, processor);
        }

        private void ScanCorpses() {
            if ((!RecycleBiologicalCorpses && !RecycleRoboticCorpses)
                    || !CorpseRuntimeReady(loadedState)) { return; }
            foreach (BeingData data in S.Query.AllBeingData.Ask().Values) {
                if (!CanProcessCorpse(data, null, null, out var kind,
                        out var disposable)) { continue; }
                if (!disposable.TryOrderDispose(false, out _)) {
                    LastVanillaOrderRejected++;
                    continue;
                }
                AIGoal goal = data.Being.Goal;
                if (goal == null || !ReferenceEquals(goal.Target, disposable)
                        || goal.IsByPlayer) { continue; }
                goal.WithVar(CorpseMarker, (int) kind)
                    .WithVar(CorpseBeingMarker, data.Id)
                    .WithVar(CorpseGoalMarker, goal.Id);
                corpseGoals[data.Id] = new CorpseRecord {
                    GameId = S.GameId, BeingId = data.Id,
                    GoalId = goal.Id, Kind = kind
                };
                AttachCorpseAvailabilityGate(goal);
                LastQueued++;
                if (kind == CorpseDisposalKind.Biological) {
                    BiologicalTasksQueued++;
                    BiologicalCorpseTasksQueued++;
                } else {
                    RoboticTasksQueued++;
                    RoboticCorpseTasksQueued++;
                }
            }
        }

        internal static void AttachCorpseAvailabilityGate(AIGoal goal) {
            if (!IsCorpseWork(goal)) { return; }
            goal.AvailabilityGate = (state, current) => activeInstance != null
                && activeInstance.ValidateCorpseExecution(state, current, current.Agent);
        }

        internal bool ValidateCorpseExecution(GameState state, AIGoal goal,
                AIAgentComp worker) {
            try {
                if (!CorpseRuntimeReady(state)
                        || !(goal?.Target is DisposableComp disposable)
                        || disposable.Being?.Data == null) { return false; }
                return CanProcessCorpse(disposable.Being.Data, goal, worker,
                    out _, out _);
            } catch {
                // An unavailable/corrupt dependency cannot grant consumption.
                return false;
            }
        }

        private bool ReconcileCorpseGoals() {
            if (corpseGoals.Count == 0) { return false; }
            List<uint> finished = null;
            var beings = S.Query.AllBeingData.Ask();
            foreach (var pair in corpseGoals) {
                BeingData data = null;
                beings?.TryGetValue(pair.Key, out data);
                AIGoal goal = data?.Being?.Goal;
                if (goal == null || goal.Id != pair.Value.GoalId
                        || !ReferenceEquals((goal.Target as DisposableComp)?.Being?.Data, data)) {
                    (finished ??= new List<uint>()).Add(pair.Key);
                    continue;
                }
                if (!ValidateCorpseExecution(S, goal, goal.Agent)) {
                    // Clock reconciliation is outside Execute. Let Core deactivate
                    // its action, drop its carrier and release its reservations.
                    AIGoal.Cancel(goal, S.Ticks, CorpsePolicyDenied,
                        removeImmediately: false);
                    (finished ??= new List<uint>()).Add(pair.Key);
                }
            }
            if (finished == null) { return false; }
            foreach (uint id in finished) { corpseGoals.Remove(id); }
            return true;
        }

        private void SaveCorpseState(ComponentData data) {
            data.SetBool("RecycleBiologicalCorpses", RecycleBiologicalCorpses);
            data.SetBool("RecycleRoboticCorpses", RecycleRoboticCorpses);
            data.SetInt("BiologicalCorpseTasksQueued", BiologicalCorpseTasksQueued);
            data.SetInt("RoboticCorpseTasksQueued", RoboticCorpseTasksQueued);
            int[] ids = new int[corpseGoals.Count];
            int[] goals = new int[ids.Length];
            int[] kinds = new int[ids.Length];
            int index = 0;
            foreach (CorpseRecord record in corpseGoals.Values) {
                ids[index] = unchecked((int) record.BeingId);
                goals[index] = unchecked((int) record.GoalId);
                kinds[index++] = (int) record.Kind;
            }
            data.SetIntSet("CorpseBeingIds", ids);
            data.SetIntSet("CorpseGoalIds", goals);
            data.SetIntSet("CorpseKinds", kinds);
        }

        private void LoadCorpseState(ComponentData data) {
            if (data.GetInt("SaveSchemaVersion", 0) > SaveSchemaVersion) {
                corpseGoals.Clear();
                RecycleBiologicalCorpses = false;
                RecycleRoboticCorpses = false;
                return;
            }
            RecycleBiologicalCorpses = data.GetBool("RecycleBiologicalCorpses", false);
            RecycleRoboticCorpses = data.GetBool("RecycleRoboticCorpses", false);
            BiologicalCorpseTasksQueued = Math.Max(0, data.GetInt("BiologicalCorpseTasksQueued", 0));
            RoboticCorpseTasksQueued = Math.Max(0, data.GetInt("RoboticCorpseTasksQueued", 0));
            corpseGoals.Clear();
            int[] ids = data.GetIntSet("CorpseBeingIds", null);
            int[] goals = data.GetIntSet("CorpseGoalIds", null);
            int[] kinds = data.GetIntSet("CorpseKinds", null);
            if (ids == null && goals == null && kinds == null) {
                corpseSettingsLoaded = true;
                return;
            }
            if (ids == null || goals == null || kinds == null
                    || ids.Length != goals.Length || ids.Length != kinds.Length) {
                D.Warn("[Intelligent Recycling] Invalid corpse task persistence; corpse automation remains blocked.");
                return;
            }
            for (int i = 0; i < ids.Length; i++) {
                uint id = unchecked((uint) ids[i]);
                uint goal = unchecked((uint) goals[i]);
                if (id == 0 || goal == 0 || kinds[i] < 1 || kinds[i] > 2
                        || corpseGoals.ContainsKey(id)) { return; }
                corpseGoals.Add(id, new CorpseRecord {
                    GameId = S.GameId, BeingId = id, GoalId = goal,
                    Kind = (CorpseDisposalKind) kinds[i]
                });
            }
            corpseSettingsLoaded = true;
        }

        private IntelligentRecyclingSpecialTargetSnapshot BuildCorpseSpecialSnapshot(
                BeingData data, ProcessorContext processors) {
            if (data?.Species == null || !data.HasStateFlag(BeingState.Dead)) { return null; }
            bool biological = data.Species.IsBiological;
            bool processor = biological ? processors.BiologicalCount > 0
                : processors.RoboticCount > 0;
            bool ready = CanProcessCorpse(data, null, null, out _, out var disposable)
                && disposable.CanOrderDispose(DisposeCheck.BulkOrder);
            return new IntelligentRecyclingSpecialTargetSnapshot(data.Id,
                biological ? IntelligentRecyclingTargetType.BiologicalCorpse
                    : IntelligentRecyclingTargetType.RoboticCorpse,
                data.Species.Id, data.Species.Name, true,
                biological ? RecycleBiologicalCorpses : RecycleRoboticCorpses,
                data.Being?.IsActive == true, ready, true, processor,
                false, false, false, false, false,
                data.HasStateFlag(BeingState.Alive), data.Body?.IsIncapacitated == true,
                true, ready ? "Harvested corpse is ready for native disposal."
                    : "Corpse disposal is disabled, already assigned, or blocked by safety requirements.");
        }
    }

    // These safety patches deliberately outlive the UI/system registration.
    // Marked saved goals must fail closed even when AUMF rejects initialization.
    internal static class CorpseDisposalGuards {
        internal static bool Ready { get; private set; }
        private static bool attempted;
        [ThreadStatic] private static Scope current;
        internal sealed class Scope {
            internal Scope Previous;
            internal AIGoal Goal;
            internal AIAgentComp Agent;
            internal bool Denied;
            internal bool Selection;
            internal string DenialReason => Selection
                ? IntelligentRecyclingSys.SelectionPolicyDenied : IntelligentRecyclingSys.CorpsePolicyDenied;
        }

        internal static void Install() {
            if (attempted) { return; }
            attempted = true;
            var guard = new Harmony("IntelligentRecycling.CorpseSafety");
            bool execute = InstallPatch(guard, AccessTools.Method(typeof(AIActionCarryToDisposer),
                nameof(AIActionCarryToDisposer.Execute), new[] { typeof(GameState),
                    typeof(AIAgentComp), typeof(AIGoal), typeof(long) }),
                nameof(BeforeExecute), nameof(AfterExecute), nameof(FinishExecute));
            bool load = InstallPatch(guard, AccessTools.Method(typeof(DisposerComp),
                nameof(DisposerComp.LoadDisposable), new[] { typeof(DisposableComp), typeof(AIAgentComp) }),
                nameof(BeforeLoad), null, null);
            bool restore = InstallPatch(guard, AccessTools.Method(typeof(DisposableComp),
                nameof(DisposableComp.OnGoalLoad), new[] { typeof(AIGoal) }),
                null, nameof(AfterGoalLoad), null);
            Ready = execute && load && restore;
            if (!Ready) {
                D.Warn("[Intelligent Recycling] Disposal safety hooks incomplete; guarded corpse, egg, and weapon override jobs are blocked.");
            }
        }

        private static bool InstallPatch(Harmony harmony, System.Reflection.MethodInfo target,
                string prefix, string postfix, string finalizer) {
            try {
                if (target == null) { return false; }
                harmony.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(CorpseDisposalGuards), prefix),
                    postfix == null ? null : new HarmonyMethod(typeof(CorpseDisposalGuards), postfix),
                    null,
                    finalizer == null ? null : new HarmonyMethod(typeof(CorpseDisposalGuards), finalizer));
                return true;
            } catch (Exception ex) {
                D.LogEx(ex, "[Intelligent Recycling] Could not install corpse safety hook.");
                return false;
            }
        }

        private static bool BeforeExecute(GameState S, AIAgentComp agent,
                AIGoal goal, ref AIActionResult __result, out Scope __state) {
            __state = null;
            bool selection = IntelligentRecyclingSys.IsSelectionWork(goal);
            if (!selection && !IntelligentRecyclingSys.IsCorpseWork(goal)) { return true; }
            __state = new Scope { Previous = current, Goal = goal, Agent = agent,
                Selection = selection };
            current = __state;
            if (ValidateExecution(S, goal, agent, selection)) {
                return true;
            }
            __state.Denied = true;
            __result = AIActionResult.Create(AIActionState.Cancelled, __state.DenialReason);
            return false;
        }

        private static void AfterExecute(ref AIActionResult __result, Scope __state) {
            if (__state?.Denied == true) {
                __result = AIActionResult.Create(AIActionState.Cancelled, __state.DenialReason);
            }
        }

        private static Exception FinishExecute(Exception __exception, Scope __state) {
            if (__state != null) { current = __state.Previous; }
            return __exception;
        }

        private static bool BeforeLoad(DisposerComp __instance, DisposableComp disposable,
                AIAgentComp agent, ref bool __result) {
            bool scopedTarget = current != null && ReferenceEquals(current.Goal.Target, disposable);
            AIGoal goal = scopedTarget ? current.Goal : disposable?.Entity?.Goal;
            bool selection = scopedTarget ? current.Selection : IntelligentRecyclingSys.IsSelectionWork(goal);
            if (scopedTarget && selection && IntelligentRecyclingSys.IsNativeSelectionTakeover(goal)
                    && ReferenceEquals(disposable.Entity.Goal, goal)
                    && ReferenceEquals(current.Agent, agent)) {
                return true;
            }
            if (!scopedTarget && !selection && !IntelligentRecyclingSys.IsCorpseWork(goal)) { return true; }
            bool allowed = current != null && ReferenceEquals(current.Goal, goal)
                && ReferenceEquals(current.Agent, agent)
                && ReferenceEquals(goal.Target, disposable)
                && ReferenceEquals(agent?.BB?.ReservedSlot?.Parent, __instance)
                && __instance.Tile != null && __instance.Tile.IsActive
                && __instance.Tile.IsConstructed
                && ReferenceEquals(__instance.S, disposable.S)
                && ValidateExecution(disposable.S, goal, agent, selection);
            if (allowed) { return true; }
            if (current != null && ReferenceEquals(current.Goal, goal)
                    && ReferenceEquals(current.Agent, agent)) {
                current.Denied = true;
            }
            __result = false;
            return false;
        }

        private static void AfterGoalLoad(AIGoal goal) {
            if (IntelligentRecyclingSys.IsSelectionWork(goal)) {
                IntelligentRecyclingSys.AttachSelectionAvailabilityGate(goal);
            } else {
                IntelligentRecyclingSys.AttachCorpseAvailabilityGate(goal);
            }
        }

        private static bool ValidateExecution(GameState state, AIGoal goal,
                AIAgentComp agent, bool selection) {
            var system = IntelligentRecyclingSys.ApiInstance;
            return system != null && (selection
                ? system.ValidateSelectionExecution(state, goal, agent)
                : system.ValidateCorpseExecution(state, goal, agent));
        }
    }
}
