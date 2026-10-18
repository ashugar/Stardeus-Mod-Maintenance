using System;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.Creatures;
using Game.Systems.Firex;
using Game.Systems.Space;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace ExpandedTraitsMod {
    public sealed class ExpandedTraitsSystem : GameSystem {
        public const string SysId = "ExpandedTraitsSystem";
        internal const string HarmonyId = "ExpandedTraitsMod.Runtime";
        internal const string Version = "0.1.4";
        private const string BootstrapHarmonyId = HarmonyId + ".Bootstrap";
        public static ExpandedTraitsSystem Instance { get; private set; }
        public override string Id => SysId;
        private GameState loadedState;
        private Harmony harmony;
        private bool incidentActive;
        private long incidentSignalUntil;

        [SelfInit]
        private static void Register() {
            var bootstrap = new Harmony(BootstrapHarmonyId);
            try {
                bootstrap.PatchAll(typeof(ExpandedTraitsSystem).Assembly);
                GameSystems.Register(SysId, () => new ExpandedTraitsSystem());
            } catch (Exception ex) {
                bootstrap.UnpatchAll(BootstrapHarmonyId);
                D.LogEx(ex, "[Expanded Traits] Compatibility initialization failed; all mod patches were rolled back.");
                throw;
            }
        }

        protected override void OnInitialize() {
            Instance = this;
            harmony = new Harmony(HarmonyId);
            S.Sig.AfterLoadState.AddListener(OnAfterLoad);
        }

        private void OnAfterLoad(GameState state) {
            Detach();
            ValidationDiagnostics.Reset();
            loadedState = state;
            state.Clock.OnTick.AddListener(OnTick);
            state.Sig.BeingDied.AddListener(OnBeingDied);
            state.Sig.CreateExplosion.AddListener(OnExplosion);
            state.Sig.TelescopeScanComplete.AddListener(OnTelescopeScanComplete);
            state.Sig.ExpeditionEnded.AddListener(OnExpeditionEnded);
            incidentActive = IsIncident(state);
            TraitDefinitionRegistry.RegisterLoadedPets();
            AppearanceSocialSkillService.Reconcile(state);
            PeriodicTraitEffects.Reset();
            BondReactionService.Reset(state);
            CuriosityService.Reset(state);
            HeavySleeperSirenPatch.Reset();
            state.Clock.OnNextUpdate(() => {
                if (loadedState != state) return;
                IntrovertBonusService.Reconcile(state);
                D.Log("[Expanded Traits] Loaded {0}. Registered {1} pet-affinity traits.",
                    Version, TraitDefinitionRegistry.PetTraits().Count);
            });
        }

        private void Detach() {
            if (loadedState != null) SecuritySweepService.CancelAll(loadedState.Clock.Ticks);
            loadedState?.Clock?.OnTick.RemoveListener(OnTick);
            loadedState?.Sig?.BeingDied.RemoveListener(OnBeingDied);
            loadedState?.Sig?.CreateExplosion.RemoveListener(OnExplosion);
            loadedState?.Sig?.TelescopeScanComplete.RemoveListener(OnTelescopeScanComplete);
            loadedState?.Sig?.ExpeditionEnded.RemoveListener(OnExpeditionEnded);
            BondReactionService.Clear();
            CuriosityService.Clear();
            AppearanceSocialSkillService.RevertAll();
            PeriodicTraitEffects.Reset();
            HeavySleeperSirenPatch.Reset();
            incidentSignalUntil = 0;
            loadedState = null;
        }

        private void OnTick(long ticks) {
            PeriodicTraitEffects.Apply(loadedState, ticks);
            BondReactionService.Tick(loadedState, ticks);
            CuriosityService.Tick(loadedState, ticks);
            if (loadedState == null || ticks % Consts.TicksPer15Minutes != 0) return;
            AppearanceSocialSkillService.Reconcile(loadedState);
            bool incidentNow = IsIncident(loadedState) || ticks < incidentSignalUntil;
            if (incidentNow && !incidentActive) {
                ValidationDiagnostics.Log(
                    "Paranoid security incident began; cancelling any pending sweep.");
                SecuritySweepService.CancelAll(ticks, "incident began");
            } else if (!incidentNow && incidentActive) {
                ValidationDiagnostics.Log(
                    "Paranoid security incident ended; evaluating sweep eligibility.");
                SecuritySweepService.ScheduleAll(loadedState, ticks);
            }
            incidentActive = incidentNow;
        }

        private void OnBeingDied(BeingData being) =>
            BondReactionService.OnBeingDied(loadedState, being);

        private void OnExplosion(Explosion explosion) => SignalIncident(loadedState?.Ticks ?? 0L);

        internal void SignalIncident(long ticks) {
            if (ticks <= 0) return;
            incidentSignalUntil = Math.Max(incidentSignalUntil, ticks + Consts.TicksPer15Minutes);
            if (incidentActive) return;
            incidentActive = true;
            ValidationDiagnostics.Log(
                "Paranoid security incident was signaled; cancelling any pending sweep.");
            SecuritySweepService.CancelAll(ticks, "incident signaled");
        }

        private void OnTelescopeScanComplete() =>
            CuriosityService.RewardAll(loadedState, "A new discovery", 4);

        private void OnExpeditionEnded(Expedition expedition) =>
            CuriosityService.RewardExpedition(loadedState, expedition);

        internal static bool IsIncident(GameState state) => state != null && (
            state.Sys.Firex.IsAnythingOnFire
            || state.Sys.Combat.IsHostileCombatInProgress
            || state.Sys.Sec.NumActiveSirens > 0);

        internal static bool IsPsychicStorm(GameState state) {
            if (state == null) return false;
            var context = state.Query.GetStoryContext.Ask();
            if (context == null) return false;
            if (context.HasSectorEffect(Hashes.S(SpaceEffectPsychicStorm.EffectId))) return true;
            foreach (var ongoing in context.OngoingEvents) {
                if (ongoing?.Def?.Event is Game.Systems.Story.PsychicStormStoryEvent) return true;
            }
            return false;
        }

        public override void Unload() {
            Detach();
            harmony?.UnpatchAll(HarmonyId);
            Harmony bootstrap = new Harmony(BootstrapHarmonyId);
            bootstrap.UnpatchAll(BootstrapHarmonyId);
            if (Instance == this) Instance = null;
        }
    }
}
