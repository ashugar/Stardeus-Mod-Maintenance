using Game;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.Items;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;

namespace TraitAndClothingCompatibility {
    public sealed class EntityOverview_TaccWashingMachine
            : EntityOverviewBlock {
        public const string BlockId = "TaccWashingMachineStatus";
        public override int Order => 10;
        public override string Id => BlockId;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register() {
            EntityOverviewBlock.Register(
                new EntityOverview_TaccWashingMachine());
        }

        public override bool ShowFor(GameState state, Tile tile) {
            return tile != null && tile.IsConstructed
                && tile.TryGetComponent<TaccWashingMachineComp>(out _);
        }

        public override void LoadContent(
                EntityOverviewContext context, WGroup group,
                ref int groupIndex) {
            Tile tile = context.Tile;
            if (tile == null || !tile.TryGetComponent(
                    out TaccWashingMachineComp machine)) return;

            Lifetime life = context.Lifetime;
            machine.OnChange.Bind(life,
                () => A.S.UIQuery.ReloadCurrentEntity.Ask(),
                invokeImmediately: false);

            WContentBlock block = UISpawn.Block(
                life, group, groupIndex++);
            int index = 0;
            UISpawn.Widget<WText>(life, "WContent2Header",
                block.Container, index++).Load(tile.Definition.NameT);

            string icon;
            string status;
            ObjData item = machine.InputData;
            if (item != null) {
                icon = machine.IsInputLoaded
                    ? IconId.CRecycling
                    : "Icons/Color/Time";
                status = machine.IsInputLoaded
                    ? "Washing: " + item.Def.NameT + " "
                        + Units.Percentage(machine.WashProgress)
                    : "Waiting for hauler: " + item.Def.NameT;
            } else if (TaccSystem.Instance?.State?.AutomaticWashing != true) {
                icon = "Icons/Color/Stop";
                status = "Automatic washing disabled in TACC";
            } else if (!machine.CanAcceptItem) {
                icon = "Icons/Color/Stop";
                status = "Machine unavailable";
            } else if (!machine.HasElectricNode
                    || !machine.IsElectricNodeReachable) {
                icon = "Icons/Color/PowerOffWarning";
                status = "Automatic washing waiting for a power connection";
            } else if (!machine.IsElectricNodeSwitchedOn) {
                icon = "Icons/Color/PowerOffWarning";
                status = "Automatic washing paused: switched off";
            } else if (!machine.IsElectricNodePowered) {
                icon = "Icons/Color/PowerOffWarning";
                status = "Automatic washing paused: no power";
            } else if (machine.HasElectricDeficit) {
                icon = "Icons/Color/ElectricityDeficit";
                status = "Automatic washing paused: power deficit";
            } else if (machine.AutomaticSearchFoundNoItems) {
                icon = IconId.CInfo;
                status = "Idle: no eligible stored clothing";
            } else {
                icon = "Icons/Color/Time";
                status = "Idle: searching object storage";
            }

            UISpawn.Widget<WIconText2>(life, "WContent2Text2",
                block.Container, index++).Load(icon, "Status", status);
            UISpawn.Widget<WIconText2>(life, "WContent2Text2",
                block.Container, index++).Load(
                    IconId.CRecycling,
                    "Automatic washing",
                    TaccSystem.Instance?.State?.AutomaticWashing == true
                        ? "Enabled in TACC"
                        : "Disabled in TACC");

            WContentListButton slot = UISpawn.Widget<WContentListButton>(
                life, "WContent2ListButton", block.Container, index++);
            slot.Load(IconId.CClothing, $"{T.NounSlot} #1", null);
            slot.LoadOnToggle(life,
                (Lifetime slotLife, UnityEngine.RectTransform container,
                    bool willShow, bool wasLoaded) => {
                    if (!willShow || wasLoaded) return wasLoaded;
                    SpawnSlotContent(slotLife, container, machine);
                    return true;
                },
                expanded: true);
        }

        private static void SpawnSlotContent(
                Lifetime life, UnityEngine.RectTransform container,
                TaccWashingMachineComp machine) {
            ObjData item = machine.InputData;
            if (item == null) {
                UISpawn.Widget<WIconText>(life, "WContent2Text",
                    container, 0).Load(IconId.CInfo, T.Empty);
                return;
            }

            WIconText2 row = UISpawn.Widget<WIconText2>(
                life, "WContent2Text2", container, 0);
            row.Load(item.Def?.Preview ?? IconId.CClothing,
                item.ListName ?? item.Def?.NameT ?? T.NounInput,
                machine.IsInputLoaded ? T.StateLoaded : T.StateWaiting);
            UIUtils.AddTipFunc(life, row, () => item.Tooltip(machine.S));
            UISpawn.Widget<WIconText2>(life, "WContent2Text2",
                container, 1).Load(
                    IconId.CRecycling,
                    "Result",
                    "Second-hand • quality preserved");
        }
    }
}
