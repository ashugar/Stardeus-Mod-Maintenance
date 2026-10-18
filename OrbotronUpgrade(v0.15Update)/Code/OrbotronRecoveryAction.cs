using System;
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
    /// <summary>Native single-selection action; registration never performs recovery.</summary>
    public sealed class OrbotronRecoveryAction : BaseEntityAction
    {
        public override string Id => "OrbotronUpgrade_RestoreMissingJobs";

        [SelfInit]
        private static void Register()
        {
            if (!RecoveryContract.IsSupported)
            {
                D.Warn("Orbotron Upgrades 2.1.0: missing-job recovery is unavailable on this unreviewed Core build. Existing JSON upgrades remain active.");
                return;
            }
            RegisterAction(new OrbotronRecoveryAction());
            D.Warn("Orbotron Upgrades 2.1.0: selected-mind recovery action registered. No minds were changed.");
        }

        protected override bool ApplyFor(GameState state, Being entity, out EntityActionData data)
        {
            data = EntityActionData.Unavailable;
            if (!RecoveryTarget.TryPreview(state, entity, out var plan, out _) || plan.Entries.Count == 0) return false;
            data = new EntityActionData(this, (life, parent, index) => CreateButton(state, entity, life, parent, index));
            return true;
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
                if (!RecoveryTarget.StillSelected(selection)) return;
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
