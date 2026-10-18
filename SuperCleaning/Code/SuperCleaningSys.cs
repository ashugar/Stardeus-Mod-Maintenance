using System;
using System.Collections.Generic;
using System.Linq;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.AI;
using Game.Systems.Creatures;
using Game.Utils;
using KL.Clock;
using KL.Utils;

namespace SuperCleaning;

public sealed class SuperCleaningSys : GameSystem, ISaveable
{
    public const string SysId = "SuperCleaningSys";
    public const string ConsumerId = "SuperCleaning";
    public const string RequiredUtilitiesVersion = "1.4.0";
    internal const int MinimumTasksPerRequest = 24;
    internal const double DirtFractionPerRequest = 0.10d;

    private const int SaveSchemaVersion = 1;
    private const int SeedSearchConflictLimit = 64;
    private const int MaximumDirtTrendSamples = 7;
    private const double RefreshIntervalSeconds = 2d;

    private readonly List<CleaningBotComp> botBuffer = new();
    private readonly List<CleaningBotComp> deployedBots = new();
    private readonly List<Being> cleaningBotQueueWorkers = new();
    private readonly List<Being> colonistQueueWorkers = new();
    private readonly Dictionary<uint, OwnedCleaningTask> ownedTasks = new();
    private readonly List<OwnedCleaningTask> ownedTaskSnapshot = new();
    private readonly Dictionary<string, BotActivityBreakdown>
        otherTaskBreakdownByKey = new(StringComparer.Ordinal);
    private readonly List<BotActivityBreakdown> otherTaskBreakdown = new();
    private readonly HashSet<int> excludedSeedPositions = new();
    private readonly HashSet<int> idleAssistCandidatePositions = new();
    private readonly Dictionary<uint, string> idleReasonByMindId = new();
    private readonly List<IdleCleaningBotStatus> idleBotSnapshot = new();
    private readonly List<DirtTrendSample> dirtTrendSamples = new();

    private GameState loadedState;
    private SuperCleaningUi ui;
    private double nextRefreshAt;
    private long lastDirtTrendSampleTick = -1;
    private string lastQueueResult = "No cleanup request has been made yet.";

    public override string Id => SysId;
    public override bool SkipInSandbox => false;

    internal bool Enabled => SuperCleaningSettings.Enabled;
    internal bool IdleAssistEnabled =>
        SuperCleaningSettings.IdleAssistEnabled;
    internal bool ColonistAssisted =>
        SuperCleaningSettings.ColonistAssisted;
    internal int DirtCount => S?.Sys?.Dirt?.DirtCount ?? 0;
    internal int DeployedBotCount { get; private set; }
    internal int ActiveCleaningBotCount { get; private set; }
    internal int SocializingBotCount { get; private set; }
    internal int MaintenanceBotCount { get; private set; }
    internal int IdleBotCount { get; private set; }
    internal int OtherTaskBotCount { get; private set; }
    internal int QueuedCleaningGoalCount { get; private set; }
    internal int VanillaCleaningGoalCount => Math.Max(0,
        QueuedCleaningGoalCount - OwnedTaskCount);
    internal int OwnedTaskCount => ownedTasks.Count;
    internal int PendingOwnedTaskCount { get; private set; }
    internal int EligibleColonistCount => colonistQueueWorkers.Count;
    internal int CurrentQueueTarget => CalculateQueueTarget(DirtCount);
    internal IReadOnlyList<BotActivityBreakdown> OtherTaskBreakdown =>
        otherTaskBreakdown;
    internal IReadOnlyList<IdleCleaningBotStatus> IdleBots => idleBotSnapshot;
    internal IReadOnlyList<DirtTrendSample> DirtTrendSamples =>
        dirtTrendSamples;
    internal int DirtTrendEntryCount => Math.Max(0,
        dirtTrendSamples.Count - 1);
    internal int DirtTrendMinutes => dirtTrendSamples.Count == 0
        ? 0
        : Math.Min(60, (int)Math.Max(0,
            (G.Ticks - dirtTrendSamples[0].Ticks) / Consts.TicksPerMinute));
    internal int DirtTrendDelta => dirtTrendSamples.Count == 0
        ? 0
        : DirtCount - dirtTrendSamples[0].DirtCount;
    internal string LastQueueResult => lastQueueResult;

    [SelfInit]
    private static void Register()
    {
        GameSystems.Register(SysId, () => new SuperCleaningSys());
    }

    protected override void OnInitialize()
    {
        SuperCleaningSettings.Initialize();
        if (AutoUtilitiesApi.TryRegisterConsumer(
                ConsumerId,
                "Super Cleaning",
                typeof(SuperCleaningSys).Assembly.GetName().Version
                    .ToString(3),
                RequiredUtilitiesVersion))
        {
            ui = new SuperCleaningUi(this);
        }

        S.Sig.AfterLoadState.AddListener(OnLoadState);
    }

    private void OnLoadState(GameState state)
    {
        DetachStateListeners();
        ui?.Close();
        loadedState = state;
        nextRefreshAt = GameTimer.realtimeSinceStartupAsDouble;
        lastDirtTrendSampleTick = -1;
        dirtTrendSamples.Clear();
        state.Clock.OnTick.AddListener(OnTick);
        state.Sig.AIRemoveGoal.AddListener(OnGoalRemoved);
        state.Clock.OnNextTick(RefreshStatus);
    }

    private void OnTick(long ticks)
    {
        double now = GameTimer.realtimeSinceStartupAsDouble;
        if (now < nextRefreshAt) return;
        nextRefreshAt = now + RefreshIntervalSeconds;
        RefreshStatus();
    }

    private void OnGoalRemoved(AIGoal goal)
    {
        if (goal != null) ownedTasks.Remove(goal.Id);
    }

    internal void SetEnabled(bool enabled)
    {
        SuperCleaningSettings.SetEnabled(enabled);
        lastQueueResult = enabled
            ? (IdleAssistEnabled
                ? "Super Cleaning is enabled and Idle Assist is active."
                : "Super Cleaning is enabled; Idle Assist remains off.")
            : "Idle assistance and queueing are off. Existing cleaning work "
                + "was left alone.";
        RefreshStatus();
    }

    internal void SetIdleAssistEnabled(bool enabled)
    {
        SuperCleaningSettings.SetIdleAssistEnabled(enabled);
        RefreshStatus();
    }

    internal void SetColonistAssisted(bool enabled)
    {
        SuperCleaningSettings.SetColonistAssisted(enabled);
        RefreshStatus();
    }

    internal int QueueCleanup()
    {
        if (!Enabled)
        {
            lastQueueResult = "Nothing queued: enable Super Cleaning in Settings.";
            RefreshStatus();
            return 0;
        }

        RefreshStatus(false);
        return QueueCleanupForWorkers(cleaningBotQueueWorkers, 100,
            "Cleaning Bot",
            "no available deployed Cleaning Bots were found");
    }

    internal int QueueColonistCleanup()
    {
        if (!Enabled)
        {
            lastQueueResult = "Nothing queued: enable Super Cleaning in Settings.";
            RefreshStatus();
            return 0;
        }
        if (!ColonistAssisted)
        {
            lastQueueResult = "Nothing queued: enable Colonist Assisted in "
                + "Settings.";
            RefreshStatus();
            return 0;
        }

        RefreshStatus(false);
        return QueueCleanupForWorkers(colonistQueueWorkers, 50,
            "Colonist", "no eligible Colonists were found");
    }

    private int QueueCleanupForWorkers(IReadOnlyList<Being> workers,
        int maximumDistance, string audience, string noWorkersReason)
    {
        if (DirtCount == 0)
        {
            lastQueueResult = "Nothing queued: the ship has no dirt.";
            RefreshStatus();
            return 0;
        }
        if (workers.Count == 0)
        {
            lastQueueResult = "Nothing queued: " + noWorkersReason + ".";
            RefreshStatus();
            return 0;
        }

        int requested = CurrentQueueTarget;
        uint[] allowedMindIds = workers
            .Select(worker => worker.Data.Mind.Id)
            .Where(id => id != 0)
            .Distinct()
            .ToArray();
        if (allowedMindIds.Length == 0)
        {
            lastQueueResult = "Nothing queued: " + audience
                + " worker identities were not available.";
            RefreshStatus();
            return 0;
        }

        excludedSeedPositions.Clear();
        foreach (AIGoal goal in S.Sys.AI.ColonyGoals.Values)
        {
            if (AIGoal.IsActive(goal)
                && goal.JobIdH == JobTypeIdH.Cleaning
                && goal.Target?.Entity != null)
            {
                excludedSeedPositions.Add(goal.Target.Entity.PosIdx);
            }
        }

        int created = 0;
        int consecutiveFailures = 0;
        int workerIndex = 0;
        while (created < requested
            && consecutiveFailures < workers.Count)
        {
            Being worker = workers[workerIndex];
            workerIndex = (workerIndex + 1) % workers.Count;
            if (TryCreateSeedTask(worker, allowedMindIds, maximumDistance))
            {
                created++;
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
            }
        }

        lastQueueResult = created == 0
            ? "Nothing queued: no unclaimed dirt was reachable by an available "
                + audience + "."
            : "Queued " + created + " of " + requested + " requested "
                + audience + " cleanup "
                + (requested == 1 ? "task." : "tasks.");
        D.Log("Super Cleaning: {0}", lastQueueResult);
        RefreshStatus();
        return created;
    }

    private bool TryCreateSeedTask(Being being, uint[] allowedMindIds,
        int maximumDistance)
    {
        if (being == null) return false;

        for (int attempt = 0; attempt < SeedSearchConflictLimit; attempt++)
        {
            if (!S.Sys.Dirt.FindClosestDirtReachableBy(
                    being,
                    excludedSeedPositions,
                    out int position,
                    maximumDistance,
                    areaId: 0))
            {
                return false;
            }

            excludedSeedPositions.Add(position);
            Tile floor = EntityUtils.FloorAt(position);
            if (floor?.Constructable == null || floor.Goal != null) continue;
            if (!floor.Constructable.CreateCleanGoal()) continue;

            AIGoal goal = floor.Goal;
            if (goal == null || goal.JobIdH != JobTypeIdH.Cleaning) continue;
            goal.WithAllowedMindIds(allowedMindIds);
            ownedTasks[goal.Id] = new OwnedCleaningTask(goal.Id, position);
            return true;
        }

        return false;
    }

    private static int CalculateQueueTarget(int dirtCount)
    {
        if (dirtCount <= 0) return 0;
        int percentageTarget = (int)Math.Ceiling(
            dirtCount * DirtFractionPerRequest);
        return Math.Min(dirtCount,
            Math.Max(MinimumTasksPerRequest, percentageTarget));
    }

    private static bool IsBotCleaning(CleaningBotComp bot)
    {
        AIAgentComp agent = bot?.Being?.AIAgent;
        return bot?.HasActiveCleaningGoal == true
            || agent?.Goal?.JobIdH == JobTypeIdH.Cleaning
            || agent?.CurrentAction?.JobType == JobTypeIdH.Cleaning;
    }

    private static bool IsSocializing(CleaningBotComp bot)
    {
        AIAgentComp agent = bot?.Being?.AIAgent;
        return agent?.CurrentAction?.JobType == JobTypeIdH.Social
            || agent?.Goal?.JobIdH == JobTypeIdH.Social;
    }

    private static bool IsMaintenanceOrRecharge(CleaningBotComp bot)
    {
        AIAgentComp agent = bot?.Being?.AIAgent;
        AIAction action = agent?.CurrentAction;
        if (action?.IdH == AIActionRecharge.ActIdH
            || action?.IdH == AIActionIdleDock.RechargeActIdH
            || action?.IdH == AIActionUseRepairStation.ActIdH)
        {
            return true;
        }

        AIGoal currentGoal = agent?.Goal;
        if (currentGoal == null || agent.BB == null) return false;
        if (agent.BB.TryNeedGoal(NeedIdH.Maintenance, out AIGoal maintenance)
            && ReferenceEquals(currentGoal, maintenance))
        {
            return true;
        }
        return agent.BB.TryNeedGoal(NeedIdH.Electricity,
                out AIGoal recharge)
            && ReferenceEquals(currentGoal, recharge);
    }

    private static bool IsIdle(CleaningBotComp bot)
    {
        AIAgentComp agent = bot?.Being?.AIAgent;
        return agent == null || agent.IsIdle;
    }

    private static bool IsTrackedBot(CleaningBotComp bot)
    {
        Being being = bot?.Being;
        if (bot?.Entity?.IsActive != true
            || being?.IsAlive != true
            || being.AIAgent == null
            || being.Data?.Mind == null
            || !being.Data.IsPlayer)
        {
            return false;
        }

        return !being.Data.HasStateFlag(BeingState.InDevice)
            || IsMaintenanceOrRecharge(bot);
    }

    private static bool IsQueueEligible(CleaningBotComp bot)
    {
        Being being = bot?.Being;
        return IsTrackedBot(bot)
            && !being.Data.HasStateFlag(BeingState.InDevice)
            && being.AIAgent.CanWorkOnColonyGoals
            && being.AIAgent.Jobs.CanDo(JobTypeIdH.Cleaning);
    }

    private static bool IsColonistQueueEligible(AIAgentComp agent)
    {
        Being being = agent?.Being;
        return being?.IsActive == true
            && being.IsAlive
            && being.Data?.Mind != null
            && being.Data.IsPlayer
            && being.Data.Species.IdH != SpeciesIdH.CleaningBot
            && !being.Data.Species.IsAnimal
            && !being.Data.HasStateFlag(BeingState.InDevice)
            && agent.CanWork
            && agent.CanWorkOnColonyGoals
            && agent.DirectControl?.IsUnderDirectControl != true
            && agent.Jobs.CanDo(JobTypeIdH.Cleaning);
    }

    private string GetIdleAssistBlockReason(CleaningBotComp bot)
    {
        Being being = bot.Being;
        AIAgentComp agent = being.AIAgent;
        if (!Enabled) return "Super Cleaning disabled";
        if (!IdleAssistEnabled) return "Idle Assist disabled";
        if (DirtCount == 0) return "No dirt marks";
        if (being.Data.HasStateFlag(BeingState.InDevice)) return "In a device";
        if (!bot.IsAutoCleanEnabled) return "Auto Clean disabled";
        if (agent.DirectControl?.IsUnderDirectControl == true)
        {
            return "Under direct control";
        }
        if (!agent.Jobs.CanDo(JobTypeIdH.Cleaning))
        {
            return "Cleaning job disabled or unavailable";
        }
        if (!agent.CanWork || !agent.CanWorkOnColonyGoals)
        {
            return "Unable to work";
        }
        return null;
    }

    private int StartIdleCleaningBots()
    {
        idleAssistCandidatePositions.Clear();
        idleReasonByMindId.Clear();
        int started = 0;
        foreach (CleaningBotComp bot in deployedBots)
        {
            if (!IsIdle(bot)
                || IsBotCleaning(bot)
                || IsSocializing(bot)
                || IsMaintenanceOrRecharge(bot))
            {
                continue;
            }

            uint mindId = bot.Being.Data.Mind.Id;
            string blockedReason = GetIdleAssistBlockReason(bot);
            if (blockedReason != null)
            {
                idleReasonByMindId[mindId] = blockedReason;
                continue;
            }
            if (!S.Sys.Dirt.FindClosestDirtReachableBy(
                    bot.Being,
                    idleAssistCandidatePositions,
                    out int candidatePosition,
                    100,
                    areaId: 0))
            {
                idleReasonByMindId[mindId] =
                    "No unreserved, safe, reachable dirt";
                continue;
            }

            idleAssistCandidatePositions.Add(candidatePosition);
            if (bot.TrySearchForDirt())
            {
                started++;
            }
            else
            {
                idleReasonByMindId[mindId] =
                    "Vanilla cleaning sweep could not start";
            }
        }
        return started;
    }

    private void AddOtherTask(CleaningBotComp bot)
    {
        AIAgentComp agent = bot.Being.AIAgent;
        AIAction action = agent.CurrentAction;
        string key;
        string label;
        if (action != null)
        {
            key = "action:" + action.Id;
            label = action.NameT;
        }
        else if (agent.Goal != null)
        {
            key = "goal:" + agent.Goal.JobIdH + ":" + agent.Goal.Name;
            label = string.IsNullOrWhiteSpace(agent.Goal.Name)
                ? "Preparing task"
                : agent.Goal.Name;
        }
        else
        {
            key = "state:between-actions";
            label = "Between actions";
        }

        if (!otherTaskBreakdownByKey.TryGetValue(key, out var entry))
        {
            entry = new BotActivityBreakdown(key, label);
            otherTaskBreakdownByKey.Add(key, entry);
        }
        entry.Count++;
    }

    private void RefreshBotActivity()
    {
        ActiveCleaningBotCount = 0;
        SocializingBotCount = 0;
        MaintenanceBotCount = 0;
        IdleBotCount = 0;
        OtherTaskBotCount = 0;
        otherTaskBreakdownByKey.Clear();
        otherTaskBreakdown.Clear();
        idleBotSnapshot.Clear();

        foreach (CleaningBotComp bot in deployedBots)
        {
            if (IsBotCleaning(bot)) ActiveCleaningBotCount++;
            else if (IsSocializing(bot)) SocializingBotCount++;
            else if (IsMaintenanceOrRecharge(bot)) MaintenanceBotCount++;
            else if (IsIdle(bot))
            {
                IdleBotCount++;
                uint mindId = bot.Being.Data.Mind.Id;
                string reason = idleReasonByMindId.TryGetValue(mindId,
                    out string knownReason)
                    ? knownReason
                    : "Waiting for the next Idle Assist check";
                idleBotSnapshot.Add(new IdleCleaningBotStatus(
                    bot.Being, reason));
            }
            else
            {
                OtherTaskBotCount++;
                AddOtherTask(bot);
            }
        }

        otherTaskBreakdown.AddRange(otherTaskBreakdownByKey.Values
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Label, StringComparer.Ordinal));
        idleBotSnapshot.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(
                left.DisplayName, right.DisplayName));
    }

    private void UpdateDirtTrend()
    {
        long ticks = G.Ticks;
        int dirtCount = DirtCount;
        if (lastDirtTrendSampleTick < 0
            || ticks < lastDirtTrendSampleTick
            || dirtTrendSamples.Count == 0)
        {
            dirtTrendSamples.Clear();
            dirtTrendSamples.Add(new DirtTrendSample(
                ticks, dirtCount, 0, 0));
            lastDirtTrendSampleTick = ticks;
            return;
        }
        if (ticks - lastDirtTrendSampleTick < Consts.TicksPer10Minutes)
        {
            return;
        }

        DirtTrendSample previous = dirtTrendSamples[
            dirtTrendSamples.Count - 1];
        int intervalMinutes = Math.Max(1, (int)(
            (ticks - previous.Ticks) / Consts.TicksPerMinute));
        dirtTrendSamples.Add(new DirtTrendSample(
            ticks,
            dirtCount,
            dirtCount - previous.DirtCount,
            intervalMinutes));
        lastDirtTrendSampleTick = ticks;
        while (dirtTrendSamples.Count > MaximumDirtTrendSamples)
        {
            dirtTrendSamples.RemoveAt(0);
        }
    }

    private static bool IsPendingOwnedGoal(AIGoal goal)
    {
        return goal?.State == AIGoalState.Available
            || goal?.State == AIGoalState.Pending
            || goal?.State == AIGoalState.Bidding;
    }

    private void RefreshStatus() => RefreshStatus(true);

    private void RefreshStatus(bool refreshUi)
    {
        ReconcileOwnedTasks();
        botBuffer.Clear();
        deployedBots.Clear();
        cleaningBotQueueWorkers.Clear();
        colonistQueueWorkers.Clear();
        S.Components.FindAll(botBuffer);
        foreach (CleaningBotComp bot in botBuffer)
        {
            if (IsTrackedBot(bot)) deployedBots.Add(bot);
            if (IsQueueEligible(bot))
            {
                cleaningBotQueueWorkers.Add(bot.Being);
            }
        }
        foreach (AIAgentComp agent in S.Sys.AI.Agents)
        {
            if (IsColonistQueueEligible(agent))
            {
                colonistQueueWorkers.Add(agent.Being);
            }
        }

        DeployedBotCount = deployedBots.Count;
        StartIdleCleaningBots();
        RefreshBotActivity();
        RefreshCleaningTaskCounts();
        UpdateDirtTrend();
        if (refreshUi) ui?.Refresh();
    }

    private static bool IsQueuedCleaningGoal(AIGoal goal)
    {
        // IsActive excludes interrupted and failed goals that Core can recover.
        // The colony registry and terminal states determine queue membership.
        return goal != null
            && goal.Type == AIGoalType.Colony
            && goal.JobIdH == JobTypeIdH.Cleaning
            && !AIGoal.IsArchived(goal.State);
    }

    private void RefreshCleaningTaskCounts()
    {
        QueuedCleaningGoalCount = S.Sys.AI.ColonyGoals.Values.Count(
            IsQueuedCleaningGoal);
        PendingOwnedTaskCount = ownedTasks.Keys.Count(id =>
            S.Sys.AI.ColonyGoals.TryGetValue(id, out AIGoal goal)
            && IsPendingOwnedGoal(goal));
    }

    private void ReconcileOwnedTasks()
    {
        if (S?.Sys?.AI?.ColonyGoals == null) return;
        foreach (uint id in ownedTasks.Keys.ToArray())
        {
            if (!S.Sys.AI.ColonyGoals.TryGetValue(id, out AIGoal goal)
                || !IsQueuedCleaningGoal(goal))
            {
                ownedTasks.Remove(id);
            }
        }
    }

    internal IReadOnlyList<OwnedCleaningTask> GetOwnedTasks()
    {
        ReconcileOwnedTasks();
        ownedTaskSnapshot.Clear();
        ownedTaskSnapshot.AddRange(ownedTasks.Values
            .OrderBy(task => task.GoalId));
        return ownedTaskSnapshot;
    }

    internal AIGoal ResolveOwnedGoal(uint goalId)
    {
        return S?.Sys?.AI?.ColonyGoals != null
            && S.Sys.AI.ColonyGoals.TryGetValue(goalId, out AIGoal goal)
            ? goal
            : null;
    }

    internal int CancelPendingOwnedTasks()
    {
        ReconcileOwnedTasks();
        int cancelled = 0;
        foreach (uint id in ownedTasks.Keys.ToArray())
        {
            if (!S.Sys.AI.ColonyGoals.TryGetValue(id, out AIGoal goal)
                || !IsPendingOwnedGoal(goal))
            {
                continue;
            }
            AIGoal.Cancel(goal, G.Ticks, removeImmediately: false);
            cancelled++;
        }
        lastQueueResult = cancelled == 0
            ? "No pending Super Cleaning tasks were available to cancel."
            : "Cancelled " + cancelled + " pending Super Cleaning "
                + (cancelled == 1 ? "task." : "tasks.");
        RefreshStatus();
        return cancelled;
    }

    internal void SelectIdleBot(Being being)
    {
        if (being?.IsActive != true) return;
        S.Sig.SelectEntity.Send(being);
    }

    internal int GetDirtTrendSampleAgeMinutes(DirtTrendSample sample)
    {
        if (sample == null) return 0;
        return Math.Max(0, (int)(
            (G.Ticks - sample.Ticks) / Consts.TicksPerMinute));
    }

    public ComponentData Save()
    {
        ReconcileOwnedTasks();
        ComponentData data = ComponentData.Create(SysId);
        data.SetInt("SchemaVersion", SaveSchemaVersion);
        data.SetStringSet("OwnedTasks", ownedTasks.Values
            .Select(task => task.Serialize()).ToArray());
        return data;
    }

    public void Load(ComponentData data)
    {
        ownedTasks.Clear();
        if (data == null) return;
        foreach (string value in data.GetStringSet(
                     "OwnedTasks", Array.Empty<string>()))
        {
            if (OwnedCleaningTask.TryDeserialize(value, out var task))
            {
                ownedTasks[task.GoalId] = task;
            }
        }
    }

    private void DetachStateListeners()
    {
        if (loadedState == null) return;
        loadedState.Clock.OnTick.RemoveListener(OnTick);
        loadedState.Sig.AIRemoveGoal.RemoveListener(OnGoalRemoved);
        loadedState = null;
    }

    public override void Unload()
    {
        S.Sig.AfterLoadState.RemoveListener(OnLoadState);
        DetachStateListeners();
        ui?.Dispose();
        ui = null;
        botBuffer.Clear();
        deployedBots.Clear();
        cleaningBotQueueWorkers.Clear();
        colonistQueueWorkers.Clear();
        ownedTasks.Clear();
        ownedTaskSnapshot.Clear();
        otherTaskBreakdownByKey.Clear();
        otherTaskBreakdown.Clear();
        excludedSeedPositions.Clear();
        idleAssistCandidatePositions.Clear();
        idleReasonByMindId.Clear();
        idleBotSnapshot.Clear();
        dirtTrendSamples.Clear();
        lastDirtTrendSampleTick = -1;
    }
}
