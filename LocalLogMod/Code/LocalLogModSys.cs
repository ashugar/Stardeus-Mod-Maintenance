using System;
using Game;
using Game.Data;
using Game.CodeGen;
using Game.Constants;
using Game.Systems;
using Game.Utils;
using Game.Systems.Creatures;
using HarmonyLib;
using KL.Utils;
using LocalLogMod.Logging;
using LocalLogMod.Research;
using LocalLogMod.Manufacturing;
using LocalLogMod.Power;
using LocalLogMod.Tasks;
using LocalLogMod.Beings;
using LocalLogMod.CrewUI;
using LocalLogMod.ShipCombat;
using LocalLogMod.Timings;
using LocalLogMod.Inventory;
using LocalLogMod.Lifecycle;
using LocalLogMod.StoryEvents;
using LocalLogMod.FireLogging;
using LocalLogMod.UIEvents;

namespace LocalLogMod {
    public sealed class LocalLogModSys : GameSystem {
        private const string HarmonyId = "jleas.stardeus.locallogmod";
        private Harmony harmony;
        private bool tickAttached;
        public override string Id => "LocalLogModSys";
        public override bool SkipInSandbox => false;

        [SelfInit]
        private static void Register() {
            GameSystems.Register("LocalLogModSys", () => new LocalLogModSys());
        }

        protected override void OnInitialize() {
            SessionLog.BeginOnce();
            SessionLog.Write("Session", "started version=" + typeof(LocalLogModSys).Assembly.GetName().Version.ToString(3)
                + " research=" + SessionLog.Research
                + " manufacturing=" + SessionLog.Manufacturing + " power=" + SessionLog.Power
                + " tasks=" + SessionLog.Tasks + " beings=" + SessionLog.Beings
                + " crewUI=" + SessionLog.CrewUI
                + " uiEvents=" + SessionLog.UIEvents + " audio=" + SessionLog.Audio
                + " shipCombat=" + SessionLog.ShipCombat + " inventory=" + SessionLog.Inventory
                + " lifecycle=" + SessionLog.Lifecycle + " storyEvents=" + SessionLog.StoryEvents
                + " fire=" + SessionLog.Fire
                + " timings=" + SessionLog.Timings);
            harmony = new Harmony(HarmonyId);
            if (SessionLog.Research) { Install(typeof(ResearchHooks)); }
            if (SessionLog.Manufacturing) { Install(typeof(ManufacturingHooks)); }
            if (SessionLog.Power) { Install(typeof(PowerHooks)); }
            if (SessionLog.Tasks) { Install(typeof(TaskHooks)); }
            if (SessionLog.Beings) { Install(typeof(BeingHooks)); }
            if (SessionLog.CrewUI) { Install(typeof(CrewUIHooks)); }
            if (SessionLog.UIEvents) { Install(typeof(UIEventHooks)); }
            if (SessionLog.Audio) { AudioObserver.Attach(); }
            if (SessionLog.ShipCombat) { Install(typeof(ShipCombatHooks)); }
            if (SessionLog.StoryEvents) { Install(typeof(StoryEventHooks)); }
            if (SessionLog.Fire) { Install(typeof(FireHooks)); }
            if (SessionLog.Timings) { Install(typeof(TimingHooks)); }
            if (SessionLog.Inventory) { InventoryObserver.Attach(S); }
            LifecycleObserver.Attach(S);
            S.Sig.AfterLoadState.AddListener(OnAfterLoadState);
            SessionLog.Flush();
        }

        private void OnAfterLoadState(GameState state) {
            SessionLog.SetTick(state.Ticks);
            LifecycleObserver.Loaded(state);
            if (!tickAttached) {
                state.Clock.OnTick.AddListener(FlushTick);
                tickAttached = true;
            }
            Ready.When(WhenH.AllSystemsLoaded, () => {
                if (!ReferenceEquals(S, state)) { return; }
                SessionLog.SetTick(state.Ticks);
                Snapshot();
                SessionLog.Flush();
            });
            SessionLog.Flush();
        }

        private void Install(Type type) {
            try {
                int count = harmony.CreateClassProcessor(type).Patch().Count;
                SessionLog.Write("Session", "hooks_installed category=" + type.Name + " methods=" + count);
            }
            catch (Exception ex) { D.Err("Local Log Mod failed to install {0}: {1}", type.Name, ex); }
        }

        private void FlushTick(long tick) {
            SessionLog.SetTick(tick);
            if (tick % 10 == 0) { SessionLog.Flush(); }
        }

        private void Snapshot() {
            SessionLog.Guard("snapshot.research", () => {
                if (!SessionLog.Research || S.Research == null) { return; }
                SessionLog.Write("Research", "snapshot progressCount=" + S.Research.Progress.Count);
                foreach (var progress in S.Research.Progress.Values) {
                    SessionLog.Write("Research", "snapshot_node id=" + progress.Id
                        + " markedDiscovered=" + progress.IsMarkedDiscovered
                        + " unlocked=" + progress.InternalUnlocked
                        + " bookmarked=" + progress.IsBookmarked);
                }
            });
            SessionLog.Guard("snapshot.tasks", () => {
                if (!SessionLog.Tasks || S.Sys.AI == null) { return; }
                SessionLog.Write("Tasks", "snapshot colonyGoalCount=" + S.Sys.AI.ColonyGoals.Count);
                foreach (var goal in S.Sys.AI.ColonyGoals.Values) {
                    SessionLog.Write("Tasks", "snapshot " + SessionLog.Goal(goal));
                }
            });
            SessionLog.Guard("snapshot.beings", () => {
                if (!SessionLog.Beings) { return; }
                BeingsSys beings = S.Sys.Get<BeingsSys>();
                if (beings == null) { return; }
                SessionLog.Write("Beings", "snapshot beingCount=" + beings.All.Count);
                foreach (var being in beings.All.Values) {
                    SessionLog.Write("Beings", "snapshot_being data=" + being.Id
                        + " entity=" + SessionLog.Entity(being.Being)
                        + " name=" + being.Mind?.Name?.DisplayName
                        + " species=" + being.Species?.Id
                        + " state=" + being.StateFlags
                        + " group=" + being.GroupId
                        + " hideAvatar=" + being.HideAvatarButton
                        + " incapacitated=" + being.Body?.IsIncapacitated
                        + " action=" + being.Being?.AIAgent?.CurrentAction?.Id);
                }
            });
            SessionLog.Guard("snapshot.fire", () => {
                if (SessionLog.Fire) { FireHooks.Snapshot(S); }
            });
        }

        public override void Unload() {
            LifecycleObserver.Unloaded();
            InventoryObserver.Detach(S);
            AudioObserver.Detach();
            LifecycleObserver.Detach(S);
            S?.Sig?.AfterLoadState.RemoveListener(OnAfterLoadState);
            S?.Clock?.OnTick.RemoveListener(FlushTick);
            TimingHooks.FlushSummary();
            SessionLog.Flush();
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }
    }
}
