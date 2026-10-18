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
    public sealed class EntityOverview_PartRestorationUnit
            : EntityOverviewBlock {
        public const string BlockId = "PartRestorationUnitSlot";
        private static readonly List<string> IntakeFilterOptions =
            new List<string> {
                "Accepted parts: Biological and robotic",
                "Accepted parts: Biological only",
                "Accepted parts: Robotic only"
            };

        public override int Order => 10;
        public override string Id => BlockId;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register() {
            EntityOverviewBlock.Register(
                new EntityOverview_PartRestorationUnit());
        }

        public override bool ShowFor(GameState state, Tile tile) {
            return tile != null && tile.IsConstructed
                && tile.TryGetComponent<PartRestorationUnitComp>(out _);
        }

        public override void LoadContent(
                EntityOverviewContext context, WGroup group,
                ref int groupIndex) {
            Tile tile = context.Tile;
            if (tile == null
                    || !tile.TryGetComponent(
                        out PartRestorationUnitComp unit)) {
                return;
            }

            Lifetime life = context.Lifetime;
            unit.OnChange.Bind(life, () =>
                A.S.UIQuery.ReloadCurrentEntity.Ask(),
                invokeImmediately: false);

            WContentBlock block = UISpawn.Block(
                life, group, groupIndex++);
            int index = 0;
            UISpawn.Widget<WText>(life, "WContent2Header",
                block.Container, index++).Load(tile.Definition.NameT);

            WToggleText automatic = UISpawn.Widget<WToggleText>(
                life, "WContent2Toggle", block.Container, index++);
            automatic.Load(
                "Automatic Intake",
                unit.AutomaticIntakeEnabled,
                unit.SetAutomaticIntakeEnabled);
            UIUtils.AddTip(life, automatic,
                "Automatically retrieve eligible damaged body parts "
                + "from object storage. Disabling this affects only "
                + "future automatic orders.");

            WDropdownWithIcon filter =
                UISpawn.Widget<WDropdownWithIcon>(
                    life, "WContent2Dropdown",
                    block.Container, index++);
            filter.Icon.Load("Icons/Color/Filter");
            filter.Dropdown.Load(
                IntakeFilterOptions,
                (int) unit.AutomaticPartFilter,
                selected => unit.SetAutomaticPartFilter(
                    (PartRestorationIntakeFilter) selected));
            UIUtils.AddTip(life, filter,
                "Limits automatic storage retrieval. Manual Repair "
                + "orders ignore this filter.");

            SpawnStatus(life, block.Container, ref index, unit);

            WContentListButton slot = UISpawn.Widget<WContentListButton>(
                life, "WContent2ListButton", block.Container, index++);
            slot.Load("Icons/Color/Info", $"{T.NounSlot} #1", null);
            slot.LoadOnToggle(life,
                (Lifetime slotLife, RectTransform container,
                    bool willShow, bool wasLoaded) => {
                    if (!willShow || wasLoaded) {
                        return wasLoaded;
                    }
                    SpawnSlotContent(slotLife, container, unit);
                    return true;
                },
                expanded: true);
        }

        private static void SpawnStatus(
                Lifetime life, RectTransform container, ref int index,
                PartRestorationUnitComp unit) {
            ObjData part = unit.InputData;
            string icon;
            string status;
            if (part != null) {
                icon = unit.IsInputLoaded
                    ? "Icons/Color/Health"
                    : "Icons/Color/Time";
                string name = part.ListName
                    ?? part.BodyPart?.NameT
                    ?? T.NounInput;
                status = unit.IsInputLoaded
                    ? "Repairing: " + name
                    : "Waiting for hauler: " + name;
            } else if (!unit.AutomaticIntakeEnabled) {
                icon = "Icons/Color/Stop";
                status = "Automatic intake disabled";
            } else if (!unit.CanAcceptPart) {
                icon = "Icons/Color/Stop";
                status = "Unit unavailable";
            } else if (!unit.HasElectricNode
                    || !unit.IsElectricNodeReachable) {
                icon = "Icons/Color/PowerOffWarning";
                status = "Automatic intake waiting for a power connection";
            } else if (!unit.IsElectricNodeSwitchedOn) {
                icon = "Icons/Color/PowerOffWarning";
                status = "Automatic intake paused: switched off";
            } else if (!unit.IsElectricNodePowered) {
                icon = "Icons/Color/PowerOffWarning";
                status = "Automatic intake paused: no power";
            } else if (unit.HasElectricDeficit) {
                icon = "Icons/Color/ElectricityDeficit";
                status = "Automatic intake paused: power deficit";
            } else if (unit.AutomaticSearchFoundNoParts) {
                icon = "Icons/Color/Info";
                status = "Idle: no eligible stored parts";
            } else {
                icon = "Icons/Color/Time";
                status = "Idle: searching object storage";
            }

            UISpawn.Widget<WIconText2>(
                life, "WContent2Text2", container, index++)
                .Load(icon, "Status", status);
        }

        private static void SpawnSlotContent(
                Lifetime life, RectTransform container,
                PartRestorationUnitComp unit) {
            ObjData part = unit.InputData;
            if (part == null) {
                UISpawn.Widget<WIconText>(life, "WContent2Text",
                    container, 0).Load("Icons/Color/Info", T.Empty);
                return;
            }

            WIconText2 row = UISpawn.Widget<WIconText2>(
                life, "WContent2Text2", container, 0);
            string name = part.ListName
                ?? part.BodyPart?.NameT
                ?? T.NounInput;
            row.Load(part.Def?.Preview ?? "Icons/Color/Health",
                name,
                unit.IsInputLoaded ? T.StateLoaded : T.StateWaiting);
            UIUtils.AddTipFunc(life, row, () => part.Tooltip(unit.S));

            if (part.BodyPartState != null) {
                float condition = part.BodyPartState.Condition;
                float ceiling = PartRestorationPolicy.RepairCeiling(
                    part.BodyPartState);
                string conditionText = Units.Percentage(condition)
                    + " → " + Units.Percentage(ceiling)
                    + " maximum";
                UISpawn.Widget<WIconText2>(
                    life, "WContent2Text2", container, 1)
                    .Load("Icons/Color/Health",
                        "Condition", conditionText);
            }
        }
    }
}
