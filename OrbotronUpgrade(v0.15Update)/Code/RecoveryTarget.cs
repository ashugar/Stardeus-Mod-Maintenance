using System;
using System.Collections.Generic;
using Game;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Creatures;
using Game.Systems.Mental;
using Game.Systems.Stats;
using Game.Utils;

namespace OrbotronUpgrade
{
    internal sealed class RecoverySelection
    {
        internal readonly GameState State;
        internal readonly Being Entity;
        internal readonly BeingData Data;
        internal readonly Mind Mind;
        internal readonly Jobs Jobs;
        internal readonly uint MindId;
        internal readonly uint DataId;
        internal readonly Lifetime Lifetime;
        internal RecoverySelection(GameState state, Being entity, BeingData data, Lifetime lifetime)
        { State = state; Entity = entity; Data = data; Mind = data.Mind; Jobs = data.Mind.Jobs; MindId = Mind.Id; DataId = data.Id; Lifetime = lifetime; }
    }

    internal static class RecoveryTarget
    {
        internal static bool TryPreview(GameState state, Being entity, out RecoveryPlan plan, out string error)
        {
            plan = null; error = null;
            try
            {
                if (!Eligible(state, entity, out error)) return false;
                if (!ResolveConfiguration(entity.Data.Mind, out var configured, out error)) return false;
                var data = entity.Data;
                return RecoveryService.TryBuildPlan(data.Mind, data.Mind.Jobs, configured, data.HasAbility, out plan, out error);
            }
            catch (Exception ex) { error = "Recovery is unavailable: " + ex.Message; return false; }
        }

        private static bool Eligible(GameState state, Being entity, out string error)
        {
            error = "Select one active, living player-owned Orbotron mind in the current game.";
            if (!StateReady(state)) return false;
            if (entity == null || !entity.IsActive || !ReferenceEquals(entity.S, state)) return false;
            var data = entity.Data;
            var mind = data?.Mind;
            if (data == null || !ReferenceEquals(data.Being, entity) || data.Id == 0 || data.Body == null || !data.Body.IsAlive
                || !data.HasStateFlag(BeingState.Alive) || data.SkipSimulate || data.IsInStasis || mind == null || mind.Id == 0
                || mind.IsBlank || mind.Species?.Id != "Orbotron" || mind.Faction?.IsPlayer != true
                || !ReferenceEquals(mind.Owner, data) || mind.Jobs == null || !ReferenceEquals(mind.Jobs.Owner, mind)) return false;
            var beings = state.Sys?.Get<BeingsSys>();
            if (beings == null || !beings.All.TryGetValue(data.Id, out var registeredData) || !ReferenceEquals(data, registeredData)
                || !beings.Minds.TryGetValue(mind.Id, out var registeredMind) || !ReferenceEquals(mind, registeredMind)
                || !beings.ActiveBeings.Contains(data)) return false;
            error = null; return true;
        }

        internal static bool StateReady(GameState state)
        {
            return RecoveryContract.IsSupported && A.IsMainThread && GameState.IsCurrent(state)
                && ReferenceEquals(A.S, state) && state.IsLoaded && !state.IsInconsistent && Ready.Save;
        }

        private static bool ResolveConfiguration(Mind mind, out IList<JobType> configured, out string error)
        {
            configured = null; error = "The current Orbotron starter configuration is unavailable.";
            if (!SpeciesJobs.TryFor(mind.Species, out var speciesJobs) || speciesJobs?.StartingJobs == null
                || speciesJobs.StartingJobs.Length == 0 || !ReferenceEquals(speciesJobs.BeingSpecies, mind.Species)) return false;
            var list = new List<JobType>();
            foreach (var starter in speciesJobs.StartingJobs)
            {
                if (string.IsNullOrEmpty(starter.Job) || !JobType.TryGet(starter.Job, out var job) || job == null
                    || !ReferenceEquals(starter.JobType, job)) return false;
                list.Add(job);
            }
            configured = list; error = null; return true;
        }

        internal static bool StillSelected(RecoverySelection selection)
        {
            if (!StillCurrent(selection)) return false;
            var selected = selection.State.Query.GetSelectedBeings.Ask();
            return selected != null && selected.Count == 1 && ReferenceEquals(selected[0], selection.Entity);
        }

        internal static bool StillCurrent(RecoverySelection selection)
        {
            if (selection?.Lifetime == null || selection.Lifetime.IsDisposed) return false;
            if (!Eligible(selection.State, selection.Entity, out _)) return false;
            var data = selection.Entity.Data;
            return ReferenceEquals(data, selection.Data) && data.Id == selection.DataId
                && ReferenceEquals(data.Mind, selection.Mind) && data.Mind.Id == selection.MindId
                && ReferenceEquals(data.Mind.Jobs, selection.Jobs);
        }

        internal static RecoveryResult Restore(RecoverySelection selection, RecoveryPlan preview)
        {
            if (!RecoveryInvocation.TryEnter(out var lease))
                return new RecoveryResult(RecoveryStatus.Rejected, "Another recovery is already running.");
            using (lease) return RestoreInScope(selection, preview, () => StillSelected(selection));
        }

        // Only called inside an acquired invocation. Scope never replaces native
        // target/ownership/plan validation; it supplies single or batch membership.
        internal static RecoveryResult RestoreInScope(RecoverySelection selection, RecoveryPlan preview, Func<bool> scopeCurrent)
        {
            if (scopeCurrent == null || !scopeCurrent() || !StillCurrent(selection))
                return new RecoveryResult(RecoveryStatus.Rejected, "The selection or game changed. Select the mind and try again.");
            if (!TryPreview(selection.State, selection.Entity, out var current, out var error))
                return new RecoveryResult(RecoveryStatus.Rejected, error);
            if (!RecoveryService.Equivalent(preview, current))
                return new RecoveryResult(RecoveryStatus.Rejected, "Jobs, parts or restrictions changed. Open recovery again.");
            return RecoveryService.Execute(current, new RecoveryCallbacks
            {
                TargetStillValid = () => scopeCurrent() && StillCurrent(selection)
                    && TryPreview(selection.State, selection.Entity, out var latest, out _)
                    && RecoveryService.Equivalent(current, latest),
                RefreshCommitted = (plan, slots) => Refresh(selection, slots, true),
                InvalidateRolledBack = (plan, slots) => Refresh(selection, slots, false)
            });
        }

        private static void Refresh(RecoverySelection selection, IList<JobSlot> slots, bool notify)
        {
            var data = selection.Data;
            if (!ReferenceEquals(data.Mind, selection.Mind) || !ReferenceEquals(selection.Mind.Owner, data))
                throw new InvalidOperationException("The mind changed before refresh.");
            bool combat = false;
            foreach (var slot in slots)
            {
                SkillStatSource.Refresh(data, slot.Job);
                if (slot.Job.Id == "Melee" || slot.Job.Id == "Shooting") combat = true;
            }
            if (combat) data.Body.ValidateAttackMethods();
            if (notify) PublishNewActions(selection.Entity.AIAgent, slots, Registries.AIActions.All.Values);
            if (notify) selection.State.Sig.BeingJobsChanged.Send(data);
        }

        // Native action membership depends on job presence. Publish only actions for this
        // transaction's new jobs, retaining every existing action and its order. In particular,
        // do not set ActionsReloaded: Core could then interrupt an unrelated stale old action.
        internal static void PublishNewActions(AIAgentComp agent, IList<JobSlot> slots, IEnumerable<AIAction> actions)
        {
            if (agent == null || agent.Actions == null || agent.ActionIndex == null || slots == null || actions == null)
                throw new InvalidOperationException("The native action cache is unavailable.");
            var newJobs = new HashSet<int>();
            foreach (var slot in slots)
            {
                if (slot?.Job == null) throw new InvalidOperationException("A restored job is unavailable for action refresh.");
                newJobs.Add(slot.Job.IdHash);
            }
            if (newJobs.Count == 0) return;
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var old in agent.Actions) if (old != null) knownIds.Add(old.Id);
            var additions = new List<AIAction>();
            foreach (var action in actions)
            {
                if (action == null) throw new InvalidOperationException("A native action definition is unavailable.");
                if (!newJobs.Contains(action.JobType) || agent.Actions.Contains(action) || knownIds.Contains(action.Id)) continue;
                if (!action.IsAvailableFor(agent)) continue;
                additions.Add(action);
                knownIds.Add(action.Id);
            }
            // Evaluate every native availability predicate before publishing any cache entry.
            foreach (var action in additions) agent.Actions.Add(action);
            if (additions.Count > 0) agent.ActionIndex.MarkDirty();
        }
    }
}
