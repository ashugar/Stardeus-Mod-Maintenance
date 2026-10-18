using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Systems;
using Game;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Creatures.Surgeries;
using Game.Systems.Items;
using Game.Utils;
using KL.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public enum PreventativeImplantQueueDisposition
    {
        InvalidTarget = 0,
        Queued = 1,
        TargetOperationPending = 2,
        ImplantOccupied = 3,
        ImplantUnavailable = 4,
        OtherOperationPending = 5,
        TrackedOperationExists = 6,
        PartUnavailable = 7,
        ChanceRejected = 8,
        ReservationFailed = 9,
        RegistrationRejected = 10,
        AlreadyHandled = 11
    }

    public sealed class PreventativeImplantQueueResult
    {
        public PreventativeImplantQueueDisposition Disposition { get; set; }
        public string Detail { get; set; }
        public UpgradeDecision Decision { get; set; }
        public AutomaticRetryChanceResult Chance { get; set; }

        public bool IsQueued =>
            Disposition == PreventativeImplantQueueDisposition.Queued;

        public bool IsCovered =>
            IsQueued ||
            Disposition ==
                PreventativeImplantQueueDisposition.TargetOperationPending ||
            Disposition ==
                PreventativeImplantQueueDisposition.TrackedOperationExists;
    }

    /// <summary>
    /// Shared native queue path for CARAU's opt-in preventative Implant
    /// features. Callers own the triggering event and retry cadence; this
    /// service owns exact-slot validation, inventory selection, the automatic
    /// surgery chance gate, native reservation/registration, and CARAU
    /// tracking.
    /// </summary>
    public static class PreventativeImplantService
    {
        public const string ImplantSlotId = "Implant";

        public static PreventativeImplantQueueResult TryQueue(
          GameState gameState,
          BeingData being,
          string targetPartId,
          string targetPartName,
          bool allowNewQueue = true
        )
        {
            if (gameState == null ||
                being?.Being == null ||
                being.Body?.Slots == null ||
                string.IsNullOrWhiteSpace(targetPartId))
            {
                return Result(
                    PreventativeImplantQueueDisposition.InvalidTarget,
                    "The Being, body, or requested Implant definition is unavailable."
                );
            }

            var implantSlot = being.Body.Slots.FirstOrDefault(slot =>
                slot?.Type != null &&
                string.Equals(
                    slot.Type.Id,
                    ImplantSlotId,
                    StringComparison.Ordinal
                )
            );
            if (implantSlot == null)
            {
                return Result(
                    PreventativeImplantQueueDisposition.ImplantUnavailable,
                    "No Implant slot is available."
                );
            }

            if (!implantSlot.IsPartRemoved)
            {
                var installedName =
                    implantSlot.InstalledDef?.NameT ?? "an existing implant";
                return Result(
                    PreventativeImplantQueueDisposition.ImplantOccupied,
                    string.Equals(
                        implantSlot.InstalledDef?.Id,
                        targetPartId,
                        StringComparison.Ordinal
                    )
                        ? $"A {targetPartName} is already installed."
                        : $"The Implant slot contains {installedName}; CARAU will not replace it."
                );
            }

            var nativeOperation =
                gameState.Query.FindSurgicalOperation.Ask(implantSlot, true);
            if (nativeOperation != null)
            {
                var pendingPartId = nativeOperation.ReservedPart?.BodyPart?.Id;
                return Result(
                    string.Equals(
                        pendingPartId,
                        targetPartId,
                        StringComparison.Ordinal
                    )
                        ? PreventativeImplantQueueDisposition.TargetOperationPending
                        : PreventativeImplantQueueDisposition.OtherOperationPending,
                    string.Equals(
                        pendingPartId,
                        targetPartId,
                        StringComparison.Ordinal
                    )
                        ? $"A {targetPartName} operation is already pending."
                        : "Another native operation already occupies the Implant slot; CARAU did not alter it."
                );
            }

            if (!allowNewQueue)
            {
                return Result(
                    PreventativeImplantQueueDisposition.AlreadyHandled,
                    $"Preventative {targetPartName} surgery was already evaluated for this episode."
                );
            }

            if (CARAUOperationTrackingSys.IsSlotTracked(
                    being.Id,
                    implantSlot.Id
                ))
            {
                return Result(
                    PreventativeImplantQueueDisposition.TrackedOperationExists,
                    "CARAU already has a retained or pending operation for the Implant slot."
                );
            }

            if (implantSlot.IsDestroyed ||
                implantSlot.HasMissingOrDestroyedParent)
            {
                return Result(
                    PreventativeImplantQueueDisposition.ImplantUnavailable,
                    "The Implant slot or its parent chain is unusable."
                );
            }

            var installableParts = new List<ObjData>();
            gameState.Query.InstallableBodyParts.Ask(
                implantSlot,
                installableParts
            );
            var replacements = installableParts
                .Where(part =>
                    part?.IsAvailable == true &&
                    string.Equals(
                        part.BodyPart?.Id,
                        targetPartId,
                        StringComparison.Ordinal
                    )
                )
                .OrderByDescending(part =>
                    part.BodyPartState?.Quality ?? part.Quality
                )
                .ThenByDescending(part =>
                    part.BodyPartState?.Condition ?? 0f
                )
                .ToList();
            var replacement = replacements.FirstOrDefault();
            if (replacement == null)
            {
                return Result(
                    PreventativeImplantQueueDisposition.PartUnavailable,
                    $"No available compatible {targetPartName} was found."
                );
            }

            var decision = BuildInstallDecision(
                implantSlot,
                replacement,
                replacements.Count
            );
            if (!AutomaticRetryChanceService.TryEvaluate(
                    decision,
                    out var chance,
                    out var chanceReason
                ) ||
                !AutomaticRetryChanceService.IsAboveConfiguredThreshold(
                    chance
                ))
            {
                var detail = chance == null
                    ? chanceReason ?? "The surgery estimate was unavailable."
                    : chance.Describe(
                        CARAUSettings
                            .AutomaticRetryChanceThresholdPercent
                    );
                return Result(
                    PreventativeImplantQueueDisposition.ChanceRejected,
                    $"{targetPartName} surgery did not pass the automatic surgery-estimate threshold. {detail}",
                    decision,
                    chance
                );
            }

            var operation = new SurgicalOperation
            {
                TargetBeing = being,
                TargetSlot = implantSlot,
                ReservedPart = replacement,
                OpType = SurgicalOpType.Install
            };
            if (!replacement.TryReserveForOperation(operation))
            {
                return Result(
                    PreventativeImplantQueueDisposition.ReservationFailed,
                    $"The selected {targetPartName} could not be reserved.",
                    decision,
                    chance
                );
            }

            if (!gameState.Query.RegisterSurgicalOperation.Ask(operation, true))
            {
                var nativeReason = operation.FailReason ??
                    "Stardeus rejected the preventative implant operation.";
                if (replacement.Reservation?.Surgery == operation)
                {
                    replacement.TryUnreserveForOperation(operation);
                }
                return Result(
                    PreventativeImplantQueueDisposition.RegistrationRejected,
                    $"{targetPartName} surgery was not registered: {nativeReason}",
                    decision,
                    chance
                );
            }

            if (replacement.Obj != null)
            {
                replacement.Obj.StorableObj.IsHaulingForbidden = true;
                if (replacement.Obj.Goal != null)
                {
                    AIGoal.Cancel(
                        replacement.Obj.Goal,
                        G.Ticks,
                        "Reserved for surgery"
                    );
                }
            }

            CARAUOperationTrackingSys.TrackQueued(decision);
            return Result(
                PreventativeImplantQueueDisposition.Queued,
                $"Preventative {targetPartName} surgery was queued.",
                decision,
                chance
            );
        }

        private static UpgradeDecision BuildInstallDecision(
          BodyPartSlot slot,
          ObjData replacement,
          int inventoryCount
        )
        {
            var upgradeSlot = new UpgradeSlot
            {
                Slot = slot,
                InstalledPart = null,
                BestPart = replacement.BodyPart,
                NeedsUpgrade = true
            };
            return new UpgradeDecision
            {
                ActionKind = UpgradeActionKind.InstallMissing,
                Type = UpgradeDecisionType.Missing,
                Slot = upgradeSlot,
                InstalledPart = null,
                InstalledQuality = 0f,
                InstalledTier = 0,
                TargetPart = replacement.BodyPart,
                InventoryPart = replacement,
                InventoryCount = inventoryCount,
                AvailableQuality =
                    replacement.BodyPartState?.Quality ??
                    replacement.Quality
            };
        }

        private static PreventativeImplantQueueResult Result(
          PreventativeImplantQueueDisposition disposition,
          string detail,
          UpgradeDecision decision = null,
          AutomaticRetryChanceResult chance = null
        )
        {
            return new PreventativeImplantQueueResult
            {
                Disposition = disposition,
                Detail = detail ?? string.Empty,
                Decision = decision,
                Chance = chance
            };
        }
    }
}
