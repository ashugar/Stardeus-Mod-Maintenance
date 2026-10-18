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
