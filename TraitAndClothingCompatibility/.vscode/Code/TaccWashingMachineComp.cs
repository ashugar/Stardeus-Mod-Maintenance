using Game.CodeGen;
using Game.Components;
using Game.Components.Upgrades;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Items;
using Game.Utils;
using KL.Randomness;
using KL.Utils;
using UnityEngine;

namespace TraitAndClothingCompatibility {
    public sealed class TaccWashingMachineComp
            : BaseComponent<TaccWashingMachineComp>, IAIGoalProvider,
                ILoadsObj, IRelocatable, ISpeedUpgradeable,
                IEnergyEfficiencyUpgradeable {
        private static readonly int EnergyCostKey = Hashes.S("EnergyCost");
        private static readonly int WashDurationKey = Hashes.S("WashDuration");
        private static readonly int GoalPriorityKey = Hashes.S("GoalPriority");

        private ElectricNodeComp electricNode;
        private ObjData input;
        private uint inputId;
        private bool inputLoaded;
        private float energyCost;
        private float washProgressPerGridTick;
        private float washProgress;
        private int goalPriority;
        private bool relocated;
        private bool automaticSearchFoundNoItems;
        private float upgradeSpeed = 1f;
        private float upgradeEnergyEfficiency = 1f;

        public int LoadObjSpotIdx => Tile.Transform.InPort;
        public uint ReservedObjId => inputId;
        public uint InputId => inputId;
        public bool IsInputLoaded => inputLoaded;
        public ObjData InputData => Input;
        public float WashProgress => washProgress;
        public bool AutomaticSearchFoundNoItems => automaticSearchFoundNoItems;
        public bool HasElectricNode => electricNode != null;
        public bool IsElectricNodeSwitchedOn => electricNode?.IsSwitchedOn == true;
        public bool IsElectricNodeReachable => electricNode?.IsReachable == true;
        public bool IsElectricNodePowered => electricNode?.IsPowered == true;
        public bool HasElectricDeficit => electricNode?.HasDeficit == true;
        public bool IsSpeedUpgradeable => true;
        public bool IsEnergyEfficiencyUpgradeable => true;

        public bool CanAcceptItem {
            get {
                if (inputId != 0u || HasAnyGoal || Tile == null
                        || !Tile.IsActive || !Tile.IsConstructed) return false;
                return Tile.Health == null || !Tile.Health.IsHeavilyDamaged;
            }
        }

        public bool CanAutomaticallyAcceptItem =>
            TaccSystem.Instance?.State?.AutomaticWashing == true
            && CanAcceptItem
            && electricNode != null
            && electricNode.IsSwitchedOn
            && electricNode.IsReachable
            && electricNode.IsPowered
            && !electricNode.HasDeficit;

        private ObjData Input {
            get {
                if (input == null && inputId != 0u) {
                    input = S.Query.FindObjData.Ask(inputId);
                    if (input == null) {
                        D.Err("[TACC] Washing Machine input {0} is missing at {1}.",
                            inputId, Tile);
                        inputId = 0u;
                        inputLoaded = false;
                        washProgress = 0f;
                    }
                }
                return input;
            }
            set {
                input = value;
                inputId = value?.Id ?? 0u;
            }
        }

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register() {
            AddComponentPrototype(new TaccWashingMachineComp());
        }

        protected override void OnConfig() {
            energyCost = Config.GetFloat(EnergyCostKey, 5f);
            float duration = Mathf.Max(10f,
                Config.GetFloat(WashDurationKey, 60f));
            washProgressPerGridTick = 10f / duration;
            goalPriority = Config.GetInt(GoalPriorityKey, 4);
        }

        public override void OnLateReady(bool wasLoaded) {
            Tile.Transform.EnsurePortPresence(
                requireWorkSpot: false,
                requireInPort: true,
                requireOutPort: true,
                this);
            WashingMachineSys.RequestStoredItemSearch(S);
        }

        internal void RestoreLoadedInputState() {
            if (!inputLoaded || inputId == 0u) return;
            ObjData item = Input;
            if (item == null) return;
            item.State = ObjState.InDevice;
            item.DeviceId = Tile.Id;
        }

        public override void Receive(IComponent sender, int message) {
            if (message == 672382408 && sender is ElectricNodeComp node) {
                electricNode = node;
                node.AddAfterTick(this, AfterTickGrid);
                WashingMachineSys.RequestStoredItemSearch(S);
            }
        }

        public override void OnSave() {
            ComponentData data = GetOrCreateData();
            data.SetUInt("InputId", inputId);
            data.SetBool("InputLoaded", inputLoaded);
            data.SetFloat("WashProgress", washProgress);
        }

        protected override void OnLoad(ComponentData data) {
            inputId = data.GetUInt("InputId", 0u);
            inputLoaded = data.GetBool("InputLoaded", false);
            washProgress = Mathf.Clamp01(data.GetFloat("WashProgress", 0f));
            automaticSearchFoundNoItems = false;
        }

        internal void SetAutomaticSearchFoundNoItems(bool empty) {
            if (automaticSearchFoundNoItems == empty) return;
            automaticSearchFoundNoItems = empty;
            OnChange.Invoke();
        }

        internal bool TryOrderWash(
                ObjData item, bool byPlayer, out string reason) {
            reason = null;
            if (!CanAcceptItem) {
                reason = "This Washing Machine is already busy or unavailable.";
                return false;
            }
            if (!WashingPolicy.IsWashable(item)) {
                reason = "This item is not corpse-origin clothing that can be washed.";
                return false;
            }
            if (item.Reservation != null
                    || item.Obj?.Goal != null
                    || !item.IsAvailableIgnoreReservation
                    || WashingPolicy.IsOwned(S, item.Id)) {
                reason = "This clothing item is already reserved or unavailable.";
                return false;
            }

            AIGoal goal = CreateGoal(
                    105668070,
                    IconId.CClothing,
                    "Wash: " + item.Def.NameT,
                    this)
                .WithVar("ObjId", item.Id)
                .ByPlayer(byPlayer)
                .WithPriority(goalPriority)
                .WithRequiredAbilities(961424176, 658376509)
                .WithWorldToDesired(-1707724012, false, true);
            if (!item.TryReserve(goal)) {
                reason = "The clothing item could not be reserved for washing.";
                return false;
            }
            if (!Entity.TrySetGoal(goal)) {
                item.TryUnreserve(goal);
                reason = "Stardeus rejected the washing haul task.";
                return false;
            }

            Input = item;
            inputLoaded = false;
            washProgress = 0f;
            automaticSearchFoundNoItems = false;
            ShowInfoIcon(IconId.CClothing, "Wash: " + item.Def.NameT);
            OnChange.Invoke();
            return true;
        }

        public bool LoadObj(ObjData item, AIGoal goal) {
            if (item == null || item.Id != inputId
                    || goal?.Target != this) {
                D.Err("[TACC] Washing Machine rejected unexpected input. "
                    + "Expected {0}, received {1} at {2}.",
                    inputId, item?.Id ?? 0u, Tile);
                return false;
            }

            Input = item;
            inputLoaded = true;
            washProgress = 0f;
            item.State = ObjState.InDevice;
            item.DeviceId = Tile.Id;
            if (item.Obj != null) S.Objs.Destroy(item.Obj);
            OnChange?.Invoke();
            return true;
        }

        private void AfterTickGrid() {
            if (inputId == 0u) {
                electricNode?.SetConsumerWantedInput(0f);
                HideInfoBar();
                return;
            }
            if (!inputLoaded) {
                electricNode?.SetConsumerWantedInput(0f);
                if (!HasGoal) AbortPending(
                    "The washing haul task ended before loading.");
                return;
            }

            ObjData item = Input;
            if (!WashingPolicy.IsWashable(item)) {
                FinishWash(false,
                    "The loaded object is no longer eligible for washing.");
                return;
            }
            if (electricNode == null || !IsUsable) {
                electricNode?.SetConsumerWantedInput(0f);
                return;
            }

            electricNode.SetConsumerWantedInput(
                WashingRules.EffectiveEnergyCost(
                    energyCost, upgradeEnergyEfficiency));
            if (electricNode.HasDeficit || !electricNode.IsSwitchedOn
                    || !electricNode.IsConsuming) return;

            washProgress = Mathf.Clamp01(
                washProgress + WashingRules.EffectiveProgressPerTick(
                    washProgressPerGridTick, upgradeSpeed));
            ShowInfoBar(Consts.ColorGreenBar, washProgress,
                "Washing " + item.Def.NameT + " "
                    + Units.Percentage(washProgress));
            OnChange?.Invoke();
            if (washProgress >= 1f) FinishWash(true, null);
        }

        private void AbortPending(string reason) {
            ObjData item = Input;
            if (item != null) item.TryUnreserve(null);
            Input = null;
            inputLoaded = false;
            washProgress = 0f;
            electricNode?.SetConsumerWantedInput(0f);
            HideAllInfo();
            if (!string.IsNullOrWhiteSpace(reason))
                D.Warn("[TACC] {0} Machine: {1}", reason, Tile);
            OnChange.Invoke();
        }

        private void FinishWash(bool applyWash, string note) {
            ObjData item = Input;
            Input = null;
            inputLoaded = false;
            washProgress = 0f;
            automaticSearchFoundNoItems = false;
            electricNode?.SetConsumerWantedInput(0f);
            HideAllInfo();
            if (item == null) return;

            item.TryUnreserve(null);
            if (applyWash) WashingPolicy.ApplyWash(item);
            item.State = ObjState.Active;
            S.Objs.Create(
                EntityUtils.CenterOf(Tile.Transform.OutPort)
                    + Rng.UInsideUnitCircle() * 0.25f,
                item);
            if (!string.IsNullOrWhiteSpace(note))
                D.Warn("[TACC] {0} Item: {1}", note, item);
            OnChange?.Invoke();
            WashingMachineSys.RequestStoredItemSearch(S);
        }

        public override void OnRemove() {
            if (relocated) return;
            if (inputLoaded) {
                FinishWash(false,
                    "The Washing Machine was removed before washing completed.");
            } else {
                if (TryGetGoal(out AIGoal goal)) AIGoal.Cancel(goal, G.Ticks);
                AbortPending(null);
            }
        }

        public void RelocateTo(Entity target) {
            if (!target.TryGetComponent(
                    out TaccWashingMachineComp destination)) return;
            destination.automaticSearchFoundNoItems =
                automaticSearchFoundNoItems;
            if (!inputLoaded) {
                if (TryGetGoal(out AIGoal goal)) AIGoal.Cancel(goal, G.Ticks);
                AbortPending(null);
                WashingMachineSys.RequestStoredItemSearch(S);
                return;
            }

            destination.input = input;
            destination.inputId = inputId;
            destination.inputLoaded = true;
            destination.washProgress = washProgress;
            if (destination.input != null)
                destination.input.DeviceId = destination.Tile.Id;
            relocated = true;
            input = null;
            inputId = 0u;
            inputLoaded = false;
            washProgress = 0f;
            WashingMachineSys.RequestStoredItemSearch(S);
        }

        public void OnGoalChange(
                AIGoal goal, AIGoalState from, AIGoalState to) {
            if (!inputLoaded && AIGoal.IsArchived(to)) {
                AbortPending(AIGoal.IsCancelled(to)
                    ? "The washing haul task was cancelled."
                    : "The washing haul task ended before loading.");
            }
        }

        public void OnGoalLoad(AIGoal goal) {
            if (inputId == 0u && goal != null
                    && goal.TryGetVar("ObjId", out uint objectId))
                inputId = objectId;
        }

        public void ApplySpeedUpgrade(float amount) {
            upgradeSpeed += amount;
            if (upgradeSpeed < 1f) {
                D.Err("[TACC] Washing Machine speed multiplier < 1 after applying {0}: {1}",
                    amount, upgradeSpeed);
                upgradeSpeed = 1f;
            }
            OnChange?.Invoke();
        }

        public void ApplyEfficiencyUpgrade(float amount) {
            upgradeEnergyEfficiency += amount;
            if (upgradeEnergyEfficiency < 1f) {
                D.Err("[TACC] Washing Machine efficiency multiplier < 1 after applying {0}: {1}",
                    amount, upgradeEnergyEfficiency);
                upgradeEnergyEfficiency = 1f;
            }
            OnChange?.Invoke();
        }

        public override string ToString() => " * TACC Washing Machine";
    }
}
