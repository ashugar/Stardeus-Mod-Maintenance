using System;
using System.Collections.Generic;
using System.Linq;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Systems.UIUX;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Systems.AutoHauling {
    internal sealed class AutoHaulingPanel : IDisposable {
        private const string AlertColor = "#FF5F5F";

        private static readonly PanelTabDefinition[] Tabs = {
            new PanelTabDefinition { Id = "Controls", Label = "Controls" },
            new PanelTabDefinition { Id = "Items", Label = "Items" },
            new PanelTabDefinition { Id = "Failures", Label = "Failures" }
        };

        private readonly AutoHaulingSys system;
        private readonly FrameworkPanel panel;
        private readonly ITopBarWidgetHandle widget;
        private readonly IShortcutHandle allowForbiddenShortcut;
        private readonly HashSet<string> collapsedDomains =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> expandedCategories =
            new HashSet<string>(StringComparer.Ordinal);

        private string activeTab = "Controls";
        private string search = string.Empty;
        private float itemScrollPosition = 1f;
        private float failureScrollPosition = 1f;
        private bool disposed;

        internal AutoHaulingPanel(AutoHaulingSys system,
                IShortcutHandle allowForbiddenShortcut) {
            this.system = system
                ?? throw new ArgumentNullException(nameof(system));
            this.allowForbiddenShortcut = allowForbiddenShortcut
                ?? throw new ArgumentNullException(nameof(allowForbiddenShortcut));
            FrameworkPanel createdPanel = null;
            ITopBarWidgetHandle createdWidget = null;
            bool shortcutSubscribed = false;
            try {
                createdPanel = new FrameworkPanel(new FrameworkPanelOptions {
                    Id = "AutoHauling.Controls",
                    Title = "Auto Hauling",
                    MinSize = new Vector2(560f, 460f),
                    MaxSize = new Vector2(1400f, 1000f),
                    RememberGeometry = true
                }, BuildPanel);

                createdWidget = AutoUtilitiesApi.RegisterTopBarWidget(
                    new TopBarWidgetRegistration {
                        Id = "AutoHauling.Status",
                        Icon = () => IconId.CHaul,
                        Tint = () => system.Enabled
                            ? Color.green
                            : Color.gray,
                        Tooltip = BuildWidgetTooltip,
                        BadgeText = () => system.ActiveTrackedTasks > 0
                            ? system.ActiveTrackedTasks.ToString()
                            : string.Empty,
                        BadgeTextColor = () => Color.white,
                        OnClick = createdPanel.Toggle,
                        Width = 36f,
                        Order = 310
                    });
                if (createdWidget == null) {
                    throw new InvalidOperationException(
                        "AUMF returned no Auto Hauling widget handle.");
                }
                allowForbiddenShortcut.StateChanged += OnShortcutStateChanged;
                shortcutSubscribed = true;
                panel = createdPanel;
                widget = createdWidget;
            } catch {
                if (shortcutSubscribed) {
                    allowForbiddenShortcut.StateChanged -=
                        OnShortcutStateChanged;
                }
                createdWidget?.Dispose();
                createdPanel?.Dispose();
                throw;
            }
        }

        internal void RefreshStatus() {
            widget?.Refresh();
        }

        internal void RefreshAll() {
            widget?.Refresh();
            panel?.Refresh();
        }

        internal void Close() {
            panel?.Close();
        }

        public void Dispose() {
            if (disposed) {
                return;
            }
            disposed = true;
            allowForbiddenShortcut.StateChanged -= OnShortcutStateChanged;
            widget?.Dispose();
            panel?.Dispose();
        }

        private void OnShortcutStateChanged() {
            RefreshAll();
        }

        private string BuildWidgetTooltip() {
            bool intelligentRecyclingReady =
                system.RefreshIntelligentRecyclingCompatibility(
                    out string intelligentRecyclingStatus);
            string intelligentRecyclingSummary = intelligentRecyclingReady
                ? "\nIntelligent Recycling: " + intelligentRecyclingStatus
                : "\n" + AlertText("Quality-object hauling unavailable: "
                    + intelligentRecyclingStatus)
                    + "\nIntelligent Recycling is optional; non-quality "
                    + "hauling remains available under the master switch.";
            string lastManualSummary = system.LastManualAllowTick > 0
                ? "\nLast Forbidden cleanup removed "
                    + system.LastManualAllow.Allowed + " flag(s)."
                : string.Empty;
            return "Auto Hauling: " + (system.Enabled ? "ON" : "OFF")
                + "\nStale Store refresh: "
                + (system.StaleStoreRefreshEnabled ? "ON" : "OFF")
                + " (" + system.LastStaleStoreTasksRequeued
                + " requeued last scan)"
                + "\nWorker bidding: managed by Stardeus"
                + "\nTracked vanilla Store tasks: "
                + system.ActiveTrackedTasks
                + "\nLast scan queued: " + system.LastQueued
                + intelligentRecyclingSummary
                + "\nAllow Forbidden shortcut: "
                + allowForbiddenShortcut.DisplayName
                + lastManualSummary
                + "\nRecent failure records: "
                + system.FailureHistory.Count
                + "\nClick for controls.";
        }

        private void BuildPanel(PanelContentContext context) {
            context.AddTabs(Tabs, activeTab, value => activeTab = value);
            switch (activeTab) {
                case "Items":
                    BuildItems(context);
                    break;
                case "Failures":
                    BuildFailures(context);
                    break;
                default:
                    BuildControls(context);
                    break;
            }
        }

        private void BuildControls(PanelContentContext context) {
            bool intelligentRecyclingReady =
                system.RefreshIntelligentRecyclingCompatibility(
                    out string intelligentRecyclingStatus);
            context.AddHeader("Automation");
            context.AddToggle(
                IconId.CHaul,
                "Automatic vanilla Store tasks",
                system.Enabled ? "On" : "Off",
                system.Enabled,
                system.SetEnabled,
                "When enabled, Auto Hauling periodically asks Stardeus to "
                + "create ordinary Store tasks for eligible loose cargo. "
                + "Turning this off never cancels an existing vanilla task.");
            context.AddToggle(
                IconId.CTime,
                "Refresh stalled Store tasks",
                system.StaleStoreRefreshEnabled ? "On" : "Off",
                system.StaleStoreRefreshEnabled,
                system.SetStaleStoreRefreshEnabled,
                "When the master automation switch is enabled, cancel and "
                + "recreate an eligible loose-cargo Store task that has "
                + "remained continuously in Stardeus's unassigned bidding "
                + "state for more than two in-game days. This applies "
                + "regardless of who originally created the Store task. It "
                + "never touches assigned or active work, disabled-priority "
                + "tasks, Optimization Mod dormant tasks, unrelated Hauling "
                + "jobs, or cargo outside Auto Hauling's current safety and "
                + "compatibility rules.");
            context.AddInfo(
                IconId.CHaul,
                "Idle-worker bidding",
                "Managed by Stardeus",
                "Stardeus now rotates idle workers and indexed tasks and "
                + "tries alternative candidates when bids compete. Auto "
                + "Hauling's former Prompt idle beings to haul option is "
                + "retired. Core manages worker bidding regardless of Auto "
                + "Hauling's master switch. The old saved preference is "
                + "retained for compatibility and has no gameplay effect.");
            context.AddInfo(
                IconId.CInfo,
                "Stale refresh threshold",
                "More than 2 in-game days",
                "Age is measured from the start of the current uninterrupted "
                + "bidding state, not from the task's original creation or a "
                + "previous failed attempt. The recreated task keeps its "
                + "existing priority.");
            context.AddInfo(
                IconId.CInfo,
                "AUMF control surface",
                system.FrameworkReady
                    ? "Ready (" + AutoHaulingSys.RequiredUtilitiesVersion + ")"
                    : "Unavailable",
                "Auto Hauling remains inactive when its required AUMF control "
                + "surface is unavailable.");
            context.AddInfo(
                intelligentRecyclingReady ? IconId.CInfo : IconId.CWarning,
                "Intelligent Recycling",
                intelligentRecyclingStatus,
                "Optional read-only cooperation for quality-bearing objects. "
                + "Auto Hauling requires Intelligent Recycling contract 1, "
                + "capability 128, an initialized game state, and a fresh "
                + "positive result from both release properties. When any "
                + "requirement is unavailable, quality objects remain alone.");
            if (!intelligentRecyclingReady) {
                AddIntelligentRecyclingUnavailableNotice(
                    context, intelligentRecyclingStatus);
            }

            context.AddHeader("Latest scan");
            context.AddInfo(
                IconId.CTime,
                "Last scan tick",
                system.LastScanTick > 0
                    ? system.LastScanTick.ToString()
                    : "Not run yet");
            context.AddInfo(
                IconId.CStore,
                "Considered / queued",
                system.LastScanned + " / " + system.LastQueued,
                "Supported safe loose cargo considered by the last scan and "
                + "the number of vanilla Store tasks successfully created.");
            context.AddInfo(
                IconId.CCheckDisabled,
                "Skipped by settings",
                system.LastDisabled.ToString());
            context.AddInfo(
                IconId.CWarning,
                "Existing task / Forbidden",
                system.LastBlockedByTask + " / " + system.LastForbidden,
                "Auto Hauling never replaces an existing task and never "
                + "clears Forbidden automatically.");
            context.AddInfo(
                IconId.CStore,
                "No compatible storage / unavailable",
                system.LastNoStorage + " / " + system.LastUnavailable,
                "Compatibility is determined by Stardeus's current storage "
                + "filters and availability rules. Unavailable also includes "
                + "material piles marked unreachable by Stardeus.");
            context.AddInfo(
                IconId.CWarning,
                "Hard-protected / order rejected",
                system.LastProtected + " / " + system.LastRejected,
                "Hard-protected objects are intentionally outside this mod. "
                + "Unexpected vanilla order rejections are recorded as "
                + "failures instead of being retried by Auto Hauling.");
            context.AddInfo(
                IconId.CCheckDisabled,
                "Held by Intelligent Recycling",
                system.LastHeldByIntelligentRecycling.ToString(),
                "Quality-bearing objects not affirmatively released by the "
                + "current Intelligent Recycling snapshot. This also includes "
                + "objects held fail-closed while the optional API is absent, "
                + "initializing, incompatible, or temporarily blocked.");
            context.AddInfo(
                IconId.CCheckDisabled,
                "Held by Part Restoration Unit",
                system.LastHeldByPartRestorationUnit.ToString(),
                "Quality-bearing body parts not affirmatively released for "
                + "external automation by an installed Part Restoration Unit. "
                + "A failed or incompatible PRU API call is treated as no "
                + "permission for that body part; PRU is not required for "
                + "ordinary cargo.");
            context.AddInfo(
                IconId.CCount,
                "Tracked vanilla Store tasks",
                system.ActiveTrackedTasks.ToString(),
                "Only tasks originally created by Auto Hauling are tracked "
                + "for diagnostic failure reasons.");
            context.AddInfo(
                IconId.CTime,
                "Stale Store tasks found / refreshed",
                system.LastStaleStoreTasksFound + " / "
                    + system.LastStaleStoreTasksRequeued,
                "Eligible Store tasks older than the two-day bidding "
                + "threshold found during the last scan and successfully "
                + "recreated through Core. Cancelled before recreation: "
                + system.LastStaleStoreTasksCancelled + ".");

            context.AddHeader("Manual action");
            ForbiddenLooseSummary preview =
                system.PreviewForbiddenLooseCargo();
            context.AddButton(
                IconId.CStoringAllow,
                "Allow Forbidden Loose Cargo",
                preview.Allowed + " flag(s)",
                system.AllowForbiddenLooseCargo,
                "Manual only. Removes every vanilla Forbidden flag "
                + "currently found on a loose material pile or storable object. "
                + "This includes quality objects, Intelligent Recycling "
                + "targets, and mind-associated objects. It does not create or "
                + "cancel a task, change a reservation, or alter either mod's "
                + "settings or clear Stardeus's unreachable-pile marker. "
                + "Warning: Forbidden now blocks more than hauling. Removing "
                + "it can also allow construction, crafting, eating, equipment "
                + "use, or processing by other mods where applicable.");
            if (system.LastManualAllowTick > 0) {
                context.AddInfo(
                    IconId.CCheck,
                    "Last manual result",
                    "Removed " + system.LastManualAllow.Allowed + " flag(s)",
                    "Executed at game tick " + system.LastManualAllowTick + ".");
            }

            context.AddHeader("Keyboard shortcut");
            context.AddInfo(
                IconId.CInfo,
                "Allow Forbidden shortcut",
                allowForbiddenShortcut.DisplayName,
                "Optional shortcut for the same full manual action above. "
                + "AUMF stores the assignment globally and prevents conflicts "
                + "with Stardeus and other registered mod shortcuts. Pressing "
                + "the assigned key immediately removes every detected loose "
                + "Forbidden flag, including quality, special-target, and "
                + "mind-associated objects. Warning: this also permits "
                + "construction, crafting, eating, equipment use, or other "
                + "mod processing where applicable; it does not clear "
                + "Stardeus's unreachable-pile marker.");
            context.AddButton(
                allowForbiddenShortcut.IsCapturing
                    ? IconId.CStop
                    : IconId.CCog,
                allowForbiddenShortcut.IsCapturing
                    ? "Cancel shortcut capture"
                    : allowForbiddenShortcut.HasShortcut
                        ? "Change Allow Forbidden shortcut"
                        : "Set Allow Forbidden shortcut",
                allowForbiddenShortcut.IsCapturing
                    ? "Cancel"
                    : allowForbiddenShortcut.HasShortcut
                        ? allowForbiddenShortcut.DisplayName
                        : "Assign",
                () => {
                    if (allowForbiddenShortcut.IsCapturing) {
                        allowForbiddenShortcut.CancelCapture();
                    } else {
                        allowForbiddenShortcut.BeginCapture();
                    }
                },
                allowForbiddenShortcut.IsCapturing
                    ? "Cancel without changing the currently assigned key."
                    : "Choose one unmodified keyboard key. Assignment alone "
                        + "does not clear anything. Escape cancels; conflicting "
                        + "and reserved keys are rejected. Warning: pressing "
                        + "the assigned key later immediately performs the full "
                        + "Allow Forbidden Loose Cargo action.");
            if (allowForbiddenShortcut.HasShortcut) {
                context.AddButton(
                    IconId.CStop,
                    "Clear Allow Forbidden shortcut",
                    "Clear",
                    allowForbiddenShortcut.Clear,
                    "Removes the saved shortcut assignment. The manual panel "
                    + "button remains available.");
            }
            context.AddInfo(
                IconId.CInfo,
                "Shortcut status",
                BuildShortcutStatusSummary(),
                allowForbiddenShortcut.StatusMessage);

            context.AddHeader("Compatibility boundary");
            context.AddInfo(
                IconId.CWarning,
                "Permanently excluded",
                "Protected by hard rules",
                "Permanently excluded: quality-bearing material piles; "
                + "Hullpiercers, Storage Capsules, and Artillery Fungus; whole "
                + "Beings and bodies; and mind-bearing or mind-associated "
                + "objects. Ordinary quality-bearing storable objects remain "
                + "fail-closed unless Intelligent Recycling affirmatively "
                + "releases them at the current decision point.");
        }

        private void BuildItems(PanelContentContext context) {
            // Rebuild the option catalog from current definitions so newly
            // loaded content appears without stale caches. Filtering forces
            // matching sections open; category-wide actions stay hidden while
            // filtered because they affect the complete category.
            List<HaulItemOption> allOptions = system.GetItemOptions();
            bool hasQualityOptions = allOptions.Any(
                option => option.QualityBearing);
            bool intelligentRecyclingReady =
                system.RefreshIntelligentRecyclingCompatibility(
                    out string intelligentRecyclingStatus);
            context.AddHeader("Eligible cargo");
            if (hasQualityOptions && !intelligentRecyclingReady) {
                AddIntelligentRecyclingUnavailableNotice(
                    context, intelligentRecyclingStatus);
            }
            context.AddInfo(
                IconId.CInfo,
                "Default policy",
                "All eligible items enabled",
                "New eligible categories and items default enabled. The hard "
                + "compatibility exclusions are omitted from this list. "
                + "Quality-bearing object settings apply only after Intelligent "
                + "Recycling affirmatively releases the current object.");
            context.AddInput(
                IconId.CFilter,
                "Filter by item, category, or internal ID",
                search,
                value => {
                    search = value?.Trim() ?? string.Empty;
                    panel.Refresh();
                },
                TMP_InputField.ContentType.Standard,
                "Press Enter or leave the field to apply the filter.");
            if (!string.IsNullOrEmpty(search)) {
                context.AddButton(
                    IconId.CStop,
                    "Clear item filter",
                    search,
                    () => {
                        search = string.Empty;
                        panel.Refresh();
                    },
                    "Restores the full item list and the previous collapse "
                    + "state.");
            }

            List<HaulItemOption> visibleOptions = allOptions
                .Where(MatchesSearch).ToList();
            bool filtering = !string.IsNullOrWhiteSpace(search);
            context.AddInfo(
                IconId.CCount,
                "Visible / eligible definitions",
                visibleOptions.Count + " / " + allOptions.Count);

            VerticalScrollViewResult scroll = context.AddVerticalScroll(
                "AutoHaulingItems",
                500f,
                820f,
                1f,
                itemScrollPosition,
                value => itemScrollPosition = value);
            LayoutElement layout = scroll.Root.GetComponent<LayoutElement>();
            if (layout != null) {
                layout.minHeight = 320f;
                layout.preferredHeight = 650f;
            }

            int row = 0;
            foreach (IGrouping<HaulDomain, HaulItemOption> domain
                    in visibleOptions.GroupBy(option => option.Domain)
                        .OrderBy(group => group.Key)) {
                string domainKey = "domain:" + domain.Key;
                bool domainExpanded = filtering
                    || !collapsedDomains.Contains(domainKey);
                int domainEnabled = domain.Count(system.IsItemEnabled);
                context.AddCollapsibleHeader(
                    scroll.Content,
                    row++,
                    domain.First().DomainName + " (" + domainEnabled
                        + "/" + domain.Count() + " enabled)",
                    domainExpanded,
                    value => {
                        if (value) {
                            collapsedDomains.Remove(domainKey);
                        } else {
                            collapsedDomains.Add(domainKey);
                        }
                    },
                    filtering
                        ? "Matching sections remain open while filtering."
                        : domainExpanded
                            ? "Collapse " + domain.First().DomainName
                            : "Expand " + domain.First().DomainName,
                    interactable: !filtering);
                if (!domainExpanded) {
                    continue;
                }

                foreach (IGrouping<string, HaulItemOption> category
                        in domain.GroupBy(option => option.CategoryKey)
                            .OrderBy(group => group.First().CategoryName,
                                StringComparer.OrdinalIgnoreCase)) {
                    HaulItemOption categoryFirst = category.First();
                    bool categoryExpanded = filtering
                        || expandedCategories.Contains(category.Key);
                    int enabledCount = category.Count(system.IsItemEnabled);
                    context.AddCollapsibleHeader(
                        scroll.Content,
                        row++,
                        categoryFirst.CategoryName + " (" + enabledCount
                            + "/" + category.Count() + " enabled)",
                        categoryExpanded,
                        value => {
                            if (value) {
                                expandedCategories.Add(category.Key);
                            } else {
                                expandedCategories.Remove(category.Key);
                            }
                        },
                        filtering
                            ? "Matching sections remain open while filtering."
                            : categoryExpanded
                                ? "Collapse " + categoryFirst.CategoryName
                                : "Expand " + categoryFirst.CategoryName,
                        interactable: !filtering);
                    if (!categoryExpanded) {
                        continue;
                    }

                    // A filtered category contains only the matching rows, but
                    // its category default applies to every item in the group.
                    // Hide the bulk actions during filtering so the visible
                    // count cannot imply that only the matches will change.
                    if (!filtering) {
                        AddScrollButton(
                            context,
                            scroll.Content,
                            ref row,
                            IconId.CCheck,
                            "Enable all " + categoryFirst.CategoryName,
                            category.Count() + " item(s)",
                            () => system.SetCategoryEnabled(category.Key, true),
                            "Sets this category's default to enabled and clears "
                            + "its individual exceptions.");
                        AddScrollButton(
                            context,
                            scroll.Content,
                            ref row,
                            IconId.CCheckDisabled,
                            "Disable all " + categoryFirst.CategoryName,
                            category.Count() + " item(s)",
                            () => system.SetCategoryEnabled(category.Key, false),
                            "Sets this category's default to disabled and clears "
                            + "its individual exceptions.");
                    }

                    foreach (HaulItemOption option in category
                            .OrderBy(item => item.Name,
                                StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.ItemId,
                                StringComparer.Ordinal)) {
                        AddItemToggle(context, scroll.Content, ref row, option,
                            intelligentRecyclingReady);
                    }
                }
            }

            if (visibleOptions.Count == 0) {
                AddScrollInfo(
                    context,
                    scroll.Content,
                    ref row,
                    IconId.CInfo,
                    "No eligible items match the current filter.",
                    string.Empty,
                    null);
            }
        }

        private bool MatchesSearch(HaulItemOption option) {
            if (string.IsNullOrWhiteSpace(search)) {
                return true;
            }
            return Contains(option.Name, search)
                || Contains(option.ItemId, search)
                || Contains(option.CategoryName, search)
                || Contains(option.DomainName, search);
        }

        private static bool Contains(string value, string searchValue) {
            return value?.IndexOf(searchValue,
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AddItemToggle(
                PanelContentContext context,
                RectTransform parent,
                ref int row,
                HaulItemOption option,
                bool intelligentRecyclingReady) {
            bool enabled = system.IsItemEnabled(option);
            bool qualityUnavailable = option.QualityBearing
                && !intelligentRecyclingReady;
            string displayedState = enabled
                ? qualityUnavailable
                    ? AlertText("On - IR unavailable")
                    : "On"
                : "Off";
            WToggleWithIconText2 result =
                UISpawn.Widget<WToggleWithIconText2>(
                    context.Lifetime,
                    UIPrefabId.WContent2Text2Toggle,
                    parent,
                    row++);
            result.Item.Load(option.Icon, option.Name, displayedState);
            result.Toggle.Load(enabled,
                value => system.SetItemEnabled(option, value));
            UIComponentFactory.SetTip(
                result,
                option.DomainName + " / " + option.CategoryName
                    + "\nStable ID: " + option.ItemId
                    + (option.QualityBearing
                        ? intelligentRecyclingReady
                            ? "\nRequires a current positive Intelligent "
                                + "Recycling release decision."
                            : "\nThis saved preference is preserved, but "
                                + "quality-object hauling is currently off "
                                + "because Intelligent Recycling is not ready. "
                                + "Non-quality hauling remains available."
                        : string.Empty));
        }

        private static void AddIntelligentRecyclingUnavailableNotice(
                PanelContentContext context,
                string status) {
            context.AddInfo(
                IconId.CWarning,
                AlertText("Quality-object hauling unavailable"),
                AlertText(status),
                "Auto Hauling has failed off only its quality-bearing object "
                + "lane because Intelligent Recycling is not ready to provide "
                + "an authoritative per-object release decision. Intelligent "
                + "Recycling is optional; all eligible non-quality hauling "
                + "continues under Auto Hauling's normal master and item "
                + "settings.");
            context.AddInfo(
                IconId.CInfo,
                "Intelligent Recycling is optional",
                "Non-quality hauling remains available",
                "Installing or restoring a compatible Intelligent Recycling "
                + "API enables quality-object decisions. It is not required "
                + "for Auto Hauling's ordinary non-quality functionality.");
        }

        private static string AlertText(string text) {
            return "<color=" + AlertColor + ">" + text + "</color>";
        }

        private string BuildShortcutStatusSummary() {
            if (allowForbiddenShortcut.IsCapturing) {
                return "Waiting for key";
            }
            string statusMessage = allowForbiddenShortcut.StatusMessage
                ?? string.Empty;
            if (statusMessage.IndexOf(
                    "conflict", StringComparison.OrdinalIgnoreCase) >= 0
                    || statusMessage.IndexOf(
                        "Cannot assign", StringComparison.OrdinalIgnoreCase)
                        >= 0) {
                return "Shortcut conflict";
            }
            return allowForbiddenShortcut.HasShortcut
                ? "Ready"
                : "Not assigned";
        }

        private static void AddScrollButton(
                PanelContentContext context,
                RectTransform parent,
                ref int row,
                string icon,
                string label,
                string value,
                Action clicked,
                string tooltip) {
            WButtonIconText2 result = UISpawn.Widget<WButtonIconText2>(
                context.Lifetime,
                UIPrefabId.WContent4Button,
                parent,
                row++);
            result.Load(icon, label, value, clicked);
            UIComponentFactory.SetTip(result, tooltip);
        }

        private static WIconText2 AddScrollInfo(
                PanelContentContext context,
                RectTransform parent,
                ref int row,
                string icon,
                string label,
                string value,
                string tooltip) {
            WIconText2 result = UISpawn.Widget<WIconText2>(
                context.Lifetime,
                UIPrefabId.WContent2Text2,
                parent,
                row++);
            result.Load(icon, label, value);
            UIComponentFactory.SetTip(result, tooltip);
            return result;
        }

        private void BuildFailures(PanelContentContext context) {
            // The list is already bounded and ordered by capture time. Render
            // newest-first without changing the persisted diagnostic order.
            context.AddHeader("Recent vanilla Store failures");
            context.AddInfo(
                IconId.CInfo,
                "Diagnostic policy",
                "Core reasons; no forced retry",
                "Auto Hauling records failure reasons only for vanilla goals "
                + "that it created. Interrupted and cancelled tasks are not "
                + "misreported as failures. Stardeus owns retry behavior. "
                + "When Core includes a map coordinate or linked being name, "
                + "click that exact text to focus its current location.");
            context.AddInfo(
                IconId.CCount,
                "Retained records",
                system.FailureHistory.Count + " / 50");
            if (system.FailureHistory.Count > 0) {
                context.AddButton(
                    IconId.CStop,
                    "Clear failure history",
                    string.Empty,
                    system.ClearFailureHistory,
                    "Clears Auto Hauling's diagnostic list only. Existing "
                    + "vanilla tasks are not changed.");
            }

            VerticalScrollViewResult scroll = context.AddVerticalScroll(
                "AutoHaulingFailures",
                500f,
                820f,
                1f,
                failureScrollPosition,
                value => failureScrollPosition = value);
            LayoutElement layout = scroll.Root.GetComponent<LayoutElement>();
            if (layout != null) {
                layout.minHeight = 320f;
                layout.preferredHeight = 650f;
            }

            int row = 0;
            foreach (HaulFailureRecord failure
                    in system.FailureHistory.Reverse()) {
                string suffix = failure.RepeatCount > 1
                    ? " (x" + failure.RepeatCount + ")"
                    : string.Empty;
                string tooltip = "Reason: " + failure.Reason
                    + "\nStable ID: " + failure.ItemId
                    + "\nPosition: " + failure.Position
                    + "\nState: " + failure.State
                    + "\nAttempt: " + failure.Attempt
                    + "\nVanilla retry pending: "
                    + (failure.Retrying ? "yes" : "no")
                    + "\nGame tick: " + failure.Tick
                    + "\nGoal ID: " + failure.GoalId;
                WIconText2 failureRow = AddScrollInfo(
                    context,
                    scroll.Content,
                    ref row,
                    IconId.CWarning,
                    failure.ItemName + suffix,
                    failure.Reason,
                    tooltip);
                FailureTextLinkHandler linkHandler =
                    failureRow.Text2.GetComponent<FailureTextLinkHandler>();
                if (linkHandler == null) {
                    linkHandler = failureRow.Text2.gameObject
                        .AddComponent<FailureTextLinkHandler>();
                }
                linkHandler.Initialize(
                    failureRow.Text2,
                    failureRow.GetComponent<RectTransform>(),
                    scroll.Scroll?.viewport,
                    failure.Reason);
            }
            if (system.FailureHistory.Count == 0) {
                AddScrollInfo(
                    context,
                    scroll.Content,
                    ref row,
                    IconId.CCheck,
                    "No recorded haul failures.",
                    string.Empty,
                    null);
            }
        }
    }
}
