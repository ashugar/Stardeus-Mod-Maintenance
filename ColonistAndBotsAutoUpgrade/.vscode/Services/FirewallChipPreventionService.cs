using ColonistAndBotsAutoUpgrade.Models;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.Creatures;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.Services
{
    /// <summary>
    /// Detects Core's active Hacked condition and requests one guarded native
    /// Firewall Chip installation per observed episode. The condition itself
    /// remains entirely Core-owned and will end when Core sees the installed
    /// working chip.
    /// </summary>
    public sealed class FirewallChipPreventionService
    {
        public const string FirewallChipPartId = "Implant/FirewallChip";
        public const string FirewallChipName = "Firewall Chip";

        private const float ScanIntervalSeconds = 1f;

        private readonly HashSet<uint> handledBeingIds = new();
        private readonly Dictionary<uint, string> lastResultByBeing = new();
        private float nextScanRealtime;

        public bool Refresh(GameState gameState)
        {
            if (!CARAUSettings.AutoInstallFirewallChipWhenHacked)
            {
                Reset();
                return false;
            }

            if (gameState == null || Time.unscaledTime < nextScanRealtime)
            {
                return false;
            }
            nextScanRealtime = Time.unscaledTime + ScanIntervalSeconds;

            var activeBeingIds = new HashSet<uint>();
            var queuedAny = false;
            var allBeings = gameState.Query.AllBeingData.Ask();
            if (allBeings == null)
            {
                return false;
            }

            foreach (var being in allBeings.Values)
            {
                if (!IsEligibleHackingTarget(being))
                {
                    continue;
                }

                activeBeingIds.Add(being.Id);
                if (handledBeingIds.Contains(being.Id))
                {
                    continue;
                }

                var result = PreventativeImplantService.TryQueue(
                    gameState,
                    being,
                    FirewallChipPartId,
                    FirewallChipName
                );
                if (result == null)
                {
                    continue;
                }

                LogResultChange(being, result);
                if (result.IsQueued)
                {
                    queuedAny = true;
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Queue,
                        $"Preventative Firewall Chip queued | " +
                        $"Being={being.Name ?? "unknown"} | " +
                        $"BeingId={being.Id} | " +
                        $"ObjId={result.Decision?.InventoryPart?.Id ?? 0} | " +
                        (result.Chance?.Describe(
                            CARAUSettings
                                .AutomaticRetryChanceThresholdPercent
                        ) ?? "Chance=unknown")
                    );
                }

                if (IsTerminalForEpisode(result.Disposition))
                {
                    handledBeingIds.Add(being.Id);
                }
            }

            handledBeingIds.RemoveWhere(id => !activeBeingIds.Contains(id));
            foreach (var staleId in lastResultByBeing.Keys
                         .Where(id => !activeBeingIds.Contains(id))
                         .ToList())
            {
                lastResultByBeing.Remove(staleId);
            }

            return queuedAny;
        }

        public void Reset()
        {
            handledBeingIds.Clear();
            lastResultByBeing.Clear();
            nextScanRealtime = 0f;
        }

        private static bool IsEligibleHackingTarget(BeingData being)
        {
            if (being?.Being == null ||
                being.Body?.IsAlive != true ||
                being.IsPlayer != true ||
                being.Species == null ||
                being.Species.IsBiological ||
                being.Conditions == null)
            {
                return false;
            }

            return being.Conditions.Values.Any(state =>
                state?.Condition != null &&
                !state.IsInactive &&
                !state.IsDisabled &&
                state.Condition.IdH == ConditionIdH.Hacked
            );
        }

        private static bool IsTerminalForEpisode(
          PreventativeImplantQueueDisposition disposition
        )
        {
            switch (disposition)
            {
                case PreventativeImplantQueueDisposition.Queued:
                case PreventativeImplantQueueDisposition.TargetOperationPending:
                case PreventativeImplantQueueDisposition.ImplantOccupied:
                case PreventativeImplantQueueDisposition.ImplantUnavailable:
                case PreventativeImplantQueueDisposition.OtherOperationPending:
                case PreventativeImplantQueueDisposition.TrackedOperationExists:
                case PreventativeImplantQueueDisposition.InvalidTarget:
                    return true;

                default:
                    // Inventory, chance, reservation, and registration state
                    // can improve while the Hacked condition is still active.
                    return false;
            }
        }

        private void LogResultChange(
          BeingData being,
          PreventativeImplantQueueResult result
        )
        {
            var summary = $"{result.Disposition}: {result.Detail}";
            if (lastResultByBeing.TryGetValue(being.Id, out var previous) &&
                string.Equals(previous, summary, StringComparison.Ordinal))
            {
                return;
            }

            lastResultByBeing[being.Id] = summary;
            CARAUDebugLog.Add(
                CARAUDebugCategory.Decision,
                $"Hacking prevention evaluated | " +
                $"Being={being.Name ?? "unknown"} | " +
                $"BeingId={being.Id} | Result={summary}"
            );
        }
    }
}
