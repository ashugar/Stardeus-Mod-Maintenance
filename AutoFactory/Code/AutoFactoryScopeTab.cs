using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Crafting;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;

namespace AutoFactory
{
    public sealed class AutoFactoryScopeTab : BaseEntityTab
    {
        public const string TabId = "AutoFactoryScope";
        private MultiCrafterComp crafter;

        // Purpose: Registers the Auto Factory scope tab with the game's entity-tab registry.
        [SelfInit]
        private static void Register()
        {
            RegisterTab(new AutoFactoryScopeTab());
        }

        public override string Id => TabId;
        public override int Order => 35;
        public override string TabName => "Scope";

        // Purpose: Shows the scope tab only for constructed, managed Auto Factory facilities.
        public override bool ShowFor(GameState state, Tile tile)
        {
            crafter = null;
            if (tile?.IsConstructed != true
                || tile.Definition?.Id is not string id
                || (id != "Objects/Devices/NanoFabricator"
                    && id != "Objects/Devices/NanoFoundry"
                    && id != "Objects/Devices/NanoCloningPod")
                || !tile.TryGetComponent(out MultiCrafterComp found))
            {
                return false;
            }

            crafter = found;
            return AutoFactorySys.Instance != null;
        }

        // Purpose: Renders the Global/Room scope control and current enclosure status.
        public override bool LoadContent(WGroup group)
        {
            var system = AutoFactorySys.Instance;
            if (system == null || crafter == null)
            {
                return false;
            }

            var block = UISpawn.Block(lifetime, group, 0);
            var row = 0;
            var scope = system.GetFactoryScope(crafter);
            var header = UISpawn.Widget<WText>(lifetime,
                UIPrefabId.WContent2Header, block.Container, row++);
            header.Load("Automation scope");

            var toggle = UISpawn.Widget<WToggleText>(lifetime,
                UIPrefabId.WContent2Toggle, block.Container, row++);
            toggle.Load("Limit automation to this room",
                scope == AutoFactoryScope.Room,
                roomOnly =>
                {
                    system.SetFactoryScope(crafter, roomOnly
                        ? AutoFactoryScope.Room
                        : AutoFactoryScope.Global);
                    ReloadTab();
                });
            UIUtils.AddTip(lifetime, toggle,
                "Global uses ship-wide demand, inventory, and ingredients. "
                + "Room prioritizes shortages originating in this enclosed "
                + "room and their eligible intermediaries, then helps with "
                + "unmet ship-wide Production Goals. Goal inventory and "
                + "ingredients remain global.");

            var roomId = system.GetFactoryRoomId(crafter);
            var status = UISpawn.Widget<WIconText2>(lifetime,
                UIPrefabId.WContent2Text2, block.Container, row++);
            status.Load(roomId > 0 ? IconId.CRoom : IconId.CWarning,
                "Current scope",
                scope == AutoFactoryScope.Global
                    ? "Global"
                    : roomId > 0 ? "Room #" + roomId : "Room - not enclosed");
            return true;
        }

        // Purpose: Releases the selected crafter reference when the tab is recycled.
        protected override void Cleanup()
        {
            crafter = null;
        }
    }
}
