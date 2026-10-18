using System;
using AutoUtilitiesModFramework;
using Game.Constants;
using UnityEngine;

namespace Game.Systems.BotAutoHack {
    internal sealed class BotAutoHackPanel : IDisposable {
        private readonly BotAutoHackSys system;
        private readonly FrameworkPanel panel;
        private readonly ITopBarWidgetHandle widget;

        internal BotAutoHackPanel(BotAutoHackSys system) {
            this.system = system;
            panel = new FrameworkPanel(new FrameworkPanelOptions {
                Id = "BotAutoHack.Controls",
                Title = "Bot Auto Hack",
                MinSize = new Vector2(520f, 400f),
                MaxSize = new Vector2(1000f, 800f),
                RememberGeometry = true
            }, BuildPanel);

            widget = AutoUtilitiesApi.RegisterTopBarWidget(
                new TopBarWidgetRegistration {
                    Id = "BotAutoHack.Status",
                    Icon = () => IconId.CRobotEye,
                    Tint = WidgetTint,
                    Tooltip = BuildWidgetTooltip,
                    BadgeText = () => system.ActiveHackCount > 0
                        ? system.ActiveHackCount.ToString()
                        : string.Empty,
                    BadgeTextColor = () => Color.white,
                    OnClick = panel.Toggle,
                    Width = 36f,
                    Order = 315
                });
        }

        internal void RefreshAll() {
            widget?.Refresh();
            panel?.Refresh();
        }

        internal void Close() {
            panel?.Close();
        }

        public void Dispose() {
            panel?.Dispose();
            widget?.Dispose();
        }

        private Color WidgetTint() {
            if (!system.FrameworkReady || !system.ResearchUnlocked) {
                return Color.gray;
            }
            return system.AutomationActive ? Color.green : Color.yellow;
        }

        private string BuildWidgetTooltip() {
            string research = system.ResearchUnlocked
                ? "Signal Intrusion unlocked"
                : "Locked: research Signal Intrusion";
            return "Bot Auto Hack: "
                + (system.AutomationActive ? "ON" : "OFF")
                + "\n" + research
                + "\nBots currently being hacked: "
                + system.ActiveHackCount
                + "\nPending individual hacks: " + system.PendingCount
                + "\nVanilla multi-Hack override: "
                + (system.FrameworkReady ? "ready" : "unavailable")
                + "\nClick for controls.";
        }

        private void BuildPanel(PanelContentContext context) {
            context.AddHeader("Research requirement");
            context.AddInfo(
                system.ResearchUnlocked ? IconId.CCheck : IconId.CWarning,
                "Signal Intrusion",
                system.ResearchUnlocked ? "Unlocked" : "Required",
                "Bot Auto Hack uses the existing Rogue AI Signal Intrusion "
                + "unlock. It does not add or bypass research.");

            context.AddHeader("Automation");
            if (system.ResearchUnlocked) {
                context.AddToggle(
                    IconId.CRobotEye,
                    "Automatically hack hostile bots",
                    system.Enabled ? "On" : "Off",
                    system.Enabled,
                    system.SetEnabled,
                    "When enabled, eligible hostile robotic Beings are added "
                    + "to the individual queue. Turning this off cancels only "
                    + "pending automatic Hack entries, disables Firewall "
                    + "removal automation, and preserves explicit requests.");

                if (system.Enabled) {
                    context.AddToggle(
                        "Icons/Color/BodyRemoveOne",
                        "Remove working Firewall Chips",
                        system.AutoRemoveFirewallEnabled ? "On" : "Off",
                        system.AutoRemoveFirewallEnabled,
                        system.SetAutoRemoveFirewallEnabled,
                        "Orders Core's ordinary Remove operation for the "
                        + "Implant slot of an incapacitated hostile bot with a "
                        + "working Firewall Chip. Existing surgery orders "
                        + "remain visible and cancelable through the vanilla "
                        + "Surgeries interface.");
                } else {
                    context.AddInfo(
                        IconId.CCheckDisabled,
                        "Remove working Firewall Chips",
                        "Requires main automation",
                        "Enable Automatically hack hostile bots first. The "
                        + "Firewall-removal setting defaults off and is turned "
                        + "off whenever main automation is disabled.");
                }

                int firewallCandidates = system.CountFirewallRemovalCandidates();
                context.AddInfo(
                    "Icons/Color/BodyRemoveOne",
                    "Firewall removals available",
                    firewallCandidates.ToString(),
                    "Candidates must be living, hostile, robotic, active, and "
                    + "incapacitated, with a removable working Firewall Chip, "
                    + "no conflicting surgery, and a suitable vanilla surgery "
                    + "facility currently available.");

                int eligible = system.CountEligibleTargets();
                context.AddInfo(
                    IconId.CCount,
                    "Currently eligible hostile bots",
                    eligible.ToString(),
                    "Core eligibility is rechecked immediately before each "
                    + "Hack. Dead, friendly, biological, inactive, coreless, "
                    + "already-Hacked, or working-Firewall-protected Beings "
                    + "are skipped.");
                if (!system.Enabled) {
                    context.AddButton(
                        IconId.CRobotEye,
                        "Hack All Hostile Bots",
                        eligible + " eligible",
                        system.QueueAllManual,
                        "Adds every currently eligible hostile robotic Being "
                        + "to the queue. One vanilla Hack starts per simulation "
                        + "tick so targets do not share a batch outcome.");
                } else {
                    context.AddInfo(
                        IconId.CInfo,
                        "Manual all-bots command",
                        "Automatic mode active",
                        "Turn automation off to expose the explicit Hack All "
                        + "Hostile Bots command.");
                }
            } else {
                context.AddInfo(
                    IconId.CCheckDisabled,
                    "Automation and manual all-bots command",
                    "Inactive",
                    "Research Signal Intrusion in the Rogue AI tree to enable "
                    + "Bot Auto Hack.");
            }

            context.AddHeader("Individual queue");
            context.AddInfo(
                IconId.CRobotEye,
                "Bots currently being hacked",
                system.ActiveHackCount.ToString(),
                "Counts living, active, non-biological Beings carrying "
                + "Core's Hacked condition. This same live count appears "
                + "below the top-bar widget.");
            context.AddInfo(
                IconId.CCount,
                "Pending targets",
                system.PendingCount.ToString(),
                "Pending target IDs and request sources are saved. Eligibility "
                + "is revalidated after loading and before dispatch.");
            context.AddInfo(
                IconId.CTime,
                "Last Core Hack started",
                system.LastDispatchTick > 0
                    ? system.LastStartedTarget + " at tick "
                        + system.LastDispatchTick
                    : "None yet");
            context.AddInfo(
                IconId.CCheck,
                "Started / skipped",
                system.TotalStarted + " / " + system.TotalSkipped,
                "Skipped entries became ineligible before their turn. No "
                + "condition is forced when Core rejects the target.");
            if (system.LastManualQueued > 0) {
                context.AddInfo(
                    IconId.CInfo,
                    "Last manual all-bots request",
                    system.LastManualQueued + " queued");
            }
            if (system.LastMultiSelectionQueued > 0) {
                context.AddInfo(
                    IconId.CInfo,
                    "Last vanilla multi-Hack click",
                    system.LastMultiSelectionQueued + " queued individually");
            }
            if (system.TotalFirewallRemovalsQueued > 0) {
                string lastTarget = string.IsNullOrEmpty(
                    system.LastFirewallRemovalTarget)
                    ? string.Empty
                    : "Last target: " + system.LastFirewallRemovalTarget + ". ";
                context.AddInfo(
                    "Icons/Color/BodyRemoveOne",
                    "Firewall removal surgeries ordered",
                    system.TotalFirewallRemovalsQueued.ToString(),
                    lastTarget + "These are ordinary vanilla surgery operations; the "
                    + "mod never removes the implant directly.");
            }

            context.AddHeader("Compatibility boundary");
            context.AddInfo(
                IconId.CRobotEye,
                "Core Hack consequences",
                "Core native per-bot roll",
                "Each dispatch calls Core's ordinary single-target Hack. The "
                + "existing condition duration, 50% chance, faction change, "
                + "and failure damage remain unchanged. Current Core hashes "
                + "the bot's persistent ID with the Hack start tick. Bot Auto "
                + "Hack does not patch or track the outcome roll. "
                + "Working Firewall Chips make the Hack action unavailable.");
            context.AddInfo(
                IconId.CCheck,
                "Multi-selection Hack button",
                system.FrameworkReady ? "Individual queue active" : "Unavailable",
                "Core still decides whether the button is available and how "
                + "many selected Beings qualify. Only the multi-target click "
                + "is redirected into the one-per-tick queue.");
            context.AddInfo(
                IconId.CWarning,
                "Excluded domains",
                "Ships, colonies, biological Beings",
                "The scanner enumerates only active Being entities and then "
                + "requires a non-biological species and Core Hack eligibility. "
                + "Ship and colony hacking paths are never inspected or patched.");
        }
    }
}
