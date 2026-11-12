using System;
using System.Collections.Generic;
using Game.CodeGen;
using Game.Components;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Items;

namespace Game.Systems.IntelligentRecycling {
    public sealed partial class IntelligentRecyclingSys {
        private const string SelectionGoalKey = "IntelligentRecycling.ObjectSelection";
        private const string SelectionObjectKey = "IntelligentRecycling.ObjectSelectionId";
        private const int EggSelectionGoal = 1;
        private const int WeaponOverrideGoal = 2;
        internal const string SelectionPolicyDenied =
            "Intelligent Recycling settings no longer permit this disposal";
        private bool selectionSettingsLoaded;
        private readonly HashSet<string> selectedWeaponDefinitionIds =
            new HashSet<string>(StringComparer.Ordinal);

        public bool RecycleAlienEggs { get; private set; }
        public bool AllowSecondHandEquipment { get; private set; } = true;
        public bool AllowDeadMansEquipment { get; private set; } = true;
        public bool FilterWeaponDefinitions { get; private set; }
        public bool WeaponsFollowQuality { get; private set; } = true;
        public int AlienEggTasksQueued { get; private set; }

        internal void SetAlienEggs(bool value) {
            if (RecycleAlienEggs == value) { return; }
            RecycleAlienEggs = value;
            MarkAlienEggReminderDirty();
            RequestScan();
            panel?.Refresh();
        }

        internal void SetAllowSecondHandEquipment(bool value) {
            if (AllowSecondHandEquipment == value) { return; }
            AllowSecondHandEquipment = value;
            RequestScan();
            panel?.Refresh();
        }

        internal void SetAllowDeadMansEquipment(bool value) {
            if (AllowDeadMansEquipment == value) { return; }
            AllowDeadMansEquipment = value;
            RequestScan();
            panel?.Refresh();
        }

        internal void SetFilterWeaponDefinitions(bool value) {
            if (FilterWeaponDefinitions == value) { return; }
            FilterWeaponDefinitions = value;
            RequestScan();
            panel?.Refresh();
        }

        internal void SetWeaponsFollowQuality(bool value) {
            if (WeaponsFollowQuality == value) { return; }
            WeaponsFollowQuality = value;
            RequestScan();
            panel?.Refresh();
        }

        private static bool IsWeaponSlot(InventoryItemDef item) =>
            string.Equals(item?.SlotId, "Weapon", StringComparison.Ordinal);

        internal List<KeyValuePair<string, string>> GetWeaponDefinitions() {
            var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (InventoryItemDef item in InventoryItemDef.All.Values) {
                if (IsWeaponSlot(item) && !string.IsNullOrEmpty(item.Def?.Id)) {
                    definitions[item.Def.Id] = item.Def.NameT ?? item.Def.Id;
                }
            }
            return SortCategories(definitions);
        }

        internal bool IsWeaponDefinitionSelected(string definitionId) =>
            definitionId != null && selectedWeaponDefinitionIds.Contains(definitionId);

        internal void SetWeaponDefinitionSelected(string definitionId, bool selected) {
            if (string.IsNullOrEmpty(definitionId)) { return; }
            bool changed = selected ? selectedWeaponDefinitionIds.Add(definitionId)
                : selectedWeaponDefinitionIds.Remove(definitionId);
            // Do not rebuild the Settings panel while a checklist row is clicked.
            if (changed) { RequestScan(); }
        }

        private static IntelligentRecyclingEquipmentOrigin EquipmentOrigin(ObjData data) {
            switch (ItemOwnership.BucketFor(data)) {
                case ItemOriginBucket.New: return IntelligentRecyclingEquipmentOrigin.New;
                case ItemOriginBucket.Untracked: return IntelligentRecyclingEquipmentOrigin.Untracked;
                case ItemOriginBucket.SecondHand: return IntelligentRecyclingEquipmentOrigin.SecondHand;
                case ItemOriginBucket.DeadMans: return IntelligentRecyclingEquipmentOrigin.DeadMans;
                default: return IntelligentRecyclingEquipmentOrigin.Unknown;
            }
        }

        private bool IsAlienEggReady(Obj obj, DisposableComp disposable) =>
            obj != null && !obj.IsRemoved && obj.TryGetComponent<EggComp>(out _)
            && disposable?.IsBiological == true
            && disposable.SlotDesignation == SlotDesignationH.Disposer_Organic
            && SelectionRuntimeReady(loadedState)
            && !IsAlienEggReminderBlocking;

        private bool SelectionRuntimeReady(GameState state) =>
            state != null && CorpseDisposalGuards.Ready && corpseInitializationReady
            && selectionSettingsLoaded && ReferenceEquals(state, loadedState)
            && loadedSettingsGameId == state.GameId;

        private static bool HasSelectionMarker(AIGoal goal) =>
            HasGoalMarker(goal, SelectionGoalKey) || HasGoalMarker(goal, SelectionObjectKey);

        internal static bool IsNativeSelectionTakeover(AIGoal goal) =>
            goal?.IsByPlayer == true || goal?.IsForceAssigned == true;

        internal static bool IsSelectionWork(AIGoal goal) =>
            !IsNativeSelectionTakeover(goal)
            && (HasSelectionMarker(goal) || activeInstance?.IsRegisteredEggGoal(goal) == true);

        private bool IsRegisteredEggGoal(AIGoal goal) {
            ObjData data = (goal?.Target as DisposableComp)?.Obj?.Data;
            return data != null && RecyclingSelectionRules.IsAlienEgg(data.Def?.Id)
                && IsRegisteredSelectionGoal(data, goal);
        }

        private bool IsRegisteredSelectionGoal(ObjData data, AIGoal goal) =>
            goal != null && !goal.IsByPlayer && ReferenceEquals(data.Obj?.Goal, goal)
            && ReferenceEquals(goal.Target?.Entity, data.Obj)
            && trackedGoalsByObjectId.TryGetValue(data.Id, out uint goalId) && goalId == goal.Id;

        private void MarkSelectionGoal(AIGoal goal, ObjData data,
                IntelligentRecyclingObjectSnapshotData snapshot) {
            int mode = snapshot.TargetType == IntelligentRecyclingTargetType.AlienEgg
                ? EggSelectionGoal : snapshot.QualityCutoffBypassed ? WeaponOverrideGoal : 0;
            if (mode == 0 || IsNativeSelectionTakeover(goal)) { return; }
            goal.WithVar(SelectionGoalKey, mode).WithVar(SelectionObjectKey, data.Id);
            AttachSelectionAvailabilityGate(goal);
        }

        internal static void AttachSelectionAvailabilityGate(AIGoal goal) {
            if (!IsSelectionWork(goal)) { return; }
            goal.AvailabilityGate = (state, current) => IsNativeSelectionTakeover(current)
                || (activeInstance != null
                    && activeInstance.ValidateSelectionExecution(state, current, current.Agent));
        }

        internal bool ValidateSelectionExecution(GameState state, AIGoal goal, AIAgentComp worker) {
            try {
                if (!SelectionRuntimeReady(state) || !(goal?.Target is DisposableComp disposable)
                        || disposable.Obj?.Data == null || !ReferenceEquals(disposable.S, state)) {
                    return false;
                }
                ObjData data = disposable.Obj.Data;
                if (!IsRegisteredSelectionGoal(data, goal)
                        || !goal.TryGetVar(SelectionGoalKey, out int mode)
                        || !goal.TryGetVar(SelectionObjectKey, out uint objectId)
                        || objectId != data.Id
                        || (mode != EggSelectionGoal && mode != WeaponOverrideGoal)) {
                    return false;
                }
                var evaluation = EvaluateObject(data, BuildProcessorContext());
                var snapshot = evaluation.Snapshot;
                bool targetMatches = mode == EggSelectionGoal
                    ? snapshot.TargetType == IntelligentRecyclingTargetType.AlienEgg
                    : snapshot.TargetType == IntelligentRecyclingTargetType.Equipment && snapshot.IsWeapon;
                bool exactWorker = worker != null && ReferenceEquals(goal.Agent, worker)
                    && ReferenceEquals(worker.Goal, goal);
                bool foreignCarrier = data.Obj.IsCarriedByAny
                    && (!exactWorker || data.Obj.NanobotCarrierHiveId != 0
                        || !ReferenceEquals(data.Obj.Carrier, worker.Being));
                var reservedSlot = worker?.BB?.ReservedSlot;
                bool foreignSlot = reservedSlot != null
                    && (!exactWorker || (reservedSlot.ReservedBy != null
                            && !ReferenceEquals(reservedSlot.ReservedBy, worker.Being))
                        || !(reservedSlot.Parent is DisposerComp disposer)
                        || disposer.SlotDesignation != disposable.SlotDesignation);
                return targetMatches && ReferenceEquals(evaluation.Disposable, disposable)
                    && snapshot.Active && !data.Obj.IsRemoved && !snapshot.Stored && !snapshot.InDevice
                    && !snapshot.Forbidden && !foreignCarrier && !foreignSlot
                    && (data.Reservation == null || ReferenceEquals(data.Reservation.Goal, goal))
                    && snapshot.IntelligentRecyclingWouldClaimUnderPolicy;
            } catch {
                // Incomplete restoration or unavailable native state cannot authorize consumption.
                return false;
            }
        }

        private void ApplyEquipmentSelection(ObjData data,
                IntelligentRecyclingObjectSnapshotData snapshot) {
            snapshot.EquipmentOrigin = EquipmentOrigin(data);
            snapshot.EquipmentOriginAllowed = RecyclingSelectionRules.AllowsOrigin(
                snapshot.EquipmentOrigin, AllowSecondHandEquipment, AllowDeadMansEquipment);
            snapshot.IsWeapon = IsWeaponSlot(data.Item);
            snapshot.WeaponDefinitionSelected = snapshot.IsWeapon
                && IsWeaponDefinitionSelected(data.Def?.Id);
            snapshot.WeaponDefinitionAllowed = RecyclingSelectionRules.AllowsWeapon(
                snapshot.IsWeapon, FilterWeaponDefinitions, snapshot.WeaponDefinitionSelected);
            snapshot.QualityCutoffBypassed = RecyclingSelectionRules.BypassesWeaponQuality(
                snapshot.IsWeapon, FilterWeaponDefinitions, snapshot.WeaponDefinitionSelected,
                WeaponsFollowQuality);
        }

        private void SaveRecyclingSelection(ComponentData data) {
            data.SetBool("RecycleAlienEggs", RecycleAlienEggs);
            data.SetBool("AllowSecondHandEquipment", AllowSecondHandEquipment);
            data.SetBool("AllowDeadMansEquipment", AllowDeadMansEquipment);
            data.SetBool("FilterWeaponDefinitions", FilterWeaponDefinitions);
            data.SetBool("WeaponsFollowQuality", WeaponsFollowQuality);
            data.SetInt("AlienEggTasksQueued", AlienEggTasksQueued);
            string[] selected = new string[selectedWeaponDefinitionIds.Count];
            selectedWeaponDefinitionIds.CopyTo(selected);
            Array.Sort(selected, StringComparer.Ordinal);
            data.SetStringSet("SelectedWeaponDefinitionIds", selected);
        }

        private void LoadRecyclingSelection(ComponentData data) {
            ResetRecyclingSelection();
            if (data.GetInt("SaveSchemaVersion", 0) > SaveSchemaVersion) { return; }
            RecycleAlienEggs = data.GetBool("RecycleAlienEggs", false);
            AllowSecondHandEquipment = data.GetBool("AllowSecondHandEquipment", true);
            AllowDeadMansEquipment = data.GetBool("AllowDeadMansEquipment", true);
            FilterWeaponDefinitions = data.GetBool("FilterWeaponDefinitions", false);
            WeaponsFollowQuality = data.GetBool("WeaponsFollowQuality", true);
            AlienEggTasksQueued = Math.Max(0, data.GetInt("AlienEggTasksQueued", 0));
            var selected = data.GetStringSet("SelectedWeaponDefinitionIds", null);
            if (selected == null) { return; }
            foreach (string id in selected) {
                if (!string.IsNullOrEmpty(id)) { selectedWeaponDefinitionIds.Add(id); }
            }
        }

        private void ResetRecyclingSelection() {
            RecycleAlienEggs = false;
            AllowSecondHandEquipment = true;
            AllowDeadMansEquipment = true;
            FilterWeaponDefinitions = false;
            WeaponsFollowQuality = true;
            AlienEggTasksQueued = 0;
            selectedWeaponDefinitionIds.Clear();
        }

        private void OnRecyclingSelectionTickEarly(long ticks) {
            if (loadedState == null || trackedGoalsByObjectId.Count == 0) { return; }
            Dictionary<uint, ObjData> objects = S.Query.GetAllObjs.Ask();
            if (objects == null) { return; }
            List<AIGoal> denied = null;
            foreach (var tracked in trackedGoalsByObjectId) {
                if (!objects.TryGetValue(tracked.Key, out ObjData data)
                        || data?.Obj?.Goal == null) { continue; }
                AIGoal goal = data.Obj.Goal;
                if (goal.Id != tracked.Value || IsNativeSelectionTakeover(goal)
                        || AIGoal.IsCompleted(goal) || AIGoal.IsCancelled(goal)
                        || !(goal.Target is DisposableComp disposable)
                        || !ReferenceEquals(disposable.Entity, data.Obj)) { continue; }

                if (RecyclingSelectionRules.IsAlienEgg(data.Def?.Id) || HasSelectionMarker(goal)) {
                    if (!ValidateSelectionExecution(loadedState, goal, goal.Agent)) {
                        (denied ??= new List<AIGoal>()).Add(goal);
                    }
                    continue;
                }

                // For previously queued equipment, enforce the new selection
                // gates only. Do not change unrelated legacy cancellation rules.
                if (data.Item != null && data.BodyPart == null
                        && (!RecyclingSelectionRules.AllowsOrigin(EquipmentOrigin(data),
                            AllowSecondHandEquipment, AllowDeadMansEquipment)
                            || !RecyclingSelectionRules.AllowsWeapon(IsWeaponSlot(data.Item),
                                FilterWeaponDefinitions, IsWeaponDefinitionSelected(data.Def?.Id)))) {
                    (denied ??= new List<AIGoal>()).Add(goal);
                }
            }
            if (denied == null) { return; }
            foreach (AIGoal goal in denied) {
                if (!IsNativeSelectionTakeover(goal) && ReferenceEquals(goal.Target?.Entity?.Goal, goal)) {
                    AIGoal.Cancel(goal, ticks, SelectionPolicyDenied, removeImmediately: false);
                }
            }
        }
    }
}
