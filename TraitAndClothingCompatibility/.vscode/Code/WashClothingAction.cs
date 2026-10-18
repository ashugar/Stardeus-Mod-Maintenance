using System.Collections.Generic;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.Items;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using UnityEngine;

namespace TraitAndClothingCompatibility {
    public sealed class WashClothingAction : BaseEntityAction {
        public const string ActionId = "TACC.WashClothing";
        public override string Id => ActionId;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register() {
            RegisterAction(new WashClothingAction());
        }

        protected override bool ApplyFor(
                GameState state, Obj obj, out EntityActionData data) {
            if (obj?.Data == null || !obj.IsActive
                    || !WashingPolicy.IsClothing(obj.Data)) {
                data = EntityActionData.Unavailable;
                return false;
            }
            data = new EntityActionData(
                this,
                (Lifetime life, RectTransform parent, int siblingIndex) =>
                    Create(life, parent, siblingIndex, state, obj.Data),
                Priority.Low,
                2);
            return true;
        }

        protected override bool ApplyForMany(
                GameState state, List<Obj> objects,
                out EntityActionData data) {
            List<ObjData> clothing = CollectClothing(objects);
            if (clothing.Count == 0) {
                data = EntityActionData.Unavailable;
                return false;
            }
            data = new EntityActionData(
                this,
                (Lifetime life, RectTransform parent, int siblingIndex) =>
                    CreateMulti(life, parent, siblingIndex, state,
                        new List<ObjData>(clothing)),
                Priority.Low,
                2);
            return true;
        }

        private WBase Create(
                Lifetime life, RectTransform parent, int siblingIndex,
                GameState state, ObjData item) {
            bool washable = WashingPolicy.IsWashable(item);
            bool unavailable = item.Reservation != null
                || WashingPolicy.IsOwned(state, item.Id)
                || !item.IsAvailableIgnoreReservation;
            bool hasMachine = washable && !unavailable
                && WashingPolicy.TryFindAvailableMachine(
                    state, item, out _);
            bool available = washable && !unavailable && hasMachine;
            string label = available ? "Wash" : Texts.Strike("Wash");
            WBottomBarButton button = SpawnButton(
                life, available, parent, siblingIndex);
            button.Load(IconId.CRecycling, label, () => {
                if (TryOrderClosest(state, item, out string reason)) {
                    UIUtils.RefreshSelectedEntityUI();
                } else if (!string.IsNullOrWhiteSpace(reason)) {
                    state.Sig.ShowErrorMessage.Send(reason);
                }
            });
            UIUtils.AddTip(life, button,
                BuildTooltip(washable, unavailable, hasMachine));
            return button;
        }

        private WBase CreateMulti(
                Lifetime life, RectTransform parent, int siblingIndex,
                GameState state, List<ObjData> clothing) {
            bool available = HasActionableItem(state, clothing);
            string label = available ? "Wash" : Texts.Strike("Wash");
            WBottomBarButton button = SpawnButtonMulti(
                life, available, parent, siblingIndex);
            button.LoadMulti(clothing.Count,
                IconId.CRecycling, label, () => {
                    int queued = 0;
                    string lastReason = null;
                    for (int i = 0; i < clothing.Count; i++) {
                        ObjData item = clothing[i];
                        if (!IsWashCandidate(state, item, out string reason)) {
                            if (!string.IsNullOrWhiteSpace(reason))
                                lastReason = reason;
                            continue;
                        }
                        if (!WashingPolicy.TryFindAvailableMachine(
                                state, item,
                                out TaccWashingMachineComp machine)) {
                            lastReason = "No idle, constructed Washing Machine is available.";
                            break;
                        }
                        if (machine.TryOrderWash(item, true, out reason)) {
                            queued++;
                        } else if (!string.IsNullOrWhiteSpace(reason)) {
                            lastReason = reason;
                        }
                    }

                    if (queued > 0) UIUtils.RefreshSelectedEntityUI();
                    else if (!string.IsNullOrWhiteSpace(lastReason))
                        state.Sig.ShowErrorMessage.Send(lastReason);
                });
            UIUtils.AddTip(life, button,
                BuildMultiTooltip(state, clothing, available));
            return button;
        }

        private static List<ObjData> CollectClothing(List<Obj> objects) {
            var clothing = new List<ObjData>();
            if (objects == null) return clothing;
            var seen = new HashSet<uint>();
            for (int i = 0; i < objects.Count; i++) {
                Obj obj = objects[i];
                ObjData item = obj?.Data;
                if (obj == null || !obj.IsActive
                        || !WashingPolicy.IsClothing(item)
                        || !seen.Add(item.Id)) continue;
                clothing.Add(item);
            }
            return clothing;
        }

        private static bool HasActionableItem(
                GameState state, List<ObjData> clothing) {
            for (int i = 0; i < clothing.Count; i++) {
                ObjData item = clothing[i];
                if (IsWashCandidate(state, item, out _)
                        && WashingPolicy.TryFindAvailableMachine(
                            state, item, out _)) return true;
            }
            return false;
        }

        private static string BuildMultiTooltip(
                GameState state, List<ObjData> clothing, bool available) {
            string text = "Queue washing for eligible corpse-origin clothing "
                + "in this selection. Non-clothing objects are ignored.";
            if (available) return text;
            for (int i = 0; i < clothing.Count; i++) {
                if (WashingPolicy.IsWashable(clothing[i]))
                    return text + " No idle Washing Machine is currently available.";
            }
            return text + " None of the selected clothing is marked Taken from the dead.";
        }

        private static string BuildTooltip(
                bool washable, bool unavailable, bool hasMachine) {
            if (!washable)
                return "This clothing is not marked Taken from the dead.";
            if (unavailable)
                return "This clothing item is already reserved or unavailable.";
            if (!hasMachine)
                return "No idle, constructed Washing Machine is available.";
            return "Send this clothing to a Washing Machine. Washing replaces Taken from the dead with Second-hand and preserves quality.";
        }

        internal static bool TryOrderClosest(
                GameState state, ObjData item, out string reason) {
            if (!IsWashCandidate(state, item, out reason)) return false;
            if (!WashingPolicy.TryFindAvailableMachine(
                    state, item, out TaccWashingMachineComp machine)) {
                reason = "No idle, constructed Washing Machine is available.";
                return false;
            }
            return machine.TryOrderWash(item, true, out reason);
        }

        private static bool IsWashCandidate(
                GameState state, ObjData item, out string reason) {
            reason = null;
            if (!WashingPolicy.IsWashable(item)) {
                reason = "This clothing is not marked Taken from the dead.";
                return false;
            }
            if (item.Reservation != null
                    || item.Obj?.Goal != null
                    || !item.IsAvailableIgnoreReservation
                    || WashingPolicy.IsOwned(state, item.Id)) {
                reason = "This clothing item is already reserved or unavailable.";
                return false;
            }
            return true;
        }
    }
}
