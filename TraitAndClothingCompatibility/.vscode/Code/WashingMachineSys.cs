using System.Collections.Generic;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Data;
using Game.Systems;
using Game.Systems.Items;
using Game.Utils;
using KL.Utils;

namespace TraitAndClothingCompatibility {
    public sealed class WashingMachineSys : GameSystem {
        public const string SysId = "TaccWashingMachineSys";
        private const long ActiveSearchIntervalTicks = 60L;
        private const long EmptySearchIntervalTicks = 300L;

        private enum DispatchOutcome {
            NoReadyMachines,
            Assigned,
            NoStoredItems
        }

        private static WashingMachineSys instance;
        private readonly List<TaccWashingMachineComp> readyMachines =
            new List<TaccWashingMachineComp>();
        private GameState loadedState;
        private long nextStoredItemSearchTick;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        [SelfInit]
        private static void Register() {
            GameSystems.Register(SysId, () => new WashingMachineSys());
        }

        protected override void OnInitialize() {
            instance = this;
            The.SysSig.AfterLoadSaveState.AddListener(OnAfterLoadSaveState);
        }

        public override void Unload() {
            The.SysSig.AfterLoadSaveState.RemoveListener(OnAfterLoadSaveState);
            DetachLoadedState();
            readyMachines.Clear();
            if (instance == this) instance = null;
        }

        internal static void RequestStoredItemSearch(GameState state) {
            WashingMachineSys sys = instance;
            if (sys?.loadedState != state || state == null) return;
            long requestedTick = state.Ticks + 1L;
            if (sys.nextStoredItemSearchTick == 0L
                    || sys.nextStoredItemSearchTick > requestedTick)
                sys.nextStoredItemSearchTick = requestedTick;
        }

        private void OnAfterLoadSaveState(GameState state) {
            DetachLoadedState();
            loadedState = state;
            nextStoredItemSearchTick = state.Ticks + 1L;
            RestoreLoadedInputs(state);
            state.Clock.OnTick.AddListener(OnTick);
        }

        private static void RestoreLoadedInputs(GameState state) {
            var components = state.Components
                .AllByType<TaccWashingMachineComp>();
            if (components == null) return;
            foreach (IComponent component in components) {
                if (component is TaccWashingMachineComp machine)
                    machine.RestoreLoadedInputState();
            }
        }

        private void DetachLoadedState() {
            loadedState?.Clock?.OnTick.RemoveListener(OnTick);
            loadedState = null;
            nextStoredItemSearchTick = 0L;
        }

        private void OnTick(long ticks) {
            if (loadedState == null || ticks < nextStoredItemSearchTick) return;
            DispatchOutcome outcome = AssignStoredItemsToIdleMachines();
            nextStoredItemSearchTick = ticks
                + (outcome == DispatchOutcome.NoStoredItems
                    ? EmptySearchIntervalTicks
                    : ActiveSearchIntervalTicks);
        }

        private DispatchOutcome AssignStoredItemsToIdleMachines() {
            var components = loadedState.Components
                .AllByType<TaccWashingMachineComp>();
            if (components == null) return DispatchOutcome.NoReadyMachines;

            readyMachines.Clear();
            foreach (IComponent component in components) {
                if (component is TaccWashingMachineComp machine
                        && machine.CanAutomaticallyAcceptItem)
                    readyMachines.Add(machine);
            }
            if (readyMachines.Count == 0)
                return DispatchOutcome.NoReadyMachines;

            Dictionary<uint, ObjData> allObjects =
                loadedState.Query.GetAllObjs.Ask();
            if (allObjects == null || allObjects.Count == 0) {
                MarkRemainingMachinesEmpty();
                return DispatchOutcome.NoStoredItems;
            }

            bool assignedAny = false;
            foreach (ObjData candidate in allObjects.Values) {
                if (!IsEligibleStoredItem(candidate)) continue;
                TaccWashingMachineComp machine = readyMachines[0];
                if (machine.TryOrderWash(candidate, false, out _)) {
                    machine.SetAutomaticSearchFoundNoItems(false);
                    readyMachines.RemoveAt(0);
                    assignedAny = true;
                }
                if (readyMachines.Count == 0) break;
            }

            MarkRemainingMachinesEmpty();
            return assignedAny
                ? DispatchOutcome.Assigned
                : DispatchOutcome.NoStoredItems;
        }

        private void MarkRemainingMachinesEmpty() {
            for (int i = 0; i < readyMachines.Count; i++)
                readyMachines[i].SetAutomaticSearchFoundNoItems(true);
            readyMachines.Clear();
        }

        private bool IsEligibleStoredItem(ObjData item) {
            if (item == null
                    || item.State != ObjState.Stored
                    || item.StorageId == 0
                    || item.Reservation != null
                    || item.Obj?.Goal != null
                    || !item.IsAvailableIgnoreReservation
                    || !WashingPolicy.IsWashable(item)
                    || WashingPolicy.IsOwned(loadedState, item.Id)) return false;

            ObjStorageComp storage = loadedState.Components
                .Find<ObjStorageComp>(item.StorageId);
            return storage != null
                && storage.Tile != null
                && storage.Tile.IsActive
                && storage.Tile.IsConstructed
                && storage.Entity.IsActive
                && !storage.Entity.IsCarriedByAny;
        }
    }
}
