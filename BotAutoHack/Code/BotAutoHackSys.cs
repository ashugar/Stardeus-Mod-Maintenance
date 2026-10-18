using System;
using System.Collections.Generic;
using System.Linq;
using AutoUtilitiesModFramework;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Conditions;
using Game.Systems.Creatures;
using Game.Systems.Creatures.Surgeries;
using Game.Systems.UIUX;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.BotAutoHack {
    /// <summary>
    /// Queues hostile robotic beings and starts at most one vanilla Hack per
    /// simulation tick. Core owns the Hacked condition, per-bot outcome hash,
    /// conversion chance, faction change, and failure damage.
    /// </summary>
    public sealed class BotAutoHackSys : GameSystem, ISaveable {
        public const string SysId = "BotAutoHackSys";
        public const string ConsumerId = "BotAutoHack";
        public const string ModVersion = "1.0.7";
        public const string RequiredUtilitiesVersion = "1.4.0";
        internal const int FirewallChipIdH = -922567253;
        internal const int HackedConditionIdH = -908119600;

        private const string HarmonyId = "jleas.stardeus.botautohack";
        private const int SaveSchemaVersion = 4;
        private const long ScanIntervalTicks = 30L;

        private readonly List<QueuedHack> pending = new List<QueuedHack>();
        private readonly HashSet<uint> pendingIds = new HashSet<uint>();
        private readonly HashSet<uint> activelyHackedIds
            = new HashSet<uint>();
        private readonly HashSet<uint> hackedIdsScratch
            = new HashSet<uint>();
        private readonly List<Being> singleBeing = new List<Being>(1);

        private ComponentData saveData;
        private GameState loadedState;
        private int loadedSettingsGameId = -1;
        private long nextScanTick;
        private long nextHackCountReconcileTick;
        private long lastDispatchTick = -1L;
        private bool lastResearchUnlocked;
        private bool hackCountDirty;
        private bool frameworkReady;
        private Harmony harmony;
        private BotAutoHackPanel panel;

        internal static BotAutoHackSys Instance { get; private set; }

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        public bool Enabled { get; private set; }
        public bool AutoRemoveFirewallEnabled { get; private set; }
        public bool FrameworkReady => frameworkReady;
        public bool ResearchUnlocked => loadedState != null
            && (loadedState.BB.Get(BBKeyH.SignalIntrusion, false)
                || loadedState.Research?.IsUnlocked(
                    DefIdsH.ResearchRogueAISignalIntrusion) == true);
        public bool AutomationActive => frameworkReady
            && Enabled
            && ResearchUnlocked;
        public bool FirewallRemovalActive => AutomationActive
            && AutoRemoveFirewallEnabled;
        public int PendingCount => pending.Count;
        public int ActiveHackCount => activelyHackedIds.Count;
        public long LastScanTick { get; private set; }
        public int LastScanEligible { get; private set; }
        public int LastScanQueued { get; private set; }
        public long LastDispatchTick { get; private set; }
        public string LastStartedTarget { get; private set; } = string.Empty;
        public int TotalStarted { get; private set; }
        public int TotalSkipped { get; private set; }
        public int LastManualQueued { get; private set; }
        public int LastMultiSelectionQueued { get; private set; }
        public int LastFirewallRemovalEligible { get; private set; }
        public int LastFirewallRemovalQueued { get; private set; }
        public int TotalFirewallRemovalsQueued { get; private set; }
        public string LastFirewallRemovalTarget { get; private set; }
            = string.Empty;

        [SelfInit]
        private static void Register() {
            GameSystems.Register(SysId, () => new BotAutoHackSys());
        }

        protected override void OnInitialize() {
            string consumerVersion = typeof(BotAutoHackSys).Assembly
                .GetName().Version.ToString(3);
            if (!AutoUtilitiesApi.TryRegisterConsumer(
                    ConsumerId,
                    "Bot Auto Hack",
                    consumerVersion,
                    RequiredUtilitiesVersion)
                    || !AutoUtilitiesApi.IsFeatureAvailable(
                        ConsumerId, RequiredUtilitiesVersion)) {
                D.Err("[Bot Auto Hack] AUMF 1.4.0 registration failed. "
                    + "The mod will remain inactive for this session.");
                return;
            }

            try {
                Instance = this;
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(BotAutoHackSys).Assembly);
                frameworkReady = true;
                panel = new BotAutoHackPanel(this);
            } catch (Exception ex) {
                frameworkReady = false;
                panel?.Dispose();
                panel = null;
                harmony?.UnpatchAll(HarmonyId);
                harmony = null;
                Instance = null;
                D.LogEx(ex, "[Bot Auto Hack] Initialization failed. The mod "
                    + "will remain inactive for this session.");
                return;
            }

            S.Sig.AfterLoadState.AddListener(OnAfterLoadState);
            D.Warn("[Bot Auto Hack] {0} initialized. Automation defaults to "
                + "off for new saves.", consumerVersion);
        }

        private void OnAfterLoadState(GameState state) {
            DetachLoadedState();
            if (loadedSettingsGameId != state.GameId) {
                ResetToDefaults();
                loadedSettingsGameId = state.GameId;
            }

            loadedState = state;
            panel?.Close();
            lastResearchUnlocked = ResearchUnlocked;
            nextScanTick = state.Ticks + 1L;
            ReconcileActiveHackCount();
            nextHackCountReconcileTick = state.Ticks + ScanIntervalTicks;
            hackCountDirty = false;
            lastDispatchTick = state.Ticks - 1L;
            state.Clock.OnTick.AddListener(OnTick);
            state.Sig.BeingAdded.AddListener(OnBeingAdded);
            state.Sig.BeingChanged.AddListener(OnBeingChanged);
            state.Sig.ConditionAdded.AddListener(OnConditionAdded);
            state.Sig.ConditionRemoved.AddListener(OnConditionRemoved);
            panel?.RefreshAll();
        }

        private void DetachLoadedState() {
            if (loadedState != null) {
                loadedState.Clock.OnTick.RemoveListener(OnTick);
                loadedState.Sig.BeingAdded.RemoveListener(OnBeingAdded);
                loadedState.Sig.BeingChanged.RemoveListener(OnBeingChanged);
                loadedState.Sig.ConditionAdded.RemoveListener(OnConditionAdded);
                loadedState.Sig.ConditionRemoved.RemoveListener(
                    OnConditionRemoved);
            }
            loadedState = null;
            activelyHackedIds.Clear();
            hackedIdsScratch.Clear();
            nextHackCountReconcileTick = 0L;
            hackCountDirty = false;
        }

        private void OnBeingAdded(Being being) {
            UpdateActiveHackMembership(being);
            SchedulePromptScan();
        }

        private void OnBeingChanged(Being being) {
            UpdateActiveHackMembership(being);
            SchedulePromptScan();
        }

        private void OnConditionAdded(ConditionState condition, bool isNew) {
            if (condition?.Condition?.IdH == HackedConditionIdH) {
                UpdateActiveHackMembership(condition.Owner?.Being);
            }
        }

        private void OnConditionRemoved(BeingData being, int conditionIdH) {
            if (conditionIdH == HackedConditionIdH) {
                if (being != null
                        && being.Id != 0u
                        && activelyHackedIds.Remove(being.Id)) {
                    hackCountDirty = true;
                }
            }
        }

        private void OnTick(long ticks) {
            bool changed = false;
            if (ticks >= nextHackCountReconcileTick) {
                nextHackCountReconcileTick = ticks + ScanIntervalTicks;
                changed |= ReconcileActiveHackCount();
            }
            if (hackCountDirty) {
                hackCountDirty = false;
                changed = true;
            }
            bool researchUnlocked = ResearchUnlocked;
            if (researchUnlocked != lastResearchUnlocked) {
                lastResearchUnlocked = researchUnlocked;
                if (researchUnlocked && Enabled) {
                    SchedulePromptScan();
                } else if (!researchUnlocked) {
                    RemovePendingAutomatic();
                }
                changed = true;
            }
            if (AutomationActive && ticks >= nextScanTick) {
                nextScanTick = ticks + ScanIntervalTicks;
                changed |= ScanAndQueueAutomatic(ticks) > 0;
            }
            changed |= DispatchNext(ticks);
            if (changed) {
                panel?.RefreshAll();
            }
        }

        public void SetEnabled(bool value) {
            if (value && (!frameworkReady || !ResearchUnlocked)) {
                D.Warn("[Bot Auto Hack] Automation cannot be enabled before "
                    + "Signal Intrusion is unlocked and AUMF is ready.");
                panel?.RefreshAll();
                return;
            }

            Enabled = value;
            if (value) {
                SchedulePromptScan();
            } else {
                AutoRemoveFirewallEnabled = false;
                RemovePendingAutomatic();
            }
            panel?.RefreshAll();
        }

        public void SetAutoRemoveFirewallEnabled(bool value) {
            if (value && (!frameworkReady || !ResearchUnlocked || !Enabled)) {
                D.Warn("[Bot Auto Hack] Automatic Firewall removal requires "
                    + "main automation to be enabled after Signal Intrusion.");
                panel?.RefreshAll();
                return;
            }

            AutoRemoveFirewallEnabled = value;
            if (value) {
                SchedulePromptScan();
            }
            panel?.RefreshAll();
        }

        private void SchedulePromptScan() {
            if (AutomationActive && loadedState != null) {
                nextScanTick = Math.Min(nextScanTick, loadedState.Ticks + 1L);
            }
        }

        public int CountEligibleTargets() {
            if (!frameworkReady || !ResearchUnlocked || loadedState?.Beings == null) {
                return 0;
            }

            int count = 0;
            List<Being> alive = loadedState.Beings.Alive;
            for (int i = 0; i < alive.Count; i++) {
                if (IsCoreHackEligible(loadedState, alive[i])) {
                    count++;
                }
            }
            return count;
        }

        public int CountFirewallRemovalCandidates() {
            if (!frameworkReady
                    || !ResearchUnlocked
                    || loadedState?.Beings == null) {
                return 0;
            }

            int count = 0;
            List<Being> alive = loadedState.Beings.Alive;
            for (int i = 0; i < alive.Count; i++) {
                if (TryGetFirewallRemovalSlot(
                        loadedState, alive[i], out BodyPartSlot _)) {
                    count++;
                }
            }
            return count;
        }

        public void QueueAllManual() {
            LastManualQueued = QueueAllEligible(HackQueueSource.ManualAll);
            panel?.RefreshAll();
        }

        private int ScanAndQueueAutomatic(long ticks) {
            LastScanTick = ticks;
            LastScanEligible = 0;
            LastScanQueued = 0;
            LastFirewallRemovalEligible = 0;
            LastFirewallRemovalQueued = 0;
            if (!AutomationActive || loadedState?.Beings == null) {
                return 0;
            }

            List<Being> alive = loadedState.Beings.Alive;
            for (int i = 0; i < alive.Count; i++) {
                Being being = alive[i];
                if (FirewallRemovalActive
                        && TryGetFirewallRemovalSlot(
                            loadedState, being, out BodyPartSlot implantSlot)) {
                    LastFirewallRemovalEligible++;
                    if (TryQueueFirewallRemoval(being, implantSlot)) {
                        LastFirewallRemovalQueued++;
                    }
                }
                if (!IsCoreHackEligible(loadedState, being)) {
                    continue;
                }
                LastScanEligible++;
                if (QueueCandidate(being, HackQueueSource.Automatic)) {
                    LastScanQueued++;
                }
            }
            return LastScanQueued + LastFirewallRemovalQueued;
        }

        private bool TryGetFirewallRemovalSlot(
                GameState state,
                Being being,
                out BodyPartSlot implantSlot) {
            implantSlot = null;
            if (state == null
                    || !IsRoboticBeing(being)
                    || !being.IsActive
                    || being.IsDead) {
                return false;
            }

            BeingData data = being.Data;
            if (data.IsFriendly
                    || !data.HasStateFlag(BeingState.Alive)
                    || data.Body == null
                    || !data.Body.IsIncapacitated
                    || !BodyUtils.HasWorkingImplant(data, FirewallChipIdH)) {
                return false;
            }

            BodyPartSlot slot = data.Body.ImplantSlot;
            if (slot == null
                    || slot.IsPartRemoved
                    || slot.State == null
                    || slot.State.IsWelded
                    || !slot.CanRemovePart(warn: false)
                    || state.Query.FindSurgicalOperation.Ask(
                        slot, p1: true) != null
                    || !data.Body.HasSurgeryFacilities(out _)) {
                return false;
            }

            implantSlot = slot;
            return true;
        }

        private bool TryQueueFirewallRemoval(
                Being being,
                BodyPartSlot implantSlot) {
            if (loadedState == null
                    || being == null
                    || implantSlot == null
                    || !TryGetFirewallRemovalSlot(
                        loadedState, being, out BodyPartSlot currentSlot)
                    || currentSlot != implantSlot) {
                return false;
            }

            SurgicalOperation operation = new SurgicalOperation {
                TargetBeing = being.Data,
                TargetSlot = currentSlot,
                OpType = SurgicalOpType.Remove
            };
            if (!loadedState.Query.RegisterSurgicalOperation.Ask(
                    operation, p1: true)) {
                return false;
            }

            LastFirewallRemovalTarget = being.Data?.Mind?.Name?.ShortName
                ?? being.Data?.ToString()
                ?? being.Data.Id.ToString();
            TotalFirewallRemovalsQueued++;
            return true;
        }

        private int QueueAllEligible(HackQueueSource source) {
            if (!frameworkReady || !ResearchUnlocked || loadedState?.Beings == null) {
                return 0;
            }

            int queued = 0;
            List<Being> alive = loadedState.Beings.Alive;
            for (int i = 0; i < alive.Count; i++) {
                if (QueueCandidate(alive[i], source)) {
                    queued++;
                }
            }
            return queued;
        }

        internal bool TryInterceptCoreMulti(
                GameState state,
                List<Being> beings,
                out int queued) {
            queued = 0;
            if (!frameworkReady
                    || state == null
                    || state != loadedState
                    || !ResearchUnlocked
                    || beings == null
                    || beings.Count <= 1) {
                return false;
            }

            for (int i = 0; i < beings.Count; i++) {
                if (QueueCandidate(beings[i],
                        HackQueueSource.CoreMultiSelection)) {
                    queued++;
                }
            }
            LastMultiSelectionQueued = queued;
            state.UIQuery.RebuildEntityActions.Ask();
            panel?.RefreshAll();
            return true;
        }

        private bool QueueCandidate(Being being, HackQueueSource source) {
            if (!IsCoreHackEligible(loadedState, being)) {
                return false;
            }

            uint targetId = being.Data.Id;
            if (targetId == 0u) {
                return false;
            }

            if (pendingIds.Contains(targetId)) {
                for (int i = 0; i < pending.Count; i++) {
                    if (pending[i].TargetId != targetId) {
                        continue;
                    }
                    if (pending[i].Source == HackQueueSource.Automatic
                            && source != HackQueueSource.Automatic) {
                        pending[i].Source = source;
                        return true;
                    }
                    return false;
                }
                pendingIds.Remove(targetId);
            }

            pending.Add(new QueuedHack {
                TargetId = targetId,
                Source = source
            });
            pendingIds.Add(targetId);
            return true;
        }

        private bool DispatchNext(long ticks) {
            if (!frameworkReady
                    || loadedState == null
                    || ticks == lastDispatchTick
                    || pending.Count == 0) {
                return false;
            }

            bool queueChanged = false;
            while (pending.Count > 0) {
                QueuedHack queued = pending[0];
                pending.RemoveAt(0);
                pendingIds.Remove(queued.TargetId);
                queueChanged = true;

                Being being = FindActiveBeing(queued.TargetId);
                if (!IsCoreHackEligible(loadedState, being)) {
                    TotalSkipped++;
                    continue;
                }

                singleBeing.Clear();
                singleBeing.Add(being);
                bool started = HackAction.HackAll(
                    loadedState,
                    singleBeing,
                    false,
                    out int count) && count == 1;
                singleBeing.Clear();
                if (!started) {
                    TotalSkipped++;
                    continue;
                }

                lastDispatchTick = ticks;
                LastDispatchTick = ticks;
                LastStartedTarget = being.Data?.Mind?.Name?.ShortName
                    ?? being.Data?.ToString()
                    ?? queued.TargetId.ToString();
                TotalStarted++;
                break;
            }
            return queueChanged;
        }

        private Being FindActiveBeing(uint targetId) {
            List<Being> alive = loadedState?.Beings?.Alive;
            if (alive == null) {
                return null;
            }
            for (int i = 0; i < alive.Count; i++) {
                Being being = alive[i];
                if (being?.HasData == true && being.Data.Id == targetId) {
                    return being;
                }
            }
            return null;
        }

        private bool IsCoreHackEligible(GameState state, Being being) {
            if (!IsRoboticBeing(being) || state == null) {
                return false;
            }
            singleBeing.Clear();
            singleBeing.Add(being);
            bool eligible = HackAction.HackAll(
                state,
                singleBeing,
                true,
                out int count) && count == 1;
            singleBeing.Clear();
            return eligible;
        }

        private static bool IsRoboticBeing(Being being) {
            if (being == null
                    || !being.HasData
                    || being.Definition == null
                    || !being.Definition.IsBeing) {
                return false;
            }
            BeingData data = being.Data;
            return data != null
                && data.Species != null
                && !data.Species.IsBiological;
        }

        private bool IsCurrentlyHackedRobot(Being being) {
            return IsRoboticBeing(being)
                && being.IsActive
                && !being.IsDead
                && being.Data.HasStateFlag(BeingState.Alive)
                && being.Data.HasCondition(HackedConditionIdH);
        }

        private void UpdateActiveHackMembership(Being being) {
            if (being == null || !being.HasData || being.Data.Id == 0u) {
                return;
            }

            uint targetId = being.Data.Id;
            bool changed = IsCurrentlyHackedRobot(being)
                ? activelyHackedIds.Add(targetId)
                : activelyHackedIds.Remove(targetId);
            if (changed) {
                hackCountDirty = true;
            }
        }

        private bool ReconcileActiveHackCount() {
            hackedIdsScratch.Clear();
            List<Being> alive = loadedState?.Beings?.Alive;
            if (alive != null) {
                for (int i = 0; i < alive.Count; i++) {
                    Being being = alive[i];
                    if (IsCurrentlyHackedRobot(being)) {
                        hackedIdsScratch.Add(being.Data.Id);
                    }
                }
            }

            bool changed = !activelyHackedIds.SetEquals(hackedIdsScratch);
            if (changed) {
                activelyHackedIds.Clear();
                activelyHackedIds.UnionWith(hackedIdsScratch);
            }
            hackedIdsScratch.Clear();
            return changed;
        }

        private void RemovePendingAutomatic() {
            for (int i = pending.Count - 1; i >= 0; i--) {
                if (pending[i].Source != HackQueueSource.Automatic) {
                    continue;
                }
                pendingIds.Remove(pending[i].TargetId);
                pending.RemoveAt(i);
            }
        }

        public ComponentData Save() {
            saveData ??= new ComponentData(0, SysId);
            saveData.SetInt("SaveSchemaVersion", SaveSchemaVersion);
            saveData.SetBool("Enabled", Enabled);
            saveData.SetBool(
                "AutoRemoveFirewallEnabled", AutoRemoveFirewallEnabled);
            saveData.SetIntSet("PendingTargetIds", pending
                .Select(item => unchecked((int)item.TargetId)).ToArray());
            saveData.SetIntSet("PendingSources", pending
                .Select(item => (int)item.Source).ToArray());
            saveData.SetLong("LastScanTick", LastScanTick);
            saveData.SetInt("LastScanEligible", LastScanEligible);
            saveData.SetInt("LastScanQueued", LastScanQueued);
            saveData.SetLong("LastDispatchTick", LastDispatchTick);
            saveData.SetInt("TotalStarted", TotalStarted);
            saveData.SetInt("TotalSkipped", TotalSkipped);
            saveData.SetInt(
                "TotalFirewallRemovalsQueued", TotalFirewallRemovalsQueued);
            saveData.RemoveIntSet("RetryTargetIds");
            saveData.RemoveIntSet("RetryOrdinals");
            return saveData;
        }

        public void Load(ComponentData data) {
            if (data == null) {
                ResetToDefaults();
                return;
            }

            saveData = data;
            loadedSettingsGameId = S.GameId;
            Enabled = data.GetBool("Enabled", false);
            AutoRemoveFirewallEnabled = Enabled
                && data.GetBool("AutoRemoveFirewallEnabled", false);
            LastScanTick = data.GetLong("LastScanTick", 0L);
            LastScanEligible = Math.Max(0,
                data.GetInt("LastScanEligible", 0));
            LastScanQueued = Math.Max(0,
                data.GetInt("LastScanQueued", 0));
            LastDispatchTick = data.GetLong("LastDispatchTick", 0L);
            TotalStarted = Math.Max(0, data.GetInt("TotalStarted", 0));
            TotalSkipped = Math.Max(0, data.GetInt("TotalSkipped", 0));
            TotalFirewallRemovalsQueued = Math.Max(0,
                data.GetInt("TotalFirewallRemovalsQueued", 0));

            pending.Clear();
            pendingIds.Clear();
            int[] ids = data.GetIntSet("PendingTargetIds", null);
            int[] sources = data.GetIntSet("PendingSources", null);
            if (ids == null) {
                return;
            }
            for (int i = 0; i < ids.Length; i++) {
                uint id = unchecked((uint)ids[i]);
                if (id == 0u || !pendingIds.Add(id)) {
                    continue;
                }
                HackQueueSource source = HackQueueSource.ManualAll;
                if (sources != null && i < sources.Length
                        && Enum.IsDefined(typeof(HackQueueSource), sources[i])) {
                    source = (HackQueueSource)sources[i];
                }
                pending.Add(new QueuedHack {
                    TargetId = id,
                    Source = source
                });
            }
        }

        private void ResetToDefaults() {
            saveData = null;
            loadedSettingsGameId = -1;
            Enabled = false;
            AutoRemoveFirewallEnabled = false;
            pending.Clear();
            pendingIds.Clear();
            activelyHackedIds.Clear();
            hackedIdsScratch.Clear();
            nextScanTick = 0L;
            nextHackCountReconcileTick = 0L;
            lastDispatchTick = -1L;
            lastResearchUnlocked = false;
            hackCountDirty = false;
            LastScanTick = 0L;
            LastScanEligible = 0;
            LastScanQueued = 0;
            LastDispatchTick = 0L;
            LastStartedTarget = string.Empty;
            TotalStarted = 0;
            TotalSkipped = 0;
            LastManualQueued = 0;
            LastMultiSelectionQueued = 0;
            LastFirewallRemovalEligible = 0;
            LastFirewallRemovalQueued = 0;
            TotalFirewallRemovalsQueued = 0;
            LastFirewallRemovalTarget = string.Empty;
        }

        public override void Unload() {
            S?.Sig?.AfterLoadState.RemoveListener(OnAfterLoadState);
            DetachLoadedState();
            panel?.Dispose();
            panel = null;
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            frameworkReady = false;
            pending.Clear();
            pendingIds.Clear();
            activelyHackedIds.Clear();
            hackedIdsScratch.Clear();
            singleBeing.Clear();
            if (Instance == this) {
                Instance = null;
            }
        }
    }

    internal enum HackQueueSource {
        Automatic = 0,
        ManualAll = 1,
        CoreMultiSelection = 2
    }

    internal sealed class QueuedHack {
        internal uint TargetId;
        internal HackQueueSource Source;
    }

    /// <summary>
    /// Core intentionally makes a working Firewall Chip cancel the Hacked
    /// condition on its first tick, but its action-availability check does not
    /// account for the implant. Correct that single eligibility result so all
    /// vanilla and Bot Auto Hack entry points consistently ignore protected
    /// bots without changing the Firewall or Hacked condition mechanics.
    /// </summary>
    [HarmonyPatch(typeof(HackAction), "CanHack")]
    internal static class HackActionCanHackPatch {
        [HarmonyPostfix]
        private static void Postfix(Being being, ref bool __result) {
            if (!__result || being == null || !being.HasData) {
                return;
            }

            if (BodyUtils.HasWorkingImplant(
                    being.Data, BotAutoHackSys.FirewallChipIdH)) {
                __result = false;
            }
        }
    }

    /// <summary>
    /// Keeps Core's multi-Hack button and dry-run availability logic, but queues
    /// an actual multi-target click instead of starting every condition at once.
    /// Single-target calls remain entirely vanilla.
    /// </summary>
    [HarmonyPatch(typeof(HackAction), nameof(HackAction.HackAll))]
    internal static class HackActionHackAllPatch {
        [HarmonyPrefix]
        private static bool Prefix(
                GameState s,
                List<Being> beings,
                bool dryRun,
                ref int cnt,
                ref bool __result) {
            if (dryRun || beings == null || beings.Count <= 1) {
                return true;
            }

            BotAutoHackSys system = BotAutoHackSys.Instance;
            if (system == null
                    || !system.TryInterceptCoreMulti(s, beings, out int queued)) {
                return true;
            }

            cnt = queued;
            __result = queued > 0;
            return false;
        }
    }
}
