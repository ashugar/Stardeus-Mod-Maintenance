using System;
using System.Collections.Generic;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using UnityEngine;

namespace Game.Systems.IntelligentRecycling {
    internal sealed class IntelligentRecyclingPanel : IDisposable {
        private const string MainTab = "Main";
        private const string SettingsTab = "Settings";
        private static readonly PanelTabDefinition[] Tabs = {
            new PanelTabDefinition { Id = MainTab, Label = "Main" },
            new PanelTabDefinition { Id = SettingsTab, Label = "Settings" }
        };

        private readonly IntelligentRecyclingSys system;
        private readonly IShortcutHandle panelShortcut;
        private readonly FrameworkPanel panel;
        private readonly Dictionary<string, WIconText2> statisticRows = new();
        private string activeTab = MainTab;
        private const int WeaponsPerPage = 12;
        private bool weaponsExpanded;
        private string weaponSearch = string.Empty;
        private int weaponPage;

        public bool IsOpen => panel.IsOpen;

        public IntelligentRecyclingPanel(IntelligentRecyclingSys system,
                IShortcutHandle panelShortcut) {
            this.system = system;
            this.panelShortcut = panelShortcut;
            panel = new FrameworkPanel(new FrameworkPanelOptions {
                Id = "IntelligentRecycling.Controls",
                Title = "Intelligent Recycling",
                MinSize = new Vector2(400f, 300f),
                MaxSize = new Vector2(1400f, 1000f),
                RememberGeometry = true
            }, Build);
            panelShortcut.StateChanged += Refresh;
        }

        public void Show() => panel.Show();
        internal void ShowSettings() {
            activeTab = SettingsTab;
            if (panel.IsOpen) panel.Refresh();
            else panel.Show();
        }
        public void Close() => panel.Close();
        public void Toggle() => panel.Toggle();
        public void Refresh() => panel.Refresh();
        public void Dispose() {
            panelShortcut.StateChanged -= Refresh;
            panel.Dispose();
        }

        public void RefreshStatistics() {
            if (!panel.IsOpen || activeTab != MainTab) return;
            UpdateResultRow("Active tracked tasks", system.ActiveTrackedTasks);
            UpdateResultRow("Biological tasks queued", system.BiologicalTasksQueued);
            UpdateResultRow("Robotic tasks queued", system.RoboticTasksQueued);
            UpdateResultRow("Equipment tasks queued", system.EquipmentTasksQueued);
            UpdateResultRow("Lost Shuttle replacements queued",
                system.LostShuttleReplacementsQueued);
            UpdateResultRow("Eligible loose items checked", system.LastEligibleLoose);
            UpdateResultRow("Queued by the last scan", system.LastQueued);
            UpdateResultRow("Above processor threshold", system.LastAboveThreshold);
            UpdateResultRow("No compatible processor", system.LastNoCompatibleProcessor);
            UpdateResultRow("Protected Brain/Core", system.LastProtectedMindParts);
            UpdateResultRow("Busy or already assigned", system.LastBlockedByTask);
        }

        private void Build(PanelContentContext context) {
            statisticRows.Clear();
            context.AddTabs(Tabs, activeTab, value => activeTab = value);
            if (activeTab == MainTab) DrawMain(context);
            else DrawSettings(context);
        }

        private void DrawMain(PanelContentContext context) {
            AddResultRow(context, "Active tracked tasks", system.ActiveTrackedTasks);
            AddResultRow(context, "Biological tasks queued", system.BiologicalTasksQueued);
            AddResultRow(context, "Robotic tasks queued", system.RoboticTasksQueued);
            AddResultRow(context, "Equipment tasks queued", system.EquipmentTasksQueued);
            AddResultRow(context, "Lost Shuttle replacements queued",
                system.LostShuttleReplacementsQueued);
            context.AddButton(IconId.WCancel, "Reset queued statistics", "Clear",
                system.ResetQueuedStatistics,
                "Clears the accumulated Biological, Robotic, Equipment, and "
                + "lost Shuttle replacement totals. Active tracked tasks, "
                + "current orders, settings, and last-scan results are unchanged.",
                UIPrefabId.WContent2Button);
            AddResultRow(context, "Eligible loose items checked", system.LastEligibleLoose);
            AddResultRow(context, "Queued by the last scan", system.LastQueued);
            AddResultRow(context, "Above processor threshold", system.LastAboveThreshold);
            AddResultRow(context, "No compatible processor", system.LastNoCompatibleProcessor);
            AddResultRow(context, "Protected Brain/Core", system.LastProtectedMindParts);
            AddResultRow(context, "Busy or already assigned", system.LastBlockedByTask);
        }

        private void DrawSettings(PanelContentContext context) {
            context.AddHeader("Automation");
            RectTransform automation = context.AddResponsiveFlow(
                "IR_AutomationCards", new Vector2(200f, 69f),
                new Vector2(6f, 6f), new RectOffset(6, 6, 6, 6));
            AddSettingCard(automation, "Automation", system.Enabled,
                system.SetEnabled, "Master switch for Intelligent Recycling.");
            AddSettingCard(automation, "Biological body parts",
                system.RecycleBiologicalParts, system.SetBiological,
                "Send qualifying loose biological parts to a Nutrient Extractor.");
            AddSettingCard(automation, "Robotic body parts",
                system.RecycleRoboticParts, system.SetRobotic,
                "Send qualifying loose robotic parts to a Disassembler.");
            AddSettingCard(automation, "Equipment", system.RecycleEquipment,
                system.SetEquipment,
                "Send qualifying loose equipment to a Disassembler.");
            AddSettingCard(automation, "Alien eggs", system.RecycleAlienEggs,
                system.SetAlienEggs,
                "Queue intact Alien Eggs for a Nutrient Extractor. Eggs can still "
                + "hatch before disposal. Disabled by default.");
            AddSettingCard(automation, "Replace storage-haul tasks",
                system.ReplaceStorageHaulTasks, system.SetReplaceStorageHaulTasks,
                "Cancel only unassigned storage-haul tasks for qualifying loose "
                + "items and replace them with recycling tasks. Assigned hauling "
                + "is never interrupted. Leave disabled to preserve every "
                + "storage-haul task.");
            AddSettingCard(automation, "Hullpiercers", system.RecycleHullpiercers,
                system.SetHullpiercers,
                "Automatically send landed Hullpiercers to a Disassembler. "
                + "This option is enabled by default.");
            AddSettingCard(automation, "Opened storage capsules",
                system.RecycleOpenedStorageCapsules,
                system.SetOpenedStorageCapsules,
                "Automatically send Storage Capsules to a Disassembler, but "
                + "only after they have been opened and Stardeus exposes the "
                + "Disassemble action. This option is enabled by default.");
            AddSettingCard(automation, "Opened Safe Boxes",
                system.RecycleOpenedSafeBoxes,
                system.SetOpenedSafeBoxes,
                "Automatically send Safe Boxes to a Disassembler, but only "
                + "after they have been opened and Stardeus exposes the "
                + "Disassemble action. This option is enabled by default.");
            AddSettingCard(automation, "Stasis pods", system.DeconstructStasisPods,
                system.SetStasisPods,
                "Automatically deconstruct Stasis Pods only after Core's "
                + "Waiting countdown has completed and the pod has opened. "
                + "Pods offering or conducting Merchant/Trade activity are "
                + "always protected. This option is enabled by default.");
            AddSettingCard(automation, "Breach capsule ports",
                system.DeconstructBreachCapsulePorts,
                system.SetBreachCapsulePorts,
                "Automatically deconstruct Breach Capsule Ports, but only after "
                + "Stardeus has released the raiders (or resolved a failed raid "
                + "spawn). This option is enabled by default.");
            AddSettingCard(automation, "Artillery Fungus",
                system.RecycleArtilleryFungus,
                system.SetArtilleryFungus,
                "Automatically send active Artillery Fungus to a Nutrient "
                + "Extractor at any growth stage. Quality thresholds do not "
                + "apply, but a constructed Nutrient Extractor is still "
                + "required. This option is enabled by default.");
            AddSettingCard(automation, "Dead Crawlers",
                system.DisposeDeadCrawlers,
                system.SetDeadCrawlers,
                "Automatically send dead Crawler corpses to a Nutrient "
                + "Extractor using Stardeus's vanilla Dispose task. Living "
                + "and incapacitated Crawlers are never targeted. A constructed "
                + "Nutrient Extractor is required. This option is enabled by "
                + "default.");
            AddSettingCard(automation, "Lost Shuttle pads",
                system.ReplaceLostShuttles,
                system.SetReplaceLostShuttles,
                system.BuildLostShuttleReplacementTooltip());
            AddSettingCard(automation, "Biological corpses",
                system.RecycleBiologicalCorpses, system.SetBiologicalCorpses,
                "Dispose dead biological bodies only after all removable parts "
                + "are gone. Welded parts, including welded Brain/Core, may remain. "
                + "Uses a Nutrient Extractor and native disposal; no automatic harvesting. "
                + "Dead Crawlers retain their separate setting. Disabled by default.");
            AddSettingCard(automation, "Robotic corpses",
                system.RecycleRoboticCorpses, system.SetRoboticCorpses,
                "Disassemble genuinely dead robotic bodies only after all removable "
                + "parts are gone. Welded parts, including welded Core, may remain. "
                + "Uses a Disassembler and native disposal; no automatic harvesting. "
                + "Disabled by default.");

            if (system.IsAlienEggReminderAwaitingDecision) {
                context.AddButton(IconId.CInfo, "Keep automatic egg disposal", "Keep enabled",
                    system.KeepAlienEggDisposalAfterReminder,
                    "Alien Datoids require Egg Shell and Crawler Goo. Turn off Alien eggs above "
                    + "to keep eggs for those materials, or keep disposal enabled.");
            }

            context.AddHeader("Quality thresholds");
            context.AddInfo(IconId.CInfo, "Shared processor thresholds",
                "Bidirectional",
                "Changing a threshold here updates every processor in that "
                + "family. Changing a processor's threshold updates Intelligent "
                + "Recycling and the other processors in the same family.");
            RectTransform thresholds = context.AddResponsiveFlow(
                "IR_QualityThresholds", new Vector2(320f, 69f),
                new Vector2(6f, 6f), new RectOffset(6, 6, 6, 6));
            AddThresholdSlider(context, thresholds,
                "Biological quality threshold",
                system.BiologicalQualityThreshold,
                system.SetBiologicalQualityThreshold,
                "Recycle qualifying biological body parts at or below this "
                + "Quality. The value is shared with every Nutrient Extractor.");
            AddThresholdSlider(context, thresholds,
                "Robotic/equipment quality threshold",
                system.RoboticQualityThreshold,
                system.SetRoboticQualityThreshold,
                "Recycle qualifying robotic body parts and equipment at or "
                + "below this Quality. The value is shared with every "
                + "Disassembler.");

            context.AddHeader("Body part categories");
            RectTransform bodyParts = context.AddResponsiveFlow(
                "IR_BodyPartCards", new Vector2(124f, 69f),
                new Vector2(6f, 6f), new RectOffset(6, 6, 6, 6));
            bool themeAwareBodyPartCards = AutoUtilitiesApi.IsFeatureAvailable(
                IntelligentRecyclingSys.ModId, "1.4.0");
            foreach (IntelligentRecyclingSys.BodyPartCategoryInfo category
                    in system.GetBodyPartCategories()) {
                string id = category.Id;
                IntelligentRecyclingBodyPartCard.Create(bodyParts, category.Name,
                    category.BiologicalIcon, category.RoboticIcon,
                    system.IsBiologicalCategoryEnabled(id),
                    system.IsRoboticCategoryEnabled(id),
                    system.Enabled && system.RecycleBiologicalParts,
                    system.Enabled && system.RecycleRoboticParts,
                    () => system.SetBiologicalCategory(id,
                        !system.IsBiologicalCategoryEnabled(id)),
                    () => system.SetRoboticCategory(id,
                        !system.IsRoboticCategoryEnabled(id)),
                    themeAwareBodyPartCards);
            }

            context.AddHeader("Equipment categories");
            RectTransform equipment = context.AddResponsiveFlow(
                "IR_EquipmentCards", new Vector2(124f, 69f),
                new Vector2(6f, 6f), new RectOffset(6, 6, 6, 6));
            foreach (KeyValuePair<string, string> category
                    in system.GetEquipmentCategories()) {
                string id = category.Key;
                AddSettingCard(equipment, category.Value,
                    system.IsEquipmentCategoryEnabled(id),
                    value => system.SetEquipmentCategory(id, value),
                    $"Allow qualifying equipment in the Inventory category "
                    + $"'{category.Value}'.",
                    system.Enabled && system.RecycleEquipment);
            }

            context.AddHeader("Equipment condition");
            context.AddToggle(IconId.CInfo, "Second-hand equipment",
                system.AllowSecondHandEquipment ? "On" : "Off",
                system.AllowSecondHandEquipment, system.SetAllowSecondHandEquipment,
                "Allow qualifying worn gear that Core classifies as second-hand.");
            context.AddToggle(IconId.CInfo, "Belonged to a dead person",
                system.AllowDeadMansEquipment ? "On" : "Off",
                system.AllowDeadMansEquipment, system.SetAllowDeadMansEquipment,
                "Allow qualifying gear with the dead-owner flag. Forbidden items remain protected.");
            DrawWeaponChecklist(context);

            context.AddHeader("Keyboard shortcut");
            context.AddInfo(IconId.CInfo, "Open/close panel",
                panelShortcut.DisplayName,
                "The assigned key opens or closes Intelligent Recycling. "
                + "AUMF stores the assignment globally and prevents conflicts "
                + "with Stardeus and other registered mod shortcuts.");
            context.AddButton(IconId.CCog,
                panelShortcut.IsCapturing
                    ? "Cancel shortcut capture"
                    : "Set panel shortcut",
                panelShortcut.IsCapturing ? "Cancel" : "Assign",
                () => {
                    if (panelShortcut.IsCapturing) {
                        panelShortcut.CancelCapture();
                    } else {
                        panelShortcut.BeginCapture();
                    }
                },
                panelShortcut.IsCapturing
                    ? "Cancel without changing the currently assigned key."
                    : "Choose one unmodified keyboard key. Escape cancels; "
                        + "conflicting and reserved keys are rejected.");
            context.AddButton(IconId.WCancel, "Clear panel shortcut", "Clear",
                panelShortcut.Clear,
                "Remove the assigned key. The Recycling widget remains "
                + "available for opening the panel.");
            context.AddInfo(IconId.CInfo, "Shortcut status",
                BuildShortcutStatusSummary(), panelShortcut.StatusMessage);
        }

        private void DrawWeaponChecklist(PanelContentContext context) {
            var weapons = new List<KeyValuePair<string, string>>(
                system.GetWeaponDefinitions());
            int selected = 0;
            foreach (var weapon in weapons) {
                if (system.IsWeaponDefinitionSelected(weapon.Key)) selected++;
            }
            context.AddCollapsibleHeader($"Weapon checklist ({selected} selected)",
                weaponsExpanded, value => weaponsExpanded = value);
            if (!weaponsExpanded) return;

            context.AddToggle(IconId.CFilter, "Use weapon checklist",
                system.FilterWeaponDefinitions ? "On" : "Off",
                system.FilterWeaponDefinitions, system.SetFilterWeaponDefinitions,
                "When on, only checked weapons can qualify. When off, the existing Weapon category rules apply.");
            context.AddToggle(IconId.CCog, "Follow quality threshold",
                system.WeaponsFollowQuality ? "On" : "Off",
                system.WeaponsFollowQuality, system.SetWeaponsFollowQuality,
                "Applies to all checked weapons when the checklist is in use. Other equipment still follows its usual quality rules.");
            context.AddInput(IconId.CFilter, "Search weapon names or IDs",
                weaponSearch, value => {
                    string search = value?.Trim() ?? string.Empty;
                    if (search == weaponSearch) return;
                    weaponSearch = search;
                    weaponPage = 0;
                    panel.Refresh();
                }, tooltip: "Press Enter or leave the search field to filter.");
            if (weaponSearch.Length > 0) {
                weapons.RemoveAll(item => (item.Value ?? item.Key).IndexOf(weaponSearch,
                    StringComparison.CurrentCultureIgnoreCase) < 0
                    && item.Key.IndexOf(weaponSearch, StringComparison.OrdinalIgnoreCase) < 0);
            }
            weapons.Sort((left, right) => {
                int order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Value, right.Value);
                return order != 0 ? order : StringComparer.Ordinal.Compare(left.Key, right.Key);
            });
            if (weapons.Count == 0) {
                context.AddInfo(IconId.CInfo, "No matching weapons", "");
                return;
            }
            int pages = (weapons.Count + WeaponsPerPage - 1) / WeaponsPerPage;
            weaponPage = Math.Max(0, Math.Min(weaponPage, pages - 1));
            context.AddInfo(IconId.CCount, "Matching weapons",
                $"{weapons.Count} · Page {weaponPage + 1} of {pages}");
            if (weaponPage > 0) {
                context.AddButton(IconId.CFilter, "Previous page", "", () => {
                    weaponPage--;
                    panel.Refresh();
                });
            }
            if (weaponPage + 1 < pages) {
                context.AddButton(IconId.CFilter, "Next page", "", () => {
                    weaponPage++;
                    panel.Refresh();
                });
            }
            int start = weaponPage * WeaponsPerPage;
            for (int i = start; i < Math.Min(start + WeaponsPerPage, weapons.Count); i++) {
                var weapon = weapons[i];
                bool checkedWeapon = system.IsWeaponDefinitionSelected(weapon.Key);
                context.AddToggle(IconId.CFilter, weapon.Value ?? weapon.Key,
                    checkedWeapon ? "Selected" : "", checkedWeapon,
                    value => {
                        system.SetWeaponDefinitionSelected(weapon.Key, value);
                        panel.Refresh();
                    },
                    weapon.Key);
            }
        }

        private void AddResultRow(PanelContentContext context,
                string label, int value) {
            statisticRows[label] =
                context.AddInfo(IconId.CInfo, label, value.ToString());
        }

        private void UpdateResultRow(string label, int value) {
            if (statisticRows.TryGetValue(label, out WIconText2 row)
                    && row != null) {
                row.Load(IconId.CInfo, label, value.ToString());
            }
        }

        private string BuildShortcutStatusSummary() {
            if (panelShortcut.IsCapturing) {
                return "Waiting for key";
            }
            if (panelShortcut.StatusMessage.IndexOf("conflict",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || panelShortcut.StatusMessage.IndexOf("Cannot assign",
                        StringComparison.OrdinalIgnoreCase) >= 0) {
                return "Shortcut conflict";
            }
            return panelShortcut.HasShortcut ? "Ready" : "Not assigned";
        }

        private static void AddSettingCard(RectTransform parent, string label,
                bool enabled, Action<bool> setter, string tooltip,
                bool available = true) {
            IntelligentRecyclingToggleCard.Create(parent, label, enabled,
                () => setter(!enabled), tooltip, available);
        }

        private static void AddThresholdSlider(PanelContentContext context,
                RectTransform parent, string label, float value,
                Action<float> setter, string tooltip) {
            WSliderWithIconText2 slider = UISpawn.Widget<WSliderWithIconText2>(
                context.Lifetime, "WContent2Text2Slider", parent,
                parent.childCount);
            slider.Item.Load(IconId.CCog, label, Units.Percentage(value));
            slider.Slider.Load(value, changed => {
                setter(changed);
                slider.Item.SetText2(Units.Percentage(changed));
            });
            UIUtils.AddTip(context.Lifetime, slider, tooltip);
        }
    }
}
