using System;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Systems.AI;
using Game.Systems.UIUX;
using Game.UI;
using KL.Grid;
using UnityEngine;

namespace SuperCleaning;

internal sealed class SuperCleaningUi : IDisposable
{
    private const int MaximumVisibleOwnedTasks = 50;

    private static readonly PanelTabDefinition[] Tabs =
    {
        new PanelTabDefinition { Id = "Tasks", Label = "Tasks" },
        new PanelTabDefinition { Id = "Settings", Label = "Settings" }
    };

    private readonly SuperCleaningSys system;
    private readonly FrameworkPanel panel;
    private readonly ITopBarWidgetHandle widget;
    private string activeTab = "Tasks";
    private bool idleBotsExpanded;
    private bool dirtHistoryExpanded;
    private bool modTasksExpanded;

    internal SuperCleaningUi(SuperCleaningSys system)
    {
        this.system = system;
        panel = new FrameworkPanel(new FrameworkPanelOptions
        {
            Id = "SuperCleaning.Controls",
            Title = "Super Cleaning",
            MinSize = new Vector2(480f, 400f),
            MaxSize = new Vector2(1000f, 900f),
            RememberGeometry = true
        }, BuildPanel);

        widget = AutoUtilitiesApi.RegisterTopBarWidget(
            new TopBarWidgetRegistration
            {
                Id = "SuperCleaning.Status",
                Icon = () => IconId.CMop,
                Tint = () => system.Enabled ? Color.white : Color.gray,
                Tooltip = BuildTooltip,
                BadgeText = () => system.OwnedTaskCount > 0
                    ? system.OwnedTaskCount.ToString()
                    : string.Empty,
                BadgeTextColor = () => Color.white,
                OnClick = Toggle,
                Width = 36f,
                Order = 30
            });
    }

    internal void Toggle() => panel.Toggle();
    internal void Close() => panel.Close();

    internal void Refresh()
    {
        widget?.Refresh();
        panel?.Refresh();
    }

    private string BuildTooltip()
    {
        return "Super Cleaning: " + (system.Enabled ? "ON" : "OFF")
            + "\nDirt marks: " + system.DirtCount
            + "\nDeployed Cleaning Bots: " + system.DeployedBotCount
            + "\nBots cleaning: " + system.ActiveCleaningBotCount
            + "\nMod-owned tasks: " + system.OwnedTaskCount
            + "\nClick for cleanup controls.";
    }

    private void BuildPanel(PanelContentContext context)
    {
        context.AddTabs(Tabs, activeTab, value => activeTab = value);
        if (activeTab == "Settings") BuildSettings(context);
        else BuildTasks(context);
    }

    private void BuildTasks(PanelContentContext context)
    {
        context.AddHeader("Ship status");
        context.AddInfo(IconId.CMop, "Dirt marks",
            system.DirtCount.ToString());
        context.AddInfo(IconId.CMop, "All queued floor-cleaning tasks",
            system.QueuedCleaningGoalCount.ToString(),
            "Includes vanilla/player tasks as well as Super Cleaning tasks, "
            + "including tasks waiting for Core to retry or resume them.");
        context.AddInfo("Icons/Color/Info", "  Vanilla / player queued",
            system.VanillaCleaningGoalCount.ToString(),
            "Queued floor-cleaning colony tasks not created by Super Cleaning, "
            + "including tasks waiting for Core to retry or resume them.");
        context.AddInfo("Icons/Color/Count", "  Super Cleaning queued",
            system.OwnedTaskCount.ToString(),
            "Queued floor-cleaning seed tasks created and tracked by Super "
            + "Cleaning. Ownership is retained while Core keeps a failed or "
            + "interrupted task for retry or recovery.");
        BuildDirtTrend(context);

        context.AddHeader("Cleaning Bots");
        context.AddInfo(IconId.CMop, "Cleaning",
            BotFraction(system.ActiveCleaningBotCount),
            BotFractionTooltip("currently cleaning"));
        context.AddInfo("Icons/Color/Info", "Socializing",
            BotFraction(system.SocializingBotCount),
            BotFractionTooltip("currently socializing"));
        context.AddInfo(IconId.CWrench2,
            "Maintenance / repairs / recharging",
            BotFraction(system.MaintenanceBotCount),
            BotFractionTooltip("undergoing maintenance, repairs, or recharging"));
        if (system.IdleBotCount == 0) idleBotsExpanded = false;
        context.AddCollapsibleHeader(
            "Idle — " + BotFraction(system.IdleBotCount),
            idleBotsExpanded,
            value => idleBotsExpanded = value,
            system.IdleBotCount == 0
                ? "No Cleaning Bots are currently idle."
                : "Expand to see why each Cleaning Bot remains idle and select "
                    + "or focus it.",
            system.IdleBotCount > 0);
        if (idleBotsExpanded)
        {
            foreach (IdleCleaningBotStatus bot in system.IdleBots)
            {
                IdleCleaningBotStatus selectedBot = bot;
                context.AddButton("Icons/Color/Time",
                    selectedBot.DisplayName,
                    selectedBot.Reason,
                    () => system.SelectIdleBot(selectedBot.Being),
                    "Select and focus " + selectedBot.DisplayName + " at "
                    + Pos.String(selectedBot.Position) + ". Idle reason: "
                    + selectedBot.Reason + ".");
            }
        }
        context.AddInfo("Icons/Color/Cog", "Other non-cleaning tasks",
            BotFraction(system.OtherTaskBotCount),
            BotFractionTooltip("doing other non-cleaning tasks"));
        foreach (BotActivityBreakdown entry in system.OtherTaskBreakdown)
        {
            context.AddInfo("Icons/Color/Info", "  " + entry.Label,
                BotFraction(entry.Count),
                entry.Count + " of " + system.DeployedBotCount
                + " tracked Cleaning Bots are currently doing this task.");
        }

        context.AddHeader("Cleanup queue");
        if (system.Enabled)
        {
            context.AddButton(IconId.CMop, "Queue Cleanup",
                system.CurrentQueueTarget + " requested",
                () => system.QueueCleanup(),
                "Requests 10% of the current dirt-mark count, with a minimum "
                + "of 24 when enough dirt exists. The actual number created can "
                + "be lower when dirt is unreachable, already claimed, or "
                + "stacked on fewer floor tiles. Bots choose the work normally; "
                + "none are force-assigned.");
            if (system.ColonistAssisted)
            {
                context.AddButton("Icons/Color/Info",
                    "Queue Colonist Cleanup",
                    system.CurrentQueueTarget + " requested",
                    () => system.QueueColonistCleanup(),
                    "Creates ordinary floor-cleaning tasks restricted to the "
                    + system.EligibleColonistCount + " currently eligible "
                    + "Colonists or other non-Cleaning Bot workers with "
                    + "Cleaning work enabled. This is manual only and does not "
                    + "force-assign or interrupt anyone.");
            }
        }
        else
        {
            context.AddInfo("Icons/Color/NoEnergy", "Queueing disabled",
                "Enable in Settings",
                "Turning Super Cleaning off pauses idle-bot assistance and "
                + "blocks new queue requests. Existing vanilla cleaning work "
                + "is left alone.");
        }
        context.AddInfo("Icons/Color/Info", "Queue status",
            system.LastQueueResult);
        if (system.PendingOwnedTaskCount > 0)
        {
            context.AddButton("Icons/Color/NoEnergy",
                "Cancel pending Super Cleaning tasks",
                system.PendingOwnedTaskCount + " pending",
                ConfirmCancelPendingTasks,
                "Cancels only Super Cleaning tasks that have not been accepted. "
                + "In-progress work and vanilla/player tasks are left alone.");
        }

        var tasks = system.GetOwnedTasks();
        context.AddCollapsibleHeader(
            "Mod-owned tasks — " + tasks.Count + " queued",
            modTasksExpanded,
            value => modTasksExpanded = value,
            modTasksExpanded
                ? "Collapse the detailed Super Cleaning task list."
                : "Expand the detailed Super Cleaning task list.");
        if (!modTasksExpanded) return;
        if (tasks.Count == 0)
        {
            context.AddInfo("Icons/Color/Info", "No queued seed tasks",
                "Ready",
                "A Super Cleaning task leaves this list when Stardeus finishes, "
                + "cancels, permanently removes it, or converts it into a bot's "
                + "continuing clean sweep. Temporary failure or interruption "
                + "does not remove ownership.");
            return;
        }

        int visibleCount = Math.Min(tasks.Count, MaximumVisibleOwnedTasks);
        for (int index = 0; index < visibleCount; index++)
        {
            OwnedCleaningTask task = tasks[index];
            AIGoal goal = system.ResolveOwnedGoal(task.GoalId);
            string state = goal == null ? "Finishing" : goal.State.ToString();
            context.AddInfo(IconId.CMop,
                "Task #" + task.GoalId + " at " + Pos.String(task.Position),
                state,
                "This is a vanilla floor-cleaning seed task created by Super "
                + "Cleaning and restricted to the eligible worker group chosen "
                + "when it was queued.");
        }
        if (tasks.Count > visibleCount)
        {
            context.AddInfo("Icons/Color/Info", "Additional queued tasks",
                "+" + (tasks.Count - visibleCount),
                "The panel shows the first " + MaximumVisibleOwnedTasks
                + " tasks to keep frequent status refreshes responsive.");
        }
    }

    private void BuildDirtTrend(PanelContentContext context)
    {
        int minutes = system.DirtTrendMinutes;
        string trendValue = minutes == 0
            ? "Collecting data"
            : FormatSigned(system.DirtTrendDelta) + " in "
                + (minutes >= 60 ? "the last hour" : minutes + " min");
        context.AddInfo("Icons/Color/Time", "Net dirt trend", trendValue,
            "Net change includes both newly created dirt and dirt removed by "
            + "cleaning. Session sampling begins after the save loads.");

        context.AddCollapsibleHeader(
            "Recent dirt changes — " + system.DirtTrendEntryCount + " samples",
            dirtHistoryExpanded,
            value => dirtHistoryExpanded = value,
            dirtHistoryExpanded
                ? "Collapse the recent net dirt-change history."
                : "Expand the recent net dirt-change history.");
        if (!dirtHistoryExpanded) return;

        var samples = system.DirtTrendSamples;
        if (samples.Count <= 1)
        {
            context.AddInfo("Icons/Color/Info", "History",
                "Collecting first 10-minute sample");
        }
        else
        {
            int first = Math.Max(1, samples.Count - 6);
            for (int index = samples.Count - 1; index >= first; index--)
            {
                DirtTrendSample sample = samples[index];
                int ageMinutes = system.GetDirtTrendSampleAgeMinutes(sample);
                string label = ageMinutes == 0
                    ? "Most recent interval"
                    : ageMinutes + " min ago";
                context.AddInfo("Icons/Color/Info", label,
                    FormatSigned(sample.Delta) + " marks",
                    BuildDirtSampleTooltip(sample, ageMinutes));
            }
        }
        context.AddInfo("Icons/Color/Info", "Cause attribution",
            "Not exposed by Stardeus",
            "Core provides each dirt mark's sprite and position, but not the "
            + "event that caused it. Super Cleaning reports measured net "
            + "changes instead of guessing.");
    }

    private static string BuildDirtSampleTooltip(DirtTrendSample sample,
        int ageMinutes)
    {
        int intervalMinutes = Math.Max(1, sample.IntervalMinutes);
        if (ageMinutes <= 0)
        {
            return "This sample ended now and covers the previous "
                + intervalMinutes + " in-game "
                + (intervalMinutes == 1 ? "minute." : "minutes.");
        }

        int startMinutesAgo = ageMinutes + intervalMinutes;
        return "This sample ended " + ageMinutes + " in-game "
            + (ageMinutes == 1 ? "minute" : "minutes")
            + " ago and covers the change from " + startMinutesAgo
            + " to " + ageMinutes + " minutes ago.";
    }

    private void BuildSettings(PanelContentContext context)
    {
        context.AddHeader("Controls");
        context.AddToggle(
            system.Enabled ? "Icons/Color/Check" : "Icons/Color/NoEnergy",
            "Enable Super Cleaning",
            system.Enabled ? "On" : "Off",
            system.Enabled,
            system.SetEnabled,
            "Controls idle-bot cleaning assistance and whether Queue Cleanup "
            + "can add new tasks. Turning it off does not cancel work already "
            + "accepted by Stardeus.");
        context.AddToggle(
            system.IdleAssistEnabled
                ? "Icons/Color/Check"
                : "Icons/Color/NoEnergy",
            "Idle Assist",
            system.IdleAssistEnabled ? "On" : "Off",
            system.IdleAssistEnabled,
            system.SetIdleAssistEnabled,
            "Idle Assist applies only to Cleaning Bots. It intentionally bypasses "
            + "the assigned Bot Dock's Work Radius > Range by using the same "
            + "vanilla sweep as Clean Now, which does not consult the selected "
            + "Range. Any finite Range may be exceeded; ∞ is already unrestricted. "
            + "Normal dock-assigned tasks, including cleaning, still obey Range. "
            + "Other work is not interrupted.");
        context.AddToggle(
            system.ColonistAssisted
                ? "Icons/Color/Check"
                : "Icons/Color/NoEnergy",
            "Colonist Assisted",
            system.ColonistAssisted ? "On" : "Off",
            system.ColonistAssisted,
            system.SetColonistAssisted,
            "Shows a manual Tasks-tab button that creates floor-cleaning tasks "
            + "restricted to eligible Colonists and other non-Cleaning Bot "
            + "workers. Nobody is automatically forced to clean.");

        context.AddHeader("Current policy");
        context.AddInfo(IconId.CMop, "Idle-bot policy",
            system.Enabled && system.IdleAssistEnabled
                ? "Clean reachable dirt"
                : "Paused",
            "While enabled, an otherwise available Cleaning Bot that becomes "
            + "idle starts the vanilla cleaning sweep when unreserved, safe, "
            + "reachable dirt exists. Other work is never interrupted.");
        context.AddInfo("Icons/Color/Info", "Queue mode",
            "Manual cleanup surge",
            "Queue Cleanup remains a manual action for adding a larger backlog "
            + "of ordinary floor-cleaning tasks.");
        context.AddInfo("Icons/Color/Count", "Tasks per request",
            "10% of dirt (minimum 24)",
            "The current requested count is " + system.CurrentQueueTarget
            + ". Actual task creation is limited to distinct, reachable, "
            + "unclaimed dirty floor tiles.");
    }

    private void ConfirmCancelPendingTasks()
    {
        int count = system.PendingOwnedTaskCount;
        if (count <= 0) return;
        WPopup.Create(false, IconId.CMop,
                "Cancel pending Super Cleaning tasks?",
                "This will cancel " + count + " waiting Super Cleaning "
                + (count == 1 ? "task" : "tasks") + ". In-progress work and "
                + "vanilla/player tasks will remain untouched.")
            .WithAction2("Cancel pending", () =>
                system.CancelPendingOwnedTasks())
            .WithAction("Keep tasks", null)
            .Spawn();
    }

    private static string FormatSigned(int value)
    {
        return value > 0 ? "+" + value : value.ToString();
    }

    private string BotFraction(int count)
    {
        return count + "/" + system.DeployedBotCount;
    }

    private string BotFractionTooltip(string activity)
    {
        return "Cleaning Bots " + activity + " out of "
            + system.DeployedBotCount
            + " alive, deployed bots currently tracked by Super Cleaning.";
    }

    public void Dispose()
    {
        panel?.Dispose();
        widget?.Dispose();
    }
}
