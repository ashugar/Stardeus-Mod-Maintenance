using Game;
using Game.Components;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.Crafting;
using Game.Systems.UIUX;
using Game.Utils;
using KL.Utils;
using System.Collections.Generic;
using UnityEngine;

namespace AutoFactory
{
    /// <summary>
    /// Adds a native bottom-bar action for changing the automation scope of
    /// multiple selected Nano facilities in one operation.
    /// </summary>
    public sealed class AutoFactoryScopeAction : BaseEntityAction
    {
        public const string ActionId = "AutoFactoryScopeMulti";

        // Purpose: Registers the multi-facility Scope action with Core's entity-action registry.
        [SelfInit]
        private static void Register()
        {
            RegisterAction(new AutoFactoryScopeAction());
        }

        public override string Id => ActionId;

        // Purpose: Shows the action only when at least two constructed Nano facilities are selected.
        protected override bool ApplyForMany(GameState s, List<Tile> tiles,
            out EntityActionData data)
        {
            var crafters = CollectManagedFactories(tiles);
            if (crafters.Count < 2
                || !ValidateLimit(crafters.Count, "Scope", true))
            {
                data = EntityActionData.Unavailable;
                return false;
            }

            data = new EntityActionData(this,
                (lifetime, parent, index) => CreateMulti(lifetime, parent,
                    index, crafters),
                Priority.Low);
            return true;
        }

        // Purpose: Creates the cog-labelled multi-selection button and matched-facility count.
        private WBase CreateMulti(Lifetime lifetime, RectTransform parent,
            int index, List<MultiCrafterComp> crafters)
        {
            var widget = SpawnButtonMulti(lifetime, true, parent, index);
            widget.LoadMulti(crafters.Count, IconId.CCog, "Scope",
                () => ShowScopeChoice(crafters, widget));
            return widget;
        }

        // Purpose: Offers explicit set-all choices so mixed current scopes are unambiguous.
        private static void ShowScopeChoice(List<MultiCrafterComp> crafters,
            WBase anchor)
        {
            if (AutoFactorySys.Instance == null || crafters == null
                || crafters.Count < 2)
            {
                return;
            }

            var choice = QuickChoice.Create("Scope");
            choice.Widget = anchor.Rect;
            choice.AddIconButton(IconId.CTargetGlobal, "Set all to Global",
                () => ApplyScope(crafters, AutoFactoryScope.Global),
                "Use ship-wide lacking-material demand for all selected Nano facilities.");
            choice.AddIconButton(IconId.CRoom, "Set all to Room",
                () => ApplyScope(crafters, AutoFactoryScope.Room),
                "Use each selected Nano facility's current enclosed room for local lacking-material demand.");
            choice.Show();
        }

        // Purpose: Applies one scope through Auto Factory's existing saved scope ownership.
        private static void ApplyScope(List<MultiCrafterComp> crafters,
            AutoFactoryScope scope)
        {
            var system = AutoFactorySys.Instance;
            if (system == null || crafters == null
                || !ValidateLimit(crafters.Count, "Scope", false))
            {
                return;
            }

            if (system.SetFactoryScopes(crafters, scope) <= 0)
            {
                return;
            }

            A.S.UIQuery.RebuildEntityActions.Ask();
        }

        // Purpose: Normalizes multi-tile selections and returns unique active managed crafters.
        private static List<MultiCrafterComp> CollectManagedFactories(
            List<Tile> tiles)
        {
            var crafters = new List<MultiCrafterComp>();
            var visited = new HashSet<int>();
            if (tiles == null)
            {
                return crafters;
            }

            foreach (var selected in tiles)
            {
                if (selected == null)
                {
                    continue;
                }

                var tile = EntityUtils.ParentObj(selected);
                if (tile?.IsActive != true || !tile.IsConstructed
                    || !visited.Add(tile.Id)
                    || !tile.TryGetComponent(out MultiCrafterComp crafter)
                    || !AutoFactorySys.IsManagedFactory(crafter))
                {
                    continue;
                }

                crafters.Add(crafter);
            }

            return crafters;
        }
    }
}
