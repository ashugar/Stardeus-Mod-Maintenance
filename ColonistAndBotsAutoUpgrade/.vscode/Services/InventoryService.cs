using Game.Data;
using ColonistAndBotsAutoUpgrade.Models;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures.Surgeries;
using Game.Systems.Items;
using System;
using System.Collections.Generic;
using ColonistAndBotsAutoUpgrade.Systems;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public static class InventoryService
    {
        private static GameState batchGameState;
        private static Dictionary<Def, List<ObjData>> batchObjectsByDef;
        private static Dictionary<uint, SurgicalOperation>
          batchSurgeriesByPartId;
        private static HashSet<uint> batchDuplicateSurgeryPartIds;
        private static int batchLookupDepth;

        /// <summary>
        /// Captures one definition-indexed view of persistent body parts for a
        /// batch operation. Dynamic availability, reservation, state,
        /// allocation, and mind checks still run for every decision.
        /// </summary>
        public static IDisposable BeginBatchLookup(GameState gameState)
        {
            if (batchLookupDepth == 0)
            {
                batchGameState = gameState;
                batchObjectsByDef = BuildBodyPartIndex(gameState);
                batchSurgeriesByPartId = BuildSurgeryIndex(
                    gameState,
                    out batchDuplicateSurgeryPartIds
                );
            }

            batchLookupDepth++;
            return new BatchLookupScope();
        }

        public static int CountPart(GameState gameState, BodyPartDef partDef)
        {
            if (gameState == null || partDef == null || partDef.Def == null)
            {
                return 0;
            }

            var count = gameState.Sys.Inventory.CountOf(partDef.Def);

            return count;
        }

        public static bool HasPart(GameState gameState, BodyPartDef partDef)
        {
            return CountPart(gameState, partDef) > 0;
        }

        public static List<ObjData> FindAvailableObjParts(
          GameState gameState,
          BodyPartDef partDef,
          bool logHostileMindExclusions = false,
          ISet<uint> excludedPartIds = null
        )
        {
            return EvaluateObjParts(
                gameState,
                partDef,
                logHostileMindExclusions,
                excludedPartIds
            ).AvailableParts;
        }

        public static InventoryCandidateReport EvaluateObjParts(
          GameState gameState,
          BodyPartDef partDef,
          bool logHostileMindExclusions = false,
          ISet<uint> excludedPartIds = null,
          float? minimumQuality = null
        )
        {
            var report = new InventoryCandidateReport();
            var candidatesAlreadySorted = false;

            if (gameState == null || partDef == null)
            {
                return report;
            }

            IEnumerable<ObjData> candidates;
            if (partDef.Def != null &&
                ReferenceEquals(gameState, batchGameState) &&
                batchObjectsByDef != null)
            {
                if (!batchObjectsByDef.TryGetValue(
                    partDef.Def,
                    out var indexedCandidates))
                {
                    return report;
                }

                candidates = indexedCandidates;
                candidatesAlreadySorted = true;
            }
            else
            {
                var allObjs = gameState.Query.GetAllObjs.Ask();
                if (allObjs == null)
                {
                    return report;
                }

                candidates = allObjs.Values;
            }

            var surgeryIndex = GetSurgeryIndex(
                gameState,
                out var duplicateSurgeryPartIds
            );

            foreach (var objData in candidates)
            {
                if (objData == null)
                {
                    continue;
                }

                if (objData.Def != partDef.Def)
                {
                    continue;
                }

                report.Summary.TotalMatchingObjects++;

                if (minimumQuality.HasValue &&
                    objData.BodyPartState != null &&
                    objData.BodyPartState.Quality < minimumQuality.Value)
                {
                    report.Summary.BelowMinimumQuality++;
                    continue;
                }

                report.Summary.EligibleMatchingObjects++;

                if (excludedPartIds != null &&
                    excludedPartIds.Contains(objData.Id))
                {
                    report.Summary.AllocatedEarlierInBatch++;
                    continue;
                }

                surgeryIndex.TryGetValue(
                    objData.Id,
                    out var registeredSurgery
                );
                var reservation = objData.Reservation;

                if (registeredSurgery != null)
                {
                    var hasDuplicateOperations =
                        duplicateSurgeryPartIds.Contains(objData.Id);
                    if (hasDuplicateOperations ||
                        !ReservationSupportsSurgery(
                            reservation,
                            registeredSurgery
                        ))
                    {
                        report.Summary.ReservationInconsistencies++;
                        AddReservationExample(
                            report.Summary,
                            objData,
                            ReservationOwnerKind.Inconsistent,
                            registeredSurgery,
                            reservation?.Goal,
                            hasDuplicateOperations
                                ? ReservationMismatchKind
                                    .DuplicateRegisteredOperations
                                : ClassifyRegisteredOperationMismatch(
                                    reservation,
                                    registeredSurgery
                                  )
                        );
                        continue;
                    }

                    var isCARAU =
                        CARAUOperationTrackingSys.IsOperationTracked(
                            registeredSurgery
                        );
                    if (isCARAU)
                    {
                        report.Summary.ReservedByCARAU++;
                    }
                    else
                    {
                        report.Summary.ReservedBySurgery++;
                    }

                    AddReservationExample(
                        report.Summary,
                        objData,
                        isCARAU
                            ? ReservationOwnerKind.CARAUSurgery
                            : ReservationOwnerKind.Surgery,
                        registeredSurgery,
                        reservation?.Goal
                    );
                    continue;
                }

                if (reservation != null)
                {
                    if (reservation.Surgery != null ||
                        reservation.IsBlank ||
                        reservation.LoadGoalId != 0 ||
                        reservation.LoadSurgeryId != 0)
                    {
                        report.Summary.ReservationInconsistencies++;
                        AddReservationExample(
                            report.Summary,
                            objData,
                            ReservationOwnerKind.Inconsistent,
                            reservation.Surgery,
                            reservation.Goal,
                            ClassifyUnregisteredReservationMismatch(
                                reservation
                            )
                        );
                        continue;
                    }

                    if (reservation.Goal != null)
                    {
                        var goal = reservation.Goal;
                        if (!AIGoal.IsActive(goal) ||
                            IsSurgeryLinkedGoal(goal))
                        {
                            report.Summary.ReservationInconsistencies++;
                            AddReservationExample(
                                report.Summary,
                                objData,
                                ReservationOwnerKind.Inconsistent,
                                null,
                                goal,
                                !AIGoal.IsActive(goal)
                                    ? ReservationMismatchKind
                                        .InactiveGoalPendingCleanup
                                    : ReservationMismatchKind
                                        .SurgeryGoalWithoutRegisteredOperation
                            );
                            continue;
                        }

                        report.Summary.ReservedByActiveGoal++;
                        AddReservationExample(
                            report.Summary,
                            objData,
                            ReservationOwnerKind.ActiveGoal,
                            null,
                            goal
                        );
                        continue;
                    }

                    report.Summary.ReservationInconsistencies++;
                    AddReservationExample(
                        report.Summary,
                        objData,
                        ReservationOwnerKind.Inconsistent,
                        null,
                        null,
                        ReservationMismatchKind.ReservationWithoutOwner
                    );
                    continue;
                }

                if (objData.State != ObjState.Stored && objData.State != ObjState.Active)
                {
                    report.Summary.UnsupportedState++;
                    continue;
                }

                if (objData.BodyPartState == null)
                {
                    report.Summary.UnsupportedState++;
                    continue;
                }

                if (!objData.IsAvailable)
                {
                    report.Summary.UnavailableObjects++;
                    continue;
                }

                if (HasHostileMind(objData))
                {
                    report.Summary.HostileMindExcluded++;
                    continue;
                }

                report.AvailableParts.Add(objData);
                report.Summary.AvailableObjects++;
            }

            if (logHostileMindExclusions &&
                report.Summary.HostileMindExcluded > 0 &&
                CARAUSettings.IsDebugCategoryEnabled(CARAUDebugCategory.Inventory))
            {
                var debugMessage =
                  $"Hostile mind parts excluded | Part={partDef.NameT} | " +
                  $"Count={report.Summary.HostileMindExcluded}";

                CARAUDebugLog.Add(CARAUDebugCategory.Inventory, debugMessage);
            }

            if (!candidatesAlreadySorted)
            {
                report.AvailableParts.Sort(
                    CompareBodyPartQualityAndCondition
                );
            }

            return report;
        }

        private static Dictionary<uint, SurgicalOperation> GetSurgeryIndex(
          GameState gameState,
          out HashSet<uint> duplicatePartIds
        )
        {
            if (ReferenceEquals(gameState, batchGameState) &&
                batchSurgeriesByPartId != null)
            {
                duplicatePartIds = batchDuplicateSurgeryPartIds;
                return batchSurgeriesByPartId;
            }

            return BuildSurgeryIndex(gameState, out duplicatePartIds);
        }

        private static Dictionary<uint, SurgicalOperation> BuildSurgeryIndex(
          GameState gameState,
          out HashSet<uint> duplicatePartIds
        )
        {
            var byPartId = new Dictionary<uint, SurgicalOperation>();
            duplicatePartIds = new HashSet<uint>();
            var surgeries = gameState?.Query.GetAllSurgeries.Ask();
            if (surgeries == null)
            {
                return byPartId;
            }

            for (var index = 0; index < surgeries.Count; index++)
            {
                var operation = surgeries[index];
                var partId = operation?.ReservedPart?.Id ?? 0;
                if (partId == 0)
                {
                    continue;
                }

                if (byPartId.ContainsKey(partId))
                {
                    duplicatePartIds.Add(partId);
                    continue;
                }

                byPartId.Add(partId, operation);
            }

            return byPartId;
        }

        /// <summary>
        /// Recognizes both native ownership forms used by Stardeus surgery.
        /// A queued operation initially owns the part through Reservation.Surgery;
        /// once Core creates the active procedure goal, ObjData.TryReserve replaces
        /// that reservation with a goal carrying the same SurgeryId.
        /// </summary>
        internal static bool ReservationSupportsSurgery(
          ObjReservation reservation,
          SurgicalOperation surgery
        )
        {
            if (reservation == null || surgery == null)
            {
                return false;
            }

            if (ReferenceEquals(reservation.Surgery, surgery))
            {
                return true;
            }

            var goal = reservation.Goal;
            return goal != null &&
                   AIGoal.IsActive(goal) &&
                   goal.TryGetVar("SurgeryId", out int surgeryId) &&
                   surgeryId == surgery.Id;
        }

        private static bool IsSurgeryLinkedGoal(AIGoal goal)
        {
            return goal != null &&
                   goal.TryGetVar("SurgeryId", out int surgeryId) &&
                   surgeryId != 0;
        }

        private static ReservationMismatchKind
          ClassifyRegisteredOperationMismatch(
            ObjReservation reservation,
            SurgicalOperation registeredSurgery
          )
        {
            if (reservation == null)
            {
                return ReservationMismatchKind
                    .RegisteredOperationMissingReservation;
            }

            if (reservation.Surgery != null)
            {
                return ReservationMismatchKind
                    .RegisteredOperationOwnedByDifferentSurgery;
            }

            var goal = reservation.Goal;
            if (goal != null)
            {
                if (!AIGoal.IsActive(goal))
                {
                    return ReservationMismatchKind
                        .InactiveGoalPendingCleanup;
                }

                if (!goal.TryGetVar("SurgeryId", out int goalSurgeryId) ||
                    goalSurgeryId == 0)
                {
                    return ReservationMismatchKind
                        .RegisteredOperationGoalMissingSurgeryLink;
                }

                if (registeredSurgery == null ||
                    goalSurgeryId != registeredSurgery.Id)
                {
                    return ReservationMismatchKind
                        .RegisteredOperationGoalReferencesDifferentSurgery;
                }
            }

            if (reservation.IsBlank)
            {
                return ReservationMismatchKind.BlankReservationMarker;
            }

            if (reservation.LoadGoalId != 0 ||
                reservation.LoadSurgeryId != 0)
            {
                return ReservationMismatchKind.UnresolvedLoadReservation;
            }

            return ReservationMismatchKind.ReservationWithoutOwner;
        }

        private static ReservationMismatchKind
          ClassifyUnregisteredReservationMismatch(
            ObjReservation reservation
          )
        {
            if (reservation?.Surgery != null)
            {
                return ReservationMismatchKind
                    .ReservationReferencesUnregisteredSurgery;
            }

            if (reservation?.IsBlank == true)
            {
                return ReservationMismatchKind.BlankReservationMarker;
            }

            if (reservation != null &&
                (reservation.LoadGoalId != 0 ||
                 reservation.LoadSurgeryId != 0))
            {
                return ReservationMismatchKind.UnresolvedLoadReservation;
            }

            var goal = reservation?.Goal;
            if (goal != null)
            {
                return !AIGoal.IsActive(goal)
                    ? ReservationMismatchKind.InactiveGoalPendingCleanup
                    : ReservationMismatchKind
                        .SurgeryGoalWithoutRegisteredOperation;
            }

            return ReservationMismatchKind.ReservationWithoutOwner;
        }

        private static void AddReservationExample(
          UpgradeInventorySummary summary,
          ObjData part,
          ReservationOwnerKind ownerKind,
          SurgicalOperation surgery,
          AIGoal goal,
          ReservationMismatchKind mismatchKind =
            ReservationMismatchKind.None
        )
        {
            if (summary == null || summary.ReservationExamples.Count >= 3)
            {
                return;
            }

            summary.ReservationExamples.Add(new ReservationExplanation
            {
                OwnerKind = ownerKind,
                MismatchKind = mismatchKind,
                PartObjectId = part?.Id ?? 0,
                PartName = part?.Def?.NameT ?? "Unknown part",
                BeingName = surgery?.TargetBeing?.Name,
                SlotName = surgery?.TargetSlot?.NameT,
                GoalName = goal?.Name,
                AgentName = goal?.Agent?.BeingData?.Name,
                OperationType = surgery == null
                    ? (SurgicalOpType?)null
                    : surgery.OpType,
                OperationState = surgery == null
                    ? (SurgicalOpState?)null
                    : surgery.State.Value,
                RegisteredSurgeryId = mismatchKind ==
                    ReservationMismatchKind
                        .ReservationReferencesUnregisteredSurgery
                    ? 0
                    : surgery?.Id ?? 0,
                ReservationSurgeryId = part?.Reservation?.Surgery?.Id ?? 0,
                GoalSurgeryId = TryGetGoalSurgeryId(goal),
                LoadGoalId = part?.Reservation == null ||
                    part.Reservation.LoadGoalId == 0
                    ? null
                    : part.Reservation.LoadGoalId.ToString(),
                LoadSurgeryId = part?.Reservation == null ||
                    part.Reservation.LoadSurgeryId == 0
                    ? null
                    : part.Reservation.LoadSurgeryId.ToString(),
                GoalIsActive = goal != null && AIGoal.IsActive(goal),
                GoalState = goal == null ? null : goal.State.ToString()
            });
        }

        private static int TryGetGoalSurgeryId(AIGoal goal)
        {
            return goal != null &&
                   goal.TryGetVar("SurgeryId", out int surgeryId)
                ? surgeryId
                : 0;
        }

        private static Dictionary<Def, List<ObjData>> BuildBodyPartIndex(
          GameState gameState
        )
        {
            var byDef = new Dictionary<Def, List<ObjData>>();
            var allObjs = gameState?.Query.GetAllObjs.Ask();
            if (allObjs == null)
            {
                return byDef;
            }

            foreach (var objData in allObjs.Values)
            {
                if (objData?.Def == null || objData.BodyPartState == null)
                {
                    continue;
                }

                if (!byDef.TryGetValue(objData.Def, out var matchingParts))
                {
                    matchingParts = new List<ObjData>();
                    byDef.Add(objData.Def, matchingParts);
                }

                matchingParts.Add(objData);
            }

            foreach (var matchingParts in byDef.Values)
            {
                matchingParts.Sort(CompareBodyPartQualityAndCondition);
            }

            return byDef;
        }

        private static int CompareBodyPartQualityAndCondition(
          ObjData a,
          ObjData b
        )
        {
            var qualityCompare = b.BodyPartState.Quality.CompareTo(
              a.BodyPartState.Quality
            );

            if (qualityCompare != 0)
            {
                return qualityCompare;
            }

            return b.BodyPartState.Condition.CompareTo(
              a.BodyPartState.Condition
            );
        }

        private static void EndBatchLookup()
        {
            if (batchLookupDepth <= 0)
            {
                return;
            }

            batchLookupDepth--;
            if (batchLookupDepth == 0)
            {
                batchGameState = null;
                batchObjectsByDef = null;
                batchSurgeriesByPartId = null;
                batchDuplicateSurgeryPartIds = null;
            }
        }

        private sealed class BatchLookupScope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                EndBatchLookup();
            }
        }

        public static ObjData FindBestAvailableObjPart(
          GameState gameState,
          BodyPartDef partDef
        )
        {
            var parts = FindAvailableObjParts(gameState, partDef);

            if (parts.Count == 0)
            {
                return null;
            }

            return parts[0];
        }

        private static bool HasHostileMind(ObjData part)
        {
            var faction = part?.Mind?.Faction;

            return faction != null &&
                   !faction.IsPlayer &&
                   !faction.IsBlank;
        }
    }

}
