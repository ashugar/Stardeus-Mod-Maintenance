using Game.CodeGen;
using Game.Components;
using Game.Components.Upgrades;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Items;
using Game.Utils;
using KL.Randomness;
using KL.Utils;

namespace Game.Systems.PartRestoration {
    public sealed class PartRestorationUnitComp
            : BaseComponent<PartRestorationUnitComp>, IAIGoalProvider,
                ILoadsObj, IRelocatable, ISpeedUpgradeable,
                IEnergyEfficiencyUpgradeable {
        private static readonly int EnergyCostKey = Hashes.S("EnergyCost");
        private static readonly int RepairRateKey = Hashes.S("RepairRate");
        private static readonly int GoalPriorityKey = Hashes.S("GoalPriority");

        private ElectricNodeComp electricNode;
        private ObjData input;
        private uint inputId;
        private bool inputLoaded;
        private float energyCost;
        private float repairRatePerGridTick;
        private int goalPriority;
        private bool relocated;
        private bool automaticIntakeEnabled = true;
        private PartRestorationIntakeFilter automaticPartFilter =
            PartRestorationIntakeFilter.Both;
        private bool automaticSearchFoundNoParts;
        private int lastOverviewConditionPercent = -1;
        private float upgradeSpeed = 1f;
        private float upgradeEnergyEfficiency = 1f;

        public int LoadObjSpotIdx => Tile.Transform.InPort;
        public uint ReservedObjId => inputId;
        public uint InputId => inputId;
        public bool IsInputLoaded => inputLoaded;
        public ObjData InputData => Input;
        public bool AutomaticIntakeEnabled => automaticIntakeEnabled;
        public PartRestorationIntakeFilter AutomaticPartFilter =>
            automaticPartFilter;
        public bool AutomaticSearchFoundNoParts =>
            automaticSearchFoundNoParts;
        public bool HasElectricNode => electricNode != null;
        public bool IsElectricNodeSwitchedOn =>
            electricNode?.IsSwitchedOn == true;
        public bool IsElectricNodeReachable =>
            electricNode?.IsReachable == true;
        public bool IsElectricNodePowered =>
            electricNode?.IsPowered == true;
        public bool HasElectricDeficit =>
            electricNode?.HasDeficit == true;
        public bool IsSpeedUpgradeable => true;
        public bool IsEnergyEfficiencyUpgradeable => true;

        public bool CanAcceptPart {
            get {
                if (inputId != 0u || HasAnyGoal || Tile == null
                        || !Tile.IsActive || !Tile.IsConstructed) {
                    return false;
                }
                return Tile.Health == null || !Tile.Health.IsHeavilyDamaged;
            }
        }

        public bool CanAutomaticallyAcceptPart =>
            automaticIntakeEnabled
            && CanAcceptPart
            && electricNode != null
            && electricNode.IsSwitchedOn
            && electricNode.IsReachable
            && electricNode.IsPowered
            && !electricNode.HasDeficit;

        internal bool CanReceiveNanobotPart(
                ObjData part, AIGoal goal) {
            return part != null
                && part.Id == inputId
                && !inputLoaded
                && goal != null
                && ReferenceEquals(goal.Target, this)
                && TryGetGoal(out AIGoal currentGoal)
                && ReferenceEquals(currentGoal, goal)
                && Entity?.IsActive == true
                && Tile != null
                && Tile.IsActive
                && Tile.IsConstructed;
        }

        private ObjData Input {
            get {
                if (input == null && inputId != 0u) {
                    input = S.Query.FindObjData.Ask(inputId);
                    if (input == null) {
                        D.Err("[Part Restoration Unit] Missing input {0} at {1}.",
                            inputId, Tile);
                        inputId = 0u;
                        inputLoaded = false;
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
            AddComponentPrototype(new PartRestorationUnitComp());
        }

        protected override void OnConfig() {
            energyCost = Config.GetFloat(EnergyCostKey, 25f);
            float repairRate = Config.GetFloat(RepairRateKey, 5f);
            repairRatePerGridTick = repairRate / 1440f * 10f;
            goalPriority = Config.GetInt(GoalPriorityKey, 4);
        }

        public override void OnLateReady(bool wasLoaded) {
            Tile.Transform.EnsurePortPresence(
                requireWorkSpot: false,
                requireInPort: true,
                requireOutPort: true,
                this);
            PartRestorationUnitSys.RequestStoredPartSearch(S);
        }

        internal void RestoreLoadedInputState() {
            if (!inputLoaded || inputId == 0u) {
                return;
            }
            ObjData part = Input;
            if (part == null) {
                return;
            }
            part.State = ObjState.InDevice;
            part.DeviceId = Tile.Id;
        }

        public override void Receive(IComponent sender, int message) {
            if (message == 672382408
                    && sender is ElectricNodeComp node) {
                electricNode = node;
                node.AddAfterTick(this, AfterTickGrid);
                PartRestorationUnitSys.RequestStoredPartSearch(S);
            }
        }

        public override void OnSave() {
            ComponentData data = GetOrCreateData();
            data.SetUInt("InputId", inputId);
            data.SetBool("InputLoaded", inputLoaded);
            data.SetBool("AutomaticIntake", automaticIntakeEnabled);
            data.SetInt("AutomaticPartFilter", (int) automaticPartFilter);
        }

        protected override void OnLoad(ComponentData data) {
            inputId = data.GetUInt("InputId", 0u);
            inputLoaded = data.GetBool("InputLoaded", false);
            automaticIntakeEnabled = data.GetBool(
                "AutomaticIntake", true);
            int savedFilter = data.GetInt(
                "AutomaticPartFilter", 0);
            automaticPartFilter = savedFilter >= 0 && savedFilter <= 2
                ? (PartRestorationIntakeFilter) savedFilter
                : PartRestorationIntakeFilter.Both;
            automaticSearchFoundNoParts = false;
        }

        public void SetAutomaticIntakeEnabled(bool enabled) {
            if (automaticIntakeEnabled == enabled) {
                return;
            }
            automaticIntakeEnabled = enabled;
            automaticSearchFoundNoParts = false;
            OnChange.Invoke();
            PartRestorationUnitSys.RequestStoredPartSearch(S);
        }

        public void SetAutomaticPartFilter(
                PartRestorationIntakeFilter filter) {
            if (filter < PartRestorationIntakeFilter.Both
                    || filter > PartRestorationIntakeFilter.Robotic) {
                filter = PartRestorationIntakeFilter.Both;
            }
            if (automaticPartFilter == filter) {
                return;
            }
            automaticPartFilter = filter;
            automaticSearchFoundNoParts = false;
            OnChange.Invoke();
            PartRestorationUnitSys.RequestStoredPartSearch(S);
        }

        internal void SetAutomaticSearchFoundNoParts(bool empty) {
            if (automaticSearchFoundNoParts == empty) {
                return;
            }
            automaticSearchFoundNoParts = empty;
            OnChange.Invoke();
        }

        internal bool TryOrderRepair(
                ObjData part, bool byPlayer, out string reason) {
            reason = null;
            if (!CanAcceptPart) {
                reason = "This Part Restoration Unit is already busy or unavailable.";
                return false;
            }
            if (!PartRestorationPolicy.HasRepairableDamage(part)) {
                reason = "This body part has no condition damage the unit can repair.";
                return false;
            }
            if (PartRestorationPolicy.WouldYieldToIntelligentRecycling(part)) {
                reason = "Intelligent Recycling currently claims this part under its quality policy.";
                return false;
            }
            if (part.Reservation != null
                    || part.Obj?.Goal != null
                    || !part.IsAvailableIgnoreReservation
                    || PartRestorationPolicy.IsOwned(S, part.Id)) {
                reason = "This body part is already reserved or unavailable.";
                return false;
            }

            AIGoal goal = CreateGoal(
                    105668070,
                    "Icons/Color/Health",
                    T.Repair + ": " + part.BodyPart.NameT,
                    this)
                .WithVar("ObjId", part.Id)
                .ByPlayer(byPlayer)
                .WithPriority(goalPriority)
                .WithRequiredAbilities(961424176, 658376509)
                .WithWorldToDesired(-1707724012, false, true)
                .NanobotCompatible();
            if (!part.TryReserve(goal)) {
                reason = "The body part could not be reserved for restoration.";
                return false;
            }
            if (!Entity.TrySetGoal(goal)) {
                part.TryUnreserve(goal);
                reason = "Stardeus rejected the restoration haul task.";
                return false;
            }

            Input = part;
            inputLoaded = false;
            PartRestorationNanobotHauling.RegisterGoal(this, goal);
            ShowInfoIcon("Icons/Color/Health",
                T.Repair + ": " + part.BodyPart.NameT);
            OnChange.Invoke();
            return true;
        }

        public bool LoadObj(ObjData part, AIGoal goal) {
            if (part == null || part.Id != inputId
                    || goal?.Target != this) {
                D.Err("[Part Restoration Unit] Rejected unexpected input. "
                    + "Expected {0}, received {1} at {2}.",
                    inputId, part?.Id ?? 0u, Tile);
                return false;
            }

            Input = part;
            inputLoaded = true;
            lastOverviewConditionPercent = -1;
            part.State = ObjState.InDevice;
            part.DeviceId = Tile.Id;
            if (part.Obj != null) {
                S.Objs.Destroy(part.Obj);
            }
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
                if (!HasGoal) {
                    AbortPending("The restoration haul task ended before loading.");
                }
                return;
            }

            ObjData part = Input;
            if (part?.BodyPart == null || part.BodyPartState == null) {
                FinishRepair("The loaded object is no longer a valid body part.");
                return;
            }
            if (!PartRestorationPolicy.HasRepairableDamage(part)) {
                FinishRepair(null);
                return;
            }
            if (electricNode == null || !IsUsable) {
                electricNode?.SetConsumerWantedInput(0f);
                return;
            }

            float currentEnergyCost = energyCost;
            if (upgradeEnergyEfficiency > 1f) {
                currentEnergyCost *= 1f / upgradeEnergyEfficiency;
            }
            electricNode.SetConsumerWantedInput(currentEnergyCost);
            if (electricNode.HasDeficit || !electricNode.IsSwitchedOn
                    || !electricNode.IsConsuming) {
                return;
            }

            bool progressed = Wound.TryHeal(
                part.BodyPartState,
                part.BodyPart.SlotType,
                repairRatePerGridTick * upgradeSpeed);
            float progress = PartRestorationPolicy.RepairProgress(
                part.BodyPartState);
            int conditionPercent =
                (int) (part.BodyPartState.Condition * 100f);
            if (conditionPercent != lastOverviewConditionPercent) {
                lastOverviewConditionPercent = conditionPercent;
                OnChange?.Invoke();
            }
            ShowInfoBar(
                Consts.ColorGreenBar,
                progress,
                T.Repair + ": " + part.BodyPart.NameT
                    + " " + Units.Percentage(progress));
            if (!progressed
                    || !PartRestorationPolicy.HasRepairableDamage(part)) {
                FinishRepair(progressed ? null
                    : "No repairable wound could make further progress.");
            }
        }

        private void AbortPending(string reason) {
            ObjData part = Input;
            if (part != null) {
                part.TryUnreserve(null);
            }
            Input = null;
            inputLoaded = false;
            electricNode?.SetConsumerWantedInput(0f);
            HideAllInfo();
            if (!string.IsNullOrWhiteSpace(reason)) {
                D.Warn("[Part Restoration Unit] {0} Unit: {1}", reason, Tile);
            }
            OnChange.Invoke();
        }

        private void FinishRepair(string note) {
            ObjData part = Input;
            Input = null;
            inputLoaded = false;
            automaticSearchFoundNoParts = false;
            lastOverviewConditionPercent = -1;
            electricNode?.SetConsumerWantedInput(0f);
            HideAllInfo();
            if (part == null) {
                return;
            }

            part.TryUnreserve(null);
            part.State = ObjState.Active;
            S.Objs.Create(
                EntityUtils.CenterOf(Tile.Transform.OutPort)
                    + Rng.UInsideUnitCircle() * 0.25f,
                part);
            if (!string.IsNullOrWhiteSpace(note)) {
                D.Warn("[Part Restoration Unit] {0} Part: {1}",
                    note, part);
            }
            OnChange?.Invoke();
            PartRestorationUnitSys.RequestStoredPartSearch(S);
        }

        public override void OnRemove() {
            if (relocated) {
                return;
            }
            if (inputLoaded) {
                FinishRepair("The unit was removed before restoration completed.");
            } else {
                if (TryGetGoal(out AIGoal goal)) {
                    AIGoal.Cancel(goal, G.Ticks);
                }
                AbortPending(null);
            }
        }

        public void RelocateTo(Entity target) {
            if (!target.TryGetComponent(
                    out PartRestorationUnitComp destination)) {
                return;
            }
            destination.automaticIntakeEnabled =
                automaticIntakeEnabled;
            destination.automaticPartFilter = automaticPartFilter;
            destination.automaticSearchFoundNoParts =
                automaticSearchFoundNoParts;
            if (!inputLoaded) {
                if (TryGetGoal(out AIGoal goal)) {
                    AIGoal.Cancel(goal, G.Ticks);
                }
                AbortPending(null);
                PartRestorationUnitSys.RequestStoredPartSearch(S);
                return;
            }

            destination.input = input;
            destination.inputId = inputId;
            destination.inputLoaded = true;
            if (destination.input != null) {
                destination.input.DeviceId = destination.Tile.Id;
            }
            relocated = true;
            input = null;
            inputId = 0u;
            inputLoaded = false;
            PartRestorationUnitSys.RequestStoredPartSearch(S);
        }

        public void OnGoalChange(
                AIGoal goal, AIGoalState from, AIGoalState to) {
            if (!inputLoaded && AIGoal.IsArchived(to)) {
                AbortPending(AIGoal.IsCancelled(to)
                    ? "The restoration haul task was cancelled."
                    : "The restoration haul task ended before loading.");
            }
        }

        public void OnGoalLoad(AIGoal goal) {
            if (inputId == 0u && goal != null
                    && goal.TryGetVar("ObjId", out uint objectId)) {
                inputId = objectId;
            }
            PartRestorationNanobotHauling.RegisterGoal(this, goal);
        }

        public void ApplySpeedUpgrade(float amount) {
            upgradeSpeed += amount;
            if (upgradeSpeed < 1f) {
                D.Err("[Part Restoration Unit] Upgrade speed < 1f after apply: {0} -> {1}",
                    amount, upgradeSpeed);
                upgradeSpeed = 1f;
            }
        }

        public void ApplyEfficiencyUpgrade(float amount) {
            upgradeEnergyEfficiency += amount;
            if (upgradeEnergyEfficiency < 1f) {
                D.Err("[Part Restoration Unit] Upgrade efficiency < 1f after apply: {0} -> {1}",
                    amount, upgradeEnergyEfficiency);
                upgradeEnergyEfficiency = 1f;
            }
        }

        public override string ToString() {
            return " * Part Restoration Unit";
        }
    }
}
