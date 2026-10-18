using System;
using System.Collections.Generic;
using System.Reflection;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Data;
using Game.Systems;
using Game.Systems.Items;
using Game.Utils;
using HarmonyLib;

namespace Game.Systems.PartRestoration {
    public sealed class PartRestorationUnitSys : GameSystem {
        public const string SysId = "PartRestorationUnitSys";
        public const string ConsumerId = "PartRestorationUnit";
        public const string HarmonyId = "jleas.partrestorationunit";
        public const string RequiredUtilitiesVersion = "1.4.0";
        private const long ActiveSearchIntervalTicks = 60L;
        private const long EmptySearchIntervalTicks = 300L;

        private enum DispatchOutcome {
            NoReadyUnits,
            Assigned,
            NoStoredParts
        }

        private static PartRestorationUnitSys instance;
        private readonly List<PartRestorationUnitComp> readyUnits =
            new List<PartRestorationUnitComp>();
        private GameState loadedState;
        private long nextStoredPartSearchTick;
        private Harmony harmony;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        [SelfInit]
        private static void Register() {
            GameSystems.Register(SysId,
                () => new PartRestorationUnitSys());
        }

        protected override void OnInitialize() {
            instance = this;
            AutoUtilitiesApi.TryRegisterConsumer(
                ConsumerId,
                "Part Restoration Unit",
                typeof(PartRestorationUnitSys).Assembly
                    .GetName().Version.ToString(3),
                RequiredUtilitiesVersion);
            try {
                PartRestorationNanobotHauling.ReportBindings();
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            } catch (Exception ex) {
                harmony?.UnpatchAll(HarmonyId);
                harmony = null;
                PartRestorationNanobotHauling.DisableForSession(
                    ex, "initialization");
            }
            The.SysSig.AfterLoadSaveState.AddListener(
                OnAfterLoadSaveState);
        }

        public override void Unload() {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            PartRestorationNanobotHauling.Reset();
            The.SysSig.AfterLoadSaveState.RemoveListener(
                OnAfterLoadSaveState);
            DetachLoadedState();
            readyUnits.Clear();
            if (instance == this) {
                instance = null;
            }
        }

        internal static void RequestStoredPartSearch(GameState state) {
            PartRestorationUnitSys sys = instance;
            if (sys?.loadedState != state || state == null) {
                return;
            }
            long requestedTick = state.Ticks + 1L;
            if (sys.nextStoredPartSearchTick == 0L
                    || sys.nextStoredPartSearchTick > requestedTick) {
                sys.nextStoredPartSearchTick = requestedTick;
            }
        }

        private void OnAfterLoadSaveState(GameState state) {
            DetachLoadedState();
            loadedState = state;
            nextStoredPartSearchTick = state.Ticks + 1L;
            RestoreLoadedUnitInputs(state);
            try {
                PartRestorationNanobotHauling.AfterLoad(state);
            } catch (Exception ex) {
                PartRestorationNanobotHauling.DisableForSession(
                    ex, "save recovery");
            }
            state.Clock.OnTick.AddListener(OnTick);
        }

        private static void RestoreLoadedUnitInputs(GameState state) {
            var components = state.Components
                .AllByType<PartRestorationUnitComp>();
            if (components == null) {
                return;
            }
            foreach (IComponent component in components) {
                if (component is PartRestorationUnitComp unit) {
                    unit.RestoreLoadedInputState();
                }
            }
        }

        private void DetachLoadedState() {
            loadedState?.Clock?.OnTick.RemoveListener(OnTick);
            loadedState = null;
            nextStoredPartSearchTick = 0L;
        }

        private void OnTick(long ticks) {
            if (loadedState == null
                    || ticks < nextStoredPartSearchTick) {
                return;
            }
            DispatchOutcome outcome = AssignStoredPartsToIdleUnits();
            nextStoredPartSearchTick = ticks
                + (outcome == DispatchOutcome.NoStoredParts
                    ? EmptySearchIntervalTicks
                    : ActiveSearchIntervalTicks);
        }

        private DispatchOutcome AssignStoredPartsToIdleUnits() {
            var components = loadedState.Components
                .AllByType<PartRestorationUnitComp>();
            if (components == null) {
                return DispatchOutcome.NoReadyUnits;
            }

            readyUnits.Clear();
            foreach (IComponent component in components) {
                if (component is PartRestorationUnitComp unit
                        && unit.CanAutomaticallyAcceptPart) {
                    readyUnits.Add(unit);
                }
            }
            if (readyUnits.Count == 0) {
                return DispatchOutcome.NoReadyUnits;
            }

            Dictionary<uint, ObjData> allObjects =
                loadedState.Query.GetAllObjs.Ask();
            if (allObjects == null || allObjects.Count == 0) {
                MarkRemainingUnitsEmpty();
                return DispatchOutcome.NoStoredParts;
            }

            bool assignedAny = false;
            foreach (ObjData candidate in allObjects.Values) {
                if (!IsEligibleStoredPart(candidate)) {
                    continue;
                }
                int unitIndex = FindBestReadyUnitIndex(candidate);
                if (unitIndex >= 0
                        && readyUnits[unitIndex].TryOrderRepair(
                            candidate, false, out _)) {
                    readyUnits[unitIndex]
                        .SetAutomaticSearchFoundNoParts(false);
                    readyUnits.RemoveAt(unitIndex);
                    assignedAny = true;
                }
                if (readyUnits.Count == 0) {
                    break;
                }
            }

            MarkRemainingUnitsEmpty();
            return assignedAny
                ? DispatchOutcome.Assigned
                : DispatchOutcome.NoStoredParts;
        }

        private int FindBestReadyUnitIndex(ObjData part) {
            PartRestorationIntakeFilter specialized =
                part.BodyPart.IsBiological
                    ? PartRestorationIntakeFilter.Biological
                    : PartRestorationIntakeFilter.Robotic;
            for (int i = 0; i < readyUnits.Count; i++) {
                if (readyUnits[i].AutomaticPartFilter == specialized) {
                    return i;
                }
            }
            for (int i = 0; i < readyUnits.Count; i++) {
                if (readyUnits[i].AutomaticPartFilter ==
                        PartRestorationIntakeFilter.Both) {
                    return i;
                }
            }
            return -1;
        }

        private void MarkRemainingUnitsEmpty() {
            for (int i = 0; i < readyUnits.Count; i++) {
                readyUnits[i].SetAutomaticSearchFoundNoParts(true);
            }
            readyUnits.Clear();
        }

        private bool IsEligibleStoredPart(ObjData part) {
            if (part == null
                    || part.State != ObjState.Stored
                    || part.StorageId == 0
                    || part.BodyPart == null
                    || part.Reservation != null
                    || part.Obj?.Goal != null
                    || !part.IsAvailableIgnoreReservation
                    || !PartRestorationPolicy.HasRepairableDamage(part)
                    || PartRestorationPolicy
                        .WouldYieldToIntelligentRecycling(part)) {
                return false;
            }

            ObjStorageComp storage = loadedState.Components
                .Find<ObjStorageComp>(part.StorageId);
            return storage != null
                && storage.Tile != null
                && storage.Tile.IsActive
                && storage.Tile.IsConstructed
                && storage.Entity.IsActive
                && !storage.Entity.IsCarriedByAny;
        }
    }
}
