using System;
using System.Collections.Generic;
using Game;
using Game.Data;
using Game.Utils;

namespace OrbotronUpgrade
{
    // Native adapter only: the tested coordinator owns snapshots and execution.
    internal static class RecoveryBatch
    {
        internal static bool HasMissing(GameState state, SelectionSnapshot<Being> selection)
        {
            foreach (var entity in selection.Members)
                if (RecoveryTarget.TryPreview(state, entity, out var plan, out _) && plan.Entries.Count > 0) return true;
            return false;
        }

        internal static bool ScopeCurrent(GameState state, SelectionSnapshot<Being> selection, Lifetime lifetime)
        {
            if (selection == null || lifetime == null || lifetime.IsDisposed || !RecoveryTarget.StateReady(state)) return false;
            return selection.Matches(state.Query.GetSelectedBeings.Ask());
        }

        internal static bool TryPreview(GameState state, SelectionSnapshot<Being> selection, Lifetime lifetime,
            out BatchRecoveryPlan<Being> batch, out string error)
        {
            Func<bool> scope = () => ScopeCurrent(state, selection, lifetime);
            return BatchRecoveryPlan<Being>.TryCreate(selection, entity => Inspect(state, entity, lifetime, scope), scope, out batch, out error);
        }

        private static BatchCandidate Inspect(GameState state, Being entity, Lifetime lifetime, Func<bool> scope)
        {
            // The original native guard includes Orbotron MIND species, active
            // player ownership and every alive/registry/body/state restriction.
            if (!RecoveryTarget.TryPreview(state, entity, out var preview, out _)) return BatchCandidate.Excluded;
            if (preview.Entries.Count == 0) return BatchCandidate.AlreadyComplete;
            var target = new RecoverySelection(state, entity, entity.Data, lifetime);
            var jobs = new List<BatchJob>();
            foreach (var entry in preview.Entries)
                jobs.Add(new BatchJob(entry.Job.Id, entry.Job.NameT, entry.Level == 0, entry.LacksAbilities));
            string name = target.Mind.Name?.FullName;
            return BatchCandidate.For(new BatchTarget(entity, target.Mind, target.Jobs, target.DataId, target.MindId, name, jobs,
                () => RecoveryTarget.StillCurrent(target)
                    && RecoveryTarget.TryPreview(state, entity, out var current, out _)
                    && RecoveryService.Equivalent(preview, current),
                () => RecoveryTarget.RestoreInScope(target, preview, scope)));
        }
    }
}
