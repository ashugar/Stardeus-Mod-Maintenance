using System.Collections.Generic;
using Game;
using Game.Constants;
using Game.Data;
using Game.Research;
using Game.Systems;
using Game.Systems.Creatures;
using HarmonyLib;

namespace ExpandedTraitsMod {
    internal static class CuriosityService {
        private static readonly Dictionary<uint, long> LastNovelty = new();

        internal static void Reset(GameState state) {
            LastNovelty.Clear();
            if (state == null) return;
            foreach (Being being in state.Beings.Alive) {
                BeingData data = being?.Data;
                if (data?.Mind?.Traits?.HasTrait(ExpandedTraitIds.CuriousH) == true) {
                    LastNovelty[data.Id] = state.Ticks;
                }
            }
        }

        internal static void Clear() => LastNovelty.Clear();

        internal static void RewardAll(GameState state, string reason, int value = 4) {
            if (state == null) return;
            foreach (Being being in state.Beings.Alive) {
                Reward(state, being?.Data, state.Ticks, reason, value);
            }
        }

        internal static void RewardExpedition(GameState state, Expedition expedition) {
            if (state == null || expedition?.Crew == null) return;
            foreach (BeingData member in expedition.Crew) {
                Reward(state, member, state.Ticks, "Returned from an expedition", 5);
            }
        }

        private static void Reward(GameState state, BeingData data, long ticks,
                string reason, int value) {
            if (data?.Mind?.Traits?.HasTrait(ExpandedTraitIds.CuriousH) != true) return;
            LastNovelty[data.Id] = ticks;
            MoodService.Add(state, data, ticks, Consts.TicksPer8Hours,
                reason, value, IconId.CResearch);
        }

        internal static void Tick(GameState state, long ticks) {
            if (state == null || ticks % Consts.TicksPerHour != 0) return;
            foreach (Being being in state.Beings.Alive) {
                BeingData data = being?.Data;
                if (data?.Mind?.Traits?.HasTrait(ExpandedTraitIds.CuriousH) != true) continue;
                if (!LastNovelty.TryGetValue(data.Id, out long last)) {
                    LastNovelty[data.Id] = ticks;
                    continue;
                }
                if (ticks - last >= Consts.TicksPerDay * 2L) {
                    MoodService.Add(state, data, ticks, Consts.TicksPer4Hours,
                        "Craving something new", -3, IconId.CMoodBad);
                }
            }
        }
    }

    [HarmonyPatch(typeof(ResearchSys), nameof(ResearchSys.NotifyCompleted),
        new[] { typeof(ResearchProgress) })]
    internal static class CuriousResearchCompletionPatch {
        private static void Postfix() =>
            CuriosityService.RewardAll(A.S, "A new discovery", 4);
    }
}
