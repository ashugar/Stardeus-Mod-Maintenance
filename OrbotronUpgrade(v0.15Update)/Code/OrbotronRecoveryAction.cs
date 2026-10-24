using System;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using Game.Utils;
using KL.Utils;
using UnityEngine;

namespace OrbotronUpgrade
{
    /// <summary>Native selected-mind recovery; registration never performs recovery.</summary>
    public sealed class OrbotronRecoveryAction : BaseEntityAction
    {
        public override string Id => "OrbotronUpgrade_RestoreMissingJobs";

        [SelfInit]
        private static void Register()
        {
            if (!RecoveryContract.IsSupported)
            {
                D.Warn("Orbotron Upgrades 2.2.0: missing-job recovery is unavailable on this unreviewed Core build. Existing JSON upgrades remain active.");
                return;
            }
            RegisterAction(new OrbotronRecoveryAction());
            D.Warn("Orbotron Upgrades 2.2.0: selected-mind recovery action registered for single and multiple selections. No minds were changed.");
        }

        protected override bool ApplyFor(GameState state, Being entity, out EntityActionData data)
        {
            data = EntityActionData.Unavailable;
            if (!RecoveryTarget.TryPreview(state, entity, out var plan, out _) || plan.Entries.Count == 0) return false;
            data = new EntityActionData(this, (life, parent, index) => CreateButton(state, entity, life, parent, index));
            return true;
        }

        protected override bool ApplyForMany(GameState state, List<Being> entities, out EntityActionData data)
        {
            data = EntityActionData.Unavailable;
            if (entities == null || entities.Count < 2) return false;
            var selection = new SelectionSnapshot<Being>(entities);
            if (!RecoveryBatch.HasMissing(state, selection)) return false;
            data = new EntityActionData(this, (life, parent, index) => CreateBatchButton(state, selection, life, parent, index));
            return true;
        }

        private WBase CreateBatchButton(GameState state, SelectionSnapshot<Being> selection, Lifetime life, RectTransform parent, int index)
        {
            var button = SpawnButtonMulti(life, true, parent, index);
            WPopupData popup = null;
            BatchRecoveryPlan<Being> confirmation = null;
            life.OnDispose(() =>
            {
                confirmation?.Cancel();
                var stalePopup = popup; popup = null;
                if (stalePopup != null) Coroutines.OnNextFrame(() => stalePopup.Despawn());
            });
            button.Load(IconId.CHammer, "Restore jobs", () =>
            {
                if (RecoveryInvocation.IsActive || !RecoveryBatch.ScopeCurrent(state, selection, life)) return;
                confirmation?.Cancel();
                popup?.Despawn(); popup = null;
                if (!RecoveryBatch.TryPreview(state, selection, life, out var plan, out var error))
                { ShowResult("Jobs unchanged", error); return; }
                confirmation = plan;
                WPopupData currentPopup = null;
                Func<bool> current = () => ReferenceEquals(confirmation, plan) && ReferenceEquals(popup, currentPopup)
                    && currentPopup?.Widget != null && !currentPopup.Widget.IsDisposed
                    && ReferenceEquals(currentPopup.Widget.Data, currentPopup) && currentPopup.Widget.gameObject.activeInHierarchy;
                currentPopup = WPopup.Create(false, IconId.CHammer, "Restore missing jobs", "Selected Orbotron minds", BatchRecoveryText.Preview(plan))
                    .WithAction("Restore", () =>
                    {
                        if (!current()) { plan.Cancel(); return; }
                        var result = plan.Execute();
                        if (!result.ShowCompletion) return;
                        // One publication, after Click1 has finished using its data.
                        Coroutines.OnNextFrame(() =>
                        {
                            if (!GameState.IsCurrent(state) || !ReferenceEquals(A.S, state)) return;
                            if (result.NeedsRebuild) state.UIQuery.RebuildEntityActions.Ask();
                            ShowResult(BatchRecoveryText.Title(result), BatchRecoveryText.Result(result));
                        });
                    })
                    .WithAction2(T.Cancel, () => plan.Cancel())
                    .WithValidateAction(() => current() && plan.CanConfirm)
                    .Spawn(true);
                popup = currentPopup;
            });
            return button;
        }

        private WBase CreateButton(GameState state, Being entity, Lifetime life, RectTransform parent, int index)
        {
            var button = SpawnButton(life, true, parent, index);
            var selection = new RecoverySelection(state, entity, entity.Data, life);
            WPopupData popup = null;
            // Native action factories are selection-lifetime-owned and pooled by Core.
            life.OnDispose(() =>
            {
                var stalePopup = popup; popup = null;
                // Another observer can dispose the selection from within a popup action.
                // Leave Click1's data alive until its callback has returned.
                if (stalePopup != null) Coroutines.OnNextFrame(() => stalePopup.Despawn());
            });
            button.Load(IconId.CHammer, "Restore jobs", () =>
            {
                if (RecoveryInvocation.IsActive || !RecoveryTarget.StillSelected(selection)) return;
                if (!RecoveryTarget.TryPreview(state, entity, out var preview, out var error))
                { ShowResult("Jobs unchanged", error); return; }
                if (preview.Entries.Count == 0) { ShowResult("Jobs unchanged", "No configured jobs are missing."); return; }
                popup?.Despawn();
                popup = WPopup.Create(false, IconId.CHammer, "Restore missing jobs", selection.Mind.Name.FullName, PreviewText(preview))
                    .WithAction("Restore", () =>
                    {
                        var result = RecoveryTarget.Restore(selection, preview);
                        // Do not close this popup or rebuild its selection lifetime inside Click1.
                        var message = result.Message;
                        bool committed = result.Status == RecoveryStatus.Committed || result.Status == RecoveryStatus.CommittedRefreshFailed;
                        string title = result.Status == RecoveryStatus.PartialFailure ? "Recovery incomplete"
                            : result.Status == RecoveryStatus.RolledBack ? "Recovery stopped"
                            : committed ? "Jobs restored" : "Jobs unchanged";
                        Coroutines.OnNextFrame(() =>
                        {
                            if (!GameState.IsCurrent(state) || !ReferenceEquals(A.S, state)) return;
                            if (committed) state.UIQuery.RebuildEntityActions.Ask();
                            ShowResult(title, message);
                        });
                    })
                    .WithAction2(T.Cancel, null)
                    .WithValidateAction(() => RecoveryTarget.StillSelected(selection))
                    .Spawn(true);
            });
            return button;
        }

        private static string PreviewText(RecoveryPlan plan)
        {
            var text = new StringBuilder("Create only these missing jobs:\n");
            foreach (var entry in plan.Entries)
            {
                text.Append("• ").Append(entry.Job.NameT);
                if (entry.Level == 0) text.Append(" — remains incapable");
                else if (entry.Job.Id == "Task") text.Append(" — internal task, enabled");
                else if (entry.LacksAbilities) text.Append(" — off; requires working body parts");
                else text.Append(" — off");
                text.Append('\n');
            }
            text.Append("\nCapable skills start at level 1. Existing skills, progress and job priorities are kept. New public jobs start off; internal Task stays enabled. Native trait restrictions remain.\n\nRestored skills can change passive combat bonuses, even while their jobs are off. This does not recover unknown past experience. Save separately before applying.");
            return text.ToString();
        }

        private static void ShowResult(string title, string message)
        { WPopup.Create(false, IconId.CHammer, title, message).Spawn(true); }
    }
}
