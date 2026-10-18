using System.Collections.Generic;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.Items;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using UnityEngine;

namespace Game.Systems.PartRestoration {
    public sealed class PartRepairAction : BaseEntityAction {
        public const string ActionId = "PartRestorationUnit.OrderRepair";
        public override string Id => ActionId;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register() {
            RegisterAction(new PartRepairAction());
        }

        protected override bool ApplyFor(
                GameState state, Obj obj, out EntityActionData data) {
            if (obj?.Data?.BodyPart == null || !obj.IsActive) {
                data = EntityActionData.Unavailable;
                return false;
            }
            data = new EntityActionData(
                this,
                (Lifetime life, RectTransform parent, int siblingIndex) =>
                    Create(life, parent, siblingIndex, state, obj.Data),
                Priority.Low,
                1);
            return true;
        }

        protected override bool ApplyForMany(
                GameState state, List<Obj> objects,
                out EntityActionData data) {
            List<ObjData> parts = CollectBodyParts(objects);
            if (parts.Count == 0) {
                data = EntityActionData.Unavailable;
                return false;
            }

            data = new EntityActionData(
                this,
                (Lifetime life, RectTransform parent, int siblingIndex) =>
                    CreateMulti(life, parent, siblingIndex, state,
                        new List<ObjData>(parts)),
                Priority.Low,
                1);
            return true;
        }

        private WBase Create(
                Lifetime life, RectTransform parent, int siblingIndex,
                GameState state, ObjData part) {
            bool repairable = PartRestorationPolicy
                .HasRepairableDamage(part);
            bool recyclingOwns = repairable
                && PartRestorationPolicy
                    .WouldYieldToIntelligentRecycling(part);
            bool unavailablePart = part.Reservation != null
                || PartRestorationPolicy.IsOwned(state, part.Id)
                || !part.IsAvailableIgnoreReservation;
            bool hasUnit = repairable && !recyclingOwns
                && !unavailablePart
                && PartRestorationPolicy.TryFindAvailableUnit(
                    state, part, out _);
            bool available = repairable && !recyclingOwns
                && !unavailablePart && hasUnit;
            string label = available ? T.Repair : Texts.Strike(T.Repair);
            WBottomBarButton button = SpawnButton(
                life, available, parent, siblingIndex);
            button.Load("Icons/Color/Health", label, () => {
                if (TryOrderClosest(state, part, out string reason)) {
                    UIUtils.RefreshSelectedEntityUI();
                } else if (!string.IsNullOrWhiteSpace(reason)) {
                    state.Sig.ShowErrorMessage.Send(reason);
                }
            });
            UIUtils.AddTip(life, button,
                BuildTooltip(repairable, recyclingOwns,
                    unavailablePart, hasUnit));
            return button;
        }

        private WBase CreateMulti(
                Lifetime life, RectTransform parent, int siblingIndex,
                GameState state, List<ObjData> parts) {
            bool available = HasActionablePart(state, parts);
            string label = available ? T.Repair : Texts.Strike(T.Repair);
            WBottomBarButton button = SpawnButtonMulti(
                life, available, parent, siblingIndex);
            button.LoadMulti(parts.Count, "Icons/Color/Health", label, () => {
                int queued = 0;
                string lastReason = null;
                for (int i = 0; i < parts.Count; i++) {
                    ObjData part = parts[i];
                    if (!IsRepairCandidate(
                            state, part, out string reason)) {
                        if (!string.IsNullOrWhiteSpace(reason)) {
                            lastReason = reason;
                        }
                        continue;
                    }
                    if (!PartRestorationPolicy.TryFindAvailableUnit(
                            state, part,
                            out PartRestorationUnitComp unit)) {
                        lastReason = "No idle, constructed Part Restoration Unit is available.";
                        break;
                    }
                    if (unit.TryOrderRepair(part, true, out reason)) {
                        queued++;
                    } else if (!string.IsNullOrWhiteSpace(reason)) {
                        lastReason = reason;
                    }
                }

                if (queued > 0) {
                    UIUtils.RefreshSelectedEntityUI();
                } else if (!string.IsNullOrWhiteSpace(lastReason)) {
                    state.Sig.ShowErrorMessage.Send(lastReason);
                }
            });
            UIUtils.AddTip(life, button,
                BuildMultiTooltip(state, parts, available));
            return button;
        }

        private static List<ObjData> CollectBodyParts(List<Obj> objects) {
            var parts = new List<ObjData>();
            if (objects == null) {
                return parts;
            }

            var seen = new HashSet<uint>();
            for (int i = 0; i < objects.Count; i++) {
                Obj obj = objects[i];
                ObjData part = obj?.Data;
                if (obj == null || !obj.IsActive || part?.BodyPart == null
                        || !seen.Add(part.Id)) {
                    continue;
                }
                parts.Add(part);
            }
            return parts;
        }

        private static bool HasActionablePart(
                GameState state, List<ObjData> parts) {
            for (int i = 0; i < parts.Count; i++) {
                ObjData part = parts[i];
                if (IsRepairCandidate(state, part, out _)
                        && PartRestorationPolicy.TryFindAvailableUnit(
                            state, part, out _)) {
                    return true;
                }
            }
            return false;
        }

        private static string BuildMultiTooltip(
                GameState state, List<ObjData> parts, bool available) {
            string text = "Queue restoration for eligible body parts in "
                + "this selection. Non-body-part items are ignored.";
            if (available) {
                return text;
            }

            bool hasRepairablePart = false;
            for (int i = 0; i < parts.Count; i++) {
                if (PartRestorationPolicy.HasRepairableDamage(parts[i])
                        && !PartRestorationPolicy
                            .WouldYieldToIntelligentRecycling(parts[i])) {
                    hasRepairablePart = true;
                    break;
                }
            }
            return text + (hasRepairablePart
                ? " No idle Part Restoration Unit is currently available."
                : " None of the selected body parts currently has eligible condition damage.");
        }

        private static string BuildTooltip(
                bool repairable, bool recyclingOwns,
                bool unavailablePart, bool hasUnit) {
            if (!repairable) {
                return "This body part has no condition damage that can be repaired.";
            }
            if (recyclingOwns) {
                return "Intelligent Recycling currently claims this part under its quality policy.";
            }
            if (unavailablePart) {
                return "This body part is already reserved or unavailable.";
            }
            if (!hasUnit) {
                return "No idle, constructed Part Restoration Unit is available.";
            }
            return "Send this detached body part to an automated Part Restoration Unit.";
        }

        internal static bool TryOrderClosest(
                GameState state, ObjData part, out string reason) {
            if (!IsRepairCandidate(state, part, out reason)) {
                return false;
            }
            if (!PartRestorationPolicy.TryFindAvailableUnit(
                    state, part, out PartRestorationUnitComp unit)) {
                reason = "No idle, constructed Part Restoration Unit is available.";
                return false;
            }
            return unit.TryOrderRepair(part, true, out reason);
        }

        private static bool IsRepairCandidate(
                GameState state, ObjData part, out string reason) {
            reason = null;
            if (!PartRestorationPolicy.HasRepairableDamage(part)) {
                reason = "This body part has no condition damage the unit can repair.";
                return false;
            }
            if (PartRestorationPolicy.WouldYieldToIntelligentRecycling(part)) {
                reason = "Intelligent Recycling currently claims this part under its quality policy.";
                return false;
            }
            if (part == null || part.Reservation != null
                    || part.Obj?.Goal != null
                    || !part.IsAvailableIgnoreReservation
                    || PartRestorationPolicy.IsOwned(state, part.Id)) {
                reason = "This body part is already reserved or unavailable.";
                return false;
            }
            return true;
        }
    }
}
