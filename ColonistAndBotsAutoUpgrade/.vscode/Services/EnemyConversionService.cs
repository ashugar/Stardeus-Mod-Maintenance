using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Systems;
using Game.Components;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Creatures.Surgeries;
using Game.Systems.Items;
using Game.Systems.Mental;
using Game.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public static class EnemyConversionService
    {
        private const string ObeyChipPartId = "Implant/ObeyChip";

        public static bool IsHostileTarget(GameState state, BeingData target)
        {
            if (state == null || target?.Mind?.Faction == null)
            {
                return false;
            }

            return state.Sys.Diplomacy.IsHostileTowardsPlayer(
                target.EffectiveFaction
            );
        }

        public static EnemyConversionPlan Evaluate(
          GameState state,
          Being being,
          ISet<uint> allocatedPartIds = null
        )
        {
            var blocked = ValidateTarget(state, being);
            if (blocked != null)
            {
                return Unavailable(being, blocked);
            }

            var first = CARAUSettings.ConversionPriority ==
                EnemyConversionPriority.CoreSwapThenObeyChip
                    ? EnemyConversionRoute.CoreSwap
                    : EnemyConversionRoute.ObeyChip;
            var second = first == EnemyConversionRoute.ObeyChip
                ? EnemyConversionRoute.CoreSwap
                : EnemyConversionRoute.ObeyChip;

            var firstPlan = EvaluateRoute(
                state,
                being,
                first,
                allocatedPartIds
            );
            if (firstPlan.IsQueueable)
            {
                return firstPlan;
            }

            var secondPlan = EvaluateRoute(
                state,
                being,
                second,
                allocatedPartIds
            );
            if (secondPlan.IsQueueable)
            {
                return secondPlan;
            }

            return Unavailable(
                being,
                BuildUnavailableReason(firstPlan, secondPlan)
            );
        }

        public static List<EnemyConversionPlan> BuildPlans(
          GameState state,
          IEnumerable<Being> beings
        )
        {
            return BuildEvaluations(state, beings)
                .Where(plan => plan.IsQueueable)
                .ToList();
        }

        public static List<EnemyConversionPlan> BuildEvaluations(
          GameState state,
          IEnumerable<Being> beings
        )
        {
            var evaluations = new List<EnemyConversionPlan>();
            var allocated = new HashSet<uint>();
            if (beings == null)
            {
                return evaluations;
            }

            using (InventoryService.BeginBatchLookup(state))
            {
                var seenBeingIds = new HashSet<uint>();
                foreach (var being in beings)
                {
                    var beingId = being?.Data?.Id ?? 0;
                    if (beingId == 0 || !seenBeingIds.Add(beingId))
                    {
                        continue;
                    }

                    var plan = Evaluate(state, being, allocated);
                    evaluations.Add(plan);
                    if (plan.IsQueueable)
                    {
                        allocated.Add(plan.ReservedPartId);
                    }
                }
            }

            return evaluations;
        }

        public static EnemyConversionQueueResult TryQueueExact(
          GameState state,
          EnemyConversionPlan planned
        )
        {
            if (!TryRevalidateExact(state, planned, out var live, out var reason))
            {
                return QueueFailure(planned, reason);
            }

            var operation = new SurgicalOperation
            {
                TargetBeing = live.Target,
                TargetSlot = live.Slot,
                ReservedPart = live.ReservedPart,
                OpType = live.Slot.IsPartRemoved
                    ? SurgicalOpType.Install
                    : SurgicalOpType.Swap,
                RestrictedOperatorMindIds = live.Route ==
                    EnemyConversionRoute.CoreSwap && live.IncomingMind == null
                        ? GetPlayerOperatorMindIds(state)
                        : null
            };

            if (!live.ReservedPart.TryReserveForOperation(operation))
            {
                return QueueFailure(
                    live,
                    "The exact confirmed body part could not be reserved."
                );
            }

            if (!state.Query.RegisterSurgicalOperation.Ask(operation, true))
            {
                var nativeReason = operation.FailReason ??
                    "Stardeus rejected the conversion procedure.";
                if (live.ReservedPart.Reservation?.Surgery == operation)
                {
                    live.ReservedPart.TryUnreserveForOperation(operation);
                }
                return QueueFailure(live, nativeReason);
            }

            if (live.ReservedPart.Obj != null)
            {
                live.ReservedPart.Obj.StorableObj.IsForbidden = true;
                if (live.ReservedPart.Obj.Goal != null)
                {
                    AIGoal.Cancel(
                        live.ReservedPart.Obj.Goal,
                        G.Ticks,
                        "Reserved for enemy conversion"
                    );
                }
            }

            CARAUOperationTrackingSys.TrackQueuedEnemyConversion(
                live,
                operation
            );
            CARAUDebugLog.Add(
                CARAUDebugCategory.Queue,
                "Enemy conversion registered | " +
                $"Being={live.Target?.Name ?? "unknown"} | " +
                $"Route={live.Route} | Slot={live.Slot?.NameT ?? "unknown"} | " +
                $"Part={live.ReservedPart?.BodyPart?.NameT ?? "unknown"} | " +
                $"ObjId={live.ReservedPartId}"
            );

            return new EnemyConversionQueueResult
            {
                Success = true,
                Plan = live,
                Operation = operation,
                Message = $"Queued {live.RouteName} for {live.Target.Name}."
            };
        }

        public static bool TryRevalidateExact(
          GameState state,
          EnemyConversionPlan planned,
          out EnemyConversionPlan live,
          out string reason
        )
        {
            live = null;
            reason = null;
            if (planned?.Being == null || !planned.IsQueueable)
            {
                reason = "The confirmed conversion mapping is incomplete.";
                return false;
            }

            var blocked = ValidateTarget(state, planned.Being);
            if (blocked != null)
            {
                reason = blocked;
                return false;
            }

            live = EvaluateRoute(
                state,
                planned.Being,
                planned.Route,
                allocatedPartIds: null,
                requiredPartId: planned.ReservedPartId
            );
            if (!live.IsQueueable)
            {
                reason = live.Reason ??
                    "The exact confirmed body part is no longer eligible.";
                return false;
            }

            if (!planned.SameMappingAs(live))
            {
                reason = "The target, route, slot, part, or mind identity " +
                    "changed after confirmation.";
                return false;
            }

            return true;
        }

        public static bool IsExactSuccessfulResult(
          GameState state,
          CARAUTrackedOperation tracked,
          out string detail
        )
        {
            detail = null;
            if (state == null || tracked == null ||
                tracked.Purpose != CARAUOperationPurpose.EnemyConversion)
            {
                return false;
            }

            var target = state.Query.FindBeingData.Ask(tracked.BeingId);
            if (target?.Body == null || !target.Body.IsAlive)
            {
                detail = "The target chassis is not alive.";
                return false;
            }

            if (tracked.ConversionRoute == EnemyConversionRoute.ObeyChip)
            {
                var slot = FindSlot(target, tracked.SlotId);
                var sameMind = tracked.ConversionOriginalMindId == 0 ||
                    target.Mind?.Id == tracked.ConversionOriginalMindId;
                var exactChip = slot?.InstalledDef?.Id == ObeyChipPartId &&
                    slot.IsUsableWithParent && target.Body.HasWorkingObeyChip;
                if (sameMind && exactChip && target.EffectiveFaction.IsPlayer)
                {
                    detail = "The Obey Chip is working and the original mind " +
                        "is now controlled by the Player faction.";
                    return true;
                }

                detail = "Obey conversion postconditions were not all met.";
                return false;
            }

            if (tracked.ConversionRoute != EnemyConversionRoute.CoreSwap)
            {
                detail = "The saved conversion route is unknown.";
                return false;
            }

            var installedMindId = tracked.ConversionObservedIncomingMindId != 0
                ? tracked.ConversionObservedIncomingMindId
                : tracked.ConversionIncomingMindId;
            var incomingMatches = installedMindId == 0
                ? target.Mind != null &&
                  target.Mind.Id != tracked.ConversionOriginalMindId
                : target.Mind?.Id == installedMindId;
            var outgoingCore = FindObj(state, tracked.ConversionOutgoingCorePartId);
            var outgoingMindMatches = outgoingCore?.Mind != null &&
                outgoingCore.Mind.Id == tracked.ConversionOriginalMindId;
            var exactInstallObserved =
                tracked.ConversionInstallObservedPartId ==
                tracked.ReservedPartId;

            if (incomingMatches && outgoingMindMatches &&
                exactInstallObserved && target.EffectiveFaction.IsPlayer)
            {
                detail = "The confirmed Core supplied the Player mind and the " +
                    "original hostile mind moved into the extracted Core.";
                return true;
            }

            detail = "Core conversion postconditions were not all met.";
            return false;
        }

        private static EnemyConversionPlan EvaluateRoute(
          GameState state,
          Being being,
          EnemyConversionRoute route,
          ISet<uint> allocatedPartIds,
          uint requiredPartId = 0
        )
        {
            return route == EnemyConversionRoute.ObeyChip
                ? EvaluateObeyChip(
                    state,
                    being,
                    allocatedPartIds,
                    requiredPartId
                  )
                : route == EnemyConversionRoute.CoreSwap
                    ? EvaluateCoreSwap(
                        state,
                        being,
                        allocatedPartIds,
                        requiredPartId
                      )
                    : Unavailable(being, "Unknown conversion route.");
        }

        private static EnemyConversionPlan EvaluateObeyChip(
          GameState state,
          Being being,
          ISet<uint> allocatedPartIds,
          uint requiredPartId
        )
        {
            if (!CARAUSettings.AllowEnemyConversionObeyChip)
            {
                return Unavailable(being, "Obey Chip conversion is disabled.");
            }

            var target = being.Data;
            var slot = target.Body.ImplantSlot;
            if (slot == null)
            {
                return Unavailable(being, "No Implant slot is available.");
            }
            if (!slot.IsPartRemoved)
            {
                return Unavailable(
                    being,
                    "The Implant slot is already occupied; CARAU will not " +
                    "replace an existing implant."
                );
            }
            if (slot.IsDestroyed || slot.BeyondRepairAncestor != null)
            {
                return Unavailable(being, "The Implant slot is unusable.");
            }
            if (target.Species.IsBiological && target.Species.IsHumanoid &&
                !Game.A.HasAsimovOverride)
            {
                return Unavailable(being, T.AsimovViolation1);
            }

            var obeyChip = BodyPartDef.Get(ObeyChipPartId);
            if (obeyChip == null ||
                !obeyChip.IsCompatibleWith(slot, out _))
            {
                return Unavailable(
                    being,
                    "An Obey Chip is not compatible with this Implant slot."
                );
            }

            var parts = InventoryService.FindAvailableObjParts(
                state,
                obeyChip,
                excludedPartIds: allocatedPartIds
            );
            parts.Sort(CompareAvailableParts);
            var part = SelectRequiredOrFirst(parts, requiredPartId);
            if (part == null)
            {
                return Unavailable(
                    being,
                    requiredPartId == 0
                        ? "No available Obey Chip was found."
                        : "The exact confirmed Obey Chip is no longer available."
                );
            }

            return Available(being, EnemyConversionRoute.ObeyChip, slot, part);
        }

        private static EnemyConversionPlan EvaluateCoreSwap(
          GameState state,
          Being being,
          ISet<uint> allocatedPartIds,
          uint requiredPartId
        )
        {
            if (!CARAUSettings.AllowEnemyConversionCoreSwap)
            {
                return Unavailable(being, "Core Swap conversion is disabled.");
            }

            var target = being.Data;
            if (target.Species.IsBiological)
            {
                return Unavailable(being, "Core Swap requires a robotic body.");
            }

            var slot = target.Body.MindSlot;
            if (slot == null || slot.Type?.IdH != BodyPartSlotIdH.Core ||
                slot.IsPartRemoved || !slot.HoldsMind)
            {
                return Unavailable(being, "No installed Core is available.");
            }
            if (slot.IsDestroyed || slot.State?.IsWelded == true ||
                !slot.CanRemovePart(false) || slot.BeyondRepairAncestor != null)
            {
                return Unavailable(
                    being,
                    "The installed Core is destroyed, welded, or not removable."
                );
            }

            var candidates = new List<ObjData>();
            var seen = new HashSet<uint>();
            foreach (var partDef in UpgradeAnalyzer.GetCompatibleParts(slot))
            {
                if (partDef?.HoldsMind != true)
                {
                    continue;
                }
                var parts = InventoryService.FindAvailableObjParts(
                    state,
                    partDef,
                    excludedPartIds: allocatedPartIds
                );
                foreach (var part in parts)
                {
                    if (part != null && seen.Add(part.Id) &&
                        IsAllowedIncomingCoreMind(part.Mind))
                    {
                        candidates.Add(part);
                    }
                }
            }

            candidates.Sort(CompareCoreCandidates);
            var selected = SelectRequiredOrFirst(candidates, requiredPartId);
            if (selected == null)
            {
                return Unavailable(
                    being,
                    requiredPartId == 0
                        ? "No available compatible Player or Blank Core was found."
                        : "The exact confirmed Core is no longer available."
                );
            }

            if (selected.Mind == null &&
                GetPlayerOperatorMindIds(state).Length == 0)
            {
                return Unavailable(
                    being,
                    "A mindless Core needs a Player-faction surgical operator " +
                    "so the generated mind cannot inherit another faction."
                );
            }

            return Available(being, EnemyConversionRoute.CoreSwap, slot, selected);
        }

        private static string ValidateTarget(GameState state, Being being)
        {
            if (!CARAUSettings.AllowEnemyConversion)
            {
                return "Enemy Conversion is disabled in CARAU Settings.";
            }
            if (!CARAUSettings.AllowEnemyConversionObeyChip &&
                !CARAUSettings.AllowEnemyConversionCoreSwap)
            {
                return "Both Enemy Conversion routes are disabled.";
            }
            if (state == null || being?.Data?.Body == null)
            {
                return "The target Being is unavailable.";
            }

            var target = being.Data;
            if (!being.IsActive || being.IsDead || !target.Body.IsAlive ||
                target.HasStateFlag(BeingState.Dead))
            {
                return "Only active living enemies can be converted.";
            }
            if (target.HasStateFlag(BeingState.Stored) ||
                target.HasStateFlag(BeingState.Expedition) ||
                target.HasStateFlag(BeingState.InDevice) ||
                target.Body.HasFlag(BodyFlags.Missing))
            {
                return "Stored, expedition, in-device, or missing bodies " +
                    "cannot be converted.";
            }
            if (!target.Body.IsIncapacitated)
            {
                return "The enemy is no longer incapacitated.";
            }
            if (target.HasCondition(ConditionIdH.Hacked))
            {
                return "The target is already being hacked.";
            }
            if (target.Mind?.Faction?.IdH == FactionIdH.AntiPlayerTemp)
            {
                return "Temporarily anti-player minds are not conversion targets.";
            }
            if (!IsHostileTarget(state, target))
            {
                return "The target is not hostile to the Player faction.";
            }
            if (CARAUOperationTrackingSys.IsEnemyConversionPending(target.Id))
            {
                return "CARAU is already tracking an Enemy Conversion for " +
                    "this target.";
            }
            if (HasAnySurgery(state, target))
            {
                return "The target already has a registered surgical procedure.";
            }
            return null;
        }

        private static bool HasAnySurgery(GameState state, BeingData target)
        {
            var operations = state.Query.GetAllSurgeries.Ask();
            return operations != null && operations.Any(operation =>
                operation?.TargetBeing == target
            );
        }

        private static bool IsAllowedIncomingCoreMind(Mind mind)
        {
            if (mind == null)
            {
                return true;
            }
            var faction = mind.Faction;
            return faction == null || faction.IsBlank || faction.IsPlayer;
        }

        private static int CompareCoreCandidates(ObjData left, ObjData right)
        {
            var category = CoreMindCategory(left).CompareTo(
                CoreMindCategory(right)
            );
            if (category != 0)
            {
                return category;
            }
            var quality = (right.BodyPartState?.Quality ?? 0f).CompareTo(
                left.BodyPartState?.Quality ?? 0f
            );
            if (quality != 0)
            {
                return quality;
            }
            var condition = (right.BodyPartState?.Condition ?? 0f).CompareTo(
                left.BodyPartState?.Condition ?? 0f
            );
            return condition != 0
                ? condition
                : left.Id.CompareTo(right.Id);
        }

        private static int CompareAvailableParts(ObjData left, ObjData right)
        {
            var quality = (right?.BodyPartState?.Quality ?? 0f).CompareTo(
                left?.BodyPartState?.Quality ?? 0f
            );
            if (quality != 0)
            {
                return quality;
            }
            var condition =
                (right?.BodyPartState?.Condition ?? 0f).CompareTo(
                    left?.BodyPartState?.Condition ?? 0f
                );
            return condition != 0
                ? condition
                : (left?.Id ?? 0).CompareTo(right?.Id ?? 0);
        }

        private static int CoreMindCategory(ObjData part)
        {
            if (part?.Mind?.Faction?.IsPlayer == true)
            {
                return 0;
            }
            return part?.Mind != null ? 1 : 2;
        }

        private static ObjData SelectRequiredOrFirst(
          List<ObjData> candidates,
          uint requiredPartId
        )
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }
            return requiredPartId == 0
                ? candidates[0]
                : candidates.FirstOrDefault(part => part.Id == requiredPartId);
        }

        private static uint[] GetPlayerOperatorMindIds(GameState state)
        {
            var allBeings = state?.Query?.AllBeingData.Ask();
            if (allBeings?.Values == null)
            {
                return Array.Empty<uint>();
            }
            return allBeings.Values
                .Where(data => data?.Mind != null && data.Mind.Id != 0 &&
                    data.Mind.Faction?.IsPlayer == true)
                .Select(data => data.Mind.Id)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }

        private static BodyPartSlot FindSlot(BeingData target, string slotId)
        {
            return target?.Body?.Slots?.FirstOrDefault(slot =>
                slot != null &&
                string.Equals(slot.Id, slotId, StringComparison.Ordinal)
            );
        }

        private static ObjData FindObj(GameState state, uint id)
        {
            if (id == 0)
            {
                return null;
            }
            var all = state?.Query.GetAllObjs.Ask();
            return all != null && all.TryGetValue(id, out var obj)
                ? obj
                : null;
        }

        private static EnemyConversionPlan Available(
          Being being,
          EnemyConversionRoute route,
          BodyPartSlot slot,
          ObjData part
        )
        {
            return new EnemyConversionPlan
            {
                Being = being,
                Route = route,
                Slot = slot,
                ReservedPart = part,
                OriginalMind = being?.Data?.Mind
            };
        }

        private static EnemyConversionPlan Unavailable(
          Being being,
          string reason
        )
        {
            return new EnemyConversionPlan
            {
                Being = being,
                Route = EnemyConversionRoute.None,
                Reason = reason
            };
        }

        private static string BuildUnavailableReason(
          EnemyConversionPlan first,
          EnemyConversionPlan second
        )
        {
            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(first?.Reason))
            {
                reasons.Add(first.Reason);
            }
            if (!string.IsNullOrWhiteSpace(second?.Reason) &&
                !reasons.Contains(second.Reason))
            {
                reasons.Add(second.Reason);
            }
            return reasons.Count == 0
                ? "No enabled conversion route is currently available."
                : string.Join(" ", reasons);
        }

        private static EnemyConversionQueueResult QueueFailure(
          EnemyConversionPlan plan,
          string message
        )
        {
            return new EnemyConversionQueueResult
            {
                Success = false,
                Plan = plan,
                Message = message ?? "Enemy conversion could not be queued."
            };
        }
    }
}
