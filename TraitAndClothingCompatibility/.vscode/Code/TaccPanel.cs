using System;
using System.Linq;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Systems.UIUX;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TraitAndClothingCompatibility {
    internal sealed class TaccPanel : IDisposable {
        private static readonly PanelTabDefinition[] Tabs = {
            new PanelTabDefinition { Id = "Traits", Label = "Traits & log" },
            new PanelTabDefinition { Id = "Policies", Label = "Policies" },
            new PanelTabDefinition { Id = "Colonists", Label = "Colonists" }
        };
        private readonly TaccSystem system;
        private readonly FrameworkPanel panel;
        private string activeTab = "Traits";
        private float logPosition = 1f;
        private float beingsPosition = 1f;
        private string search = "";
        private int page;
        private const int PageSize = 40;

        internal TaccPanel(TaccSystem system) {
            this.system = system;
            panel = new FrameworkPanel(new FrameworkPanelOptions {
                Id = TaccSystem.ConsumerId + ".Panel",
                Title = "TACC — Trait and Clothing Compatibility",
                MinSize = new Vector2(680f, 550f), MaxSize = new Vector2(1400f, 1100f), RememberGeometry = true
            }, Build);
        }

        internal void Toggle() => panel.Toggle();
        internal void Close() => panel.Close();
        internal void Refresh() => panel.Refresh();
        public void Dispose() => panel.Dispose();

        private void Build(PanelContentContext ui) {
            ui.AddTabs(Tabs, activeTab, tab => activeTab = tab);
            if (system.Failure != null) Block(ui, system.Failure);
            if (activeTab == "Traits") BuildTraits(ui);
            else if (activeTab == "Policies") BuildPolicies(ui);
            else BuildColonists(ui);
        }

        private void BuildTraits(PanelContentContext ui) {
            ui.AddHeader("Known equipment effects");
            Block(ui, "Reference values checked against Core " + TraitCatalog.TestedCoreVersion + ". Base mood for one affected item. New clothing adds no origin effect. "
                + "Second-hand, corpse-origin, and tattered gear produce separate effects. Each category adds up to two points in the same direction for extra items.");
            ui.AddInfo(IconId.CRecycling, "Thrifty", "Used +3  •  Dead −3  •  Tattered −3",
                "Likes second-hand clothing and is less bothered by corpse-origin gear. Both effects can be present together.");
            ui.AddInfo(IconId.CClothing, "Fastidious", "Used −8  •  Dead −10  •  Tattered −6",
                "Stronger penalties. Core also refuses second-hand and corpse-origin gear when this being selects new equipment.");
            ui.AddInfo(IconId.CMoodGood, "Optimist", "+1 to each emitted mood effect",
                "Applied after Core's clothing/count calculation. Does not create a bonus for new clothing.");
            ui.AddInfo(IconId.CMoodBad, "Pessimist", "−1 to each emitted mood effect",
                "Applied after Core's clothing/count calculation.");
            ui.AddInfo(IconId.CInfo, "Other traits", "Used −4  •  Dead −6  •  Tattered −3",
                "Psychopath, Sociopath, and Cleaning OCD provide no special exemption. Minds without mental health receive no clothing mood effects.");
            ui.AddHeader("Compatibility diagnostic log");
            Block(ui, TaccSystem.Disclaimer);
            ui.AddButton(IconId.CInfo, "Recheck traits and policies", "Check now", system.Recheck,
                "Compare traits, clothing-effect values, code, and patches with TACC's validated reference; refresh per-being policies.", UIPrefabId.WContent2Button);
            ui.AddButton(IconId.CInfo, "Copy diagnostic log", system.Diagnostics.Active.Count + " unvalidated",
                () => GUIUtility.systemCopyBuffer = system.CopyLogText(), "Copies the current log as plain text.", UIPrefabId.WContent2Button);
            ui.AddButton(IconId.WCancel, "Clear diagnostic log", "Clear", () => { logPosition = 1f; system.ClearLog(); },
                "Clears history and checks again immediately. Unknown or changed traits and equipment effects return until a TACC update validates them.", UIPrefabId.WContent2Button);
            var scroll = ui.AddVerticalScroll("TaccDiagnosticLog", 520f, 1000f, 1f, logPosition, value => logPosition = value);
            var layout = scroll.Root.GetComponent<LayoutElement>() ?? scroll.Root.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 140f; layout.preferredHeight = 240f; layout.flexibleHeight = 1f;
            int row = 0;
            Block(ui, "Current unvalidated findings: " + system.Diagnostics.Active.Count
                + "\nClearing the log does not validate a finding. Only a TACC update can add a reviewed comparison.", scroll.Content, row++);
            if (!system.DiagnosticScanSucceeded)
                Block(ui, "The latest comparison has not completed successfully. Existing findings may be incomplete; check the history below.", scroll.Content, row++);
            foreach (var entry in system.Diagnostics.Active.OrderBy(e => e.Subject, StringComparer.Ordinal))
                Block(ui, entry.Time + "  |  " + entry.Kind + "\n" + entry.Subject + "\n" + entry.Detail, scroll.Content, row++);
            var history = system.Diagnostics.History(system.State.Log).ToList();
            if (history.Count > 0) Block(ui, "History — older findings below may no longer apply.", scroll.Content, row++);
            foreach (var entry in history)
                Block(ui, entry.Time + "  |  " + entry.Kind + "\n" + entry.Subject + "\n" + entry.Detail, scroll.Content, row++);
        }

        private void BuildPolicies(PanelContentContext ui) {
            ui.AddHeader("Automatic clothing policies");
            ui.AddToggle(IconId.CClothing, "Enable TACC", system.State.Enabled ? "On" : "Off", system.State.Enabled, system.SetEnabled,
                "Updates living player colonists after loading, recruitment, trait changes, and mind transfers. Turning this off restores unchanged TACC-owned policies.");
            ui.AddInfo(IconId.CInfo, "Last policy pass", system.Rows.Count + " colonists • " + system.LastPassMs.ToString("0.00") + " ms",
                "Measured duration of the latest policy reconciliation. Equipment searching, hauling, and changing clothes are subsequent Core work.");
            ui.AddInfo(IconId.CInfo, "Last trait catalog check", system.LastCatalogMs.ToString("0.00") + " ms");
            ui.AddToggle(IconId.WCancel, "Allow forbidden loose equipment",
                system.State.AllowForbiddenEquipment ? "Allow" : "Respect marker",
                system.State.AllowForbiddenEquipment, system.SetAllowForbiddenEquipment,
                "When enabled, TACC-managed colonists may equip compatible loose items carrying the red forbidden marker. "
                + "The marker stays in place and ordinary hauling still respects it. Turning this off restores Core's normal equipment-search exclusion.");
            ui.AddInfo(IconId.CMoodGood, "Managed clothing priority", "Wearer mood first",
                "TACC compares the total clothing mood result for the individual before Core's normal item ranking. "
                + "Thrifty beings prefer a second-hand mood bonus; other beings prefer avoiding penalties. Equal-mood choices use Core's value-and-quality comparison.");
            ui.AddInfo(IconId.CClothing, "Empty clothing acquisition", "Priority 5",
                "An empty automatic worn slot becomes the managed being's highest-priority personal task. A completely naked being may interrupt ordinary work after Core's grace period when compatible clothing is already available. "
                + "Upgrades and replacements for filled slots retain Core's normal low priority.");
            Block(ui, "Manual equipment slots are respected. Existing weapon, color, item, replacement, and stricter quality choices are preserved. "
                + "Core's default is to keep currently worn clothing until a replacement is available. "
                + "Fastidious beings still refuse to select used gear under Core's trait rule, even if TACC's used-gear restrictions are turned off. "
                + "Core's Forbid Dropped Weapon setting controls the marker assigned when a weapon is automatically dropped; it does not control loose clothing.");
            ui.AddHeader("Washing Machine");
            ui.AddToggle(IconId.CRecycling, "Automatic corpse-origin washing",
                system.State.AutomaticWashing ? "On" : "Off",
                system.State.AutomaticWashing, system.SetAutomaticWashing,
                "When enabled, powered idle Washing Machines retrieve eligible corpse-origin hats, helmets, suits, and clothing from object storage. "
                + "Disabling this stops new automatic orders; manual Wash orders and work already in progress remain available.");
            Block(ui, "Washing replaces Taken from the dead with Second-hand without changing item quality. "
                + "Select loose clothing to use the Wash action manually; mixed and multi-selections ignore ineligible objects.");
            foreach (var profile in new[] { WearerProfile.Thrifty, WearerProfile.Fastidious, WearerProfile.Ordinary }) {
                var captured = profile;
                var rule = system.Rule(profile);
                ui.AddHeader(TaccSystem.ProfileName(profile));
                ui.AddToggle(IconId.CRecycling, "Forbid second-hand", rule.ForbidSecondHand ? "Forbid" : "Allow", rule.ForbidSecondHand,
                    value => system.ChangeRule(captured, "SecondHand", value), "Core's second-hand restriction also excludes another being's corpse gear.");
                ui.AddToggle(IconId.CSkull, "Forbid corpse-origin", rule.ForbidDeadMans ? "Forbid" : "Allow", rule.ForbidDeadMans,
                    value => system.ChangeRule(captured, "DeadMans", value), "Excludes equipment marked Taken from the dead.");
                ui.AddToggle(IconId.CTattered, "Avoid tattered clothing", rule.AvoidTattered ? "Above " + Percent(system.TatteredThreshold) : "Use existing quality", rule.AvoidTattered,
                    value => system.ChangeRule(captured, "Tattered", value), "Raises the minimum to strictly above Core's tattered threshold, keeping an existing stricter minimum.");
            }
            ui.AddHeader("Minds without mental health");
            Block(ui, "TACC adds no mood-based restrictions to these beings. Their existing equipment policies continue to apply.");
            ui.AddButton(IconId.CInfo, "Restore default profile rules", "Defaults", system.ResetRules,
                "Restores the Thrifty, Fastidious, and other-trait defaults. Individual opt-outs are kept.", UIPrefabId.WContent2Button);
        }

        private void BuildColonists(PanelContentContext ui) {
            ui.AddHeader("Per-being management");
            Block(ui, "Switch a being off to restore their prior policy. Editing an individual equipment policy outside TACC pauses that being's automation automatically. "
                + "Manual slots remain manual even while TACC manages the being's other slots.");
            ui.AddInput(IconId.CInfo, "Search names or traits", search, value => { search = value ?? ""; page = 0; beingsPosition = 1f; panel.Refresh(); });
            var filtered = system.Rows.Where(r => string.IsNullOrWhiteSpace(search)
                || r.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                || r.Traits.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            int pages = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
            page = Math.Min(page, pages - 1);
            ui.AddInfo(IconId.CInfo, "Colonists", filtered.Count + " • page " + (page + 1) + "/" + pages);
            if (page > 0) ui.AddButton(IconId.CInfo, "Previous page", "Previous", () => { page--; beingsPosition = 1f; panel.Refresh(); }, null, UIPrefabId.WContent2Button);
            if (page + 1 < pages) ui.AddButton(IconId.CInfo, "Next page", "Next", () => { page++; beingsPosition = 1f; panel.Refresh(); }, null, UIPrefabId.WContent2Button);
            var scroll = ui.AddVerticalScroll("TaccColonists", 520f, 1000f, 1f, beingsPosition, value => beingsPosition = value);
            var layout = scroll.Root.GetComponent<LayoutElement>() ?? scroll.Root.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 220f; layout.preferredHeight = 400f; layout.flexibleHeight = 1f;
            if (filtered.Count == 0) Block(ui, "No matching player colonists with clothing slots are available.", scroll.Content, 0);
            foreach (var row in filtered.Skip(page * PageSize).Take(PageSize)) {
                uint id = row.MindId;
                bool enabled = system.IsManagedChoice(id);
                var toggle = ui.AddToggle(IconId.CClothing, Safe(row.Name), TaccSystem.ProfileName(row.Profile), enabled,
                    value => system.SetManaged(id, value), Safe(row.Status + "\nTraits: " + row.Traits));
                toggle.transform.SetParent(scroll.Content, false);
                var status = ui.AddInfo(IconId.CInfo, "Status", Safe(row.Status));
                status.transform.SetParent(scroll.Content, false);
            }
        }

        private static string Percent(float value) => (value * 100f).ToString("0.##") + "%";
        private static string Safe(string value) => (value ?? "").Replace("<", "‹").Replace(">", "›");

        private static void Block(PanelContentContext ui, string text, RectTransform parent = null, int order = -1) {
            if (parent == null) {
                GameObject owned = ui.CreateOwned("TaccParagraph");
                parent = owned.AddComponent<RectTransform>();
                var group = owned.AddComponent<VerticalLayoutGroup>();
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = true;
                group.childForceExpandHeight = false;
                owned.AddComponent<LayoutElement>().flexibleHeight = 0f;
                order = 0;
            }
            WText row = UISpawn.Widget<WText>(ui.Lifetime, UIPrefabId.WContent2TextBlock,
                parent, Math.Max(0, order));
            row.Load(Safe(text));
            row.Text.textWrappingMode = TextWrappingModes.Normal;
            row.Text.overflowMode = TextOverflowModes.Overflow;
            row.Text.alignment = TextAlignmentOptions.TopLeft;
            var layout = row.GetComponent<LayoutElement>() ?? row.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 36f;
            layout.preferredHeight = -1f;
            layout.flexibleHeight = 0f;
        }
    }
}
