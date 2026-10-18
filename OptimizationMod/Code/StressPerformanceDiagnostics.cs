using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Game.Constants;
using Game.Systems.AI;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Temporary, low-frequency instrumentation for a heavily damaged ship with
    /// a large construction queue. Only complete system methods are timed; no
    /// per-agent, per-goal, or per-object probes are installed.
    /// </summary>
    internal static class StressPerformanceDiagnostics {
        internal const int ExpectedProbeCount = 72;

        private static readonly Dictionary<MethodBase, ProfileTarget> Targets =
            new Dictionary<MethodBase, ProfileTarget>();
        private static readonly List<ProfileTarget> CorrelatedOnTickTargets =
            new List<ProfileTarget>();
        private static readonly List<SpikeRecord> WorstSpikes =
            new List<SpikeRecord>(MaxRetainedSpikes);
        private const int MaxRetainedSpikes = 12;
        private const int MaxSpikeContributors = 6;
        private static readonly long FourMsTicks =
            (long) (Stopwatch.Frequency * 0.004d);
        private static readonly long EightMsTicks =
            (long) (Stopwatch.Frequency * 0.008d);
        private static readonly long SixteenMsTicks =
            (long) (Stopwatch.Frequency * 0.016d);
        private static readonly long ThirtyThreeMsTicks =
            (long) (Stopwatch.Frequency * 0.033d);
        private static readonly long SpikeThresholdTicks =
            (long) (Stopwatch.Frequency * 0.033d);

        private static long sessionStartedAt;
        private static int maxAiGoals;
        private static int maxAiAgents;
        private static long[] currentOnTickElapsed;
        private static bool fullTickActive;
        private static long fullTickOrdinal;
        private static long qualifyingSpikeCount;

        internal static int Install(Harmony harmony) {
            sessionStartedAt = Stopwatch.GetTimestamp();
            int installed = 0;

            installed += Install(harmony, "KL.Clock.Clock", "IncrementTick",
                Type.EmptyTypes, "Clock.FullSimulationTick");
            installed += Install(harmony, "KL.Clock.Clock", "ExecuteAfter",
                Type.EmptyTypes, "Clock.ExecuteAfter");
            installed += Install(harmony, "KL.Clock.Clock", "InvokeEveryTickOrFrame",
                new[] { typeof(long) }, "Clock.EveryTickOrFrame");
            installed += Install(harmony, "KL.Clock.Clock", "DoOnNextTick",
                Type.EmptyTypes, "Clock.OnNextTick");
            installed += Install(harmony, "KL.Clock.Clock", "DoTickProgress",
                new[] { typeof(float), typeof(bool) }, "Clock.DoTickProgress");
            installed += Install(harmony, "Game.Components.ComponentState",
                "OnTick", new[] { typeof(long) }, "Components.OnTick");
            installed += Install(harmony, "Game.Data.GameState", "OnTick",
                new[] { typeof(long) }, "GameState.OnTick");
            installed += Install(harmony, "Game.Systems.AI.AISys", "OnTick",
                new[] { typeof(long) }, "AI.OnTick", true);
            installed += Install(harmony, "Game.Systems.AI.AIAuction", "Run",
                null, "AI.AuctionRun");
            installed += Install(harmony, "Game.Systems.AI.AINeedsSys", "OnTick",
                new[] { typeof(long) }, "AINeeds.OnTick");
            installed += Install(harmony,
                "Game.Systems.Conditions.ConditionsSys", "OnTick",
                new[] { typeof(long) }, "Conditions.OnTick");
            installed += Install(harmony, "Game.Systems.Creatures.BeingsSys", "OnTick",
                new[] { typeof(long) }, "Beings.OnTick");
            installed += Install(harmony, "Game.Systems.Items.ObjsSys", "OnTick",
                new[] { typeof(long) }, "Objects.OnTick");
            installed += Install(harmony,
                "Game.Systems.Inventory.InventorySys", "OnTick",
                new[] { typeof(long) }, "Inventory.OnTick");
            installed += Install(harmony, "Game.Systems.Crafting.CraftingSys", "OnTick",
                new[] { typeof(long) }, "Crafting.OnTick");
            installed += Install(harmony,
                "Game.Systems.Electricity.ElectricitySys", "OnTick",
                new[] { typeof(long) }, "Electricity.OnTick");
            installed += Install(harmony,
                "Game.Systems.Electricity.ElectricitySys", "EveryFrame",
                Type.EmptyTypes, "Electricity.EveryFrame");
            installed += Install(harmony, "Game.Systems.Atmo.AtmoSys", "OnTick",
                new[] { typeof(long) }, "Atmosphere.OnTick");
            installed += Install(harmony, "Game.Systems.Atmo.AtmoSys", "EveryFrame",
                Type.EmptyTypes, "Atmosphere.EveryFrame");
            installed += Install(harmony, "Game.Systems.Firex.FirexSys", "OnTick",
                new[] { typeof(long) }, "Fire.OnTick");
            installed += Install(harmony, "Game.Systems.Firex.FirexSys", "EveryFrame",
                Type.EmptyTypes, "Fire.EveryFrame");
            installed += Install(harmony, "Game.Systems.Motion.MotionSys", "OnTick",
                new[] { typeof(long) }, "Motion.OnTick");
            installed += Install(harmony,
                "Game.Systems.Motion.MotionSys", "OnTickProgress",
                new[] { typeof(float) }, "Motion.TickProgress");
            installed += Install(harmony,
                "Game.Systems.Path.PathSys", "RunAllPathfinding",
                Type.EmptyTypes, "Path.RunAllPathfinding");
            installed += Install(harmony,
                "Game.Systems.Path.PathSys", "UpdatePassability",
                Type.EmptyTypes, "Path.UpdatePassability");
            installed += Install(harmony,
                "Game.Systems.Path.PathSys", "AfterPathJob",
                new[] { typeof(bool) }, "Path.AfterPathJob");
            installed += Install(harmony,
                "Game.Systems.Path.PathSys", "RunPathfinding",
                Type.EmptyTypes, "Path.RunPathfinding");
            installed += Install(harmony,
                "Game.Systems.AI.Combat.CombatSys", "OnTick",
                new[] { typeof(long) }, "Combat.OnTick");
            installed += Install(harmony, "Game.Systems.SecuritySys", "OnTick",
                new[] { typeof(long) }, "Security.OnTick");
            installed += Install(harmony, "Game.Systems.Ships.NPCShipSys", "OnTick",
                new[] { typeof(long) }, "NPCShips.OnTick");
            installed += Install(harmony,
                "Game.Systems.Ships.NPCShipSys", "OnTickProgress",
                new[] { typeof(float) }, "NPCShips.TickProgress");
            installed += Install(harmony, "Game.Systems.Ships.ShipCombatSys", "OnTick",
                new[] { typeof(long) }, "ShipCombat.OnTick");
            installed += Install(harmony, "Game.Systems.Ships.ShipWeaponSys", "OnTick",
                new[] { typeof(long) }, "ShipWeapons.OnTick");
            installed += Install(harmony,
                "Game.Systems.Ships.ShipWeaponSys", "OnTickProgress",
                new[] { typeof(float) }, "ShipWeapons.TickProgress");

            // Second-pass coverage for the remaining Clock subscribers. These
            // are still whole-method probes called at most once per tick or
            // tick-progress event; no entity, goal, or tile loops are patched.
            installed += Install(harmony, "Game.Systems.Stats.StatsSys",
                "OnTickEarly", new[] { typeof(long) }, "Stats.OnTickEarly");
            installed += Install(harmony, "Game.Systems.Nanobots.NanobotSys",
                "OnTick", new[] { typeof(long) }, "Nanobots.OnTick");
            installed += Install(harmony, "Game.Systems.Nanobots.NanobotSys",
                "OnTickProgress", new[] { typeof(float) },
                "Nanobots.TickProgress");
            installed += Install(harmony, "Game.Systems.BotDockSys", "OnTick",
                new[] { typeof(long) }, "BotDocks.OnTick");
            installed += Install(harmony, "Game.Systems.BetaWavesSys", "OnTick",
                new[] { typeof(long) }, "BetaWaves.OnTick");
            installed += Install(harmony, "Game.Systems.EconomySys", "OnTick",
                new[] { typeof(long) }, "Economy.OnTick");
            installed += Install(harmony, "Game.Systems.Drama.DramaSys", "OnTick",
                new[] { typeof(long) }, "Drama.OnTick");
            installed += Install(harmony,
                "Game.Systems.Diplomacy.DiplomacySys", "OnTick",
                new[] { typeof(long) }, "Diplomacy.OnTick");
            installed += Install(harmony, "Game.Systems.ExpeditionSys", "OnTick",
                new[] { typeof(long) }, "Expeditions.OnTick");
            installed += Install(harmony, "Game.Systems.InfoSys", "OnTick",
                new[] { typeof(long) }, "Info.OnTick");
            installed += Install(harmony, "Game.Systems.ScanSys", "OnTick",
                new[] { typeof(long) }, "Scan.OnTick");
            installed += Install(harmony, "Game.Systems.SuggestionSys", "OnTick",
                new[] { typeof(long) }, "Suggestions.OnTick");
            installed += Install(harmony, "Game.Systems.ResearchSys", "OnTick",
                new[] { typeof(long) }, "Research.OnTick");
            installed += Install(harmony,
                "Game.Systems.Creatures.Decay.RottingSys", "OnTick",
                new[] { typeof(long) }, "Rotting.OnTick");
            installed += Install(harmony, "Game.Systems.Copters.CoptersSys", "OnTick",
                new[] { typeof(long) }, "Copters.OnTick");
            installed += Install(harmony, "Game.Systems.Turrets.TurretsSys", "OnTick",
                new[] { typeof(long) }, "Turrets.OnTick");
            installed += Install(harmony, "Game.Systems.Twitch.TwitchSys", "OnTick",
                new[] { typeof(long) }, "Twitch.OnTick");
            installed += Install(harmony, "Game.Systems.Trade.TradeSys", "OnTick",
                new[] { typeof(long) }, "Trade.OnTick");
            installed += Install(harmony, "Game.Systems.Reg.RegSys", "OnTick",
                new[] { typeof(long) }, "Regions.OnTick");
            installed += Install(harmony, "Game.Systems.Reg.RegSys", "OnTickProgress",
                new[] { typeof(float) }, "Regions.TickProgress");
            installed += Install(harmony, "Game.Systems.PlantSys", "OnTick",
                new[] { typeof(long) }, "Plants.OnTick");
            installed += Install(harmony,
                "Game.Systems.Planets.RemoteDrillSys", "OnTick",
                new[] { typeof(long) }, "RemoteDrills.OnTick");
            installed += Install(harmony, "Game.Systems.Planets.PlanetsSys", "OnTick",
                new[] { typeof(long) }, "Planets.OnTick");
            installed += Install(harmony, "Game.Systems.ParticlesSys", "OnTick",
                new[] { typeof(long) }, "Particles.OnTick");
            installed += Install(harmony, "Game.Systems.Story.StorySys", "OnTick",
                new[] { typeof(long) }, "Story.OnTick");
            installed += Install(harmony,
                "Game.Systems.Notify.NotificationSys", "OnTick",
                new[] { typeof(long) }, "Notifications.OnTick");
            installed += Install(harmony, "Game.Systems.Policies.PoliciesSys", "OnTick",
                new[] { typeof(long) }, "Policies.OnTick");
            installed += Install(harmony, "Game.Systems.Quests.QuestSys", "OnTick",
                new[] { typeof(long) }, "Quests.OnTick");
            installed += Install(harmony,
                "Game.Systems.Statistics.StatisticsSys", "OnTick",
                new[] { typeof(long) }, "Statistics.OnTick");
            installed += Install(harmony, "Game.Systems.Space.SpaceSys", "OnTick",
                new[] { typeof(long) }, "Space.OnTick");
            installed += Install(harmony, "Game.Systems.ShipSys", "OnTick",
                new[] { typeof(long) }, "Ship.OnTick");
            installed += Install(harmony, "Game.Systems.Ships.ShipShieldSys", "OnTick",
                new[] { typeof(long) }, "ShipShields.OnTick");
            installed += Install(harmony, "Game.Systems.Ships.ShipEngineSys", "OnTick",
                new[] { typeof(long) }, "ShipEngines.OnTick");
            installed += Install(harmony,
                "Game.Systems.Ships.ShipCapacitorSys", "OnTick",
                new[] { typeof(long) }, "ShipCapacitors.OnTick");
            installed += Install(harmony,
                "Game.Systems.Creatures.BeingGroupsUI", "OnTick",
                new[] { typeof(long) }, "UI.BeingGroups.OnTick");
            installed += Install(harmony,
                "Game.Systems.UIUX.ShipCombatModeHandler", "OnTick",
                new[] { typeof(long) }, "UI.ShipCombat.OnTick");
            installed += Install(harmony,
                "Game.Systems.UIUX.StarmapHandler", "OnTick",
                new[] { typeof(long) }, "UI.Starmap.OnTick");
            installed += Install(harmony,
                "Game.Systems.UIUX.HyperspaceHandler", "OnTick",
                new[] { typeof(long) }, "UI.Hyperspace.OnTick");

            currentOnTickElapsed =
                new long[CorrelatedOnTickTargets.Count];
            return installed;
        }

        private static int Install(
                Harmony harmony,
                string typeName,
                string methodName,
                Type[] parameterTypes,
                string label,
                bool capturesAiCounts = false) {
            try {
                Type targetType = AccessTools.TypeByName(typeName);
                MethodInfo original = targetType == null
                    ? null
                    : parameterTypes == null
                        ? AccessTools.DeclaredMethod(targetType, methodName)
                        : AccessTools.DeclaredMethod(
                            targetType, methodName, parameterTypes);
                if (original == null) {
                    D.Err("Optimization Mod stress timing probe unavailable: {0}.{1}",
                        typeName, methodName);
                    return 0;
                }

                bool isFullTick = label == "Clock.FullSimulationTick";
                bool correlatesOnTick = label.EndsWith(
                    ".OnTick", StringComparison.Ordinal);
                int correlationIndex = correlatesOnTick
                    ? CorrelatedOnTickTargets.Count
                    : -1;
                var target = new ProfileTarget(label, capturesAiCounts,
                    isFullTick, correlationIndex);
                Targets[original] = target;
                if (correlatesOnTick) {
                    CorrelatedOnTickTargets.Add(target);
                }
                var prefix = new HarmonyMethod(
                    typeof(StressPerformanceDiagnostics), nameof(Begin)) {
                    priority = HarmonyLib.Priority.First
                };
                var postfix = new HarmonyMethod(
                    typeof(StressPerformanceDiagnostics), nameof(End)) {
                    priority = HarmonyLib.Priority.Last
                };
                harmony.Patch(original, prefix: prefix, postfix: postfix);
                return 1;
            } catch (Exception ex) {
                D.Err("Optimization Mod failed to install stress timing probe "
                    + "{0}.{1}: {2}", typeName, methodName, ex);
                return 0;
            }
        }

        private static void Begin(MethodBase __originalMethod, out long __state) {
            __state = Stopwatch.GetTimestamp();
            if (Targets.TryGetValue(
                    __originalMethod, out ProfileTarget target)
                    && target.IsFullTick) {
                fullTickOrdinal++;
                fullTickActive = true;
                if (currentOnTickElapsed != null) {
                    Array.Clear(currentOnTickElapsed, 0,
                        currentOnTickElapsed.Length);
                }
            }
        }

        private static void End(
                MethodBase __originalMethod,
                object __instance,
                long __state) {
            if (__state <= 0 || !Targets.TryGetValue(
                    __originalMethod, out ProfileTarget target)) {
                return;
            }

            long elapsed = Stopwatch.GetTimestamp() - __state;
            int speed = (int) G.ClockSpeed;
            if (speed < 0 || speed >= target.BySpeed.Length) { speed = 0; }
            TimingStats stats = target.BySpeed[speed];
            stats.Calls++;
            stats.TotalTicks += elapsed;
            if (elapsed > stats.MaxTicks) { stats.MaxTicks = elapsed; }
            if (elapsed >= FourMsTicks) { stats.OverFourMs++; }
            if (elapsed >= EightMsTicks) { stats.OverEightMs++; }
            if (elapsed >= SixteenMsTicks) { stats.OverSixteenMs++; }
            if (elapsed >= ThirtyThreeMsTicks) { stats.OverThirtyThreeMs++; }

            if (fullTickActive && target.CorrelationIndex >= 0
                    && currentOnTickElapsed != null
                    && target.CorrelationIndex < currentOnTickElapsed.Length) {
                currentOnTickElapsed[target.CorrelationIndex] += elapsed;
            }

            if (target.IsFullTick) {
                if (elapsed >= SpikeThresholdTicks) {
                    qualifyingSpikeCount++;
                    RetainSpike(elapsed, speed);
                }
                fullTickActive = false;
            }

            if (target.CapturesAiCounts && __instance is AISys ai) {
                if (ai.ColonyGoals.Count > maxAiGoals) {
                    maxAiGoals = ai.ColonyGoals.Count;
                }
                if (ai.Agents.Count > maxAiAgents) {
                    maxAiAgents = ai.Agents.Count;
                }
            }
        }

        internal static void LogSummary() {
            long now = Stopwatch.GetTimestamp();
            double sessionSeconds = sessionStartedAt == 0
                ? 0d
                : (now - sessionStartedAt) / (double) Stopwatch.Frequency;
            D.Warn("Optimization Mod stress timing summary: {0:0.0}s captured, "
                + "max AI goals {1}, max AI agents {2}. Timings are nested "
                + "main-thread measurements and are not additive.",
                sessionSeconds, maxAiGoals, maxAiAgents);

            foreach (ProfileTarget target in Targets.Values
                    .OrderByDescending(value => value.TotalTicks)) {
                long calls = target.BySpeed.Sum(value => value.Calls);
                if (calls == 0) { continue; }
                double totalMs = ToMilliseconds(target.TotalTicks);
                double averageMs = totalMs / calls;
                double maxMs = ToMilliseconds(
                    target.BySpeed.Max(value => value.MaxTicks));
                long overFour = target.BySpeed.Sum(value => value.OverFourMs);
                long overEight = target.BySpeed.Sum(value => value.OverEightMs);
                long overSixteen = target.BySpeed.Sum(
                    value => value.OverSixteenMs);
                long overThirtyThree = target.BySpeed.Sum(
                    value => value.OverThirtyThreeMs);
                var speedParts = new List<string>();
                for (int i = 0; i < target.BySpeed.Length; i++) {
                    TimingStats stats = target.BySpeed[i];
                    if (stats.Calls == 0) { continue; }
                    double speedTotalMs = ToMilliseconds(stats.TotalTicks);
                    speedParts.Add($"{(ClockSpeed) i}: {stats.Calls} calls, "
                        + $"{speedTotalMs / stats.Calls:0.###} ms avg, "
                        + $"{ToMilliseconds(stats.MaxTicks):0.###} ms max");
                }
                D.Warn($"Optimization Mod stress timing {target.Label}: "
                    + $"{calls} calls, {totalMs:0.###} ms total, "
                    + $"{averageMs:0.###} ms avg, {maxMs:0.###} ms max, "
                    + $">=4 ms {overFour}, >=8 ms {overEight}, "
                    + $">=16 ms {overSixteen}, >=33 ms {overThirtyThree}; "
                    + string.Join(" | ", speedParts));
            }

            D.Warn("Optimization Mod spike attribution summary: {0} full ticks "
                + ">=33 ms; retained the {1} worst. Tracked time is the sum "
                + "of direct, instrumented Clock.OnTick subscribers from that "
                + "same tick; residual includes uninstrumented subscribers, "
                + "dispatch overhead, and runtime pauses.",
                qualifyingSpikeCount, WorstSpikes.Count);
            int spikeNumber = 0;
            foreach (SpikeRecord spike in WorstSpikes
                    .OrderByDescending(value => value.FullTicks)) {
                spikeNumber++;
                var contributors = new List<string>();
                for (int i = 0; i < spike.ContributorCount; i++) {
                    contributors.Add($"{spike.ContributorLabels[i]} "
                        + $"{ToMilliseconds(spike.ContributorTicks[i]):0.###} ms");
                }
                D.Warn("Optimization Mod spike attribution #{0}: +{1:0.000}s, "
                    + "tick {2}, {3}, full {4:0.###} ms, tracked OnTick "
                    + "{5:0.###} ms, residual {6:0.###} ms; top: {7}",
                    spikeNumber,
                    ToSeconds(spike.CapturedAt - sessionStartedAt),
                    spike.TickOrdinal,
                    (ClockSpeed) spike.Speed,
                    ToMilliseconds(spike.FullTicks),
                    ToMilliseconds(spike.TrackedTicks),
                    ToMilliseconds(Math.Max(
                        0L, spike.FullTicks - spike.TrackedTicks)),
                    contributors.Count == 0
                        ? "none"
                        : string.Join(" | ", contributors));
            }
        }

        private static void RetainSpike(long fullTicks, int speed) {
            int replaceIndex = -1;
            if (WorstSpikes.Count >= MaxRetainedSpikes) {
                long smallest = long.MaxValue;
                for (int i = 0; i < WorstSpikes.Count; i++) {
                    if (WorstSpikes[i].FullTicks < smallest) {
                        smallest = WorstSpikes[i].FullTicks;
                        replaceIndex = i;
                    }
                }
                if (fullTicks <= smallest) { return; }
            }

            var spike = new SpikeRecord {
                CapturedAt = Stopwatch.GetTimestamp(),
                TickOrdinal = fullTickOrdinal,
                Speed = speed,
                FullTicks = fullTicks
            };
            for (int i = 0; i < CorrelatedOnTickTargets.Count; i++) {
                long elapsed = currentOnTickElapsed[i];
                spike.TrackedTicks += elapsed;
                InsertContributor(spike, CorrelatedOnTickTargets[i].Label,
                    elapsed);
            }

            if (replaceIndex >= 0) {
                WorstSpikes[replaceIndex] = spike;
            } else {
                WorstSpikes.Add(spike);
            }
        }

        private static void InsertContributor(
                SpikeRecord spike,
                string label,
                long elapsed) {
            if (elapsed <= 0) { return; }
            int insertion = spike.ContributorCount;
            for (int i = 0; i < spike.ContributorCount; i++) {
                if (elapsed > spike.ContributorTicks[i]) {
                    insertion = i;
                    break;
                }
            }
            if (insertion >= MaxSpikeContributors) { return; }

            int last = Math.Min(
                spike.ContributorCount, MaxSpikeContributors - 1);
            for (int i = last; i > insertion; i--) {
                spike.ContributorTicks[i] = spike.ContributorTicks[i - 1];
                spike.ContributorLabels[i] = spike.ContributorLabels[i - 1];
            }
            spike.ContributorTicks[insertion] = elapsed;
            spike.ContributorLabels[insertion] = label;
            if (spike.ContributorCount < MaxSpikeContributors) {
                spike.ContributorCount++;
            }
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static double ToSeconds(long ticks) {
            return ticks / (double) Stopwatch.Frequency;
        }

        internal static void Reset() {
            Targets.Clear();
            CorrelatedOnTickTargets.Clear();
            WorstSpikes.Clear();
            sessionStartedAt = 0;
            maxAiGoals = 0;
            maxAiAgents = 0;
            currentOnTickElapsed = null;
            fullTickActive = false;
            fullTickOrdinal = 0;
            qualifyingSpikeCount = 0;
        }

        private sealed class ProfileTarget {
            internal readonly string Label;
            internal readonly bool CapturesAiCounts;
            internal readonly bool IsFullTick;
            internal readonly int CorrelationIndex;
            internal readonly TimingStats[] BySpeed = {
                new TimingStats(),
                new TimingStats(),
                new TimingStats(),
                new TimingStats(),
                new TimingStats()
            };

            internal ProfileTarget(
                    string label,
                    bool capturesAiCounts,
                    bool isFullTick,
                    int correlationIndex) {
                Label = label;
                CapturesAiCounts = capturesAiCounts;
                IsFullTick = isFullTick;
                CorrelationIndex = correlationIndex;
            }

            internal long TotalTicks => BySpeed.Sum(value => value.TotalTicks);
        }

        private sealed class TimingStats {
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long OverFourMs;
            internal long OverEightMs;
            internal long OverSixteenMs;
            internal long OverThirtyThreeMs;
        }

        private sealed class SpikeRecord {
            internal long CapturedAt;
            internal long TickOrdinal;
            internal int Speed;
            internal long FullTicks;
            internal long TrackedTicks;
            internal int ContributorCount;
            internal readonly string[] ContributorLabels =
                new string[MaxSpikeContributors];
            internal readonly long[] ContributorTicks =
                new long[MaxSpikeContributors];
        }
    }
}
