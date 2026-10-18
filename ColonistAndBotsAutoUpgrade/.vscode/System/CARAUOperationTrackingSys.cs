using AutoUtilitiesModFramework;
using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Services;
using ColonistAndBotsAutoUpgrade.UI;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Creatures.Surgeries;
using Game.Systems.Items;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.Systems
{
    /// <summary>
    /// Owns save-persistent records for operations queued by CARAU and the
    /// compact top-bar widget that exposes those records.
    ///
    /// The widget deliberately uses the electricity widget's parent layout
    /// and sibling position. It does not use screen coordinates, so other
    /// vanilla or modded widgets can share the same layout cooperatively.
    /// </summary>
    public sealed class CARAUOperationTrackingSys : GameSystem, ISaveableSpecial
    {
        public const string SysId = "CARAUOperationTrackingSys";
        private const string SaveKey = "ColonistAndBotsAutoUpgrade.TrackedOperations";
        private const string HarmonyId =
            "jleas.ColonistAndBotsAutoUpgrade.SurgeryOutcomeTracking";
        private const string RemoveAllPartsSlotId =
            "__CARAU_REMOVE_ALL_PARTS__";
        private const float CoreTransitionStabilizationSeconds = 0.5f;

        private readonly List<CARAUTrackedOperation> operations = new();
        private readonly List<uint> displayedBeingOrder = new();
        private readonly FirewallChipPreventionService
          firewallChipPrevention = new();
        private int successfulOperations;
        private int failedOperations;

        private GameObject trackingUpdaterObject;
        private CARAUOperationTrackingUpdater trackingUpdater;
        private ITopBarWidgetHandle frameworkWidget;
        private CARAUOperationTrackingPanel trackingPanel;
        private Harmony harmony;
        private bool gameReady;
        private bool trackingDataLoadedForCurrentGame;
        private bool operatorlessAutomatedHubDiagnosticEmitted;
        private int bulkQueueTrackingDepth;
        private Dictionary<uint, Dictionary<string, CARAUTrackedOperation>>
          bulkTrackedOperationsBySlot;
        private bool bulkQueueWidgetRefreshPending;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        public static CARAUOperationTrackingSys Instance { get; private set; }

        public IReadOnlyList<CARAUTrackedOperation> Operations => operations;
        private int PendingOperationCount =>
            operations.Count(operation => operation.IsPending);
        private int ActiveNativeOperationCount =>
            operations.Count(operation =>
                operation.IsPending &&
                !operation.IsAwaitingAutomaticRetry
            );
        private int WaitingRetryCount =>
            operations.Count(operation =>
                operation.IsAwaitingAutomaticRetry
            );
        private int RetainedFailureCount =>
            operations.Count(operation => operation.IsFailure);

        public void OnRetrySettingsChanged()
        {
            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Automatic retry settings changed | " +
                $"Policy={CARAUSettings.RetryPolicy} | " +
                $"Limit={CARAUSettings.AutomaticRetryLimit} | " +
                $"ChanceThreshold=" +
                $"{CARAUSettings.AutomaticRetryChanceThresholdPercent}%"
            );

            var reconciled = 0;
            foreach (var tracked in operations)
            {
                if (tracked?.IsAwaitingAutomaticRetry != true)
                {
                    continue;
                }

                var originalReason = string.IsNullOrWhiteSpace(
                    tracked.PendingRetryReason
                )
                    ? "The previous CARAU operation failed."
                    : tracked.PendingRetryReason;

                if (tracked.IsAwaitingPolicyRetry &&
                    !IsFailureKindAllowedByPolicy(tracked.FailureKind))
                {
                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.Failed,
                        originalReason + "\n\n" +
                        BuildAutomaticRetryPolicyExplanation(
                            tracked,
                            UpgradeReasonCode.RetryPolicyDisallows,
                            "The selected Failure Retry Policy no longer " +
                            "permits this failure kind."
                        ),
                        tracked.FailureKind
                    );
                    reconciled++;
                    continue;
                }

                if (RetainIfAutomaticRetryBudgetExhausted(
                        tracked,
                        originalReason,
                        CARAUOperationOutcome.Failed,
                        tracked.FailureKind == CARAUFailureKind.None
                            ? CARAUFailureKind.HubUnavailable
                            : tracked.FailureKind
                    ))
                {
                    reconciled++;
                    continue;
                }

                var previewBuilt = TryBuildAutomaticRetryPreview(
                    tracked,
                    out var decision,
                    out var unavailableReason
                );
                string chanceReason = null;
                var chancePassed = previewBuilt &&
                    TryPassAutomaticChanceGate(
                        tracked,
                        decision,
                        out chanceReason
                    );
                if (!previewBuilt || !chancePassed)
                {
                    var currentReason = !string.IsNullOrWhiteSpace(
                        unavailableReason
                    )
                        ? unavailableReason
                        : chanceReason;
                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.Failed,
                        originalReason + "\n\n" + currentReason,
                        tracked.FailureKind == CARAUFailureKind.None
                            ? CARAUFailureKind.HubUnavailable
                            : tracked.FailureKind
                    );
                    reconciled++;
                }
            }

            if (reconciled > 0)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Cleanup,
                    $"Automatic retry waits reconciled | " +
                    $"Retained={reconciled}"
                );
            }
            RefreshWidget();
            trackingPanel?.Refresh(operations);
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();
        }

        public void OnPreventativeImplantSettingsChanged()
        {
            firewallChipPrevention.Reset();
        }

        public static void SetDisplayedBeingOrder(
          IReadOnlyList<Being> beings
        )
        {
            Instance?.SetDisplayedBeingOrderInternal(beings);
        }

        public static int GetDisplayedBeingOrder(uint beingId)
        {
            if (Instance == null)
            {
                return int.MaxValue;
            }

            var index = Instance.displayedBeingOrder.IndexOf(beingId);
            return index >= 0 ? index : int.MaxValue;
        }

        private void SetDisplayedBeingOrderInternal(
          IReadOnlyList<Being> beings
        )
        {
            var newOrder = new List<uint>();

            if (beings != null)
            {
                for (var index = 0; index < beings.Count; index++)
                {
                    var being = beings[index];
                    if (being == null)
                    {
                        continue;
                    }

                    var beingId = unchecked((uint)being.Id);
                    if (!newOrder.Contains(beingId))
                    {
                        newOrder.Add(beingId);
                    }
                }
            }

            if (displayedBeingOrder.SequenceEqual(newOrder))
            {
                return;
            }

            displayedBeingOrder.Clear();
            displayedBeingOrder.AddRange(newOrder);
            trackingPanel?.Refresh(operations);
        }

        [SelfInit(SelfInit.Stage.CoreDefs, 1)]
        private static void Register()
        {
            GameSystems.Register(SysId, () => new CARAUOperationTrackingSys());
        }

        protected override void OnInitialize()
        {
            if (!AutoUtilitiesApi.TryRegisterConsumer(
                    "ColonistAndBotsAutoUpgrade",
                    "Colonist And Bots Auto Upgrade",
                    CARAUVersion.Current,
                    ColonistAndBotsAutoUpgradeSys.RequiredUtilitiesVersion))
            {
                return;
            }

            Instance = this;
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(CARAUOperationTrackingSys).Assembly);
            S.Sig.AfterLoadState.AddListener(OnAfterLoadState);
            S.Sig.NewGameInitialized.AddListener(OnNewGameInitialized);
            S.Sig.SurgicalOperationRegistered.AddListener(
                OnSurgicalOperationRegistered
            );

            // Operation classification must continue independently of the
            // optional top-bar widget. AUMF owns widget presentation and can
            // recreate or hide it without interrupting CARAU's tracker.
            trackingUpdaterObject = new GameObject(
                "CARAU_OperationTrackingUpdater"
            );
            UnityEngine.Object.DontDestroyOnLoad(trackingUpdaterObject);
            trackingUpdater =
                trackingUpdaterObject.AddComponent<
                    CARAUOperationTrackingUpdater
                >();
            trackingUpdater.Initialize(this);

            Ready.WhenGame(this, "Initialize CARAU operation tracker", () =>
            {
                gameReady = true;
                RefreshWidget();
            });
        }

        public static void TrackQueued(UpgradeDecision decision)
        {
            Instance?.TrackQueuedInternal(decision);
        }

        public static void TrackQueuedRemoveAllParts(
          Being being,
          SurgicalOperation operation,
          RemoveAllPartsEligibility eligibility
        )
        {
            Instance?.TrackQueuedRemoveAllPartsInternal(
                being,
                operation,
                eligibility
            );
        }

        public static IDisposable BeginBulkQueueTracking()
        {
            var instance = Instance;
            if (instance == null)
            {
                return EmptyBulkQueueTrackingScope.Instance;
            }

            instance.BeginBulkQueueTrackingInternal();
            return new BulkQueueTrackingScope(instance);
        }

        public static bool IsSlotTracked(uint beingId, string slotId)
        {
            if (Instance == null || string.IsNullOrEmpty(slotId))
            {
                return false;
            }

            return Instance.operations.Any(operation =>
                operation.BeingId == beingId &&
                string.Equals(
                    operation.SlotId,
                    slotId,
                    StringComparison.Ordinal
                )
            );
        }

        public static bool HasPendingOperationForBeing(uint beingId)
        {
            return Instance?.operations.Any(operation =>
                operation?.IsPending == true &&
                operation.BeingId == beingId
            ) == true;
        }

        public static bool IsOperationTracked(
          SurgicalOperation operation
        )
        {
            var instance = Instance;
            var being = operation?.TargetBeing;
            if (instance == null || being == null)
            {
                return false;
            }

            if (operation.TargetSlot == null &&
                operation.OpType == SurgicalOpType.RemoveAll)
            {
                return instance.operations.Any(tracked =>
                    tracked?.IsPending == true &&
                    IsTrackedOperationMatch(tracked, operation)
                );
            }

            var slot = operation?.TargetSlot;
            var reservedPartId = operation?.ReservedPart?.Id ?? 0;
            if (slot == null || reservedPartId == 0)
            {
                return false;
            }

            return instance.operations.Any(tracked =>
                tracked?.IsPending == true &&
                tracked.BeingId == being.Id &&
                tracked.ReservedPartId == reservedPartId &&
                IsTrackedSlotMatch(
                    tracked,
                    being.Id,
                    slot.Id,
                    slot.NameT
                )
            );
        }

        private void TrackQueuedInternal(UpgradeDecision decision)
        {
            var being = decision?.Slot?.Slot?.Body?.Owner;
            var slot = decision?.Slot?.Slot;
            var reservedPart = decision?.InventoryPart;

            if (being == null || slot == null || reservedPart == null)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    "Tracked operation was not created because its being, slot, or reserved part was unavailable"
                );
                return;
            }

            var previousTracked = FindTrackedOperationForQueue(
                being.Id,
                slot.Id,
                slot.NameT
            );
            var preservedSilentRetryCount =
                previousTracked?.IsAwaitingAutomaticRetry == true
                    ? previousTracked.SilentHubRetryCount
                    : 0;
            var preservedPolicyRetryCount =
                previousTracked?.IsAwaitingAutomaticRetry == true
                    ? previousTracked.PolicyRetryCount
                    : 0;
            var preservedAutoDocRetryCount =
                previousTracked?.IsAwaitingAutomaticRetry == true
                    ? previousTracked.PreSurgeryAutoDocRetryCount
                    : 0;
            var preservedNativeHubFailureCount =
                previousTracked?.ObservedNativeHubFailureCount ?? 0;

            // A slot can only have one pending surgical operation. Replacing
            // the matching record also prevents duplicate tracking if a queue
            // notification is received twice.
            if (bulkQueueTrackingDepth > 0)
            {
                if (previousTracked != null)
                {
                    operations.RemoveAll(op =>
                        IsTrackedSlotMatch(
                            op,
                            being.Id,
                            slot.Id,
                            slot.NameT
                        )
                    );
                }
            }
            else
            {
                operations.RemoveAll(op =>
                    IsTrackedSlotMatch(
                        op,
                        being.Id,
                        slot.Id,
                        slot.NameT
                    )
                );
            }

            var trackedOperation = new CARAUTrackedOperation
            {
                BeingId = being.Id,
                BeingName = being.Name ?? "Unknown being",
                SlotName = slot.NameT ?? "Unknown slot",
                SlotId = slot.Id ?? string.Empty,
                TargetPartId = decision.TargetPart?.Id ?? string.Empty,
                TargetPartName = decision.TargetPart?.NameT ?? "Unknown part",
                ReservedPartId = reservedPart.Id,
                QueuedAtTick = G.Ticks,
                Outcome = CARAUOperationOutcome.Pending,
                OutcomeReason = string.Empty,
                InstalledPartId =
                    decision.InstalledPart?.Id ?? string.Empty,
                InstalledQuality = decision.InstalledQuality,
                TargetQuality = decision.AvailableQuality,
                ActionKind = decision.ActionKind,
                HasOutcomeEvidence = true,
                SilentHubRetryCount = preservedSilentRetryCount,
                IsAwaitingHubRetry = false,
                ObservedNativeHubFailureCount =
                    preservedNativeHubFailureCount,
                FailureKind = CARAUFailureKind.None,
                PolicyRetryCount = preservedPolicyRetryCount,
                IsAwaitingPolicyRetry = false,
                NextPolicyRetryTick = 0,
                PendingRetryReason = string.Empty,
                PreSurgeryAutoDocRetryCount =
                    preservedAutoDocRetryCount,
                NextPreSurgeryAutoDocRetryTick = 0,
                RuntimeSlot = slot,
                RuntimeBeing = being.Being
            };
            operations.Add(trackedOperation);
            AddBulkTrackedOperation(trackedOperation);

            if (CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Intent
                ))
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Intent,
                    $"Operation tracking started | Being={being.Name ?? "unknown"} | " +
                    $"BeingId={being.Id} | Slot={slot.NameT ?? "unknown"} | " +
                    $"Target={decision.TargetPart?.NameT ?? "unknown"} | " +
                    $"Tracked={operations.Count}"
                );
            }
            RequestQueueTrackingWidgetRefresh();
        }

        private void TrackQueuedRemoveAllPartsInternal(
          Being being,
          SurgicalOperation operation,
          RemoveAllPartsEligibility eligibility
        )
        {
            var beingData = being?.Data;
            if (beingData == null ||
                operation == null ||
                operation.TargetSlot != null ||
                operation.OpType != SurgicalOpType.RemoveAll)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    "Remove All Parts tracking was not created because " +
                    "the Being or native mass operation was unavailable"
                );
                return;
            }

            operations.RemoveAll(tracked =>
                tracked?.BeingId == beingData.Id &&
                tracked.IsRemoveAllParts
            );

            var trackedOperation = new CARAUTrackedOperation
            {
                BeingId = beingData.Id,
                BeingName = beingData.Name ?? "Unknown being",
                SlotName = "All removable parts",
                SlotId = RemoveAllPartsSlotId,
                TargetPartId = "RemoveAll",
                TargetPartName = "Remove All Parts",
                ReservedPartId = 0,
                QueuedAtTick = G.Ticks,
                Outcome = CARAUOperationOutcome.Pending,
                OutcomeReason = eligibility?.Message ?? string.Empty,
                HasOutcomeEvidence = true,
                FailureKind = CARAUFailureKind.None,
                IsRemoveAllParts = true,
                NativeOperationId = operation.Id,
                RuntimeBeing = being,
                RuntimeOperation = operation
            };
            operations.Add(trackedOperation);
            AddBulkTrackedOperation(trackedOperation);

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Remove All Parts tracking started | " +
                $"Being={trackedOperation.BeingName} | " +
                $"BeingId={trackedOperation.BeingId} | " +
                $"NativeOperationId={trackedOperation.NativeOperationId} | " +
                $"TerminalGroups=" +
                $"{eligibility?.TerminalGroupSummary ?? "unknown"} | " +
                $"RemovableParts={eligibility?.RemovablePartCount ?? 0} | " +
                $"Tracked={operations.Count}"
            );
            RequestQueueTrackingWidgetRefresh();
        }

        private void BeginBulkQueueTrackingInternal()
        {
            bulkQueueTrackingDepth++;
            if (bulkQueueTrackingDepth > 1)
            {
                return;
            }

            bulkQueueWidgetRefreshPending = false;
            bulkTrackedOperationsBySlot = new Dictionary<
                uint,
                Dictionary<string, CARAUTrackedOperation>
            >();

            for (var index = 0; index < operations.Count; index++)
            {
                AddBulkTrackedOperation(operations[index]);
            }
        }

        private void EndBulkQueueTrackingInternal()
        {
            if (bulkQueueTrackingDepth <= 0)
            {
                return;
            }

            bulkQueueTrackingDepth--;
            if (bulkQueueTrackingDepth > 0)
            {
                return;
            }

            bulkTrackedOperationsBySlot = null;
            if (!bulkQueueWidgetRefreshPending)
            {
                return;
            }

            bulkQueueWidgetRefreshPending = false;
            RefreshWidget();
        }

        private CARAUTrackedOperation FindTrackedOperationForQueue(
          uint beingId,
          string slotId,
          string slotName
        )
        {
            if (bulkQueueTrackingDepth > 0 &&
                bulkTrackedOperationsBySlot != null &&
                bulkTrackedOperationsBySlot.TryGetValue(
                    beingId,
                    out var beingSlots
                ) &&
                beingSlots.TryGetValue(
                    GetTrackedSlotKey(slotId, slotName),
                    out var tracked
                ))
            {
                return tracked;
            }

            if (bulkQueueTrackingDepth > 0)
            {
                return null;
            }

            return operations.FirstOrDefault(op =>
                IsTrackedSlotMatch(op, beingId, slotId, slotName)
            );
        }

        private void AddBulkTrackedOperation(CARAUTrackedOperation tracked)
        {
            if (bulkTrackedOperationsBySlot == null || tracked == null)
            {
                return;
            }

            if (!bulkTrackedOperationsBySlot.TryGetValue(
                    tracked.BeingId,
                    out var beingSlots
                ))
            {
                beingSlots = new Dictionary<
                    string,
                    CARAUTrackedOperation
                >(StringComparer.Ordinal);
                bulkTrackedOperationsBySlot[tracked.BeingId] = beingSlots;
            }

            beingSlots[GetTrackedSlotKey(
                tracked.SlotId,
                tracked.SlotName
            )] = tracked;
        }

        private static bool IsTrackedSlotMatch(
          CARAUTrackedOperation tracked,
          uint beingId,
          string slotId,
          string slotName
        )
        {
            if (tracked == null || tracked.BeingId != beingId)
            {
                return false;
            }

            return !string.IsNullOrEmpty(slotId)
                ? string.Equals(
                    tracked.SlotId,
                    slotId,
                    StringComparison.Ordinal
                )
                : string.IsNullOrEmpty(tracked.SlotId) &&
                  string.Equals(
                      tracked.SlotName,
                      slotName,
                      StringComparison.Ordinal
                  );
        }

        private static string GetTrackedSlotKey(
          string slotId,
          string slotName
        )
        {
            return !string.IsNullOrEmpty(slotId)
                ? "Id:" + slotId
                : "Name:" + (slotName ?? string.Empty);
        }

        private void RequestQueueTrackingWidgetRefresh()
        {
            if (bulkQueueTrackingDepth > 0)
            {
                bulkQueueWidgetRefreshPending = true;
                return;
            }

            RefreshWidget();
        }

        /// <summary>
        /// Called on unscaled real time by the widget updater. This continues
        /// while Stardeus is paused, which is essential because surgery is
        /// commonly managed while paused.
        /// </summary>
        public void RefreshTrackedOperations()
        {
            if (!gameReady)
            {
                return;
            }

            var changed = firewallChipPrevention.Refresh(S);
            if (operations.Count == 0)
            {
                return;
            }

            var beingsInCoreTransition = new HashSet<uint>(
                operations
                    .Where(operation =>
                        operation?.IsPending == true &&
                        operation.RuntimeCoreTransitionAwaitingValidation
                    )
                    .Select(operation => operation.BeingId)
            );

            for (var i = operations.Count - 1; i >= 0; i--)
            {
                var tracked = operations[i];

                if (tracked == null)
                {
                    continue;
                }

                if (RefreshRuntimeTarget(tracked))
                {
                    changed = true;
                }

                // Remove All Parts is a slotless native mass operation and
                // is intentionally valid for dead mechanical chassis. Keep
                // it out of the ordinary slot/death/retry classifiers.
                if (tracked.IsRemoveAllParts)
                {
                    if (RefreshRemoveAllPartsOperation(tracked))
                    {
                        changed = true;
                    }
                    continue;
                }

                var slot = tracked.RuntimeSlot;

                if (beingsInCoreTransition.Contains(tracked.BeingId) &&
                    (!tracked.RuntimeCoreTransitionAwaitingValidation ||
                     Time.realtimeSinceStartup <
                         tracked.RuntimeCoreTransitionValidateAfterRealtime))
                {
                    continue;
                }

                if (TryReconcileRetainedCoreSuccess(tracked, slot))
                {
                    changed = true;
                    continue;
                }

                if (TryFinalizeIdentityCoreDestroyedFailure(tracked, slot))
                {
                    changed = true;
                    continue;
                }

                if (TryReconcileRetainedRepairableInstalledSuccess(
                        tracked,
                        slot
                    ))
                {
                    changed = true;
                    continue;
                }

                if (!tracked.IsPending)
                {
                    if (TryEnrichRetainedFailureReason(tracked))
                    {
                        changed = true;
                    }

                    if (NormalizeConsumedPartFailure(tracked, slot))
                    {
                        changed = true;
                    }
                    continue;
                }

                // Replacing an identity-bearing Core briefly kills the
                // outgoing mind before Stardeus respawns the same chassis
                // with the incoming Core's identity. The stable Being id and
                // refreshed runtime slot are authoritative: if that chassis
                // contains the exact planned Core, the operation succeeded
                // despite the outgoing identity's death record.
                if (IsSuccessfulCoreIdentityTransition(tracked, slot))
                {
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Intent,
                        $"Core identity transition validated | " +
                        $"BeingId={tracked.BeingId} | " +
                        $"Current={tracked.BeingName} | " +
                        $"Target={tracked.TargetPartName}"
                    );
                    CompleteSuccessfully(tracked);
                    changed = true;
                    continue;
                }

                if (TryFinalizeCapturedCoreExtractionFailure(tracked))
                {
                    changed = true;
                    continue;
                }

                if (TryBuildDeathFailureReason(
                        tracked,
                        out var deathReason
                    ))
                {
                    CancelLiveOperationAfterDeath(tracked);
                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.Failed,
                        deathReason,
                        CARAUFailureKind.BeingDied
                    );
                    changed = true;
                    continue;
                }

                var beingData =
                    S.Query.FindBeingData.Ask(tracked.BeingId);
                if (tracked.IsAwaitingPolicyRetry)
                {
                    if (ProcessAwaitingPolicyRetry(tracked))
                    {
                        changed = true;
                    }
                    continue;
                }

                if (SurgeryQueue.HasMentalBreakdown(beingData))
                {
                    CancelLiveOperationAfterMentalBreakdown(tracked);
                    HandleOperationFailure(
                        tracked,
                        CARAUOperationOutcome.Failed,
                        "Mental Instability: Cannot target being right now",
                        CARAUFailureKind.BeingTemporarilyUntargetable
                    );
                    changed = true;
                    continue;
                }

                // A record can briefly lack a runtime slot while a save is
                // loading. Restoration reconnects it once the game is ready.
                if (slot == null)
                {
                    continue;
                }

                if (tracked.IsAwaitingHubRetry)
                {
                    if (ProcessAwaitingHubRetry(tracked))
                    {
                        changed = true;
                    }
                    continue;
                }

                var operation =
                    S.Query.FindSurgicalOperation.Ask(slot, true);

                if (IsTrackedOperationMatch(tracked, operation))
                {
                    if (TryRecoverFailedPreSurgeryAutoDocTransfer(
                            tracked,
                            operation
                        ))
                    {
                        changed = true;
                        continue;
                    }

                    if (TryRecoverStaleHubBodyPartGoal(
                            tracked,
                            operation
                        ))
                    {
                        changed = true;
                        continue;
                    }

                    ObserveNativeHubTaskLifecycle(tracked, operation);

                    if (TryHandleInvalidPreSurgeryReplacement(
                            tracked,
                            operation
                        ))
                    {
                        changed = true;
                        continue;
                    }

                    switch (operation.State.Value)
                    {
                        case SurgicalOpState.Completed:
                            CompleteValidatedOperation(tracked, operation);
                            changed = true;
                            break;

                        case SurgicalOpState.Failed:
                            ClassifyFailedOperation(tracked, operation);
                            changed = true;
                            break;

                        case SurgicalOpState.Canceled:
                            if (tracked.RuntimeInterruptedByCoreIdentityTransition)
                            {
                                HandleOperationFailure(
                                    tracked,
                                    CARAUOperationOutcome.Failed,
                                    "The operation was interrupted while " +
                                    "Stardeus transferred a replacement Core " +
                                    "identity into this chassis.",
                                    CARAUFailureKind.PreSurgeryTaskFailure
                                );
                            }
                            else
                            {
                                RetainFailure(
                                    tracked,
                                    CARAUOperationOutcome.CancelledExternally,
                                    "The operation was cancelled outside CARAU.",
                                    CARAUFailureKind.ExternalCancellation
                                );
                            }
                            changed = true;
                            break;
                    }

                    continue;
                }

                if (HasSuccessfulSlotResult(tracked, slot))
                {
                    CompleteSuccessfully(tracked);
                }
                else if (TryGetRelevantBeingFailureLog(
                        tracked,
                        out var beingFailureReason
                    ))
                {
                    HandleOperationFailure(
                        tracked,
                        CARAUOperationOutcome.Failed,
                        beingFailureReason,
                        GetCapturedSurgicalFailureKind(tracked)
                    );
                }
                else if (TryGetInvalidReason(
                        tracked,
                        slot,
                        out var invalidReason
                    ))
                {
                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.Invalid,
                        invalidReason,
                        CARAUFailureKind.InvalidTarget
                    );
                }
                else if (operation != null)
                {
                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.Stale,
                        "The slot now contains a different registered " +
                        "operation than the one CARAU queued.",
                        CARAUFailureKind.StaleOwnership
                    );
                }
                else if (tracked.RuntimeInterruptedByCoreIdentityTransition)
                {
                    HandleOperationFailure(
                        tracked,
                        CARAUOperationOutcome.Failed,
                        "The operation was interrupted while Stardeus " +
                        "transferred a replacement Core identity into this " +
                        "chassis.",
                        CARAUFailureKind.PreSurgeryTaskFailure
                    );
                }
                else if (IsReservedPartMissing(tracked))
                {
                    HandleOperationFailure(
                        tracked,
                        CARAUOperationOutcome.Failed,
                        BuildConsumedPartFailureReason(tracked, slot),
                        CARAUFailureKind.ReplacementDestroyedOrConsumed
                    );
                }
                else
                {
                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.CancelledExternally,
                        tracked.HasOutcomeEvidence
                            ? "The operation disappeared before completion " +
                              "and the planned replacement was not installed."
                            : "A legacy tracked operation disappeared without " +
                              "enough saved evidence to confirm completion.",
                        CARAUFailureKind.ExternalCancellation
                    );
                }

                changed = true;
            }

            if (!changed)
            {
                return;
            }

            RefreshWidget();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();
        }

        private void CompleteSuccessfully(CARAUTrackedOperation tracked)
        {
            if (tracked == null || !tracked.IsPending)
            {
                return;
            }

            operations.Remove(tracked);
            successfulOperations++;
        }

        private void RetainFailure(
          CARAUTrackedOperation tracked,
          CARAUOperationOutcome outcome,
          string reason,
          CARAUFailureKind kind
        )
        {
            if (tracked == null || !tracked.IsPending)
            {
                return;
            }

            var normalizedKind = kind == CARAUFailureKind.None
                ? CARAUFailureKind.Unknown
                : kind;
            tracked.Outcome = outcome;
            tracked.OutcomeReason = AppendInstalledSurgicalResult(
                tracked,
                string.IsNullOrWhiteSpace(reason)
                    ? "No failure reason was provided."
                    : reason,
                normalizedKind
            );
            tracked.FailureKind = normalizedKind;
            tracked.EndedAtTick = G.Ticks;
            tracked.IsAwaitingHubRetry = false;
            tracked.IsAwaitingPolicyRetry = false;
            tracked.NextPolicyRetryTick = 0;
            tracked.PendingRetryReason = string.Empty;
            failedOperations++;

            CARAUDebugLog.Add(
                CARAUDebugCategory.Error,
                $"Operation ended without success | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"Target={tracked.TargetPartName} | Outcome={outcome} | " +
                $"FailureKind={tracked.FailureKind} | " +
                $"Reason={tracked.OutcomeReason} | " +
                $"FailedTotal={failedOperations}"
            );
        }

        internal static bool HasInstalledUnusableReplacement(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked == null ||
                (tracked.FailureKind !=
                     CARAUFailureKind.SurgicalRecoverableDamage &&
                 tracked.RuntimeNativeSurgicalFailureKind !=
                     CARAUFailureKind.SurgicalRecoverableDamage))
            {
                return false;
            }

            var slot = tracked.RuntimeSlot;
            return IsTrackedReplacementInstalled(tracked, slot) &&
                   (!slot.IsUsableSelf || !slot.IsUsableWithParent);
        }

        private static string AppendInstalledSurgicalResult(
          CARAUTrackedOperation tracked,
          string reason,
          CARAUFailureKind kind
        )
        {
            if (kind != CARAUFailureKind.SurgicalRecoverableDamage ||
                string.IsNullOrWhiteSpace(reason) ||
                reason.IndexOf(
                    "Installed result:",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0)
            {
                return reason;
            }

            var slot = tracked?.RuntimeSlot;
            if (!IsTrackedReplacementInstalled(tracked, slot) ||
                slot.State == null)
            {
                return reason;
            }

            string result;
            if (!slot.IsUsableSelf)
            {
                result =
                    $"Installed result: Unusable. The replacement " +
                    $"{slot.ItemName} is installed in {tracked.SlotName} at " +
                    $"{slot.State.Condition * 100f:0.0}% Condition, but " +
                    "Stardeus reports the body part itself as unusable.";
            }
            else if (!slot.IsUsableWithParent)
            {
                result =
                    $"Installed result: Unusable. The replacement " +
                    $"{slot.ItemName} is installed in {tracked.SlotName} at " +
                    $"{slot.State.Condition * 100f:0.0}% Condition, but an " +
                    "unusable parent part prevents it from functioning.";
            }
            else
            {
                result =
                    $"Installed result: Usable but damaged. The replacement " +
                    $"{slot.ItemName} is installed in {tracked.SlotName} at " +
                    $"{slot.State.Condition * 100f:0.0}% Condition.";
            }

            var automaticSuffixIndex = reason.IndexOf(
                "\n\nAutomatic retry stopped:",
                StringComparison.Ordinal
            );
            if (automaticSuffixIndex < 0)
            {
                return reason + "\n\n" + result;
            }

            return reason.Substring(0, automaticSuffixIndex) +
                   "\n\n" + result +
                   reason.Substring(automaticSuffixIndex);
        }

        private static bool IsTrackedReplacementInstalled(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            return tracked != null &&
                   slot?.InstalledDef != null &&
                   !string.IsNullOrWhiteSpace(tracked.TargetPartId) &&
                   string.Equals(
                       slot.InstalledDef.Id,
                       tracked.TargetPartId,
                       StringComparison.Ordinal
                   );
        }

        private void HandleOperationFailure(
          CARAUTrackedOperation tracked,
          CARAUOperationOutcome outcome,
          string reason,
          CARAUFailureKind kind
        )
        {
            if (tracked == null || !tracked.IsPending)
            {
                return;
            }

            if (kind == CARAUFailureKind.IdentityCoreDestroyed)
            {
                reason = BuildIdentityCoreDestroyedReason(tracked, reason);
            }

            reason = AppendInstalledSurgicalResult(
                tracked,
                reason,
                kind
            );

            var automaticAllowed = IsFailureKindAllowedByPolicy(kind);
            CARAUDebugLog.Add(
                CARAUDebugCategory.Decision,
                $"Failure retry disposition | Being={tracked.BeingName} | " +
                $"Slot={tracked.SlotName} | FailureKind={kind} | " +
                $"Policy={CARAUSettings.RetryPolicy} | " +
                $"AutomaticAllowed={automaticAllowed} | " +
                $"Attempts={GetAutomaticRetryCount(tracked)}/" +
                $"{CARAUSettings.AutomaticRetryLimit}"
            );

            if (!automaticAllowed)
            {
                RetainFailure(
                    tracked,
                    outcome,
                    reason + "\n\n" +
                    BuildAutomaticRetryPolicyExplanation(
                        tracked,
                        UpgradeReasonCode.RetryPolicyDisallows,
                        $"Failure kind {kind} is not allowed by the " +
                        $"current {CARAUSettings.RetryPolicy} policy."
                    ),
                    kind
                );
                return;
            }

            if (RetainIfAutomaticRetryBudgetExhausted(
                    tracked,
                    reason,
                    outcome,
                    kind
                ))
            {
                return;
            }

            if (!TrySchedulePolicyRetry(
                    tracked,
                    kind,
                    reason,
                    out var unavailableReason
                ))
            {
                RetainFailure(
                    tracked,
                    outcome,
                    string.IsNullOrWhiteSpace(unavailableReason)
                        ? reason
                        : reason + "\n\n" + unavailableReason,
                    kind
                );
            }
        }

        private static bool IsFailureKindAllowedByPolicy(
          CARAUFailureKind kind
        )
        {
            if (CARAUSettings.RetryPolicy == CARAURetryPolicy.Manual)
            {
                return false;
            }

            if (kind == CARAUFailureKind.BeingTemporarilyUntargetable ||
                kind == CARAUFailureKind.PreSurgeryTaskFailure ||
                kind == CARAUFailureKind.InconsistentNativeReservation)
            {
                return true;
            }

            return CARAUSettings.RetryPolicy ==
                   CARAURetryPolicy.TransientAndSurgical &&
                   (kind ==
                        CARAUFailureKind.ReplacementDestroyedOrConsumed ||
                    kind == CARAUFailureKind.SurgicalRejection ||
                    kind == CARAUFailureKind.SurgicalRecoverableDamage);
        }

        private string BuildAutomaticRetryPolicyExplanation(
          CARAUTrackedOperation tracked,
          UpgradeReasonCode reason,
          string detail
        )
        {
            RefreshRuntimeTarget(tracked);
            var evaluation = UpgradeEvaluator.Evaluate(
                S,
                UpgradeAnalyzer.AnalyzeSlot(tracked?.RuntimeSlot),
                UpgradeEvaluationPurpose.AutomaticRetry,
                logHostileMindExclusions: true,
                actionKind: ResolveRetryActionKind(
                    tracked,
                    tracked?.RuntimeSlot
                )
            );
            UpgradeEvaluator.WithPolicyBlock(
                evaluation,
                reason,
                detail
            );
            return "Automatic retry stopped:\n" +
                UpgradeExplanationFormatter.BuildTooltip(evaluation) +
                "\n\n" + BuildManualRetryAvailabilityNote(tracked);
        }

        private static string BuildManualRetryAvailabilityNote(
          CARAUTrackedOperation tracked
        )
        {
            var slot = tracked?.RuntimeSlot;
            if (slot != null &&
                !slot.IsPartRemoved &&
                slot.State?.IsWelded == true)
            {
                return "Manual retry is currently unavailable: the installed " +
                       "part is welded and cannot be removed. Changing CARAU's " +
                       "retry policy cannot override that native restriction.";
            }

            if (tracked?.FailureKind ==
                CARAUFailureKind.IdentityCoreDestroyed)
            {
                return "Manual retry is unavailable for this destructive Core " +
                       "failure.";
            }

            return "Manual retry availability is evaluated separately against " +
                   "the current slot, Being, inventory, and reservation state.";
        }

        private bool TrySchedulePolicyRetry(
          CARAUTrackedOperation tracked,
          CARAUFailureKind kind,
          string reason,
          out string unavailableReason
        )
        {
            unavailableReason = null;

            // Some transient failures make an immediate retry preview
            // impossible by definition. A mental breakdown temporarily makes
            // the Being untargetable, while an external AI interruption may
            // still be releasing the old goal and exact-part reservation.
            // Authorize the wait now and perform every live eligibility and
            // chance check only when CARAU is actually ready to requeue.
            var waitForFreshValidation =
                kind == CARAUFailureKind.BeingTemporarilyUntargetable ||
                (kind == CARAUFailureKind.PreSurgeryTaskFailure &&
                 IsExternalAiPreSurgeryInterruption(reason));
            if (waitForFreshValidation)
            {
                var deferredAttempt = GetAutomaticRetryCount(tracked) + 1;
                var deferredDelay = GetPolicyRetryDelay(deferredAttempt);
                tracked.FailureKind = kind;
                tracked.PendingRetryReason = reason;
                tracked.IsAwaitingPolicyRetry = true;
                tracked.IsAwaitingHubRetry = false;
                tracked.NextPolicyRetryTick = G.Ticks + deferredDelay;
                ClearRuntimeRetryChance(tracked);

                CARAUDebugLog.Add(
                    CARAUDebugCategory.Intent,
                    $"Transient retry waiting for fresh validation | " +
                    $"Cause={kind} | " +
                    $"Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | " +
                    $"NextAttempt={deferredAttempt}/" +
                    $"{CARAUSettings.AutomaticRetryLimit} | " +
                    $"DueTick={tracked.NextPolicyRetryTick} | " +
                    $"Delay={deferredDelay}"
                );
                return true;
            }

            if (!TryBuildAutomaticRetryPreview(
                    tracked,
                    out var decision,
                    out unavailableReason
                ))
            {
                return false;
            }

            if (!TryPassAutomaticChanceGate(
                    tracked,
                    decision,
                    out unavailableReason
                ))
            {
                return false;
            }

            var nextAttempt = GetAutomaticRetryCount(tracked) + 1;
            var delay = GetPolicyRetryDelay(nextAttempt);
            tracked.FailureKind = kind;
            tracked.PendingRetryReason = string.IsNullOrWhiteSpace(reason)
                ? "The operation failed without a detailed reason."
                : reason;
            tracked.IsAwaitingPolicyRetry = true;
            tracked.IsAwaitingHubRetry = false;
            tracked.NextPolicyRetryTick = G.Ticks + delay;

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Automatic policy retry scheduled | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"FailureKind={kind} | Policy={CARAUSettings.RetryPolicy} | " +
                $"NextAttempt={nextAttempt}/" +
                $"{CARAUSettings.AutomaticRetryLimit} | " +
                $"DueTick={tracked.NextPolicyRetryTick} | Delay={delay}"
            );
            return true;
        }

        private bool TryBuildAutomaticRetryPreview(
          CARAUTrackedOperation tracked,
          out UpgradeDecision decision,
          out string unavailableReason,
          bool logPlan = true
        )
        {
            decision = null;
            unavailableReason = null;
            if (tracked == null)
            {
                unavailableReason =
                    "Automatic retry stopped: The tracking record is invalid.";
                return false;
            }

            RefreshRuntimeTarget(tracked);
            var slot = tracked.RuntimeSlot;
            var occupyingOperation = slot == null
                ? null
                : S.Query.FindSurgicalOperation.Ask(slot, true);
            var ignoredOperation =
                IsTrackedOperationMatch(tracked, occupyingOperation)
                    ? occupyingOperation
                    : null;
            decision = UpgradeAnalyzer.AnalyzeDecision(
                S,
                UpgradeAnalyzer.AnalyzeSlot(slot),
                logHostileMindExclusions: logPlan,
                purpose: UpgradeEvaluationPurpose.AutomaticRetry,
                ignoredExistingOperation: ignoredOperation,
                actionKind: ResolveRetryActionKind(tracked, slot)
            );
            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (beingData?.Body == null || !beingData.Body.IsAlive)
            {
                UpgradeEvaluator.WithPolicyBlock(
                    decision?.Evaluation,
                    UpgradeReasonCode.DeadBeingRetryUnsupported,
                    "CARAU does not requeue a failed operation after the " +
                    "tracked Being has died."
                );
                unavailableReason =
                    "Automatic retry stopped:\n" +
                    UpgradeExplanationFormatter.BuildTooltip(
                        decision?.Evaluation
                    ) +
                    "\n\nManual retry is unavailable for this operation.";
                return false;
            }
            if (decision?.Evaluation?.IsQueueable != true)
            {
                unavailableReason =
                    "Automatic retry stopped:\n" +
                    UpgradeExplanationFormatter.BuildTooltip(
                        decision?.Evaluation
                    ) +
                    "\n\nManual retry is currently unavailable. CARAU will " +
                    "re-evaluate the current slot, inventory, and reservation " +
                    "state when retry is requested.";
                return false;
            }

            if (logPlan)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Decision,
                    $"Automatic retry fresh plan | Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | " +
                    $"PreviousReservedObjId={tracked.ReservedPartId} | " +
                    $"FreshObjId={decision.InventoryPart.Id} | " +
                    $"Disposition={decision.Evaluation?.Disposition} | " +
                    $"Reason={decision.Evaluation?.PrimaryReason} | " +
                    $"Decision={decision.Type}"
                );
            }

            return true;
        }

        private static long GetPolicyRetryDelay(int attemptNumber)
        {
            if (attemptNumber <= 1)
            {
                return 15L;
            }

            return attemptNumber == 2 ? 30L : 60L;
        }

        /// <summary>
        /// Stardeus can unregister and classify a surgical operation before
        /// its authoritative surgery result is copied into the Being log.
        /// Keep a retained generic failure eligible for later enrichment so
        /// a delayed DestroyPart, WoundPart, welding, or rejection message is
        /// displayed without reopening or reclassifying the operation.
        /// </summary>
        private bool TryEnrichRetainedFailureReason(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked == null ||
                !tracked.IsFailure ||
                !HasLowerPriorityFailureReason(tracked.OutcomeReason) ||
                !TryGetRelevantBeingFailureLog(
                    tracked,
                    out var beingFailureReason
                ))
            {
                return false;
            }

            var previousReason = tracked.OutcomeReason;
            var automaticRetrySuffixIndex = previousReason.IndexOf(
                "\n\nAutomatic retry stopped:",
                StringComparison.Ordinal
            );
            var automaticRetrySuffix = automaticRetrySuffixIndex >= 0
                ? previousReason.Substring(automaticRetrySuffixIndex)
                : string.Empty;
            tracked.OutcomeReason =
                AppendInstalledSurgicalResult(
                    tracked,
                    beingFailureReason,
                    tracked.FailureKind
                ) + automaticRetrySuffix;

            CARAUDebugLog.Add(
                CARAUDebugCategory.Warning,
                $"Failure reason enriched from delayed Being log | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"Previous={previousReason} | Reason={beingFailureReason}"
            );
            return true;
        }

        private static bool HasLowerPriorityFailureReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return true;
            }

            return reason.IndexOf(
                       "Stardeus surgery task failed:",
                       StringComparison.OrdinalIgnoreCase
                   ) >= 0 ||
                    reason.IndexOf(
                        "Stardeus marked the operation complete",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0 ||
                    reason.IndexOf(
                        "Native completion mismatch:",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0 ||
                   reason.IndexOf(
                       "reserved replacement part was consumed or destroyed",
                       StringComparison.OrdinalIgnoreCase
                   ) >= 0 ||
                   reason.IndexOf(
                       "operation disappeared before completion",
                       StringComparison.OrdinalIgnoreCase
                   ) >= 0 ||
                   reason.IndexOf(
                       "No failure reason was provided",
                       StringComparison.OrdinalIgnoreCase
                   ) >= 0 ||
                   (reason.IndexOf(
                        "Surgical result -",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0 &&
                    reason.IndexOf(
                        "Raw Stardeus Being log",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0);
        }

        private static bool HasSuccessfulSlotResult(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            if (tracked == null ||
                slot == null ||
                !tracked.HasOutcomeEvidence ||
                slot.InstalledDef == null ||
                !string.Equals(
                    slot.InstalledDef.Id,
                    tracked.TargetPartId,
                    StringComparison.Ordinal
                ))
            {
                return false;
            }

            if (string.IsNullOrEmpty(tracked.InstalledPartId) ||
                !string.Equals(
                    tracked.InstalledPartId,
                    tracked.TargetPartId,
                    StringComparison.Ordinal
                ))
            {
                return true;
            }

            // Stardeus presents part quality as a whole percentage. Treat an
            // installed result that matches that displayed target as the
            // planned result; otherwise tiny floating-point differences can
            // report "expected 58%, found 58%" as a failure.
            return Math.Round(slot.Quality * 100f) ==
                Math.Round(tracked.TargetQuality * 100f);
        }

        /// <summary>
        /// Stardeus can publish the extraction wound before the successful
        /// replacement installation becomes visible in the slot. If CARAU
        /// retained that early failure, reconcile it once the exact planned
        /// result is installed and remains usable. Implantation damage and
        /// unexplained native completion mismatches deliberately stay outside
        /// this correction.
        /// </summary>
        private bool TryReconcileRetainedRepairableInstalledSuccess(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            if (tracked?.IsFailure != true ||
                tracked.FailureKind !=
                    CARAUFailureKind.SurgicalRecoverableDamage ||
                !tracked.RuntimeNativeSurgicalFailureWasExtraction ||
                slot == null ||
                slot.IsPartRemoved ||
                slot.IsDestroyed ||
                slot.State == null ||
                slot.State.Condition <= 0f ||
                !slot.IsUsableSelf ||
                !slot.IsUsableWithParent ||
                !HasSuccessfulSlotResult(tracked, slot))
            {
                return false;
            }

            operations.Remove(tracked);
            successfulOperations++;
            failedOperations = Math.Max(0, failedOperations - 1);

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Repairable extraction damage reconciled to success | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"Replacement={tracked.TargetPartName} | " +
                $"Quality={slot.Quality * 100f:0.0}% | " +
                $"Condition={slot.State.Condition * 100f:0.0}%"
            );
            return true;
        }

        private static bool HasSuccessfulCoreTransitionResult(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            if (HasSuccessfulSlotResult(tracked, slot))
            {
                return true;
            }

            if (tracked == null ||
                slot?.HoldsMind != true ||
                !tracked.HasOutcomeEvidence ||
                slot.IsPartRemoved ||
                slot.IsDestroyed ||
                !slot.IsUsableSelf ||
                slot.InstalledDef == null ||
                !string.Equals(
                    slot.InstalledDef.Id,
                    tracked.TargetPartId,
                    StringComparison.Ordinal
                ) ||
                string.IsNullOrEmpty(tracked.InstalledPartId) ||
                !string.Equals(
                    tracked.InstalledPartId,
                    tracked.TargetPartId,
                    StringComparison.Ordinal
                ))
            {
                return false;
            }

            // A same-definition Core quality swap can cross a whole-percent
            // display boundary when its BodyPartState becomes the installed
            // slot state. Accept only the narrow one-point display drift, and
            // only when the final Core is still an actual improvement over
            // the pre-swap Core. Other slots retain exact-result validation.
            var previousPercent =
                Mathf.RoundToInt(tracked.InstalledQuality * 100f);
            var plannedPercent =
                Mathf.RoundToInt(tracked.TargetQuality * 100f);
            var finalPercent = Mathf.RoundToInt(slot.Quality * 100f);

            return slot.Quality > tracked.InstalledQuality &&
                finalPercent >= plannedPercent - 1 &&
                finalPercent > previousPercent;
        }

        private bool IsSuccessfulCoreIdentityTransition(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            if (slot?.HoldsMind != true ||
                !HasSuccessfulCoreTransitionResult(tracked, slot))
            {
                return false;
            }

            // Stardeus can report Fatality for the outgoing identity during
            // extraction and still install the intended incoming Core. Once
            // the native Core-transfer guard has stabilized, the exact
            // installed result is the authoritative outcome of this swap.
            return true;
        }

        /// <summary>
        /// Re-resolves the current Being and body-slot instances before
        /// classifying an operation. Replacing an identity-bearing Core can
        /// change the Being's displayed identity and replace its runtime body
        /// graph even though the chassis keeps the same tracked Being id.
        /// Holding the pre-operation slot instance would therefore inspect the
        /// removed Core rather than the replacement now installed in the
        /// chassis.
        /// </summary>
        private bool RefreshRuntimeTarget(CARAUTrackedOperation tracked)
        {
            if (tracked == null)
            {
                return false;
            }

            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (beingData?.Being == null || beingData.Body?.Slots == null)
            {
                return false;
            }

            var changed = !ReferenceEquals(
                tracked.RuntimeBeing,
                beingData.Being
            );
            tracked.RuntimeBeing = beingData.Being;

            var currentName =
                beingData.Body.Owner?.Name ?? "Unknown being";
            if (!string.Equals(
                    tracked.BeingName,
                    currentName,
                    StringComparison.Ordinal
                ))
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Intent,
                    $"Tracked being identity changed | " +
                    $"BeingId={tracked.BeingId} | " +
                    $"Previous={tracked.BeingName} | Current={currentName}"
                );
                tracked.BeingName = currentName;
                changed = true;
            }

            if (tracked.IsRemoveAllParts)
            {
                tracked.RuntimeSlot = null;
                return changed;
            }

            var currentSlot = beingData.Body.Slots.FirstOrDefault(candidate =>
                candidate != null &&
                (
                    (!string.IsNullOrEmpty(tracked.SlotId) &&
                     string.Equals(
                         candidate.Id,
                         tracked.SlotId,
                         StringComparison.Ordinal
                     )) ||
                    (string.IsNullOrEmpty(tracked.SlotId) &&
                     string.Equals(
                         candidate.NameT,
                         tracked.SlotName,
                         StringComparison.Ordinal
                     ))
                )
            );

            if (currentSlot != null &&
                !ReferenceEquals(tracked.RuntimeSlot, currentSlot))
            {
                tracked.RuntimeSlot = currentSlot;
                changed = true;
            }

            return changed;
        }

        private bool TryBuildDeathFailureReason(
          CARAUTrackedOperation tracked,
          out string reason
        )
        {
            reason = null;

            if (tracked == null)
            {
                return false;
            }

            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            var body = beingData?.Body;

            // A Core replacement kills the outgoing mind while the robotic
            // chassis remains alive and Stardeus creates or restores the
            // incoming identity. Body.IsAlive is therefore the authoritative
            // death boundary for tracked surgery; Being.IsDead can describe
            // only the mind being removed from a still-living chassis.
            if (body == null || body.IsAlive)
            {
                return false;
            }

            var logEntries =
                S.Query.GetBeingLogEntries.Ask(tracked.BeingId);
            var deathTick = body?.DeathTick ?? 0;
            var latestDeathLogText = logEntries?
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Text) &&
                    (
                        body == null ||
                        deathTick <= 0 ||
                        entry.Tick <= deathTick
                    )
                )
                .OrderByDescending(entry => entry.Tick)
                .Select(entry => entry.Text)
                .FirstOrDefault();

            // Stardeus records the detailed surgical failure immediately
            // around the death entry. Prefer the explicit "patient did not
            // survive" message over the broader Body.DeathCause value.
            var surgicalDeathLogText = logEntries?
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Text) &&
                    entry.Text.IndexOf(
                        "failed to perform surgery",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0 &&
                    entry.Text.IndexOf(
                        "patient did not survive",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0
                )
                .OrderBy(entry =>
                    deathTick <= 0
                        ? 0L
                        : Math.Abs(entry.Tick - deathTick)
                )
                .ThenByDescending(entry => entry.Tick)
                .Select(entry => entry.Text)
                .FirstOrDefault();

            var cause = body?.DeathCause;
            if (!string.IsNullOrWhiteSpace(latestDeathLogText))
            {
                reason =
                    "The being died while this CARAU operation was pending. " +
                    $"Stardeus Being log: {latestDeathLogText}";

                if (!string.IsNullOrWhiteSpace(cause))
                {
                    reason += $" Recorded cause of death: {cause}";
                }

                if (!string.IsNullOrWhiteSpace(surgicalDeathLogText))
                {
                    reason +=
                        $" Reason of death: {surgicalDeathLogText}";
                }
            }
            else if (!string.IsNullOrWhiteSpace(cause))
            {
                reason =
                    "The being died while this CARAU operation was pending. " +
                    $"Recorded cause of death: {cause}";
            }
            else
            {
                reason =
                    "The being died while this CARAU operation was pending. " +
                    "Stardeus did not provide a specific cause of death.";
            }

            return true;
        }

        private void CancelLiveOperationAfterDeath(
          CARAUTrackedOperation tracked
        )
        {
            var slot = tracked?.RuntimeSlot;
            var operation = slot == null
                ? null
                : S.Query.FindSurgicalOperation.Ask(slot, true);

            if (!IsTrackedOperationMatch(tracked, operation))
            {
                return;
            }

            try
            {
                operation.State.Set(SurgicalOpState.Canceled);
                S.Query.RegisterSurgicalOperation.Ask(operation, false);

                CARAUDebugLog.Add(
                    CARAUDebugCategory.Cleanup,
                    $"Tracked operation auto-cancelled after death | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"Target={tracked.TargetPartName}"
                );
            }
            catch (Exception ex)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    $"Tracked operation could not be auto-cancelled after " +
                    $"death | Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | Error={ex.Message}"
                );
            }
        }

        /// <summary>
        /// A destroyed identity-bearing Core makes every other queued body
        /// operation for that chassis invalid. Cancel only CARAU's exact
        /// matching native operations, discard sibling records that no longer
        /// have a matching native owner, and retain the Core failure as the
        /// single explanation for the dead chassis.
        /// </summary>
        private int CancelPendingSiblingOperationsAfterCoreDestruction(
          CARAUTrackedOperation coreOperation
        )
        {
            if (coreOperation == null)
            {
                return 0;
            }

            var siblings = operations
                .Where(candidate =>
                    candidate?.IsPending == true &&
                    candidate.BeingId == coreOperation.BeingId &&
                    !ReferenceEquals(candidate, coreOperation)
                )
                .ToList();
            var cancelled = 0;
            var trackingOnly = 0;
            var failed = 0;

            foreach (var sibling in siblings)
            {
                RefreshRuntimeTarget(sibling);
                var slot = sibling.RuntimeSlot;
                var operation = slot == null
                    ? null
                    : S.Query.FindSurgicalOperation.Ask(slot, true);

                if (!IsTrackedOperationMatch(sibling, operation))
                {
                    if (operations.Remove(sibling))
                    {
                        trackingOnly++;
                    }

                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Cleanup,
                        $"Dead-chassis sibling tracking removed | " +
                        $"Being={sibling.BeingName} | " +
                        $"Slot={sibling.SlotName} | " +
                        "Reason=no matching CARAU native operation remained"
                    );
                    continue;
                }

                try
                {
                    operation.State.Set(SurgicalOpState.Canceled);
                    var unregistered =
                        S.Query.RegisterSurgicalOperation.Ask(
                            operation,
                            false
                        );
                    operations.Remove(sibling);
                    cancelled++;

                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Cleanup,
                        $"Dead-chassis sibling operation cancelled | " +
                        $"Being={sibling.BeingName} | " +
                        $"Slot={sibling.SlotName} | " +
                        $"Target={sibling.TargetPartName} | " +
                        $"NativeUnregistered={unregistered}"
                    );
                }
                catch (Exception ex)
                {
                    failed++;
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Error,
                        $"Dead-chassis sibling operation could not be " +
                        $"cancelled | Being={sibling.BeingName} | " +
                        $"Slot={sibling.SlotName} | Error={ex.Message}"
                    );
                }
            }

            if (siblings.Count > 0)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Cleanup,
                    $"Dead-chassis sibling cleanup complete | " +
                    $"Being={coreOperation.BeingName} | " +
                    $"Requested={siblings.Count} | Cancelled={cancelled} | " +
                    $"TrackingOnly={trackingOnly} | Failed={failed}"
                );
            }

            return cancelled + trackingOnly;
        }

        private void CancelLiveOperationAfterMentalBreakdown(
          CARAUTrackedOperation tracked
        )
        {
            var slot = tracked?.RuntimeSlot;
            var operation = slot == null
                ? null
                : S.Query.FindSurgicalOperation.Ask(slot, true);

            if (!IsTrackedOperationMatch(tracked, operation))
            {
                return;
            }

            try
            {
                operation.State.Set(SurgicalOpState.Canceled);
                S.Query.RegisterSurgicalOperation.Ask(operation, false);

                CARAUDebugLog.Add(
                    CARAUDebugCategory.Cleanup,
                    $"Tracked operation auto-cancelled for mental " +
                    $"instability | Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | " +
                    $"Target={tracked.TargetPartName}"
                );
            }
            catch (Exception ex)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    $"Tracked operation could not be auto-cancelled for " +
                    $"mental instability | Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | Error={ex.Message}"
                );
            }
        }

        private bool TryGetInvalidReason(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot,
          out string reason
        )
        {
            reason = null;

            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (beingData?.Being == null)
            {
                reason = "The tracked being no longer exists.";
                return true;
            }

            if (slot == null ||
                beingData.Body?.Slots == null ||
                !beingData.Body.Slots.Contains(slot))
            {
                reason = "The tracked body slot no longer exists.";
                return true;
            }

            return false;
        }

        private CARAUFailureKind ClassifyCurrentRetryBlock(
          CARAUTrackedOperation tracked,
          string unavailableReason
        )
        {
            if (tracked == null)
            {
                return CARAUFailureKind.InvalidTarget;
            }

            RefreshRuntimeTarget(tracked);
            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (beingData?.Body == null || !beingData.Body.IsAlive)
            {
                return CARAUFailureKind.BeingDied;
            }

            var slot = tracked.RuntimeSlot;
            if (slot == null)
            {
                return CARAUFailureKind.InvalidTarget;
            }

            if (slot.IsDestroyed)
            {
                return CARAUFailureKind.SlotDestroyed;
            }

            if (!slot.IsPartRemoved && slot.State?.IsWelded == true)
            {
                return CARAUFailureKind.InstalledPartWelded;
            }

            return string.IsNullOrWhiteSpace(unavailableReason)
                ? CARAUFailureKind.Unknown
                : CARAUFailureKind.PreSurgeryTaskFailure;
        }

        private static CARAUFailureKind GetCapturedSurgicalFailureKind(
          CARAUTrackedOperation tracked
        )
        {
            return tracked?.RuntimeNativeSurgicalFailureKind !=
                   CARAUFailureKind.None
                ? tracked.RuntimeNativeSurgicalFailureKind
                : CARAUFailureKind.Unknown;
        }

        private bool IsReservedPartMissing(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked == null || tracked.ReservedPartId == 0)
            {
                return false;
            }

            var allObjects = S.Query.GetAllObjs.Ask();
            return allObjects != null &&
                !allObjects.ContainsKey(tracked.ReservedPartId);
        }

        private string BuildCompletedWithoutPlannedReplacementReason(
          CARAUTrackedOperation tracked
        )
        {
            var slot = tracked?.RuntimeSlot;
            var targetName = tracked?.TargetPartName ?? "planned replacement";
            var builder = new StringBuilder();
            builder.Append(
                "Native completion mismatch: Stardeus marked the operation " +
                "Completed, but CARAU's post-operation validation did not " +
                "find the exact planned result. "
            );

            if (slot?.InstalledDef == null)
            {
                builder.Append("Current slot result: Missing. ");
            }
            else if (string.Equals(
                         slot.InstalledDef.Id,
                         tracked?.TargetPartId,
                         StringComparison.Ordinal
                     ))
            {
                builder.Append(
                    $"The target part type {targetName} is present, but its " +
                    $"quality does not match the queued object (planned " +
                    $"Q:{tracked.TargetQuality * 100f:0}%, observed " +
                    $"Q:{slot.Quality * 100f:0}%)"
                );
                if (slot.State != null)
                {
                    builder.Append(
                        $" at {slot.State.Condition * 100f:0.0}% Condition"
                    );
                }
                builder.Append(". ");
            }
            else
            {
                builder.Append(
                    $"The planned {targetName} was not installed in " +
                    $"{tracked?.SlotName ?? "the target slot"}. Current slot " +
                    $"result: {slot.ItemName ?? "Unknown part"}, " +
                    $"Q:{slot.Quality * 100f:0}%"
                );
                if (slot.State != null)
                {
                    builder.Append(
                        $", {slot.State.Condition * 100f:0.0}% Condition"
                    );
                }
                builder.Append(". ");
            }

            var partId = tracked?.ReservedPartId ?? 0;
            if (partId == 0)
            {
                builder.Append(
                    "CARAU has no exact replacement-object id for further " +
                    "diagnosis."
                );
                return builder.ToString();
            }

            var allObjects = S.Query.GetAllObjs.Ask();
            if (allObjects == null)
            {
                builder.Append(
                    $"The final state of exact replacement object #{partId} " +
                    "could not be queried."
                );
                return builder.ToString();
            }

            if (allObjects.TryGetValue(partId, out var replacement) &&
                replacement != null)
            {
                builder.Append(
                    $"Exact replacement object #{partId} still exists after " +
                    $"completion in state {replacement.State}"
                );
                if (replacement.BodyPartState != null)
                {
                    builder.Append(
                        $" at Q:{replacement.BodyPartState.Quality * 100f:0}% " +
                        $"and {replacement.BodyPartState.Condition * 100f:0.0}% " +
                        "Condition"
                    );
                }
                builder.Append("; it was not applied as planned.");
                return builder.ToString();
            }

            builder.Append(
                $"Exact replacement object #{partId} no longer exists. No " +
                "native surgical failure result, task-failure reason, or " +
                "matching Being failure log was captured, so CARAU cannot " +
                "determine whether Stardeus consumed or destroyed that object " +
                "or completed through another unreported path."
            );
            return builder.ToString();
        }

        /// <summary>
        /// Revalidates only the exact replacement object owned by a CARAU
        /// operation, and only before native surgery begins. Pending and
        /// Preparing operations may hold a loose, stored, or device-loaded
        /// part. Any other state, a missing object, or loss of the exact
        /// native operation ownership means Core can no longer complete this
        /// plan. Ownership may be represented by the operation itself or by
        /// its active procedure goal after Core begins preparation.
        /// Ongoing operations are deliberately left entirely to Core.
        /// </summary>
        private bool TryHandleInvalidPreSurgeryReplacement(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            if (!TryGetInvalidPreSurgeryReplacementReason(
                    tracked,
                    operation,
                    out var reason,
                    out var failureKind
                ))
            {
                return false;
            }

            if (!TryCancelInvalidPreSurgeryOperation(
                    tracked,
                    operation,
                    reason
                ))
            {
                return false;
            }

            HandleOperationFailure(
                tracked,
                CARAUOperationOutcome.Failed,
                reason,
                failureKind
            );
            return true;
        }

        /// <summary>
        /// Stardeus may attach its emergency Move-to-AutoDoc hauling goal to
        /// an incapacitated Being while that Being is waiting for surgery.
        /// If the carrier cannot pick the Being up, the native non-retryable
        /// goal can remain failed on the Being and block SurgicalQueue from
        /// starting an otherwise-valid registered operation indefinitely.
        ///
        /// CARAU uses only Stardeus's normal task Retry action, and only when
        /// every part of that exact native signature is present. Active work,
        /// unrelated goals, operations that have started Preparing, recovered
        /// patients, hostile Beings, and paused time remain untouched.
        /// </summary>
        private bool TryRecoverFailedPreSurgeryAutoDocTransfer(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            if (tracked == null ||
                operation == null ||
                operation.State.Value != SurgicalOpState.Pending ||
                operation.AssociatedGoal != null ||
                S.Clock.IsStopped ||
                G.Ticks < tracked.NextPreSurgeryAutoDocRetryTick ||
                !IsFailureKindAllowedByPolicy(
                    CARAUFailureKind.PreSurgeryTaskFailure
                ))
            {
                return false;
            }

            var beingData = operation.TargetBeing;
            var being = beingData?.Being;
            var body = beingData?.Body;
            if (being == null ||
                body == null ||
                !being.IsActive ||
                being.IsDead ||
                !body.IsAlive ||
                !body.IsIncapacitated ||
                !beingData.IsFriendly)
            {
                return false;
            }

            var goal = being.Goal;
            if (goal == null ||
                !ReferenceEquals(goal.Target, being.Conditions) ||
                goal.JobIdH != JobTypeIdH.Hauling ||
                !goal.NonRetryable ||
                !AIGoal.IsFailed(goal) ||
                goal.Agent != null ||
                !string.Equals(
                    goal.Name,
                    T.MoveToPos_V1.F(T.ObjectsDevicesAutodoc),
                    StringComparison.Ordinal
                ))
            {
                return false;
            }

            var nativeReason = goal.FailReason;
            if (string.IsNullOrWhiteSpace(nativeReason))
            {
                nativeReason = goal.TaskLineFail;
            }

            if (!string.Equals(
                    nativeReason,
                    "Failed to carry being",
                    StringComparison.Ordinal
                ))
            {
                return false;
            }

            if (!HasAutomaticRetryBudget(tracked))
            {
                if (!tracked.RuntimeAutoDocRetryLimitReported)
                {
                    tracked.RuntimeAutoDocRetryLimitReported = true;
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Warning,
                        $"Pre-surgery AutoDoc transfer retry limit reached | " +
                        $"Being={tracked.BeingName} | " +
                        $"Slot={tracked.SlotName} | " +
                        $"OperationId={operation.Id} | " +
                        $"Attempts={GetAutomaticRetryCount(tracked)}/" +
                        $"{CARAUSettings.AutomaticRetryLimit} | " +
                        $"NativeReason={nativeReason}"
                    );
                }
                return false;
            }

            var previousState = goal.State;
            if (!AIGoal.TryRetry(
                    goal,
                    G.Ticks,
                    force: true,
                    boost: true
                ))
            {
                tracked.NextPreSurgeryAutoDocRetryTick =
                    G.Ticks + GetPolicyRetryDelay(
                        GetAutomaticRetryCount(tracked) + 1
                    );
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    $"Pre-surgery AutoDoc transfer retry was rejected | " +
                    $"Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | " +
                    $"OperationId={operation.Id} | " +
                    $"GoalState={previousState} | " +
                    $"NativeReason={nativeReason}"
                );
                return false;
            }

            tracked.PreSurgeryAutoDocRetryCount++;
            tracked.NextPreSurgeryAutoDocRetryTick =
                G.Ticks + GetPolicyRetryDelay(
                    GetAutomaticRetryCount(tracked) + 1
                );
            tracked.RuntimeAutoDocRetryLimitReported = false;
            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Pre-surgery AutoDoc transfer retried | " +
                $"Being={tracked.BeingName} | " +
                $"Slot={tracked.SlotName} | " +
                $"OperationId={operation.Id} | " +
                $"GoalState={previousState} | " +
                $"NativeReason={nativeReason} | " +
                $"Attempt={GetAutomaticRetryCount(tracked)}/" +
                $"{CARAUSettings.AutomaticRetryLimit}"
            );
            return true;
        }

        /// <summary>
        /// Stardeus can leave a failed, non-retryable body-part hauling goal
        /// attached to an Assembly/Surgery Hub after its target disappears.
        /// A later patient can enter that same Hub, but TryOrderPartDelivery
        /// refuses to create the new delivery goal while the stale goal is
        /// still attached and leaves the new operation Preparing forever.
        ///
        /// CARAU intervenes only before surgery begins, only for the exact Hub
        /// currently containing its tracked patient, and only when the Hub's
        /// terminal goal is an unavailable-target body-part haul for a
        /// different object. Active, retryable, current-operation, and
        /// unrelated goals remain untouched.
        /// </summary>
        private bool TryRecoverStaleHubBodyPartGoal(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            if (tracked == null ||
                operation == null ||
                tracked.RuntimeStaleHubGoalRecoveryAttempted ||
                operation.State.Value != SurgicalOpState.Preparing ||
                operation.ReservedPart == null ||
                AIGoal.IsActive(operation.AssociatedGoal))
            {
                return false;
            }

            var being = tracked.RuntimeBeing ?? operation.TargetBeing?.Being;
            if (being == null ||
                !operation.TargetBeing.HasStateFlag(BeingState.InDevice))
            {
                return false;
            }

            var device = EntityUtils.ParentObjAt(being.PosIdx);
            if (device == null ||
                !device.TryGetComponent<BodyModSpotComp>(out var modSpot) ||
                !ReferenceEquals(modSpot.Patient?.Data, operation.TargetBeing) ||
                !ReferenceEquals(
                    S.Query.GetOngoingSurgicalOp.Ask(operation.TargetBeing),
                    operation
                ))
            {
                return false;
            }

            var staleGoal = device.Goal;
            if (staleGoal == null ||
                !ReferenceEquals(staleGoal.Target, modSpot) ||
                staleGoal.JobIdH != JobTypeIdH.Hauling ||
                !staleGoal.NonRetryable ||
                !AIGoal.IsFailed(staleGoal) ||
                staleGoal.Agent != null ||
                !string.Equals(
                    staleGoal.Name,
                    $"{T.HaulItem}: {T.NounBodyPart}",
                    StringComparison.Ordinal
                ))
            {
                return false;
            }

            var staleReason = staleGoal.FailReason;
            if (string.IsNullOrWhiteSpace(staleReason))
            {
                staleReason = staleGoal.TaskLineFail;
            }

            if (!string.Equals(
                    staleReason,
                    T.AdRejTargetUnavailable,
                    StringComparison.Ordinal
                ) ||
                !staleGoal.TryGetVar(
                    AIGoalVars.ObjId_Uint,
                    out uint stalePartId
                ) ||
                stalePartId == 0 ||
                stalePartId == operation.ReservedPart.Id)
            {
                return false;
            }

            tracked.RuntimeStaleHubGoalRecoveryAttempted = true;
            var stalePart = S.Query.FindObjData.Ask(stalePartId);
            var releasedStaleReservation = false;
            if (stalePart?.Reservation?.IsFor(staleGoal) == true)
            {
                releasedStaleReservation = stalePart.TryUnreserve(staleGoal);
            }

            if (!device.UnsetGoal(staleGoal))
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    $"Stale Hub goal could not be detached | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"Hub={device} | StalePartId={stalePartId}"
                );
                return false;
            }

            // Native Stop performs the normal one-time operation cleanup and
            // ejects the untouched patient before CARAU enters policy retry.
            modSpot.Stop(false, cancelSurgery: true);
            tracked.RuntimeCoreTransitionAwaitingValidation = false;
            tracked.RuntimeCoreTransitionValidateAfterRealtime = 0f;
            foreach (var sibling in operations)
            {
                if (sibling?.IsPending == true &&
                    sibling.BeingId == tracked.BeingId)
                {
                    sibling.RuntimeInterruptedByCoreIdentityTransition = false;
                }
            }

            var remaining = tracked.RuntimeSlot == null
                ? null
                : S.Query.FindSurgicalOperation.Ask(
                    tracked.RuntimeSlot,
                    true
                );
            if (IsTrackedOperationMatch(tracked, remaining))
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    $"Stale Hub goal detached but native cleanup did not " +
                    $"remove the operation | Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | StalePartId={stalePartId}"
                );
                return true;
            }

            var reason =
                "Pre-surgery Hub recovery: Stardeus left a terminal " +
                "body-part delivery task attached to the facility after its " +
                $"previous target object #{stalePartId} became unavailable. " +
                "That stale facility task blocked this untouched operation " +
                "from requesting its own replacement. CARAU detached only " +
                "that inactive Hub-owned task, performed native cleanup, and " +
                "will retry according to the Failure Retry Policy.";

            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                $"Stale Hub body-part goal recovered | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"Hub={device} | StalePartId={stalePartId} | " +
                $"CurrentPartId={operation.ReservedPart.Id} | " +
                $"ReleasedStaleReservation={releasedStaleReservation}"
            );

            if (tracked.IsPending && !tracked.IsAwaitingPolicyRetry)
            {
                HandleOperationFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    reason,
                    CARAUFailureKind.PreSurgeryTaskFailure
                );
            }
            return true;
        }

        private bool TryGetInvalidPreSurgeryReplacementReason(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation,
          out string reason,
          out CARAUFailureKind failureKind
        )
        {
            reason = null;
            failureKind = CARAUFailureKind.None;
            if (tracked == null ||
                operation == null ||
                tracked.ReservedPartId == 0)
            {
                return false;
            }

            var state = operation.State.Value;
            if (state != SurgicalOpState.Pending &&
                state != SurgicalOpState.Preparing)
            {
                return false;
            }

            var allObjects = S.Query.GetAllObjs.Ask();
            if (allObjects == null)
            {
                return false;
            }

            if (!allObjects.TryGetValue(
                    tracked.ReservedPartId,
                    out var currentPart
                ) ||
                currentPart == null)
            {
                reason =
                    "CARAU stopped this pre-surgery operation because its " +
                    $"exact reserved replacement (object id " +
                    $"{tracked.ReservedPartId}) no longer exists. No surgery " +
                    "had begun.";
                failureKind =
                    CARAUFailureKind.ReplacementDestroyedOrConsumed;
                return true;
            }

            if (!ReferenceEquals(currentPart, operation.ReservedPart))
            {
                reason =
                    "CARAU stopped this pre-surgery operation because its " +
                    $"reserved replacement object id {tracked.ReservedPartId} " +
                    "no longer resolves to the exact object registered with " +
                    "the native operation. No surgery had begun.";
                failureKind =
                    CARAUFailureKind.ReplacementDestroyedOrConsumed;
                return true;
            }

            if (currentPart.State != ObjState.Active &&
                currentPart.State != ObjState.Stored &&
                currentPart.State != ObjState.InDevice)
            {
                reason =
                    "CARAU stopped this pre-surgery operation because its " +
                    $"exact reserved replacement (object id " +
                    $"{tracked.ReservedPartId}) entered the invalid " +
                    $"pre-surgery state {currentPart.State}. No surgery had " +
                    "begun.";
                failureKind =
                    CARAUFailureKind.ReplacementDestroyedOrConsumed;
                return true;
            }

            if (!InventoryService.ReservationSupportsSurgery(
                    currentPart.Reservation,
                    operation
                ) &&
                !IsReservationReleasedByFailedNativeGoal(
                    currentPart.Reservation,
                    operation
                ))
            {
                if (TryRestoreOwnedPreSurgeryReservation(
                        tracked,
                        operation,
                        currentPart
                    ))
                {
                    return false;
                }

                reason = BuildInconsistentNativeReservationReason(
                    tracked,
                    currentPart.Reservation
                );
                failureKind =
                    CARAUFailureKind.InconsistentNativeReservation;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Core may validate and release an inactive goal reservation after a
        /// different registered operation has already selected the same exact
        /// part. CARAU may clear only that natively-invalid goal ownership and
        /// may restore only an unowned part to its still-active registered
        /// operation. Active foreign goals and all foreign surgeries remain
        /// untouched. One repair attempt prevents ownership ping-pong.
        /// </summary>
        private bool TryRestoreOwnedPreSurgeryReservation(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation,
          ObjData currentPart
        )
        {
            if (tracked == null ||
                operation == null ||
                currentPart == null ||
                tracked.RuntimePreSurgeryReservationRepairCount > 0)
            {
                return false;
            }

            var operationGoal = operation.AssociatedGoal;
            if (operationGoal != null && !AIGoal.IsActive(operationGoal))
            {
                return false;
            }

            var reservation = currentPart.Reservation;
            if (reservation != null)
            {
                var foreignGoal = reservation.Goal;
                if (reservation.Surgery != null ||
                    foreignGoal == null ||
                    ReferenceEquals(
                        foreignGoal,
                        operation.AssociatedGoal
                    ) ||
                    AIGoal.IsActive(foreignGoal))
                {
                    return false;
                }

                var foreignReason = foreignGoal.FailReason;
                if (string.IsNullOrWhiteSpace(foreignReason))
                {
                    foreignReason = foreignGoal.TaskLineFail;
                }

                if (!currentPart.TryUnreserve(foreignGoal))
                {
                    return false;
                }

                CARAUDebugLog.Add(
                    CARAUDebugCategory.Cleanup,
                    $"Inactive foreign replacement reservation cleared | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"PartId={tracked.ReservedPartId} | " +
                    $"ForeignGoal={foreignGoal.Name ?? "unknown"} | " +
                    $"ForeignGoalState={foreignGoal.State} | " +
                    $"ForeignReason=" +
                    $"{(string.IsNullOrWhiteSpace(foreignReason) ? "none" : foreignReason)}"
                );
            }

            if (currentPart.Reservation != null)
            {
                return false;
            }

            if (!currentPart.TryReserveForOperation(operation))
            {
                return false;
            }

            tracked.RuntimePreSurgeryReservationRepairCount++;
            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                $"Pre-surgery replacement reservation restored | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"PartId={tracked.ReservedPartId} | " +
                $"OperationId={operation.Id} | " +
                $"AssociatedGoal={operationGoal?.Name ?? "pending"} | " +
                $"AssociatedGoalState=" +
                $"{(operationGoal == null ? "none" : operationGoal.State.ToString())}"
            );
            return true;
        }

        private static string BuildInconsistentNativeReservationReason(
          CARAUTrackedOperation tracked,
          ObjReservation reservation
        )
        {
            var partId = tracked?.ReservedPartId ?? 0;
            if (reservation == null)
            {
                return
                    "CARAU stopped this pre-surgery operation because its " +
                    $"exact replacement (object id {partId}) still exists, " +
                    "but its reservation could not be safely restored to " +
                    "this registered native operation. No surgery had begun.";
            }

            var owner = reservation.Surgery != null
                ? "another native surgery"
                : reservation.Goal != null
                    ? $"the native goal {reservation.Goal.Name ?? "unknown"}"
                    : "inconsistent native work";
            return
                "CARAU stopped this pre-surgery operation because its exact " +
                $"replacement (object id {partId}) still exists, but its " +
                $"reservation belongs to {owner}. CARAU left that ownership " +
                "untouched. No surgery had begun.";
        }

        /// <summary>
        /// Core replaces the direct surgery reservation with the active
        /// procedure goal while an operation is Preparing. If that exact goal
        /// fails, ObjData validation can release its reservation before the
        /// registered operation is retried or unregistered. The registered
        /// operation remains authoritative during that native recovery window.
        /// Do not mistake its cleanup for external replacement loss; a null
        /// reservation or the same failed goal is allowed, but foreign
        /// ownership is not.
        /// </summary>
        private static bool IsReservationReleasedByFailedNativeGoal(
          ObjReservation reservation,
          SurgicalOperation operation
        )
        {
            var goal = operation?.AssociatedGoal;
            if (goal == null || AIGoal.IsActive(goal))
            {
                return false;
            }

            var goalReason = goal.FailReason;
            if (string.IsNullOrWhiteSpace(goalReason))
            {
                goalReason = goal.TaskLineFail;
            }

            if (string.IsNullOrWhiteSpace(goalReason))
            {
                return false;
            }

            return reservation == null ||
                ReferenceEquals(reservation.Goal, goal);
        }

        private bool TryCancelInvalidPreSurgeryOperation(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation,
          string reason
        )
        {
            var previousState = operation.State.Value;
            var reservedPart = operation.ReservedPart;
            var ownsReservation =
                reservedPart?.Reservation?.Surgery == operation;

            try
            {
                // If another operation now owns the object reservation, keep
                // Core's normal removal path from trying to unreserve someone
                // else's work. The CARAU operation itself is still removed
                // through RegisterSurgicalOperation so its goal is cancelled.
                if (!ownsReservation)
                {
                    operation.ReservedPart = null;
                }

                operation.State.Set(SurgicalOpState.Canceled);
                if (!S.Query.RegisterSurgicalOperation.Ask(operation, false))
                {
                    operation.ReservedPart = reservedPart;
                    operation.State.Set(previousState);
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Error,
                        $"Invalid pre-surgery operation could not be " +
                        $"cancelled | Being={tracked.BeingName} | " +
                        $"Slot={tracked.SlotName} | " +
                        $"NativeState={previousState} | " +
                        $"ReservedPartId={tracked.ReservedPartId}"
                    );
                    return false;
                }

                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    $"Invalid pre-surgery replacement stopped | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"Target={tracked.TargetPartName} | " +
                    $"NativeState={previousState} | " +
                    $"ReservedPartId={tracked.ReservedPartId} | " +
                    $"Reason={reason}"
                );
                return true;
            }
            catch (Exception ex)
            {
                operation.ReservedPart = reservedPart;
                operation.State.Set(previousState);
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    $"Invalid pre-surgery operation cleanup failed | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"NativeState={previousState} | " +
                    $"ReservedPartId={tracked.ReservedPartId} | " +
                    $"Error={ex.Message}"
                );
                return false;
            }
        }

        private static string BuildConsumedPartFailureReason(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            if (slot?.InstalledDef != null &&
                string.Equals(
                    slot.InstalledDef.Id,
                    tracked.TargetPartId,
                    StringComparison.Ordinal
                ))
            {
                return
                    "The replacement definition reached the target slot, " +
                    "but its precise quality did not match the planned " +
                    $"result (expected " +
                    $"{tracked.TargetQuality * 100f:0.00}%, found " +
                    $"{slot.Quality * 100f:0.00}%).";
            }

            var installedPartId =
                slot?.InstalledDef?.Id ?? "none";
            return
                "The target slot does not contain the planned replacement " +
                $"definition (expected id: {tracked.TargetPartId}, " +
                $"found id: {installedPartId}). The reserved replacement " +
                "part was consumed or destroyed before CARAU could confirm " +
                "a successful installation.";
        }

        private static bool NormalizeConsumedPartFailure(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            if (tracked?.Outcome != CARAUOperationOutcome.Invalid ||
                !string.Equals(
                    tracked.OutcomeReason,
                    "The reserved replacement part no longer exists.",
                    StringComparison.Ordinal
                ))
            {
                return false;
            }

            tracked.Outcome = CARAUOperationOutcome.Failed;
            tracked.FailureKind =
                CARAUFailureKind.ReplacementDestroyedOrConsumed;
            tracked.OutcomeReason =
                BuildConsumedPartFailureReason(tracked, slot);
            return true;
        }

        private void BeginCoreTransitionValidation(
          CARAUTrackedOperation tracked,
          string source
        )
        {
            if (tracked?.IsPending != true ||
                tracked.RuntimeSlot?.HoldsMind != true)
            {
                return;
            }

            var wasAwaiting =
                tracked.RuntimeCoreTransitionAwaitingValidation;
            tracked.RuntimeCoreTransitionAwaitingValidation = true;
            tracked.RuntimeCoreTransitionValidateAfterRealtime = Math.Max(
                tracked.RuntimeCoreTransitionValidateAfterRealtime,
                Time.realtimeSinceStartup +
                    CoreTransitionStabilizationSeconds
            );

            foreach (var sibling in operations)
            {
                if (sibling?.IsPending == true &&
                    sibling.BeingId == tracked.BeingId &&
                    !ReferenceEquals(sibling, tracked))
                {
                    sibling.RuntimeInterruptedByCoreIdentityTransition = true;
                }
            }

            if (!wasAwaiting)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Intent,
                    $"Core identity transition deferred for final validation | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"Source={source} | StabilizationSeconds=" +
                    $"{CoreTransitionStabilizationSeconds:0.0}"
                );
            }
        }

        private bool IsBeingInCoreTransition(uint beingId)
        {
            return operations.Any(operation =>
                operation?.IsPending == true &&
                operation.BeingId == beingId &&
                operation.RuntimeCoreTransitionAwaitingValidation
            );
        }

        private bool TryReconcileRetainedCoreSuccess(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            if (tracked?.IsFailure != true ||
                slot?.HoldsMind != true ||
                !HasSuccessfulCoreTransitionResult(tracked, slot) ||
                (tracked.FailureKind !=
                     CARAUFailureKind.IdentityCoreDestroyed &&
                 tracked.FailureKind != CARAUFailureKind.BeingDied &&
                 tracked.FailureKind !=
                     CARAUFailureKind.ReplacementDestroyedOrConsumed))
            {
                return false;
            }

            var priorKind = tracked.FailureKind;
            var interruptedSiblingCount = 0;
            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (beingData?.Body?.IsAlive == true)
            {
                foreach (var sibling in operations)
                {
                    if (sibling?.IsFailure != true ||
                        ReferenceEquals(sibling, tracked) ||
                        sibling.BeingId != tracked.BeingId ||
                        sibling.FailureKind != CARAUFailureKind.BeingDied ||
                        Math.Abs(sibling.EndedAtTick - tracked.EndedAtTick) > 15L)
                    {
                        continue;
                    }

                    sibling.FailureKind =
                        CARAUFailureKind.PreSurgeryTaskFailure;
                    sibling.OutcomeReason =
                        "This operation was interrupted during a Core " +
                        "identity transition that ultimately installed the " +
                        "planned Core. It remains retained for current " +
                        "eligibility to determine whether the player can " +
                        "retry it.";
                    interruptedSiblingCount++;
                }
            }

            operations.Remove(tracked);
            successfulOperations++;
            failedOperations = Math.Max(0, failedOperations - 1);

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Retained Core failure corrected to installed success | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"PreviousFailureKind={priorKind} | " +
                $"InterruptedSiblingsReclassified=" +
                $"{interruptedSiblingCount}"
            );
            return true;
        }

        private bool TryFinalizeIdentityCoreDestroyedFailure(
          CARAUTrackedOperation tracked,
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            if (tracked == null ||
                slot?.HoldsMind != true ||
                (tracked.RuntimeNativeSurgicalFailureKind !=
                     CARAUFailureKind.IdentityCoreDestroyed &&
                 !tracked.RuntimeCoreTransitionAwaitingValidation &&
                 tracked.FailureKind !=
                     CARAUFailureKind.IdentityCoreDestroyed &&
                 tracked.FailureKind !=
                     CARAUFailureKind.ReplacementDestroyedOrConsumed))
            {
                return false;
            }

            if (tracked.RuntimeCoreTransitionAwaitingValidation &&
                Time.realtimeSinceStartup <
                    tracked.RuntimeCoreTransitionValidateAfterRealtime)
            {
                return false;
            }

            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            var body = beingData?.Body;
            var coreIsAbsentOrDestroyed = slot.IsPartRemoved ||
                                          slot.IsDestroyed ||
                                          slot.InstalledDef == null;
            if (body == null || body.IsAlive || !coreIsAbsentOrDestroyed)
            {
                return false;
            }

            var cleanedSiblingCount =
                CancelPendingSiblingOperationsAfterCoreDestruction(tracked);

            if (tracked.IsFailure &&
                tracked.FailureKind ==
                    CARAUFailureKind.IdentityCoreDestroyed)
            {
                return cleanedSiblingCount > 0;
            }

            var reason = BuildIdentityCoreDestroyedReason(tracked);
            if (cleanedSiblingCount > 0)
            {
                reason +=
                    $"\n\nCARAU ended {cleanedSiblingCount} remaining " +
                    $"tracked operation" +
                    (cleanedSiblingCount == 1 ? string.Empty : "s") +
                    " for this dead chassis and cancelled every matching " +
                    "native operation so Stardeus can release the body from " +
                    "the surgery facility.";
            }
            if (tracked.IsPending)
            {
                CancelLiveOperationAfterDeath(tracked);
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    reason,
                    CARAUFailureKind.IdentityCoreDestroyed
                );
            }
            else
            {
                tracked.FailureKind =
                    CARAUFailureKind.IdentityCoreDestroyed;
                tracked.OutcomeReason = reason;
                tracked.IsAwaitingHubRetry = false;
                tracked.IsAwaitingPolicyRetry = false;
                tracked.NextPolicyRetryTick = 0;
                tracked.PendingRetryReason = string.Empty;
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Warning,
                $"Identity-bearing Core failure retained without retry | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"FailureKind={CARAUFailureKind.IdentityCoreDestroyed}"
            );
            return true;
        }

        private bool TryFinalizeCapturedCoreExtractionFailure(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked?.IsPending != true ||
                !tracked.RuntimeCoreTransitionAwaitingValidation ||
                Time.realtimeSinceStartup <
                    tracked.RuntimeCoreTransitionValidateAfterRealtime ||
                !tracked.RuntimeNativeSurgicalFailureWasExtraction ||
                (tracked.RuntimeNativeSurgicalFailureKind !=
                     CARAUFailureKind.SlotDestroyed &&
                 tracked.RuntimeNativeSurgicalFailureKind !=
                     CARAUFailureKind.InstalledPartWelded))
            {
                return false;
            }

            HandleOperationFailure(
                tracked,
                CARAUOperationOutcome.Failed,
                string.IsNullOrWhiteSpace(
                    tracked.RuntimeNativeSurgicalFailureReason
                )
                    ? "Stardeus reported a terminal Core extraction failure."
                    : tracked.RuntimeNativeSurgicalFailureReason,
                tracked.RuntimeNativeSurgicalFailureKind
            );
            return true;
        }

        private static string BuildIdentityCoreDestroyedReason(
          CARAUTrackedOperation tracked,
          string preferredReason = null
        )
        {
            var nativeReason = preferredReason;
            if (string.IsNullOrWhiteSpace(nativeReason))
            {
                nativeReason = tracked?.RuntimeNativeSurgicalFailureReason;
            }
            if (string.IsNullOrWhiteSpace(nativeReason))
            {
                nativeReason = tracked?.PendingRetryReason;
            }
            if (string.IsNullOrWhiteSpace(nativeReason))
            {
                nativeReason = tracked?.OutcomeReason;
            }

            if (!string.IsNullOrWhiteSpace(nativeReason))
            {
                var automaticSuffix = nativeReason.IndexOf(
                    "\n\nAutomatic retry stopped:",
                    StringComparison.Ordinal
                );
                if (automaticSuffix >= 0)
                {
                    nativeReason = nativeReason.Substring(0, automaticSuffix);
                }
                nativeReason = nativeReason.Trim();
            }

            if (string.IsNullOrWhiteSpace(nativeReason))
            {
                nativeReason =
                    "Stardeus destroyed the Core during surgery.";
            }

            return nativeReason +
                "\n\nCARAU will not automatically select another Core or " +
                "attempt post-death recovery. This retained failure also " +
                "has no CARAU manual retry.";
        }

        /// <summary>
        /// Cancels the exact live surgery represented by a CARAU tracking
        /// record. The operation is removed through Stardeus's normal
        /// registration query so vanilla reservation and goal cleanup still
        /// runs. Manually-created operations are never added to this tracking
        /// list and therefore cannot be cancelled from CARAU's tracking UI.
        /// </summary>
        public bool CancelTrackedOperation(CARAUTrackedOperation tracked)
        {
            if (tracked == null ||
                !tracked.IsPending ||
                tracked.IsAwaitingAutomaticRetry ||
                !operations.Contains(tracked))
            {
                return false;
            }

            if (tracked.IsRemoveAllParts)
            {
                var cancelled = CancelTrackedRemoveAllParts(
                    tracked,
                    "tracked operations window"
                );
                RefreshWidget();
                ColonistAndBotsAutoUpgradeSys.Instance?
                    .RefreshTrackedOperationState();
                return cancelled;
            }

            var slot = tracked.RuntimeSlot;
            if (slot == null)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    $"Tracked operation cancellation failed | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    "Reason=runtime slot unavailable"
                );
                return false;
            }

            var operation = S.Query.FindSurgicalOperation.Ask(slot, true);
            if (operation == null)
            {
                if (HasSuccessfulSlotResult(tracked, slot))
                {
                    CompleteSuccessfully(tracked);
                }
                else
                {
                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.CancelledExternally,
                        "The operation was no longer registered when CARAU " +
                        "received the cancellation request.",
                        CARAUFailureKind.ExternalCancellation
                    );
                }

                RefreshWidget();
                ColonistAndBotsAutoUpgradeSys.Instance?
                    .RefreshTrackedOperationState();
                return false;
            }

            if (IsTrackedOperationMatch(tracked, operation) &&
                ClassifyTerminalOperation(tracked, operation))
            {
                RefreshWidget();
                ColonistAndBotsAutoUpgradeSys.Instance?
                    .RefreshTrackedOperationState();
                return false;
            }

            if (!IsTrackedOperationMatch(tracked, operation))
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Stale,
                    "Cancellation was refused because the registered " +
                    "operation no longer matches CARAU's record.",
                    CARAUFailureKind.StaleOwnership
                );
                RefreshWidget();
                ColonistAndBotsAutoUpgradeSys.Instance?
                    .RefreshTrackedOperationState();
                return false;
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Tracked operation cancellation requested | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"Target={tracked.TargetPartName}"
            );

            operation.State.Set(SurgicalOpState.Canceled);
            S.Query.RegisterSurgicalOperation.Ask(operation, false);

            operations.Remove(tracked);
            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                $"Operation tracking ended | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                "Reason=cancelled from tracked operations window"
            );

            RefreshWidget();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();
            return true;
        }

        public void DismissFailedOperation(CARAUTrackedOperation tracked)
        {
            if (tracked == null ||
                !tracked.IsFailure ||
                !operations.Remove(tracked))
            {
                return;
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                $"Failed operation dismissed | Being={tracked.BeingName} | " +
                $"Slot={tracked.SlotName} | Outcome={tracked.Outcome}"
            );

            RefreshWidget();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();
        }

        public void DismissFailedOperationsForBeing(uint beingId)
        {
            DismissFailedOperations(
                operation => operation.BeingId == beingId,
                $"BeingId={beingId}"
            );
        }

        public void DismissAllFailedOperations()
        {
            DismissFailedOperations(
                operation => true,
                "All tracked beings"
            );
        }

        public int GetRetryableFailureCount(uint? beingId = null)
        {
            var count = 0;

            using (InventoryService.BeginBatchLookup(S))
            {
                foreach (var operation in operations)
                {
                    if (operation == null ||
                        !operation.IsFailure ||
                        (beingId.HasValue &&
                         operation.BeingId != beingId.Value))
                    {
                        continue;
                    }

                    if (TryBuildRetryDecision(operation, out _, out _))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        public bool IsRetryableFailure(CARAUTrackedOperation tracked)
        {
            return TryBuildRetryDecision(tracked, out _, out _);
        }

        public string GetRetryExplanation(
          CARAUTrackedOperation tracked
        )
        {
            if (TryBuildRetryDecision(
                    tracked,
                    out var decision,
                    out var unavailableReason
                ))
            {
                return UpgradeExplanationFormatter.BuildTooltip(
                    decision?.Evaluation
                );
            }

            return string.IsNullOrWhiteSpace(unavailableReason)
                ? "Retry unavailable: The target is no longer eligible."
                : unavailableReason;
        }

        public string GetAutomaticRetryExplanation(
          CARAUTrackedOperation tracked
        )
        {
            if (TryBuildAutomaticRetryPreview(
                    tracked,
                    out var decision,
                    out var unavailableReason,
                    logPlan: false
                ))
            {
                return UpgradeExplanationFormatter.BuildTooltip(
                    decision?.Evaluation
                );
            }

            return unavailableReason;
        }

        public void RetryFailedOperation(CARAUTrackedOperation tracked)
        {
            if (tracked == null)
            {
                return;
            }

            RetryFailedOperations(
                new List<CARAUTrackedOperation> { tracked },
                $"Being={tracked.BeingName} | Slot={tracked.SlotName}"
            );
        }

        public void RetryFailedOperationsForBeing(uint beingId)
        {
            RetryFailedOperations(
                operations
                    .Where(operation =>
                        operation != null &&
                        operation.IsFailure &&
                        operation.BeingId == beingId
                    )
                    .ToList(),
                $"BeingId={beingId}"
            );
        }

        public void RetryAllFailedOperations()
        {
            var retryTargets = operations
                .Where(operation =>
                    operation != null && operation.IsFailure
                )
                .OrderBy(operation =>
                    GetDisplayedBeingOrder(operation.BeingId)
                )
                .ThenBy(operation => operations.IndexOf(operation))
                .ToList();

            RetryFailedOperations(retryTargets, "All tracked beings");
        }

        private void RetryFailedOperations(
          List<CARAUTrackedOperation> targets,
          string scope
        )
        {
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            var queued = 0;
            var hardBlocked = 0;
            var temporarilyBlocked = 0;
            var rejected = 0;
            string firstBlockedReason = null;

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Manual retry requested | Scope={scope} | " +
                $"Failures={targets.Count}"
            );

            using (InventoryService.BeginBatchLookup(S))
            {
              foreach (var tracked in targets)
              {
                if (!operations.Contains(tracked) || !tracked.IsFailure)
                {
                    continue;
                }

                if (!TryBuildRetryDecision(
                        tracked,
                        out var decision,
                        out var unavailableReason
                    ))
                {
                    if (!string.IsNullOrWhiteSpace(unavailableReason))
                    {
                        hardBlocked++;
                        firstBlockedReason ??= unavailableReason;
                    }
                    else
                    {
                        temporarilyBlocked++;
                    }

                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Warning,
                        $"Manual retry currently blocked | " +
                        $"Being={tracked.BeingName} | " +
                        $"Slot={tracked.SlotName} | " +
                        $"Reason=" +
                        $"{(string.IsNullOrWhiteSpace(unavailableReason) ? "target temporarily unavailable" : unavailableReason)}"
                    );

                    continue;
                }

                var queueResult = SurgeryService.TryQueueUpgradeResult(
                    S,
                    decision
                );
                if (queueResult.Success)
                {
                    queued++;
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Queue,
                        $"Failed operation retry queued | " +
                        $"Being={tracked.BeingName} | " +
                        $"Slot={tracked.SlotName} | " +
                        $"Replacement={decision.TargetPart?.NameT ?? "unknown"} | " +
                        $"ObjId={decision.InventoryPart?.Id ?? 0}"
                    );
                    continue;
                }

                rejected++;
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    $"Failed operation retry rejected | " +
                    $"Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | " +
                    $"Reason={queueResult.Message ?? "unknown"}"
                );
              }
            }

            RefreshWidget();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Manual retry complete | Scope={scope} | " +
                $"Queued={queued} | HardBlocked={hardBlocked} | " +
                $"TemporarilyBlocked={temporarilyBlocked} | " +
                $"Rejected={rejected}"
            );

            if (hardBlocked > 0 || temporarilyBlocked > 0 || rejected > 0)
            {
                var message = targets.Count == 1 && queued == 0 &&
                              !string.IsNullOrWhiteSpace(firstBlockedReason)
                    ? "CARAU retry remains blocked:\n" +
                      firstBlockedReason +
                      "\n\nThe original failure remains available for " +
                      "another manual retry. CARAU did not alter any " +
                      "existing reservation."
                    : $"CARAU retry completed: {queued} queued, " +
                      $"{hardBlocked} currently blocked, " +
                      $"{temporarilyBlocked} temporarily blocked, " +
                      $"{rejected} rejected. Retained failures were not " +
                      "cleared.";
                S.Sig.ShowErrorMessage.Send(message);
            }
        }

        private static UpgradeActionKind ResolveRetryActionKind(
          CARAUTrackedOperation tracked,
          BodyPartSlot slot
        )
        {
            if (tracked == null ||
                tracked.ActionKind == UpgradeActionKind.Upgrade)
            {
                return UpgradeActionKind.Upgrade;
            }

            // A failed replacement can legitimately change the slot from an
            // irreparable installed part to a missing part, or vice versa.
            // Preserve the restoration intent while matching the current
            // state instead of forcing the stale original sub-action.
            if (slot?.IsPartRemoved == true)
            {
                return UpgradeActionKind.InstallMissing;
            }

            if (slot?.State?.IsDestroyed == true)
            {
                return UpgradeActionKind.ReplaceBadParts;
            }

            return tracked.ActionKind;
        }

        private bool TryBuildRetryDecision(
          CARAUTrackedOperation tracked,
          out UpgradeDecision decision,
          out string unavailableReason
        )
        {
            decision = null;
            unavailableReason = null;

            if (tracked == null || !tracked.IsFailure)
            {
                return false;
            }

            if (tracked.IsRemoveAllParts)
            {
                unavailableReason =
                    "Retry unavailable: Remove All Parts is a native mass " +
                    "operation. Re-evaluate and queue it again from CARAU's " +
                    "Main tab if the terminal chassis still qualifies.";
                return false;
            }

            if (tracked.FailureKind ==
                CARAUFailureKind.IdentityCoreDestroyed)
            {
                unavailableReason =
                    "Retry unavailable: A destructive Core surgery left " +
                    "the Being dead. CARAU will not select another Core or " +
                    "attempt post-death recovery.";
                return false;
            }

            return TryBuildFreshRetryDecision(
                tracked,
                out decision,
                out unavailableReason
            );
        }

        private bool TryBuildFreshRetryDecision(
          CARAUTrackedOperation tracked,
          out UpgradeDecision decision,
          out string unavailableReason
        )
        {
            decision = null;
            unavailableReason = null;

            if (tracked == null)
            {
                return false;
            }

            RefreshRuntimeTarget(tracked);
            var slot = tracked.RuntimeSlot;
            decision = UpgradeAnalyzer.AnalyzeDecision(
                S,
                UpgradeAnalyzer.AnalyzeSlot(slot),
                logHostileMindExclusions: true,
                purpose: UpgradeEvaluationPurpose.ManualRetry,
                actionKind: ResolveRetryActionKind(tracked, slot)
            );
            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (beingData?.Body == null || !beingData.Body.IsAlive)
            {
                UpgradeEvaluator.WithPolicyBlock(
                    decision?.Evaluation,
                    UpgradeReasonCode.DeadBeingRetryUnsupported,
                    "CARAU does not manually requeue a failed operation " +
                    "after the tracked Being has died."
                );
            }

            if (decision?.Evaluation?.IsQueueable != true)
            {
                unavailableReason =
                    "Retry unavailable:\n" +
                    UpgradeExplanationFormatter.BuildTooltip(
                        decision?.Evaluation
                    );
                return false;
            }

            return true;
        }

        private bool CancelTrackedRemoveAllParts(
          CARAUTrackedOperation tracked,
          string scope
        )
        {
            var operation = ResolveRemoveAllPartsOperation(tracked);
            if (operation == null)
            {
                RefreshRemoveAllPartsOperation(tracked);
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    $"Remove All Parts cancellation skipped | " +
                    $"Being={tracked?.BeingName ?? "unknown"} | " +
                    "Reason=native operation unavailable"
                );
                return false;
            }

            if (ClassifyRemoveAllPartsTerminal(tracked, operation))
            {
                return false;
            }

            try
            {
                tracked.RuntimeCancellationRequestedByCARAU = true;
                operation.State.Set(SurgicalOpState.Canceled);
                var unregistered =
                    S.Query.RegisterSurgicalOperation.Ask(operation, false);
                operations.Remove(tracked);

                CARAUDebugLog.Add(
                    CARAUDebugCategory.Cleanup,
                    $"Remove All Parts tracking ended | " +
                    $"Being={tracked.BeingName} | " +
                    $"NativeOperationId={tracked.NativeOperationId} | " +
                    $"NativeUnregister={unregistered} | " +
                    $"Reason=cancelled from {scope}"
                );
                return true;
            }
            catch (Exception ex)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    $"Remove All Parts cancellation failed | " +
                    $"Being={tracked.BeingName} | Error={ex.Message}"
                );
                return false;
            }
            finally
            {
                tracked.RuntimeCancellationRequestedByCARAU = false;
            }
        }

        public void StopAutomaticRetry(CARAUTrackedOperation tracked)
        {
            if (tracked?.IsAwaitingAutomaticRetry != true ||
                !operations.Contains(tracked))
            {
                return;
            }

            var reason = string.IsNullOrWhiteSpace(tracked.PendingRetryReason)
                ? "The previous CARAU operation failed."
                : tracked.PendingRetryReason;
            var kind = tracked.FailureKind == CARAUFailureKind.None
                ? CARAUFailureKind.HubUnavailable
                : tracked.FailureKind;
            RetainFailure(
                tracked,
                CARAUOperationOutcome.Failed,
                reason + "\n\nAutomatic retry was stopped by the player. " +
                BuildManualRetryAvailabilityNote(tracked),
                kind
            );

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Automatic retry stopped by player | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"FailureKind={kind}"
            );
            RefreshWidget();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();
        }

        private bool RefreshRemoveAllPartsOperation(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked == null || !tracked.IsPending)
            {
                return false;
            }

            var operation = ResolveRemoveAllPartsOperation(tracked);
            if (operation != null)
            {
                return ClassifyRemoveAllPartsTerminal(tracked, operation);
            }

            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (beingData?.Being == null || beingData.Body?.Slots == null)
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Invalid,
                    "The Being for this Remove All Parts operation no " +
                    "longer exists.",
                    CARAUFailureKind.InvalidTarget
                );
                return true;
            }

            // If no removable part remains, the native mass operation did
            // produce its intended final body state even if CARAU missed the
            // terminal unregister signal during save/load.
            if (!beingData.Body.Slots.Any(slot =>
                    slot != null &&
                    !slot.IsPartRemoved &&
                    slot.CanRemovePart(warn: false)))
            {
                CompleteSuccessfully(tracked);
                return true;
            }

            RetainFailure(
                tracked,
                CARAUOperationOutcome.CancelledExternally,
                "The native Remove All Parts operation is no longer " +
                "registered, but removable parts still remain. Stardeus " +
                "did not expose a terminal result that CARAU can verify.",
                CARAUFailureKind.ExternalCancellation
            );
            return true;
        }

        private SurgicalOperation ResolveRemoveAllPartsOperation(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked?.IsRemoveAllParts != true)
            {
                return null;
            }

            if (IsTrackedOperationMatch(
                    tracked,
                    tracked.RuntimeOperation
                ))
            {
                return tracked.RuntimeOperation;
            }

            var operation = S.Query.GetAllSurgeries.Ask()?
                .FirstOrDefault(candidate =>
                    IsTrackedOperationMatch(tracked, candidate)
                );
            tracked.RuntimeOperation = operation;
            return operation;
        }

        private bool ClassifyRemoveAllPartsTerminal(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            if (tracked == null ||
                operation == null ||
                !tracked.IsPending)
            {
                return false;
            }

            tracked.RuntimeOperation = operation;
            switch (operation.State.Value)
            {
                case SurgicalOpState.Completed:
                    CompleteSuccessfully(tracked);
                    return true;

                case SurgicalOpState.Failed:
                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.Failed,
                        BuildRemoveAllPartsFailureReason(
                            operation,
                            "Stardeus marked the Remove All Parts operation " +
                            "Failed."
                        ),
                        CARAUFailureKind.RemoveAllPartsFailed
                    );
                    return true;

                case SurgicalOpState.Canceled:
                    if (tracked.RuntimeCancellationRequestedByCARAU)
                    {
                        return false;
                    }

                    RetainFailure(
                        tracked,
                        CARAUOperationOutcome.CancelledExternally,
                        BuildRemoveAllPartsFailureReason(
                            operation,
                            "The Remove All Parts operation was cancelled " +
                            "outside CARAU."
                        ),
                        CARAUFailureKind.ExternalCancellation
                    );
                    return true;

                default:
                    return false;
            }
        }

        private static string BuildRemoveAllPartsFailureReason(
          SurgicalOperation operation,
          string fallback
        )
        {
            if (!string.IsNullOrWhiteSpace(operation?.FailReason))
            {
                return operation.FailReason;
            }

            var goalReason = operation?.AssociatedGoal?.FailReason;
            if (string.IsNullOrWhiteSpace(goalReason))
            {
                goalReason = operation?.AssociatedGoal?.TaskLineFail;
            }

            return string.IsNullOrWhiteSpace(goalReason)
                ? fallback
                : goalReason;
        }

        private static int GetAutomaticRetryCount(
          CARAUTrackedOperation tracked
        )
        {
            return Math.Max(0, tracked?.SilentHubRetryCount ?? 0) +
                   Math.Max(0, tracked?.PolicyRetryCount ?? 0) +
                   Math.Max(
                       0,
                       tracked?.PreSurgeryAutoDocRetryCount ?? 0
                   );
        }

        private static bool HasAutomaticRetryBudget(
          CARAUTrackedOperation tracked
        )
        {
            return CARAUSettings.AutomaticRetryLimit > 0 &&
                   GetAutomaticRetryCount(tracked) <
                   CARAUSettings.AutomaticRetryLimit;
        }

        private bool RetainIfAutomaticRetryBudgetExhausted(
          CARAUTrackedOperation tracked,
          string originalReason,
          CARAUOperationOutcome outcome,
          CARAUFailureKind failureKind
        )
        {
            if (HasAutomaticRetryBudget(tracked))
            {
                return false;
            }

            RetainFailure(
                tracked,
                outcome,
                originalReason + "\n\n" +
                BuildAutomaticRetryPolicyExplanation(
                    tracked,
                    UpgradeReasonCode.RetryLimitReached,
                    $"The configured shared limit of " +
                    $"{CARAUSettings.AutomaticRetryLimit} was reached."
                ),
                failureKind
            );
            return true;
        }

        private bool TryPassAutomaticChanceGate(
          CARAUTrackedOperation tracked,
          UpgradeDecision decision,
          out string reason
        )
        {
            reason = null;
            if (!AutomaticRetryChanceService.TryEvaluate(
                    decision,
                    out var chance,
                    out reason
                ))
            {
                ClearRuntimeRetryChance(tracked);
                return false;
            }

            tracked.RuntimeRetryExtractChance = chance.ExtractChance;
            tracked.RuntimeRetryInstallChance = chance.InstallChance;
            tracked.RuntimeRetryEffectiveChance = chance.EffectiveChance;
            var passed =
                AutomaticRetryChanceService.IsAboveConfiguredThreshold(chance);
            var detail = chance.Describe(
                CARAUSettings.AutomaticRetryChanceThresholdPercent
            );

            CARAUDebugLog.Add(
                passed
                    ? CARAUDebugCategory.Decision
                    : CARAUDebugCategory.Warning,
                $"Automatic retry chance evaluated | " +
                $"Being={tracked?.BeingName ?? "unknown"} | " +
                $"Slot={tracked?.SlotName ?? "unknown"} | " +
                $"{detail} | Passed={passed}"
            );

            if (!passed)
            {
                UpgradeEvaluator.WithPolicyBlock(
                    decision?.Evaluation,
                    UpgradeReasonCode.RetryChanceBelowThreshold,
                    "The current Stardeus operator-neutral surgery " +
                    "estimate does not exceed the configured threshold. " +
                    detail + "."
                );
                reason = "Automatic retry stopped:\n" +
                    UpgradeExplanationFormatter.BuildTooltip(
                        decision?.Evaluation
                    ) +
                    "\n\n" + BuildManualRetryAvailabilityNote(tracked);
            }

            return passed;
        }

        private static void ClearRuntimeRetryChance(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked == null)
            {
                return;
            }

            tracked.RuntimeRetryExtractChance = null;
            tracked.RuntimeRetryInstallChance = null;
            tracked.RuntimeRetryEffectiveChance = null;
        }

        private void DismissFailedOperations(
          Func<CARAUTrackedOperation, bool> predicate,
          string scope
        )
        {
            var removed = operations.RemoveAll(operation =>
                operation != null &&
                operation.IsFailure &&
                predicate(operation)
            );

            if (removed == 0)
            {
                return;
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                $"Failed operations cleared | Scope={scope} | " +
                $"Removed={removed} | Remaining={operations.Count}"
            );

            RefreshWidget();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();
        }

        public void CancelTrackedOperationsForBeing(uint beingId)
        {
            CancelTrackedOperations(
                operations
                    .Where(operation =>
                        operation.BeingId == beingId &&
                        operation.IsPending &&
                        !operation.IsAwaitingAutomaticRetry
                    )
                    .ToList(),
                $"BeingId={beingId}"
            );
        }

        public void CancelAllTrackedOperations()
        {
            CancelTrackedOperations(
                operations
                    .Where(operation =>
                        operation.IsPending &&
                        !operation.IsAwaitingAutomaticRetry
                    )
                    .ToList(),
                "All tracked operations"
            );
        }

        private void CancelTrackedOperations(
          List<CARAUTrackedOperation> targets,
          string scope
        )
        {
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            var cancelled = 0;
            var skipped = 0;
            var failed = 0;

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Bulk cancellation requested | Scope={scope} | " +
                $"Requested={targets.Count}"
            );

            for (var i = 0; i < targets.Count; i++)
            {
                var tracked = targets[i];

                if (tracked == null ||
                    !tracked.IsPending ||
                    !operations.Contains(tracked))
                {
                    skipped++;
                    continue;
                }

                if (tracked.IsRemoveAllParts)
                {
                    if (CancelTrackedRemoveAllParts(tracked, scope))
                    {
                        cancelled++;
                    }
                    else
                    {
                        skipped++;
                    }
                    continue;
                }

                var slot = tracked.RuntimeSlot;
                var operation = slot == null
                    ? null
                    : S.Query.FindSurgicalOperation.Ask(slot, true);

                if (IsTrackedOperationMatch(tracked, operation) &&
                    ClassifyTerminalOperation(tracked, operation))
                {
                    skipped++;
                    continue;
                }

                if (!IsTrackedOperationMatch(tracked, operation))
                {
                    if (operation == null &&
                        HasSuccessfulSlotResult(tracked, slot))
                    {
                        CompleteSuccessfully(tracked);
                    }
                    else
                    {
                        RetainFailure(
                            tracked,
                            operation == null
                                ? CARAUOperationOutcome.CancelledExternally
                                : CARAUOperationOutcome.Stale,
                            operation == null
                                ? "The operation was no longer registered " +
                                  "when bulk cancellation began."
                                : "Bulk cancellation found a different " +
                                  "operation in the tracked slot.",
                            operation == null
                                ? CARAUFailureKind.ExternalCancellation
                                : CARAUFailureKind.StaleOwnership
                        );
                    }

                    skipped++;
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Warning,
                        $"Bulk cancellation skipped stale record | " +
                        $"Being={tracked.BeingName} | " +
                        $"Slot={tracked.SlotName} | " +
                        "Reason=operation missing or ownership mismatch"
                    );
                    continue;
                }

                try
                {
                    operation.State.Set(SurgicalOpState.Canceled);
                    S.Query.RegisterSurgicalOperation.Ask(operation, false);
                    operations.Remove(tracked);
                    cancelled++;

                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Cleanup,
                        $"Operation tracking ended | " +
                        $"Being={tracked.BeingName} | " +
                        $"Slot={tracked.SlotName} | " +
                        $"Reason=bulk cancellation ({scope})"
                    );
                }
                catch (Exception ex)
                {
                    failed++;
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Error,
                        $"Bulk cancellation failed | " +
                        $"Being={tracked.BeingName} | " +
                        $"Slot={tracked.SlotName} | Error={ex.Message}"
                    );
                }
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                $"Bulk cancellation complete | Scope={scope} | " +
                $"Requested={targets.Count} | Cancelled={cancelled} | " +
                $"Skipped={skipped} | Failed={failed} | " +
                $"Remaining={operations.Count}"
            );

            RefreshWidget();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();

            if (skipped > 0 || failed > 0)
            {
                S.Sig.ShowErrorMessage.Send(
                    $"CARAU cancellation complete. Cancelled: {cancelled}; " +
                    $"Skipped: {skipped}; Failed: {failed}."
                );
            }
        }

        private bool ClassifyTerminalOperation(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            if (tracked == null ||
                operation == null ||
                !tracked.IsPending)
            {
                return false;
            }

            switch (operation.State.Value)
            {
                case SurgicalOpState.Completed:
                    CompleteValidatedOperation(tracked, operation);
                    return true;

                case SurgicalOpState.Failed:
                    ClassifyFailedOperation(tracked, operation);
                    return true;

                case SurgicalOpState.Canceled:
                    if (tracked.RuntimeInterruptedByCoreIdentityTransition)
                    {
                        HandleOperationFailure(
                            tracked,
                            CARAUOperationOutcome.Failed,
                            "The operation was interrupted while Stardeus " +
                            "transferred a replacement Core identity into " +
                            "this chassis.",
                            CARAUFailureKind.PreSurgeryTaskFailure
                        );
                    }
                    else
                    {
                        RetainFailure(
                            tracked,
                            CARAUOperationOutcome.CancelledExternally,
                            "The operation was cancelled outside CARAU.",
                            CARAUFailureKind.ExternalCancellation
                        );
                    }
                    return true;

                default:
                    return false;
            }
        }

        private void CompleteValidatedOperation(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            RefreshRuntimeTarget(tracked);

            if (HasSuccessfulSlotResult(tracked, tracked?.RuntimeSlot))
            {
                CompleteSuccessfully(tracked);
                return;
            }

            var nativeReason = BuildNativeOperationFailureReason(
                tracked,
                operation,
                "Stardeus marked the operation complete, but the " +
                "queued replacement was not installed in the target " +
                "slot."
            );

            if (TryDeferHubSaturationRetry(
                    tracked,
                    nativeReason
                ))
            {
                return;
            }

            if (TryGetRelevantBeingFailureLog(
                    tracked,
                    out var beingFailureReason
                ))
            {
                HandleOperationFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    beingFailureReason,
                    GetCapturedSurgicalFailureKind(tracked)
                );
                return;
            }

            var capturedKind = GetCapturedSurgicalFailureKind(tracked);
            var goal = operation?.AssociatedGoal;
            var hasNativeFailureDetails =
                !string.IsNullOrWhiteSpace(
                    tracked?.RuntimeNativeSurgicalFailureReason
                ) ||
                !string.IsNullOrWhiteSpace(goal?.FailReason) ||
                !string.IsNullOrWhiteSpace(goal?.TaskLineFail) ||
                !string.IsNullOrWhiteSpace(operation?.FailReason);
            if (hasNativeFailureDetails)
            {
                HandleOperationFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    nativeReason,
                    capturedKind == CARAUFailureKind.Unknown
                        ? CARAUFailureKind.PreSurgeryTaskFailure
                        : capturedKind
                );
                return;
            }

            HandleOperationFailure(
                tracked,
                CARAUOperationOutcome.Failed,
                BuildCompletedWithoutPlannedReplacementReason(tracked),
                CARAUFailureKind.CompletedWithoutPlannedReplacement
            );
        }

        private void ClassifyFailedOperation(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            RefreshRuntimeTarget(tracked);

            if (HasSuccessfulSlotResult(tracked, tracked?.RuntimeSlot))
            {
                CompleteSuccessfully(tracked);
                return;
            }

            var nativeReason =
                BuildNativeOperationFailureReason(tracked, operation);
            if (TryDeferHubSaturationRetry(tracked, nativeReason))
            {
                return;
            }

            HandleOperationFailure(
                tracked,
                CARAUOperationOutcome.Failed,
                nativeReason,
                GetCapturedSurgicalFailureKind(tracked)
            );
        }

        private string BuildNativeOperationFailureReason(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation,
          string fallback = null
        )
        {
            if (TryGetRelevantBeingFailureLog(
                    tracked,
                    out var beingFailureReason
                ))
            {
                return beingFailureReason;
            }

            if (!string.IsNullOrWhiteSpace(
                    tracked?.RuntimeNativeSurgicalFailureReason
                ))
            {
                return tracked.RuntimeNativeSurgicalFailureReason;
            }

            var goal = operation?.AssociatedGoal;
            if (!string.IsNullOrWhiteSpace(goal?.FailReason))
            {
                return BuildPreSurgeryTaskFailureReason(
                    tracked,
                    goal.FailReason
                );
            }

            if (!string.IsNullOrWhiteSpace(goal?.TaskLineFail))
            {
                return BuildPreSurgeryTaskFailureReason(
                    tracked,
                    goal.TaskLineFail
                );
            }

            // SurgicalOperation.FailReason is frequently only a generic
            // "task rejected" summary. Preserve it as a fallback after the
            // Being log and associated task have had an opportunity to
            // provide the actionable reason.
            if (!string.IsNullOrWhiteSpace(operation?.FailReason))
            {
                return operation.FailReason;
            }

            return string.IsNullOrWhiteSpace(fallback)
                ? "Stardeus reported that the operation failed, but did " +
                  "not provide a failure reason."
                : fallback;
        }

        private static string BuildPreSurgeryTaskFailureReason(
          CARAUTrackedOperation tracked,
          string nativeReason
        )
        {
            if (TryGetInterruptingAiGoal(nativeReason, out var goalName))
            {
                return
                    $"Pre-surgery interruption: Stardeus interrupted " +
                    $"{tracked?.BeingName ?? "the target Being"}'s own " +
                    $"body-part operation goal before surgery began because " +
                    $"the AI goal {goalName} took priority. This did not " +
                    "refer to a worker hauling the replacement part. No " +
                    "body-part action was attempted.";
            }

            return $"Stardeus surgery task failed: {nativeReason}";
        }

        private static bool TryGetInterruptingAiGoal(
          string nativeReason,
          out string goalName
        )
        {
            goalName = null;
            if (string.IsNullOrWhiteSpace(nativeReason))
            {
                return false;
            }

            const string prefix = "Interrupted:";
            var trimmed = nativeReason.Trim();
            if (!trimmed.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase
                ))
            {
                return false;
            }

            goalName = trimmed.Substring(prefix.Length).Trim();
            return !string.IsNullOrWhiteSpace(goalName);
        }

        private static bool IsExternalAiPreSurgeryInterruption(
          string reason
        )
        {
            return !string.IsNullOrWhiteSpace(reason) &&
                reason.StartsWith(
                    "Pre-surgery interruption:",
                    StringComparison.Ordinal
                );
        }

        private bool TryDeferHubSaturationRetry(
          CARAUTrackedOperation tracked,
          string reason
        )
        {
            if (tracked == null ||
                !tracked.IsPending ||
                !IsHubSaturationReason(reason))
            {
                return false;
            }

            tracked.FailureKind = CARAUFailureKind.HubUnavailable;
            tracked.PendingRetryReason = reason;

            if (!HasAutomaticRetryBudget(tracked))
            {
                var hubName = GetRequiredHubName(tracked.BeingId);
                var configuredLimit = CARAUSettings.AutomaticRetryLimit;
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    $"{reason}\n\n" +
                    BuildAutomaticRetryPolicyExplanation(
                        tracked,
                        UpgradeReasonCode.RetryLimitReached,
                        $"This operation used " +
                        $"{GetAutomaticRetryCount(tracked)} of " +
                        $"{configuredLimit} configured automatic attempts " +
                        $"while the required {hubName} was intermittently " +
                        "saturated or unavailable."
                    ),
                    CARAUFailureKind.HubUnavailable
                );
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    $"Silent Hub retries exhausted | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"Attempts={GetAutomaticRetryCount(tracked)}/" +
                    $"{configuredLimit}"
                );
                return true;
            }

            // Authorization happens while the failed native operation may
            // still be registered. Recheck every automatic eligibility rule
            // here, but defer the "no operation owns this slot" queueability
            // check until the actual Hub requeue below.
            if (!TryBuildAutomaticRetryPreview(
                    tracked,
                    out var decision,
                    out var unavailableReason
                ))
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    reason + "\n\n" + unavailableReason,
                    ClassifyCurrentRetryBlock(tracked, unavailableReason)
                );
                return true;
            }

            if (!TryPassAutomaticChanceGate(
                    tracked,
                    decision,
                    out var chanceReason
                ))
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    reason + "\n\n" + chanceReason,
                    CARAUFailureKind.HubUnavailable
                );
                return true;
            }

            var transferredFromPolicy = tracked.IsAwaitingPolicyRetry;
            tracked.IsAwaitingPolicyRetry = false;
            tracked.NextPolicyRetryTick = 0;
            tracked.IsAwaitingHubRetry = true;
            CARAUDebugLog.Add(
                CARAUDebugCategory.Warning,
                $"Silent Hub retry deferred | Being={tracked.BeingName} | " +
                $"Slot={tracked.SlotName} | " +
                $"TransferredFromPolicy={transferredFromPolicy} | " +
                $"CompletedAttempts={GetAutomaticRetryCount(tracked)}/" +
                $"{CARAUSettings.AutomaticRetryLimit} | " +
                $"Reason={reason}"
            );
            return true;
        }

        /// <summary>
        /// Records Stardeus task-level Hub failures that do not unregister the
        /// surgical operation. Stardeus can recover these internally, so they
        /// are diagnostics rather than CARAU-managed retry attempts.
        /// </summary>
        private void ObserveNativeHubTaskLifecycle(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            if (tracked == null || operation == null)
            {
                return;
            }

            var goal = operation.AssociatedGoal;
            var reason = goal?.FailReason;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = goal?.TaskLineFail;
            }

            if (IsHubSaturationReason(reason))
            {
                if (ReferenceEquals(
                        tracked.RuntimeObservedHubFailureGoal,
                        goal
                    ))
                {
                    return;
                }

                tracked.RuntimeObservedHubFailureGoal = goal;
                tracked.RuntimeNativeHubRecoveryPending = true;
                tracked.ObservedNativeHubFailureCount++;

                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    $"Native Hub task failure observed | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"ObservedFailures=" +
                    $"{tracked.ObservedNativeHubFailureCount} | " +
                    $"CARAURequeues={tracked.SilentHubRetryCount} | " +
                    $"SurgicalState={operation.State.Value} | Reason={reason}"
                );
                return;
            }

            if (!tracked.RuntimeNativeHubRecoveryPending)
            {
                return;
            }

            tracked.RuntimeNativeHubRecoveryPending = false;
            tracked.RuntimeObservedHubFailureGoal = null;
            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Native Hub task recovered internally | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"ObservedFailures={tracked.ObservedNativeHubFailureCount} | " +
                $"CARAURequeues={tracked.SilentHubRetryCount} | " +
                $"SurgicalState={operation.State.Value}"
            );
        }

        private bool TryHandleOccupiedRetrySlot(
          CARAUTrackedOperation tracked,
          string originalReason,
          BodyPartSlot slot,
          out bool processResult
        )
        {
            processResult = false;
            var occupyingOperation = slot == null
                ? null
                : S.Query.FindSurgicalOperation.Ask(slot, true);
            if (occupyingOperation == null)
            {
                return false;
            }

            // A terminal CARAU operation can remain registered for one
            // tracker refresh while Core emits its unregister notification.
            // Wait for that exact operation; only foreign work is stale.
            if (IsTrackedOperationMatch(tracked, occupyingOperation))
            {
                return true;
            }

            RetainFailure(
                tracked,
                CARAUOperationOutcome.Stale,
                originalReason + "\n\nAutomatic retry stopped: A different " +
                "operation now occupies the tracked slot. CARAU did not " +
                "alter that operation.",
                CARAUFailureKind.StaleOwnership
            );
            processResult = true;
            return true;
        }

        private bool TryValidateAutomaticRetryDecision(
          CARAUTrackedOperation tracked,
          UpgradeDecision decision,
          string originalReason,
          CARAUFailureKind failureKind
        )
        {
            if (!SurgeryQueue.IsQueueable(S, decision))
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    originalReason + "\n\nAutomatic retry stopped: Fresh " +
                    "planning is no longer queueable under CARAU's current " +
                    "eligibility policies.\n\n" +
                    BuildManualRetryAvailabilityNote(tracked),
                    failureKind
                );
                return false;
            }

            if (!TryPassAutomaticChanceGate(
                    tracked,
                    decision,
                    out var chanceReason
                ))
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    originalReason + "\n\n" + chanceReason,
                    failureKind
                );
                return false;
            }

            return true;
        }

        private bool ProcessAwaitingHubRetry(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked == null || !tracked.IsAwaitingHubRetry)
            {
                return false;
            }

            var originalReason = string.IsNullOrWhiteSpace(
                tracked.PendingRetryReason
            )
                ? "Stardeus could not start the operation because the " +
                  $"required {GetRequiredHubName(tracked.BeingId)} was " +
                  "temporarily unavailable."
                : tracked.PendingRetryReason;

            if (RetainIfAutomaticRetryBudgetExhausted(
                    tracked,
                    originalReason,
                    CARAUOperationOutcome.Failed,
                    CARAUFailureKind.HubUnavailable
                ))
            {
                return true;
            }

            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (beingData?.Being == null ||
                beingData.Body == null ||
                beingData.Species == null ||
                !beingData.Body.IsAlive)
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    originalReason + "\n\nAutomatic retry stopped: The " +
                    "Being is no longer alive. Manual retry remains " +
                    "unavailable for this operation.",
                    CARAUFailureKind.BeingDied
                );
                return true;
            }

            var hubSlot = tracked.RuntimeSlot;
            if (TryHandleOccupiedRetrySlot(
                    tracked,
                    originalReason,
                    hubSlot,
                    out var occupiedSlotResult
                ))
            {
                return occupiedSlotResult;
            }

            // These are Stardeus's biological Surgery Hub and robotic
            // Assembly Hub slot designations. Waiting for HasFreeFor avoids
            // reproducing the narrow queue/goal availability race.
            var hubDesignation = beingData.Species.IsBiological
                ? 411591400
                : -1740156858;
            if (!S.Sys.Slots.HasFreeFor(hubDesignation))
            {
                return false;
            }

            if (!TryBuildAutomaticRetryPreview(
                    tracked,
                    out var decision,
                    out var unavailableReason
                ))
            {
                if (IsExternalAiPreSurgeryInterruption(
                        tracked.PendingRetryReason
                    ) &&
                    decision?.Evaluation?.Disposition ==
                        UpgradeDisposition.BlockedByExistingWork)
                {
                    var delay = GetPolicyRetryDelay(
                        GetAutomaticRetryCount(tracked) + 1
                    );
                    tracked.NextPolicyRetryTick = G.Ticks + delay;
                    ClearRuntimeRetryChance(tracked);

                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Intent,
                        $"External AI interruption retry still waiting | " +
                        $"Being={tracked.BeingName} | " +
                        $"Slot={tracked.SlotName} | " +
                        $"Block={decision.Evaluation.PrimaryReason} | " +
                        $"NextCheckTick={tracked.NextPolicyRetryTick}"
                    );
                    return false;
                }

                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    originalReason + "\n\n" + unavailableReason,
                    ClassifyCurrentRetryBlock(tracked, unavailableReason)
                );
                return true;
            }

            if (!TryValidateAutomaticRetryDecision(
                    tracked,
                    decision,
                    originalReason,
                    CARAUFailureKind.HubUnavailable
                ))
            {
                return true;
            }

            tracked.SilentHubRetryCount++;
            var queueResult = SurgeryService.TryQueueUpgradeResult(
                S,
                decision
            );
            if (queueResult.Success)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Queue,
                    $"Silent Hub retry queued | Being={tracked.BeingName} | " +
                    $"Slot={tracked.SlotName} | " +
                    $"Attempt={GetAutomaticRetryCount(tracked)}/" +
                    $"{CARAUSettings.AutomaticRetryLimit} | " +
                    $"Replacement={decision.TargetPart?.NameT ?? "unknown"}"
                );
                return true;
            }

            tracked.SilentHubRetryCount--;
            var queueFailureReason =
                queueResult.Evaluation?.Context?.NativeReason ??
                queueResult.Message;
            if (IsHubSaturationReason(queueFailureReason))
            {
                return false;
            }

            RetainFailure(
                tracked,
                CARAUOperationOutcome.Failed,
                "Automatic retry could not be queued. " +
                (string.IsNullOrWhiteSpace(queueResult.Message)
                    ? "Stardeus did not provide a reason."
                    : queueResult.Message) +
                "\n\n" + BuildManualRetryAvailabilityNote(tracked),
                CARAUFailureKind.PreSurgeryTaskFailure
            );
            return true;
        }

        private bool ProcessAwaitingPolicyRetry(
          CARAUTrackedOperation tracked
        )
        {
            if (tracked == null || !tracked.IsAwaitingPolicyRetry)
            {
                return false;
            }

            var originalReason = string.IsNullOrWhiteSpace(
                tracked.PendingRetryReason
            )
                ? "The previous CARAU operation failed."
                : tracked.PendingRetryReason;

            RefreshRuntimeTarget(tracked);
            var slot = tracked.RuntimeSlot;
            if (HasSuccessfulSlotResult(tracked, slot))
            {
                CompleteSuccessfully(tracked);
                return true;
            }

            if (TryHandleOccupiedRetrySlot(
                    tracked,
                    originalReason,
                    slot,
                    out var occupiedSlotResult
                ))
            {
                return occupiedSlotResult;
            }

            if (!IsFailureKindAllowedByPolicy(tracked.FailureKind))
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    originalReason + "\n\n" +
                    BuildAutomaticRetryPolicyExplanation(
                        tracked,
                        UpgradeReasonCode.RetryPolicyDisallows,
                        "The current Failure Retry Policy no longer " +
                        "permits this failure kind."
                    ),
                    tracked.FailureKind
                );
                return true;
            }

            if (RetainIfAutomaticRetryBudgetExhausted(
                    tracked,
                    originalReason,
                    CARAUOperationOutcome.Failed,
                    tracked.FailureKind
                ))
            {
                return true;
            }

            var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);
            if (tracked.FailureKind ==
                    CARAUFailureKind.BeingTemporarilyUntargetable &&
                SurgeryQueue.HasMentalBreakdown(beingData))
            {
                return false;
            }

            if (S.Clock.IsStopped || G.Ticks < tracked.NextPolicyRetryTick)
            {
                return false;
            }

            if (ShouldWaitForPreventativeMoodRegulator(
                    tracked,
                    beingData,
                    out var moodRegulatorStateChanged
                ))
            {
                return moodRegulatorStateChanged;
            }

            if (!TryBuildAutomaticRetryPreview(
                    tracked,
                    out var decision,
                    out var unavailableReason
                ))
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    originalReason + "\n\n" + unavailableReason,
                    ClassifyCurrentRetryBlock(tracked, unavailableReason)
                );
                return true;
            }

            if (!TryValidateAutomaticRetryDecision(
                    tracked,
                    decision,
                    originalReason,
                    tracked.FailureKind
                ))
            {
                return true;
            }

            tracked.PolicyRetryCount++;
            var queueResult = SurgeryService.TryQueueUpgradeResult(
                S,
                decision
            );
            if (queueResult.Success)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Queue,
                    $"Automatic policy retry queued | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                    $"FailureKind={tracked.FailureKind} | " +
                    $"Attempt={GetAutomaticRetryCount(tracked)}/" +
                    $"{CARAUSettings.AutomaticRetryLimit} | " +
                    $"Replacement={decision.TargetPart?.NameT ?? "unknown"} | " +
                    $"ObjId={decision.InventoryPart?.Id ?? 0}"
                );
                return true;
            }

            tracked.PolicyRetryCount--;
            var queueFailureReason =
                queueResult.Evaluation?.Context?.NativeReason ??
                queueResult.Message;
            if (IsHubSaturationReason(queueFailureReason))
            {
                return TryDeferHubSaturationRetry(
                    tracked,
                    string.IsNullOrWhiteSpace(queueFailureReason)
                        ? "The required surgery facility is temporarily " +
                          "unavailable."
                        : queueFailureReason
                );
            }

            RetainFailure(
                tracked,
                CARAUOperationOutcome.Failed,
                originalReason + "\n\nAutomatic retry could not be queued. " +
                (string.IsNullOrWhiteSpace(queueResult.Message)
                    ? "Stardeus did not provide a reason."
                    : queueResult.Message) +
                "\n\n" + BuildManualRetryAvailabilityNote(tracked),
                tracked.FailureKind
            );
            return true;
        }

        private bool ShouldWaitForPreventativeMoodRegulator(
          CARAUTrackedOperation tracked,
          BeingData beingData,
          out bool changed
        )
        {
            changed = false;
            if (!CARAUSettings.AutoInstallMoodRegulatorAfterMentalBreakdown ||
                tracked?.FailureKind !=
                    CARAUFailureKind.BeingTemporarilyUntargetable)
            {
                return false;
            }

            var result = PreventativeImplantService.TryQueue(
                S,
                beingData,
                "Implant/MoodRegulator",
                "Mood Regulator",
                allowNewQueue: !tracked.MoodRegulatorAttempted
            );
            if (result.Disposition ==
                PreventativeImplantQueueDisposition.TargetOperationPending)
            {
                // Keep the interrupted operation behind the preventative
                // implant until Core reports a terminal surgery result.
                return true;
            }

            if (result.IsQueued)
            {
                MarkMoodRegulatorAttempted(
                    tracked.BeingId,
                    result.Detail
                );
                changed = true;
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Queue,
                    $"Preventative Mood Regulator queued | " +
                    $"Being={tracked.BeingName} | " +
                    $"BeingId={tracked.BeingId} | " +
                    $"ObjId={result.Decision?.InventoryPart?.Id ?? 0} | " +
                    (result.Chance?.Describe(
                        CARAUSettings
                            .AutomaticRetryChanceThresholdPercent
                    ) ?? "Chance=unknown")
                );
                return true;
            }

            if (result.Disposition ==
                PreventativeImplantQueueDisposition.AlreadyHandled)
            {
                return false;
            }

            changed = MarkMoodRegulatorAttempted(
                tracked.BeingId,
                result.Detail
            );
            return false;
        }
        private bool MarkMoodRegulatorAttempted(
          uint beingId,
          string reason
        )
        {
            var changed = false;
            foreach (var operation in operations)
            {
                if (operation?.BeingId != beingId ||
                    operation.FailureKind !=
                        CARAUFailureKind.BeingTemporarilyUntargetable ||
                    !operation.IsAwaitingPolicyRetry ||
                    operation.MoodRegulatorAttempted)
                {
                    continue;
                }

                operation.MoodRegulatorAttempted = true;
                changed = true;
            }

            if (changed)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Decision,
                    $"Mood Regulator prevention evaluated | " +
                    $"BeingId={beingId} | Result={reason}"
                );
            }
            return changed;
        }

        private static bool IsHubSaturationReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            return reason.IndexOf(
                       "Required device is not available",
                       StringComparison.OrdinalIgnoreCase
                   ) >= 0 ||
                   (reason.IndexOf(
                        "Busy",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0 &&
                    (reason.IndexOf(
                         "Surgery Hub",
                         StringComparison.OrdinalIgnoreCase
                     ) >= 0 ||
                     reason.IndexOf(
                         "Assembly Hub",
                         StringComparison.OrdinalIgnoreCase
                     ) >= 0));
        }

        private string GetRequiredHubName(uint beingId)
        {
            var beingData = S.Query.FindBeingData.Ask(beingId);
            return beingData?.Species?.IsBiological == true
                ? "Surgery Hub"
                : "Assembly Hub";
        }

        /// <summary>
        /// Finds Stardeus's own failure entry for this exact slot. A generic
        /// AI-goal failure (for example, a temporarily busy Assembly Hub) can
        /// coexist with the authoritative surgery result, so the slot log is
        /// preferred whenever it is available.
        /// </summary>
        private bool TryGetRelevantBeingFailureLog(
          CARAUTrackedOperation tracked,
          out string reason
        )
        {
            reason = null;
            if (tracked == null ||
                string.IsNullOrWhiteSpace(tracked.SlotName))
            {
                return false;
            }

            var matchingEntries = S.Query.GetBeingLogEntries
                .Ask(tracked.BeingId)?
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Text) &&
                    entry.Tick >= tracked.QueuedAtTick &&
                    entry.Text.IndexOf(
                        tracked.SlotName,
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0 &&
                    entry.Text.IndexOf(
                        "failed to perform surgery",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0
                )
                .OrderByDescending(entry => entry.Tick)
                .ToArray();

            if (matchingEntries == null || matchingEntries.Length == 0)
            {
                return false;
            }

            var terminalTick = matchingEntries[0].Tick;
            var terminalEntries = matchingEntries
                .Where(entry => entry.Tick == terminalTick)
                .ToArray();

            string rawReason;
            if (terminalEntries.Length == 1)
            {
                rawReason =
                    $"Raw Stardeus Being log: {terminalEntries[0].Text}";
            }
            else
            {
                rawReason =
                    $"Raw Stardeus Being logs ({terminalEntries.Length}):\n" +
                    string.Join(
                        "\n",
                        terminalEntries.Select((entry, index) =>
                            $"{index + 1}. {entry.Text}"
                        )
                    );
            }

            reason = string.IsNullOrWhiteSpace(
                tracked.RuntimeNativeSurgicalFailureReason
            )
                ? rawReason
                : tracked.RuntimeNativeSurgicalFailureReason +
                  "\n\n" + rawReason;
            return true;
        }

        /// <summary>
        /// Captures Stardeus's authoritative terminal state while the removed
        /// operation object still carries its native failure reason. Polling
        /// alone can observe the operation only after it has disappeared and
        /// would then have to infer a less useful fallback reason.
        /// </summary>
        private void OnSurgicalOperationRegistered(
          SurgicalOperation operation,
          bool registered
        )
        {
            if (registered ||
                operation == null)
            {
                return;
            }

            var tracked = operations.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.IsPending &&
                IsTrackedOperationMatch(candidate, operation)
            );

            if (tracked == null)
            {
                return;
            }

            RefreshRuntimeTarget(tracked);

            if (tracked.IsRemoveAllParts)
            {
                tracked.RuntimeOperation = operation;
                if (!tracked.RuntimeCancellationRequestedByCARAU)
                {
                    ClassifyRemoveAllPartsTerminal(tracked, operation);
                }

                RefreshWidget();
                ColonistAndBotsAutoUpgradeSys.Instance?
                    .RefreshTrackedOperationState();
                return;
            }

            if (tracked.RuntimeSlot?.HoldsMind == true)
            {
                BeginCoreTransitionValidation(
                    tracked,
                    "native Core operation unregistered"
                );
                return;
            }

            if (IsBeingInCoreTransition(tracked.BeingId))
            {
                tracked.RuntimeInterruptedByCoreIdentityTransition = true;
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Intent,
                    $"Sibling operation held during Core identity transition | " +
                    $"Being={tracked.BeingName} | Slot={tracked.SlotName}"
                );
                return;
            }

            var beingDied = TryBuildDeathFailureReason(
                tracked,
                out var deathReason
            );

            if (!beingDied &&
                operation.State.Value == SurgicalOpState.Canceled)
            {
                var goalReason = operation.AssociatedGoal?.FailReason;
                if (string.IsNullOrWhiteSpace(goalReason))
                {
                    goalReason = operation.AssociatedGoal?.TaskLineFail;
                }

                if (!string.IsNullOrWhiteSpace(goalReason))
                {
                    var operationReason =
                        BuildPreSurgeryTaskFailureReason(
                            tracked,
                            goalReason
                        );
                    if (!TryDeferHubSaturationRetry(
                            tracked,
                            operationReason
                        ))
                    {
                        HandleOperationFailure(
                            tracked,
                            CARAUOperationOutcome.Failed,
                            operationReason,
                            CARAUFailureKind.PreSurgeryTaskFailure
                        );
                    }
                }

                // A reasonless cancellation may be CARAU-initiated. The
                // normal caller removes that record silently; an external
                // cancellation is classified by the next tracking refresh.
                if (tracked.IsPending)
                {
                    return;
                }
            }
            else if (!beingDied &&
                !ClassifyTerminalOperation(tracked, operation))
            {
                return;
            }

            if (beingDied && tracked.IsPending)
            {
                RetainFailure(
                    tracked,
                    CARAUOperationOutcome.Failed,
                    deathReason,
                    CARAUFailureKind.BeingDied
                );
            }

            RefreshWidget();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();
        }

        private void OnAfterLoadState(GameState state)
        {
            var restoredFromSave = trackingDataLoadedForCurrentGame;
            trackingDataLoadedForCurrentGame = false;
            ResetRuntimeStateForGameBoundary(
                clearTrackingData: !restoredFromSave,
                reason: restoredFromSave
                    ? "loaded game with CARAU tracking data"
                    : "loaded game without CARAU tracking data"
            );

            Ready.WhenGame(this, "Restore CARAU operation tracker widget", () =>
            {
                gameReady = true;
                RestoreRuntimeReferences();
                RefreshWidget();
            });
        }

        private void OnNewGameInitialized()
        {
            trackingDataLoadedForCurrentGame = false;
            ResetRuntimeStateForGameBoundary(
                clearTrackingData: true,
                reason: "new game initialized"
            );
        }

        private void ResetRuntimeStateForGameBoundary(
          bool clearTrackingData,
          string reason
        )
        {
            var discardedOperations = clearTrackingData
                ? operations.Count
                : 0;
            var discardedSuccessful = clearTrackingData
                ? successfulOperations
                : 0;
            var discardedFailed = clearTrackingData
                ? failedOperations
                : 0;

            trackingPanel?.Dispose();
            trackingPanel = null;
            DisposeWidget();
            displayedBeingOrder.Clear();
            ColonistAndBotsAutoUpgradeSys.Instance?
                .ResetForGameBoundary();

            if (clearTrackingData)
            {
                operations.Clear();
                successfulOperations = 0;
                failedOperations = 0;
            }

            gameReady = false;
            operatorlessAutomatedHubDiagnosticEmitted = false;
            firewallChipPrevention.Reset();
            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                $"Game-boundary tracking reset | Reason={reason} | " +
                $"TrackingDataCleared={clearTrackingData} | " +
                $"DiscardedOperations={discardedOperations} | " +
                $"DiscardedSuccessful={discardedSuccessful} | " +
                $"DiscardedFailed={discardedFailed}"
            );
        }

        /// <summary>
        /// Reconnects save-persistent tracking records to Stardeus's live
        /// BeingData and BodyPartSlot instances. Active records also require
        /// their matching native operation; persisted automatic waits do not.
        /// </summary>
        private void RestoreRuntimeReferences()
        {
            if (operations.Count == 0)
            {
                return;
            }

            var restored = 0;
            var classified = 0;

            for (var i = operations.Count - 1; i >= 0; i--)
            {
                var tracked = operations[i];
                var beingData = S.Query.FindBeingData.Ask(tracked.BeingId);

                if (beingData?.Being == null || beingData.Body?.Slots == null)
                {
                    if (tracked.IsPending)
                    {
                        RetainFailure(
                            tracked,
                            CARAUOperationOutcome.Invalid,
                            "The tracked being no longer exists after loading.",
                            CARAUFailureKind.InvalidTarget
                        );
                        classified++;
                    }

                    continue;
                }

                tracked.RuntimeBeing = beingData.Being;

                if (tracked.IsRemoveAllParts)
                {
                    if (!tracked.IsPending)
                    {
                        restored++;
                        continue;
                    }

                    var massOperation =
                        ResolveRemoveAllPartsOperation(tracked);
                    if (massOperation != null)
                    {
                        restored++;
                        CARAUDebugLog.Add(
                            CARAUDebugCategory.Intent,
                            $"Remove All Parts tracking restored | " +
                            $"Being={tracked.BeingName} | " +
                            $"BeingId={tracked.BeingId} | " +
                            $"NativeOperationId=" +
                            $"{tracked.NativeOperationId}"
                        );
                    }
                    else if (RefreshRemoveAllPartsOperation(tracked))
                    {
                        classified++;
                    }

                    continue;
                }

                var slot = beingData.Body.Slots.FirstOrDefault(candidate =>
                    candidate != null &&
                    (
                        (!string.IsNullOrEmpty(tracked.SlotId) &&
                         string.Equals(
                             candidate.Id,
                             tracked.SlotId,
                             StringComparison.Ordinal
                         )) ||
                        (string.IsNullOrEmpty(tracked.SlotId) &&
                         string.Equals(
                             candidate.NameT,
                             tracked.SlotName,
                             StringComparison.Ordinal
                         ))
                    )
                );

                if (slot == null)
                {
                    if (tracked.IsPending)
                    {
                        RetainFailure(
                            tracked,
                            CARAUOperationOutcome.Invalid,
                            "The tracked body slot no longer exists after loading.",
                            CARAUFailureKind.InvalidTarget
                        );
                        classified++;
                    }

                    continue;
                }

                tracked.RuntimeSlot = slot;

                if (TryReconcileRetainedCoreSuccess(tracked, slot))
                {
                    restored++;
                    classified++;
                    continue;
                }

                if (TryFinalizeIdentityCoreDestroyedFailure(tracked, slot))
                {
                    restored++;
                    classified++;
                    continue;
                }

                if (!tracked.IsPending)
                {
                    restored++;
                    continue;
                }

                if (tracked.IsAwaitingHubRetry ||
                    tracked.IsAwaitingPolicyRetry)
                {
                    if (tracked.IsAwaitingHubRetry &&
                        tracked.FailureKind == CARAUFailureKind.None)
                    {
                        // 0.4.10 already persisted Hub waits but did not have
                        // the typed failure key introduced by this build.
                        tracked.FailureKind =
                            CARAUFailureKind.HubUnavailable;
                    }

                    restored++;
                    continue;
                }

                var operation =
                    S.Query.FindSurgicalOperation.Ask(slot, true);

                if (!IsTrackedOperationMatch(tracked, operation))
                {
                    if (HasSuccessfulSlotResult(tracked, slot))
                    {
                        CompleteSuccessfully(tracked);
                    }
                    else
                    {
                        RetainFailure(
                            tracked,
                            operation == null
                                ? CARAUOperationOutcome.CancelledExternally
                                : CARAUOperationOutcome.Stale,
                            operation == null
                                ? "The saved operation is no longer registered."
                                : "The slot contains a different operation " +
                                  "than the saved CARAU record.",
                            operation == null
                                ? CARAUFailureKind.ExternalCancellation
                                : CARAUFailureKind.StaleOwnership
                        );
                    }

                    classified++;
                    continue;
                }

                // Upgrade older save records so their next save uses the
                // stable internal slot identity.
                if (string.IsNullOrEmpty(tracked.SlotId))
                {
                    tracked.SlotId = slot.Id ?? string.Empty;
                }

                restored++;
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Intent,
                    $"Operation tracking restored | " +
                    $"Being={tracked.BeingName} | BeingId={tracked.BeingId} | " +
                    $"Slot={tracked.SlotName} | Target={tracked.TargetPartName}"
                );
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Operation tracking restoration complete | " +
                $"Restored={restored} | Classified={classified} | " +
                $"Tracked={operations.Count}"
            );

            trackingPanel?.Refresh(operations);
            ColonistAndBotsAutoUpgradeSys.Instance?
                .RefreshTrackedOperationState();
        }

        private static bool IsTrackedOperationMatch(
          CARAUTrackedOperation tracked,
          SurgicalOperation operation
        )
        {
            if (tracked == null || operation == null)
            {
                return false;
            }

            if (tracked.IsRemoveAllParts)
            {
                return operation.TargetBeing?.Id == tracked.BeingId &&
                    operation.TargetSlot == null &&
                    operation.OpType == SurgicalOpType.RemoveAll &&
                    (tracked.NativeOperationId == 0 ||
                     operation.Id == tracked.NativeOperationId);
            }

            // Ownership cannot be proven without the exact reserved object.
            // Refuse zero-id or incomplete records instead of risking a match
            // with unrelated work registered in the same body slot.
            if (tracked.ReservedPartId == 0)
            {
                return false;
            }

            return operation.ReservedPart != null &&
                operation.ReservedPart.Id == tracked.ReservedPartId;
        }

        private void RefreshWidget()
        {
            if (!gameReady)
            {
                return;
            }

            if (operations.Count == 0)
            {
                trackingPanel?.Refresh(operations);
                ResetCompletedSessionCounters();
                DisposeWidget();
                return;
            }

            if (frameworkWidget == null)
            {
                frameworkWidget = AutoUtilitiesApi.RegisterTopBarWidget(
                    new TopBarWidgetRegistration
                {
                    Id = "ColonistAndBotsAutoUpgrade.TrackedOperations",
                    Icon = () => RetainedFailureCount > 0
                        ? "Icons/Color/CARAUIconFailure"
                        : "Icons/Color/CARAUIcon",
                    Tint = () => Color.white,
                    Tooltip = BuildTooltip,
                    BadgeText = () => PendingOperationCount.ToString(),
                    BadgeTextColor = () => Color.white,
                    OnClick = OnWidgetClicked,
                    Width = GetWidgetWidth(),
                    Order = 400
                });
            }

            frameworkWidget.Refresh();
            trackingPanel?.Refresh(operations);
        }

        private sealed class BulkQueueTrackingScope : IDisposable
        {
            private CARAUOperationTrackingSys owner;

            public BulkQueueTrackingScope(CARAUOperationTrackingSys owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                var currentOwner = owner;
                owner = null;
                currentOwner?.EndBulkQueueTrackingInternal();
            }
        }

        private sealed class EmptyBulkQueueTrackingScope : IDisposable
        {
            public static readonly EmptyBulkQueueTrackingScope Instance =
                new EmptyBulkQueueTrackingScope();

            public void Dispose()
            {
            }
        }

        private void ResetCompletedSessionCounters()
        {
            if (successfulOperations == 0 && failedOperations == 0)
            {
                return;
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                $"Operation tracking session cleared | " +
                $"Successful={successfulOperations} | " +
                $"Failed={failedOperations}"
            );

            successfulOperations = 0;
            failedOperations = 0;
        }

        private float GetWidgetWidth()
        {
            // The count is displayed below the icon, matching vanilla alert
            // widgets. A fixed width keeps cooperative spacing stable when
            // the operation count crosses 10 or 100.
            return 48f;
        }

        private string BuildTooltip()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<b>CARAU Tracked Operations</b>");
            sb.AppendLine(
                $"Active Operations: {ActiveNativeOperationCount}"
            );
            sb.AppendLine($"Waiting to Retry: {WaitingRetryCount}");
            sb.AppendLine(
                $"Successful Operations: {successfulOperations}"
            );
            sb.AppendLine($"Failed Operations: {failedOperations}");

            foreach (var group in operations
                .Where(operation =>
                    operation.IsPending &&
                    !operation.IsAwaitingAutomaticRetry
                )
                .GroupBy(op => new { op.BeingId, op.BeingName })
                .OrderBy(group => group.Key.BeingName))
            {
                sb.AppendLine(
                    $"→ {group.Key.BeingName}: {group.Count()}"
                );
            }

            foreach (var group in operations
                .Where(operation => operation.IsAwaitingAutomaticRetry)
                .GroupBy(op => new { op.BeingId, op.BeingName })
                .OrderBy(group => group.Key.BeingName))
            {
                sb.AppendLine(
                    $"↻ {group.Key.BeingName}: {group.Count()} waiting"
                );
            }

            if (RetainedFailureCount > 0)
            {
                sb.AppendLine(
                    $"Retained Failure Details: {RetainedFailureCount}"
                );
            }

            sb.Append("\nClick to open or close operation tracking.");
            return sb.ToString();
        }

        private void OnWidgetClicked()
        {
            if (trackingPanel?.IsOpen == true)
            {
                trackingPanel.Close();
                return;
            }

            if (trackingPanel == null)
            {
                trackingPanel = new CARAUOperationTrackingPanel(S);
            }

            trackingPanel.Show(operations);
        }

        private void DisposeWidget()
        {
            frameworkWidget?.Dispose();
            frameworkWidget = null;
        }

        internal static void RecordNativeSurgicalFailure(
          Game.Systems.BodyParts.BodyPartDef part,
          Game.Systems.BodyParts.BodyPartSlot slot,
          bool isExtract,
          SurgicalOpFailureType outcome
        )
        {
            var instance = Instance;
            if (instance == null ||
                slot == null ||
                outcome == SurgicalOpFailureType.None)
            {
                return;
            }

            var tracked = instance.operations.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.IsPending &&
                ReferenceEquals(candidate.RuntimeSlot, slot)
            );
            if (tracked == null)
            {
                return;
            }

            var partName = part?.NameT ?? tracked.TargetPartName ?? "part";
            var reason = DescribeNativeSurgicalFailure(
                outcome,
                partName,
                tracked.SlotName,
                isExtract
            );
            tracked.RuntimeNativeSurgicalFailureReason =
                string.IsNullOrWhiteSpace(
                    tracked.RuntimeNativeSurgicalFailureReason
                )
                    ? reason
                    : tracked.RuntimeNativeSurgicalFailureReason +
                      "\n" + reason;
            tracked.RuntimeNativeSurgicalFailureWasExtraction = isExtract;
            tracked.RuntimeNativeSurgicalFailureKind =
                ClassifyNativeSurgicalFailure(slot, isExtract, outcome);

            if (slot.HoldsMind && isExtract)
            {
                instance.BeginCoreTransitionValidation(
                    tracked,
                    $"native extraction outcome {outcome}"
                );
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Warning,
                $"Native surgical failure captured | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                $"Part={partName} | " +
                $"Phase={(isExtract ? "Extraction" : "Implantation")} | " +
                $"Outcome={outcome} | Reason={reason}"
            );
        }

        internal static void RecordCoreMindTransfer(
          Game.Systems.BodyParts.BodyPartSlot slot
        )
        {
            var instance = Instance;
            if (instance == null || slot?.HoldsMind != true)
            {
                return;
            }

            var tracked = instance.operations.FirstOrDefault(candidate =>
                candidate?.IsPending == true &&
                ReferenceEquals(candidate.RuntimeSlot, slot)
            );
            if (tracked != null)
            {
                instance.BeginCoreTransitionValidation(
                    tracked,
                    "native mind transfer started"
                );
            }
        }

        internal static void ObserveOperatorlessAutomatedHub(
          Game.Systems.BodyParts.BodyPartSlot slot,
          Game.Components.OperatableDeviceComp operatable
        )
        {
            var instance = Instance;
            if (instance == null ||
                instance.operatorlessAutomatedHubDiagnosticEmitted ||
                slot == null ||
                operatable == null ||
                !operatable.IsAutomated ||
                operatable.Operator != null)
            {
                return;
            }

            var tracked = instance.operations.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.IsPending &&
                ReferenceEquals(candidate.RuntimeSlot, slot)
            );
            if (tracked == null)
            {
                return;
            }

            instance.operatorlessAutomatedHubDiagnosticEmitted = true;
            CARAUDebugLog.Add(
                CARAUDebugCategory.Warning,
                "Operatorless automated Hub observed | " +
                $"Being={tracked.BeingName} | Slot={tracked.SlotName} | " +
                "CARAU's retry gate used the operator-neutral Stardeus " +
                "estimate shown in Vanilla. Core is executing this procedure " +
                "with no operator and therefore applies skill level 0 to the " +
                "actual roll; the actual probability can be materially lower."
            );
        }

        private static CARAUFailureKind ClassifyNativeSurgicalFailure(
          Game.Systems.BodyParts.BodyPartSlot slot,
          bool isExtract,
          SurgicalOpFailureType outcome
        )
        {
            if (slot?.HoldsMind == true && isExtract)
            {
                switch (outcome)
                {
                    case SurgicalOpFailureType.DestroySlot:
                        return CARAUFailureKind.SlotDestroyed;

                    case SurgicalOpFailureType.WeldPart:
                        return CARAUFailureKind.InstalledPartWelded;

                    default:
                        // Fatality, DestroyPart, wounds, and crippling can
                        // affect the outgoing Core or identity while Stardeus
                        // continues the same swap into InstallPart. The final
                        // installed Core state decides the operation result.
                        return CARAUFailureKind.None;
                }
            }

            switch (outcome)
            {
                case SurgicalOpFailureType.DestroyPart:
                    return slot?.HoldsMind == true
                        ? CARAUFailureKind.IdentityCoreDestroyed
                        : CARAUFailureKind.ReplacementDestroyedOrConsumed;

                case SurgicalOpFailureType.DestroySlot:
                    return CARAUFailureKind.SlotDestroyed;

                case SurgicalOpFailureType.Rejection:
                    return CARAUFailureKind.SurgicalRejection;

                case SurgicalOpFailureType.CripplePart:
                case SurgicalOpFailureType.WoundPart:
                    return CARAUFailureKind.SurgicalRecoverableDamage;

                case SurgicalOpFailureType.WeldPart:
                    return CARAUFailureKind.InstalledPartWelded;

                case SurgicalOpFailureType.Fatality:
                    return CARAUFailureKind.BeingDied;

                default:
                    return CARAUFailureKind.Unknown;
            }
        }

        private static string DescribeNativeSurgicalFailure(
          SurgicalOpFailureType outcome,
          string partName,
          string slotName,
          bool isExtract
        )
        {
            switch (outcome)
            {
                case SurgicalOpFailureType.DestroyPart:
                    return isExtract
                        ? $"Surgical result - existing-part removal: The " +
                          $"previously installed {partName} was destroyed " +
                          $"while Stardeus removed it from {slotName}."
                        : $"Surgical result - replacement installation: The " +
                          $"incoming replacement {partName} was destroyed " +
                          $"during installation and was not installed in " +
                          $"{slotName}.";

                case SurgicalOpFailureType.DestroySlot:
                    return $"Surgical result - " +
                           $"{(isExtract ? "existing-part removal" : "replacement installation")}: " +
                           $"The {slotName} slot was destroyed and cannot " +
                           "hold the replacement.";

                case SurgicalOpFailureType.CripplePart:
                    return isExtract
                        ? $"Surgical result - existing-part removal: The " +
                          $"previously installed {partName} was crippled " +
                          $"during removal from {slotName}."
                        : $"Surgical result - replacement installation: The " +
                          $"incoming replacement {partName} was crippled " +
                          $"during installation. Stardeus then installed that " +
                          $"damaged replacement in {slotName}.";

                case SurgicalOpFailureType.WeldPart:
                    return isExtract
                        ? $"Surgical result - existing-part removal: The " +
                          $"previously installed {partName} became welded in " +
                          $"{slotName} and could not be removed."
                        : $"Surgical result - replacement installation: The " +
                          $"incoming replacement {partName} became welded " +
                          $"during installation and was installed welded in " +
                          $"{slotName}.";

                case SurgicalOpFailureType.WoundPart:
                    return isExtract
                        ? $"Surgical result - existing-part removal: The " +
                          $"previously installed {partName} was damaged " +
                          $"during removal from {slotName}."
                        : $"Surgical result - replacement installation: The " +
                          $"incoming replacement {partName} was damaged " +
                          $"during installation. Stardeus then installed that " +
                          $"damaged replacement in {slotName}.";

                case SurgicalOpFailureType.Rejection:
                    return $"Surgical result - replacement installation: The " +
                           $"incoming replacement {partName} was rejected by " +
                           $"the immune system and was not installed in " +
                           $"{slotName}.";

                case SurgicalOpFailureType.Fatality:
                    return $"Surgical result - " +
                           $"{(isExtract ? "existing-part removal" : "replacement installation")}: " +
                           "The patient did not survive this phase of the " +
                           "surgery.";

                default:
                    return $"Surgical result - " +
                           $"{(isExtract ? "existing-part removal" : "replacement installation")}: " +
                           $"Stardeus reported {outcome} for {partName} in " +
                           $"{slotName}.";
            }
        }

        public void SaveSpecial(SystemsDataSpecial sd)
        {
            if (sd == null)
            {
                return;
            }

            sd.ModData ??= new Dictionary<string, byte[]>();
            sd.ModData[SaveKey] = SaveLoadUtils.Serialize(
                new CARAUTrackingData
                {
                    Operations = new List<CARAUTrackedOperation>(operations),
                    SuccessfulOperations = successfulOperations,
                    FailedOperations = failedOperations
                }
            );
        }

        public void LoadSpecial(SystemsDataSpecial sd)
        {
            trackingDataLoadedForCurrentGame = false;
            operations.Clear();
            successfulOperations = 0;
            failedOperations = 0;

            if (sd?.ModData == null ||
                !sd.ModData.TryGetValue(SaveKey, out var raw) ||
                raw == null ||
                raw.Length == 0)
            {
                return;
            }

            trackingDataLoadedForCurrentGame = true;

            try
            {
                var data = SaveLoadUtils.Deserialize<CARAUTrackingData>(raw);
                if (data?.Operations != null)
                {
                    operations.AddRange(data.Operations);
                }

                successfulOperations =
                    Math.Max(0, data?.SuccessfulOperations ?? 0);
                failedOperations =
                    Math.Max(0, data?.FailedOperations ?? 0);
            }
            catch (Exception ex)
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    $"Tracked operations could not be restored | Error={ex.Message}"
                );
            }
        }

        public override void Unload()
        {
            S.Sig.AfterLoadState.RemoveListener(OnAfterLoadState);
            S.Sig.NewGameInitialized.RemoveListener(OnNewGameInitialized);
            S.Sig.SurgicalOperationRegistered.RemoveListener(
                OnSurgicalOperationRegistered
            );
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            trackingPanel?.Dispose();
            trackingPanel = null;
            DisposeWidget();
            if (trackingUpdaterObject != null)
            {
                UnityEngine.Object.Destroy(trackingUpdaterObject);
                trackingUpdaterObject = null;
                trackingUpdater = null;
            }
            operations.Clear();
            successfulOperations = 0;
            failedOperations = 0;
            firewallChipPrevention.Reset();
            gameReady = false;
            trackingDataLoadedForCurrentGame = false;

            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }
    }
}
