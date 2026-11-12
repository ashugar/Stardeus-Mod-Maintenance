using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using Game.UI;
using KL.Utils;

namespace Game.Systems.IntelligentRecycling {
    public sealed partial class IntelligentRecyclingSys {
        private enum AlienEggReminderStatus {
            Unseen = 0,
            Pending = 1,
            Acknowledged = 2
        }

        private const string AlienEggReminderKey = "AlienEggResearchReminder";
        private AlienEggReminderStatus alienEggReminderStatus;
        private GameState alienEggReminderState;
        private bool alienEggReminderResearchChecked;
        private long nextAlienEggReminderCheck;
        private int alienEggReminderSession;
        private bool alienEggReminderShowScheduled;
        private bool alienEggReminderPresented;
        private WPopupData alienEggReminderPopup;

        public bool IsAlienEggReminderAwaitingDecision =>
            RecycleAlienEggs && alienEggReminderStatus == AlienEggReminderStatus.Pending;

        // API queries only read this gate. Resolve the initial or invalidated
        // research state on the simulation tick before queuing any egg orders.
        internal bool IsAlienEggReminderBlocking =>
            RecycleAlienEggs && alienEggReminderStatus != AlienEggReminderStatus.Acknowledged
                && (!alienEggReminderResearchChecked || IsAlienEggReminderAwaitingDecision);

        private void AttachAlienEggReminder(GameState state) {
            DetachAlienEggReminder();
            alienEggReminderState = state;
            state.Sig.ResearchChanged.AddListener(OnAlienEggResearchChanged);
        }

        private void DetachAlienEggReminder() {
            alienEggReminderState?.Sig.ResearchChanged.RemoveListener(OnAlienEggResearchChanged);
            alienEggReminderState = null;
            alienEggReminderSession++;
            alienEggReminderResearchChecked = false;
            nextAlienEggReminderCheck = 0;
            alienEggReminderShowScheduled = false;
            alienEggReminderPresented = false;
            CloseAlienEggReminderPopup();
        }

        private void ResetAlienEggReminder() {
            DetachAlienEggReminder();
            alienEggReminderStatus = AlienEggReminderStatus.Unseen;
        }

        private void LoadAlienEggReminder(ComponentData data) {
            ResetAlienEggReminder();
            var value = data.GetInt(AlienEggReminderKey, 0);
            if (value == (int) AlienEggReminderStatus.Pending
                    || value == (int) AlienEggReminderStatus.Acknowledged) {
                alienEggReminderStatus = (AlienEggReminderStatus) value;
            }
        }

        private void SaveAlienEggReminder(ComponentData data) =>
            data.SetInt(AlienEggReminderKey, (int) alienEggReminderStatus);

        private void OnAlienEggResearchChanged(Def _) {
            if (Enabled && RecycleAlienEggs && alienEggReminderResearchChecked
                    && alienEggReminderStatus == AlienEggReminderStatus.Unseen) {
                alienEggReminderResearchChecked = false;
                MarkApiRevision();
            }
        }

        private void MarkAlienEggReminderDirty() {
            if (!RecycleAlienEggs && alienEggReminderStatus == AlienEggReminderStatus.Pending) {
                // Turning off egg disposal is an explicit answer to this reminder.
                AcknowledgeAlienEggReminder();
            }
            alienEggReminderResearchChecked = false;
            if (!Enabled || !RecycleAlienEggs) {
                alienEggReminderPresented = false;
                CloseAlienEggReminderPopup();
            }
        }

        private void TickAlienEggReminder(long ticks) {
            var state = alienEggReminderState;
            if (!Enabled || !RecycleAlienEggs || !Ready.Game || !Ready.Gen
                    || !IsCurrentAlienEggReminderState(state, alienEggReminderSession)
                    || alienEggReminderStatus == AlienEggReminderStatus.Acknowledged) {
                return;
            }

            // ResearchChanged covers discovery/completion. Recheck with scans
            // and periodically for availability changes without that signal.
            if (alienEggReminderStatus == AlienEggReminderStatus.Unseen
                    && state.Research != null
                    && (!alienEggReminderResearchChecked || ticks >= nextAlienEggReminderCheck
                        || scanRequested || ticks >= nextScanTick || pendingObjectScanIds.Count != 0)) {
                bool wasBlocking = IsAlienEggReminderBlocking;
                bool available = HasAvailableAlienDatoidResearch(state);
                alienEggReminderResearchChecked = true;
                nextAlienEggReminderCheck = ticks + ScanIntervalTicks;
                if (available) {
                    alienEggReminderStatus = AlienEggReminderStatus.Pending;
                    MarkApiRevision();
                    int session = alienEggReminderSession;
                    Ready.WhenMainThread(this, "Refresh Intelligent Recycling egg settings", () => {
                        if (IsCurrentAlienEggReminderState(state, session)
                                && IsAlienEggReminderAwaitingDecision) {
                            panel?.Refresh();
                        }
                    });
                } else if (wasBlocking) {
                    MarkApiRevision();
                }
            }
            if (IsAlienEggReminderAwaitingDecision && !alienEggReminderPresented
                    && !alienEggReminderShowScheduled && Ready.Input && Ready.Rendering
                    && G.HasEnteredGame) {
                QueueAlienEggReminderPopup(state, alienEggReminderSession);
            }
        }

        private static bool HasAvailableAlienDatoidResearch(GameState state) {
            var tree = state.Research;
            // Available covers unfinished nodes, tier and prerequisites. Like
            // Core's research UI, respect discovery and seasonal availability.
            // Datoid inventory and disk capacity are deliberately not required.
            foreach (var def in tree.Available) {
                var researchable = def.Researchable;
                if (researchable?.DatoidCosts == null || !def.IsInSeason
                        || !tree.IsDiscovered(def.IdH)) {
                    continue;
                }
                foreach (var cost in researchable.AdjustedDatoidCosts(state.Difficulty)) {
                    if (cost.StackSize > 0 && cost.Type?.IdH == MatIdH.DatoidAlien) {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool IsCurrentAlienEggReminderState(GameState state, int session) =>
            state != null && session == alienEggReminderSession
                && ReferenceEquals(alienEggReminderState, state)
                && ReferenceEquals(loadedState, state)
                && GameState.IsCurrent(state) && ReferenceEquals(A.S, state);

        private bool CanShowAlienEggReminder(GameState state, int session) =>
            IsCurrentAlienEggReminderState(state, session) && panel != null
                && Enabled && IsAlienEggReminderAwaitingDecision
                && Ready.Input && Ready.Rendering && G.HasEnteredGame
                && !UIShowing.LoadingScreen && !UIShowing.FullScreenWidget
                && !UIShowing.PanelModal && !UIShowing.Dialog && !UIShowing.ScreenBlock
                && !UIShowing.QuickChoice && !UIShowing.TutorialPopup
                && WPopup.Spawned.Count == 0;

        private void QueueAlienEggReminderPopup(GameState state, int session) {
            alienEggReminderShowScheduled = true;
            Ready.WhenMainThread(this, "Intelligent Recycling egg reminder", () => {
                if (!IsCurrentAlienEggReminderState(state, session)) {
                    return;
                }
                alienEggReminderShowScheduled = false;
                if (alienEggReminderPresented || !CanShowAlienEggReminder(state, session)) {
                    return;
                }

                WPopupData popup = WPopup.Create(false, IconId.CRecycling,
                    "Review alien egg disposal", DisplayName,
                    "Research requiring Alien Datoids became available. One Egg Shell and one Crawler Goo "
                    + "craft one Alien Datoid. New automatic egg disposal "
                    + "orders are paused until you choose to keep the setting enabled or turn it off.");
                alienEggReminderPopup = popup;
                bool IsCurrentPopup() =>
                    IsCurrentAlienEggReminderState(state, session) && Ready.Save
                        && Enabled && IsAlienEggReminderAwaitingDecision
                        && ReferenceEquals(alienEggReminderPopup, popup)
                        && popup.Widget != null && ReferenceEquals(popup.Widget.Data, popup)
                        && popup.Widget.gameObject.activeInHierarchy;

                popup.WithAction("Review egg disposal", () => {
                        // The native button closes the popup after this callback.
                        // Keep the research hold while the player reviews Settings.
                        alienEggReminderPopup = null;
                        Coroutines.OnNextFrame(() => {
                            if (IsCurrentAlienEggReminderState(state, session) && Ready.Input
                                    && Enabled && IsAlienEggReminderAwaitingDecision) {
                                panel?.ShowSettings();
                            }
                        });
                    })
                    .WithAction2("Keep current setting", () => {
                        alienEggReminderPopup = null;
                        KeepAlienEggDisposalAfterReminder();
                    })
                    .WithValidateAction(IsCurrentPopup)
                    .WithValidateAction2(IsCurrentPopup)
                    .Spawn(true);
                alienEggReminderPresented = popup.Widget != null;
            });
        }

        public void KeepAlienEggDisposalAfterReminder() {
            if (!IsAlienEggReminderAwaitingDecision || !Ready.Save
                    || !IsCurrentAlienEggReminderState(alienEggReminderState,
                        alienEggReminderSession)) {
                return;
            }
            AcknowledgeAlienEggReminder();
            panel?.Refresh();
        }

        private void AcknowledgeAlienEggReminder() {
            alienEggReminderStatus = AlienEggReminderStatus.Acknowledged;
            CloseAlienEggReminderPopup();
            RequestScan();
        }

        private void CloseAlienEggReminderPopup() {
            var popup = alienEggReminderPopup;
            alienEggReminderPopup = null;
            if (popup != null) {
                // Despawn checks that the pooled widget still belongs to this
                // popup; it cannot close a different dialog after a reload.
                Ready.WhenMainThread(this, "Close Intelligent Recycling egg reminder", popup.Despawn);
            }
        }
    }
}
