using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoFactory
{
    internal sealed class AutoFactoryUi : IDisposable
    {
        private static readonly PanelTabDefinition[] Tabs =
        {
            new PanelTabDefinition { Id = "Overview", Label = "Overview" },
            new PanelTabDefinition { Id = "Goals", Label = "Production Goals" }
        };

        private readonly AutoFactorySys system;
        private readonly FrameworkPanel panel;
        private readonly ITopBarWidgetHandle widget;
        private readonly IShortcutHandle automationShortcut;
        private readonly Dictionary<Def, WIconText2> inventoryRows = new();
        private readonly Dictionary<TMP_InputField, GoalInputBinding>
            goalInputBindings = new();
        private readonly HashSet<string> collapsedGoalCategories =
            new HashSet<string>(StringComparer.Ordinal);
        private string activeTab = "Overview";
        private string search = string.Empty;
        private string priorityInput;
        private string productionGoalTableNameInput = string.Empty;
        private string productionGoalTableStatus = string.Empty;
        private bool productionGoalTableStatusSucceeded;
        private TMP_InputField productionGoalTableNameField;
        private float savedProductionGoalTableScrollPosition = 1f;
        private float goalsScrollPosition = 1f;

        // Purpose: Builds and registers Auto Factory's shared panel, widget, and shortcut UI.
        internal AutoFactoryUi(AutoFactorySys system)
        {
            this.system = system;
            priorityInput = system.FactoryPriority.ToString();
            panel = new FrameworkPanel(new FrameworkPanelOptions
            {
                Id = "AutoFactory.Controls",
                Title = "Auto Factory",
                MinSize = new Vector2(520f, 420f),
                MaxSize = new Vector2(1400f, 1000f),
                RememberGeometry = true,
                OnClosed = () =>
                {
                    inventoryRows.Clear();
                    productionGoalTableNameField = null;
                    ClearGoalInputBindings();
                }
            }, BuildPanel);

            widget = AutoUtilitiesApi.RegisterTopBarWidget(
                new TopBarWidgetRegistration
                {
                    Id = "AutoFactory.Status",
                    Icon = () => "Icons/Color/AutoFactoryNanobot",
                    Tint = () => system.Enabled ? Color.white : Color.gray,
                    Tooltip = BuildTooltip,
                    BadgeText = () => system.UnmetTargetCount > 0
                        ? system.UnmetTargetCount.ToString()
                        : string.Empty,
                    BadgeTextColor = () => Color.white,
                    OnClick = () => panel.Toggle(),
                    Width = 36f,
                    Order = 20
                });

            automationShortcut = AutoUtilitiesApi.RegisterShortcut(
                new ShortcutRegistration
                {
                    ConsumerId = AutoFactorySys.ConsumerId,
                    Id = "ToggleAutomation",
                    DisplayName = "enable or disable Auto Factory",
                    PreferenceKey = "autofactory.automation.shortcut",
                    GameState = system.ShortcutGameState,
                    OnPressed = () => system.SetEnabled(!system.Enabled)
                });
            automationShortcut.StateChanged += OnShortcutStateChanged;
        }

        // Purpose: Refreshes all framework-managed UI that reflects automation state.
        internal void RefreshState()
        {
            widget?.Refresh();
            panel?.Refresh();
        }

        // Purpose: Updates visible owned counts without rebuilding the complete panel.
        internal void OnInventoryChanged()
        {
            foreach (var pair in inventoryRows.ToArray())
            {
                if (pair.Value == null)
                {
                    inventoryRows.Remove(pair.Key);
                    continue;
                }
                var count = system.GetInventoryCount(pair.Key);
                pair.Value.SetText2("Owned: " + Units.Num(count));
            }
            widget?.Refresh();
        }

        // Purpose: Removes UI listeners and disposes every framework handle owned by Auto Factory.
        public void Dispose()
        {
            inventoryRows.Clear();
            ClearGoalInputBindings();
            productionGoalTableNameField = null;
            if (automationShortcut != null)
            {
                automationShortcut.StateChanged -= OnShortcutStateChanged;
                automationShortcut.Dispose();
            }
            panel?.Dispose();
            widget?.Dispose();
        }

        // Purpose: Rebuilds panel content when the assigned automation shortcut changes.
        private void OnShortcutStateChanged()
        {
            panel?.Refresh();
        }

        // Purpose: Produces the current top-bar status tooltip.
        private string BuildTooltip()
        {
            return "Auto Factory: " + (system.Enabled ? "ON" : "OFF")
                + "\nNano Fabricators: " + system.NanoFabricatorCount
                + "\nNano Foundries: " + system.NanoFoundryCount
                + "\nNano Cloning Pods: " + system.NanoCloningPodCount
                + "\nActive orders: " + system.ActiveOrderCount
                + "\nGlobally Available Nanobots: "
                + Units.Num(system.GlobalNanobotsCount)
                + "\nFactory priority: " + system.FactoryPriority + "/6"
                + "\nProduction goals: " + system.ConfiguredTargetCount
                + "\nUnmet goals: " + system.UnmetTargetCount
                + "\nClick for automation controls.";
        }

        // Purpose: Routes panel rendering to the currently selected Auto Factory tab.
        private void BuildPanel(PanelContentContext context)
        {
            // Native text fields are globally pooled. Release Auto Factory's
            // prior custom search/goal listeners before any returned field can
            // be reused for a different row in this rebuild.
            ClearGoalInputBindings();
            inventoryRows.Clear();
            context.AddTabs(Tabs, activeTab, value => activeTab = value);
            if (activeTab == "Goals")
            {
                BuildGoals(context);
            }
            else
            {
                BuildOverview(context);
            }
        }

        // Purpose: Renders automation controls, shortcut state, priority, and summary statistics.
        private void BuildOverview(PanelContentContext context)
        {
            context.AddHeader("Automation");
            context.AddToggle(
                system.Enabled ? "Icons/Color/Check" : "Icons/Color/NoEnergy",
                "Auto Factory automation",
                system.Enabled ? "On" : "Off",
                system.Enabled,
                system.SetEnabled,
                "Turns automatic order selection on or off. Turning it off "
                + "clears Auto Factory's current automated orders.");
            context.AddHeader("Keyboard shortcut");
            context.AddInfo(
                "Icons/Color/Info",
                "Enable/disable shortcut",
                automationShortcut.DisplayName,
                "The keyboard key assigned to enable or disable Auto Factory.");
            if (automationShortcut.IsCapturing)
            {
                context.AddButton(
                    "Icons/Color/Stop",
                    "Cancel shortcut capture",
                    string.Empty,
                    automationShortcut.CancelCapture,
                    "Stops waiting for a keyboard key without changing the "
                    + "currently assigned shortcut.");
            }
            else
            {
                context.AddButton(
                    "Icons/Color/Cog",
                    automationShortcut.HasShortcut
                        ? "Change enable/disable shortcut"
                        : "Set enable/disable shortcut",
                    automationShortcut.HasShortcut
                        ? automationShortcut.DisplayName
                        : string.Empty,
                    automationShortcut.BeginCapture,
                    "Waits for one unmodified keyboard key. Escape cancels.");
            }
            if (automationShortcut.HasShortcut)
            {
                context.AddButton(
                    "Icons/Color/Stop",
                    "Clear enable/disable shortcut",
                    string.Empty,
                    automationShortcut.Clear,
                    "Removes the saved Auto Factory automation shortcut.");
            }
            context.AddInfo(
                "Icons/Color/Info",
                "Shortcut status",
                BuildShortcutStatusSummary(),
                automationShortcut.StatusMessage);
            context.AddHeader("Factory status");
            context.AddInfo("Icons/Color/Cog", "Nano Fabricators",
                system.NanoFabricatorCount.ToString(),
                "Constructed Nano Fabricators currently known to Auto Factory.");
            context.AddInfo("Icons/Color/Loading", "Nano Foundries",
                system.NanoFoundryCount.ToString(),
                "Constructed Nano Foundries currently known to Auto Factory.");
            context.AddInfo("Icons/Color/Cloning", "Nano Cloning Pods",
                system.NanoCloningPodCount.ToString(),
                "Constructed Nano Cloning Pods currently known to Auto Factory.");
            context.AddInfo("Icons/Color/Time", "Active automated orders",
                system.ActiveOrderCount.ToString());
            priorityInput = system.FactoryPriority.ToString();
            context.AddInput("Icons/Color/Count", "Auto Factory priority (1-6)",
                priorityInput, value =>
                {
                    var priority = int.TryParse(value, out var parsed)
                        ? parsed
                        : system.FactoryPriority;
                    system.SetFactoryPriority(priority);
                    priorityInput = system.FactoryPriority.ToString();
                    panel.Refresh();
                }, TMP_InputField.ContentType.IntegerNumber,
                "Sets production and refill priority for every managed Auto "
                + "Factory building. 6/6 gives Auto Factory work precedence "
                + "over vanilla 5/5 work without changing other buildings.");
            context.AddInfo("Icons/Color/AutoFactoryGoalEquals", "Configured production goals",
                system.ConfiguredTargetCount.ToString(),
                "Products with a desired global inventory greater than zero.");
            context.AddInfo("Icons/Color/Warning", "Unmet production goals",
                system.UnmetTargetCount.ToString(),
                "Goals whose current inventory and in-flight output remain "
                + "below the desired amount.");
            context.AddHeader("How production is selected");
            context.AddInfo("Icons/Color/Info", "Priority order",
                "Lacking materials, then ranked goals",
                "Room/Global Lacking materials demand is assigned first. "
                + "Production Goals then run from priority 100 down to 0; "
                + "equal priorities are shuffled. Each goal's direct and "
                + "intermediary routes are considered before moving lower.");
            context.AddInfo("Icons/Color/Check", "Order size", "Produce One",
                "Each managed facility keeps at most one automated order, "
                + "allowing several compatible machines to divide available work.");
        }

        // Purpose: Converts detailed shortcut state into a compact overview label.
        private string BuildShortcutStatusSummary()
        {
            if (automationShortcut.IsCapturing)
            {
                return "Waiting for key";
            }
            if (automationShortcut.StatusMessage.IndexOf("conflict",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || automationShortcut.StatusMessage.IndexOf("Cannot assign",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Shortcut conflict";
            }
            return automationShortcut.HasShortcut ? "Ready" : "Not assigned";
        }

        // Purpose: Renders searchable, collapsible production-goal groups and inputs.
        private void BuildGoals(PanelContentContext context)
        {
            context.AddHeader("Production Goals Tables");
            context.AddInfo("Icons/Color/Info", "Reusable goal presets",
                system.LoadedProductionGoalTableName == null
                    ? "No table loaded"
                    : "Loaded: " + system.LoadedProductionGoalTableName,
                "Tables save every nonzero Goal and Priority value in Core's "
                + "global settings, making the same presets available in every "
                + "playthrough. Loading a table replaces the current grid.");
            if (!string.IsNullOrWhiteSpace(productionGoalTableStatus))
            {
                context.AddInfo(productionGoalTableStatusSucceeded
                        ? "Icons/Color/Check" : "Icons/Color/Warning",
                    productionGoalTableStatusSucceeded
                        ? "Production Goals Table updated"
                        : "Production Goals Table needs attention",
                    productionGoalTableStatus);
            }
            var tableNameInput = context.AddInput("Icons/Color/Count",
                "Table label (required)", productionGoalTableNameInput,
                value => productionGoalTableNameInput = value,
                TMP_InputField.ContentType.Standard,
                "Enter a unique label for a new Production Goals Table. "
                + "Labels may contain up to 64 characters.");
            productionGoalTableNameField = tableNameInput.Input.InputField;
            context.AddButton("Icons/Color/Check",
                "Create Production Goals Table", string.Empty,
                CreateProductionGoalTable,
                "Creates a new cross-playthrough table from every currently "
                + "set Goal and Priority value, then makes it the loaded table.");
            context.AddButton("Icons/Color/Cog",
                "Save Production Goals Table",
                system.LoadedProductionGoalTableName ?? "No table loaded",
                SaveProductionGoalTable,
                "Updates the loaded table with every currently set Goal and "
                + "Priority value. If no table is loaded, enter a label and "
                + "create one first.");

            var savedTables = system.ProductionGoalTableSummaries;
            if (savedTables.Count == 0)
            {
                context.AddInfo("Icons/Color/Info", "Saved tables", "None",
                    "Create a table to make the current Production Goals "
                    + "available to other playthroughs.");
            }
            else
            {
                context.AddHeader("Saved tables · " + savedTables.Count);
                var tableScroll = context.AddVerticalScroll(
                    "AutoFactorySavedProductionGoalTables", 300f, 600f, 1f,
                    savedProductionGoalTableScrollPosition,
                    value => savedProductionGoalTableScrollPosition = value);
                var tableScrollSize = tableScroll.Root
                    .GetComponent<LayoutElement>();
                var tableScrollHeight = Mathf.Clamp(savedTables.Count * 47f,
                    47f, 235f);
                tableScrollSize.minHeight = tableScrollHeight;
                tableScrollSize.preferredHeight = tableScrollHeight;
                tableScrollSize.flexibleHeight = 0f;
                var tableRow = 0;
                foreach (var table in savedTables)
                {
                    AddSavedProductionGoalTableRow(context,
                        tableScroll.Content, tableRow++, table);
                }
            }

            context.AddHeader("Global inventory production goals");
            context.AddInfo("Icons/Color/Info", "How targets work",
                "Shortages first; goal priority 0-100",
                "When global inventory is below a target, compatible Auto "
                + "Factory facilities continue producing that product even "
                + "without a Lacking materials warning. Lacking-material "
                + "requests always run before Production Goals. Goal priority "
                + "0 is the default, 1 is the lowest explicit priority, and "
                + "100 is highest. Equal priorities are selected randomly. "
                + "Recipe output stacks may exceed a target.");
            context.AddInfo("Icons/Color/Count", "Product row columns",
                "Item / Owned  |  Goal  |  Priority",
                "Each product uses one compact row. Goal is the desired global "
                + "inventory amount; Priority controls which unmet goal is "
                + "considered first after lacking-material demand.");
            var searchInput = context.AddInput("Icons/Color/AutoFactoryFilter",
                "Filter products", search, _ => { },
                TMP_InputField.ContentType.Standard,
                "Enter part of a product name, group, or internal ID.");
            BindSearchHandler(searchInput);
            context.AddButton("Icons/Color/Stop", "Clear all production goals",
                string.Empty, ClearProductionGoals,
                "Sets every desired stockpile amount and goal priority to zero. "
                + "Unloads the selected table without changing its saved copy. "
                + "Existing orders that are no longer required will be cancelled.");

            var scroll = context.AddVerticalScroll(
                "AutoFactoryProductionGoals",
                460f,
                760f,
                1f,
                goalsScrollPosition,
                value => goalsScrollPosition = value);
            var size = scroll.Root.GetComponent<UnityEngine.UI.LayoutElement>();
            size.minHeight = 300f;
            size.preferredHeight = 620f;

            var products = system.EligibleTargetProducts()
                .Where(MatchesSearch)
                .GroupBy(AutoFactorySys.TargetGroup)
                .OrderBy(group => group.Key);
            var filtering = !string.IsNullOrWhiteSpace(search);
            var row = 0;
            foreach (var group in products)
            {
                var expanded = filtering
                    || !collapsedGoalCategories.Contains(group.Key);
                context.AddCollapsibleHeader(
                    scroll.Content,
                    row++,
                    group.Key,
                    expanded,
                    value =>
                    {
                        if (value)
                        {
                            collapsedGoalCategories.Remove(group.Key);
                        }
                        else
                        {
                            collapsedGoalCategories.Add(group.Key);
                        }
                    },
                    filtering
                        ? "Matching sections remain open while filtering."
                        : expanded
                            ? "Collapse " + group.Key
                            : "Expand " + group.Key,
                    interactable: !filtering);
                if (!expanded)
                {
                    continue;
                }
                foreach (var product in group.OrderBy(item => item.NameT))
                {
                    AddGoalProductRow(context, scroll.Content, row++, product);
                }
            }
        }

        // Purpose: Clears the grid and unloads the selected table without
        // overwriting its globally saved values.
        private void ClearProductionGoals()
        {
            productionGoalTableStatusSucceeded = true;
            productionGoalTableStatus = "Cleared all goals and priorities. "
                + "The saved table was not changed and is no longer loaded; "
                + "create or load a table before using Save.";
            system.ClearProductionTargets();
        }

        // Purpose: Creates a named global table from the current goal grid.
        private void CreateProductionGoalTable()
        {
            productionGoalTableNameInput =
                productionGoalTableNameField?.text
                ?? productionGoalTableNameInput;
            productionGoalTableStatusSucceeded =
                system.TryCreateProductionGoalTable(
                    productionGoalTableNameInput,
                    out productionGoalTableStatus);
            if (productionGoalTableStatusSucceeded)
            {
                productionGoalTableNameInput = string.Empty;
            }
            panel.Refresh();
            if (!productionGoalTableStatusSucceeded)
            {
                FocusProductionGoalTableName();
            }
        }

        // Purpose: Updates the selected global table or directs the player to
        // the required Create flow when no table is selected.
        private void SaveProductionGoalTable()
        {
            productionGoalTableStatusSucceeded =
                system.TrySaveLoadedProductionGoalTable(
                    out productionGoalTableStatus);
            panel.Refresh();
            if (!productionGoalTableStatusSucceeded)
            {
                FocusProductionGoalTableName();
            }
        }

        // Purpose: Replaces the current goal grid with a selected global table.
        private void LoadProductionGoalTable(string name)
        {
            productionGoalTableStatusSucceeded =
                system.TryLoadProductionGoalTable(name,
                    out productionGoalTableStatus);
            panel.Refresh();
        }

        // Purpose: Deletes a globally saved table while preserving the active grid.
        private void DeleteProductionGoalTable(string name)
        {
            productionGoalTableStatusSucceeded =
                system.TryDeleteProductionGoalTable(name,
                    out productionGoalTableStatus);
            panel.Refresh();
        }

        // Purpose: Builds a compact Load row with a Planning Overhaul-style
        // destruction action for one globally saved table.
        private void AddSavedProductionGoalTableRow(
            PanelContentContext context, RectTransform parent,
            int siblingIndex, ProductionGoalTableSummary table)
        {
            var root = new GameObject("AutoFactorySavedProductionGoalTableRow");
            var rect = root.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.SetSiblingIndex(siblingIndex);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            var size = root.AddComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = 40f;
            size.flexibleHeight = 0f;
            var layout = root.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;

            var isLoaded = string.Equals(
                system.LoadedProductionGoalTableName, table.Name,
                StringComparison.OrdinalIgnoreCase);
            var load = UISpawn.Widget<WButtonIconText2>(context.Lifetime,
                UIPrefabId.WContent4Button,
                CreateProductionGoalTableSlot(rect, "Load", 0f), 0);
            load.Load(IconId.WConfigLoad, table.Name,
                isLoaded
                    ? "Loaded; " + FormatConfiguredValues(table.ConfiguredCount)
                    : FormatConfiguredValues(table.ConfiguredCount),
                () => LoadProductionGoalTable(table.Name));
            UIComponentFactory.SetTip(load,
                "Replace every current Goal and Priority value with the values "
                + "saved in \"" + table.Name + "\".");

            var delete = UISpawn.Widget<WButtonIcon>(context.Lifetime,
                UIPrefabId.WButtonIcon,
                CreateProductionGoalTableSlot(rect, "Delete", 40f), 0);
            delete.Load(IconId.CDemolition,
                () => DeleteProductionGoalTable(table.Name));
            UIComponentFactory.SetTip(delete,
                "Delete saved Production Goals Table \"" + table.Name
                + "\". Current goals and priorities are preserved.");
        }

        // Purpose: Sizes one widget slot in a saved-table management row.
        private static RectTransform CreateProductionGoalTableSlot(
            RectTransform parent, string name, float width)
        {
            var root = new GameObject(name);
            var rect = root.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            var size = root.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = width;
            size.flexibleWidth = width > 0f ? 0f : 1f;
            size.minHeight = size.preferredHeight = 40f;
            var layout = root.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = true;
            return rect;
        }

        // Purpose: Returns keyboard focus to the required table-label field.
        private void FocusProductionGoalTableName()
        {
            productionGoalTableNameField?.Select();
            productionGoalTableNameField?.ActivateInputField();
        }

        // Purpose: Formats the sparse Goal/Priority entry count for table rows.
        private static string FormatConfiguredValues(int count)
        {
            return count + (count == 1
                ? " configured product" : " configured products");
        }

        // Purpose: Builds one compact Item/Owned/Goal/Priority production-goal row.
        private void AddGoalProductRow(PanelContentContext context,
            RectTransform parent, int siblingIndex, Def product)
        {
            var rowObject = new GameObject("AutoFactoryGoalRow",
                typeof(RectTransform), typeof(LayoutElement),
                typeof(HorizontalLayoutGroup));
            rowObject.transform.SetParent(parent, false);
            rowObject.transform.SetSiblingIndex(siblingIndex);
            var rowSize = rowObject.GetComponent<LayoutElement>();
            rowSize.minHeight = 48f;
            rowSize.preferredHeight = 48f;
            rowSize.flexibleHeight = 0f;
            var rowLayout = rowObject.GetComponent<HorizontalLayoutGroup>();
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = true;
            rowLayout.spacing = 6f;

            var row = rowObject.GetComponent<RectTransform>();
            var info = UISpawn.Widget<WIconText2>(context.Lifetime,
                UIPrefabId.WContent2Text2, row, 0);
            info.Load(product.Preview, product.NameT,
                "Owned: " + Units.Num(system.GetInventoryCount(product)));
            ConfigureGoalCell(info, 180f, 320f, 1f);
            inventoryRows[product] = info;

            var target = UISpawn.Widget<WInputWithIcon>(context.Lifetime,
                "WContent2Input", row, 1);
            target.Icon.Load("Icons/Color/AutoFactoryGoalEquals");
            target.Input.Load("Goal", system.GetProductionTarget(product)
                .ToString(), _ => { });
            target.Input.InputField.contentType =
                TMP_InputField.ContentType.IntegerNumber;
            UIComponentFactory.SetTip(target, "Goal for " + product.NameT
                + ". Zero disables production for this goal.");
            AddCommitHandler(target, product, GoalInputRole.Target);
            ConfigureGoalCell(target, 105f, 150f, 0f);

            var priority = UISpawn.Widget<WInputWithIcon>(context.Lifetime,
                "WContent2Input", row, 2);
            priority.Icon.Load("Icons/Color/Count");
            priority.Input.Load("Priority", system.GetProductionPriority(product)
                .ToString(), _ => { });
            priority.Input.InputField.contentType =
                TMP_InputField.ContentType.IntegerNumber;
            UIComponentFactory.SetTip(priority, "Production Goal priority for "
                + product.NameT + ": 0 default, 1 lowest, 100 highest.");
            AddCommitHandler(priority, product, GoalInputRole.Priority);
            ConfigureGoalCell(priority, 105f, 150f, 0f);
        }

        // Purpose: Assigns responsive width rules to one compact goal-row cell.
        private static void ConfigureGoalCell(Component cell, float minWidth,
            float preferredWidth, float flexibleWidth)
        {
            var size = cell.GetComponent<LayoutElement>()
                ?? cell.gameObject.AddComponent<LayoutElement>();
            size.minWidth = minWidth;
            size.preferredWidth = preferredWidth;
            size.flexibleWidth = flexibleWidth;
            size.minHeight = 48f;
            size.preferredHeight = 48f;
            size.flexibleHeight = 0f;
        }

        // Purpose: Tests whether a production-goal product matches the active filter.
        private bool MatchesSearch(Def product)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }
            return product.NameT.IndexOf(search,
                       StringComparison.OrdinalIgnoreCase) >= 0
                || AutoFactorySys.TargetKey(product).IndexOf(search,
                       StringComparison.OrdinalIgnoreCase) >= 0
                || AutoFactorySys.TargetGroup(product).IndexOf(search,
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Purpose: Binds a pooled goal input to the product whose target it edits.
        private void AddCommitHandler(WInputWithIcon input, Def product,
            GoalInputRole role)
        {
            var field = input.Input.InputField;
            if (!goalInputBindings.TryGetValue(field, out var binding))
            {
                binding = new GoalInputBinding(system);
                goalInputBindings[field] = binding;
            }
            binding.Input = input.Input;
            binding.Product = product;
            binding.Role = role;
            binding.CustomCommit = null;
            ReplaceCommitHandlers(input, binding);
        }

        // Purpose: Binds the pooled search input to filter state and panel refreshes.
        private void BindSearchHandler(WInputWithIcon input)
        {
            var field = input.Input.InputField;
            if (!goalInputBindings.TryGetValue(field, out var binding))
            {
                binding = new GoalInputBinding(system);
                goalInputBindings[field] = binding;
            }
            binding.Input = input.Input;
            binding.Product = null;
            binding.Role = GoalInputRole.Target;
            binding.CustomCommit = value =>
            {
                search = value?.Trim() ?? string.Empty;
                panel.Refresh();
            };
            ReplaceCommitHandlers(input, binding);
        }

        // Purpose: Releases custom listeners and submit delegates before their
        // globally pooled native fields are reused by another input role.
        private void ClearGoalInputBindings()
        {
            foreach (var pair in goalInputBindings)
            {
                pair.Key?.onEndEdit.RemoveListener(pair.Value.Commit);
                pair.Key?.onValueChanged.RemoveListener(
                    pair.Value.CommitClearedInput);
                pair.Value.Input?.SetOnSubmit(null);
                pair.Value.Input = null;
                pair.Value.Product = null;
                pair.Value.CustomCommit = null;
            }
            goalInputBindings.Clear();
        }

        // Purpose: Replaces stale pooled-input listeners with one current binding.
        private static void ReplaceCommitHandlers(WInputWithIcon input,
            GoalInputBinding binding)
        {
            var field = input.Input.InputField;
            // A native input can move between the search row and a product row
            // as the pooled panel contents change. Remove callbacks belonging to
            // its previous role before assigning exactly one current binding.
            field.onEndEdit.RemoveAllListeners();
            field.onValueChanged.RemoveListener(binding.CommitClearedInput);
            input.Input.SetOnSubmit(binding.Commit);
            field.onEndEdit.AddListener(binding.Commit);
            // Core's clear button changes the value without guaranteeing a
            // submit or end-edit event. Every role therefore watches only for
            // an empty value and commits its own zero/default semantics.
            field.onValueChanged.AddListener(binding.CommitClearedInput);
        }

        private sealed class GoalInputBinding
        {
            private readonly AutoFactorySys system;
            private int lastCommitFrame = -1;

            internal WInput Input;
            internal Def Product;
            internal GoalInputRole Role;
            internal Action<string> CustomCommit;

            // Purpose: Creates a reusable binding for either a goal row or the search row.
            internal GoalInputBinding(AutoFactorySys system)
            {
                this.system = system;
            }

            // Purpose: Commits one frame-deduplicated search or production-target edit.
            internal void Commit(string text)
            {
                if (lastCommitFrame == Time.frameCount)
                {
                    return;
                }
                lastCommitFrame = Time.frameCount;
                if (CustomCommit != null)
                {
                    CustomCommit(text);
                    return;
                }
                if (Product == null)
                {
                    return;
                }
                var value = int.TryParse(text, out var parsed) ? parsed : 0;
                if (Role == GoalInputRole.Priority)
                {
                    system.SetProductionPriority(Product, value);
                    Input?.SetValue(system.GetProductionPriority(Product)
                        .ToString(), notify: false);
                }
                else
                {
                    system.SetProductionTarget(Product, value);
                    Input?.SetValue(system.GetProductionTarget(Product)
                        .ToString(), notify: false);
                }
            }

            // Purpose: Commits TMP clear-button changes that do not emit submit events.
            internal void CommitClearedInput(string text)
            {
                // TMP's built-in clear button changes the value without
                // submitting or ending edit. Ignore normal typing; an empty
                // product field commits zero while Search clears its filter.
                if (!string.IsNullOrEmpty(text))
                {
                    return;
                }
                if (CustomCommit != null)
                {
                    CustomCommit(string.Empty);
                }
                else
                {
                    Commit(string.Empty);
                }
            }
        }

        private enum GoalInputRole
        {
            Target,
            Priority
        }
    }
}
