using System.Collections.Generic;
using System.Linq;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.Conditions;
using Game.Systems.Creatures;
using Game.Systems.Drama;
using Game.Utils;

namespace ExpandedTraitsMod {
    internal static class BondReactionService {
        private struct Snapshot {
            internal bool Injured;
            internal bool Breakdown;
        }

        private static readonly Dictionary<uint, Snapshot> Last = new();
        private const BeingOpinionFlags CloseBond = BeingOpinionFlags.Friend
            | BeingOpinionFlags.Lover | BeingOpinionFlags.Spouse
            | BeingOpinionFlags.Master | BeingOpinionFlags.Pet;

        internal static void Reset(GameState state) {
            Last.Clear();
            if (state == null) return;
            foreach (Being being in state.Beings.Alive) Seed(being?.Data);
        }

        internal static void Clear() => Last.Clear();

        private static void Seed(BeingData data) {
            if (data?.Mind == null) return;
            Last[data.Id] = new Snapshot {
                Injured = data.Body.IsInjured,
                Breakdown = data.HasCondition(ConditionIdH.MentalBreakdown)
            };
        }

        internal static void Tick(GameState state, long ticks) {
            if (state == null || ticks % Consts.TicksPer15Minutes != 0) return;
            var aliveIds = new HashSet<uint>();
            foreach (Being being in state.Beings.Alive) {
                BeingData subject = being?.Data;
                if (subject?.Mind == null) continue;
                aliveIds.Add(subject.Id);
                var now = new Snapshot {
                    Injured = subject.Body.IsInjured,
                    Breakdown = subject.HasCondition(ConditionIdH.MentalBreakdown)
                };
                if (!Last.TryGetValue(subject.Id, out Snapshot before)) {
                    Last[subject.Id] = now;
                    continue;
                }
                if (now.Injured != before.Injured) {
                    if (now.Injured) {
                        Notify(state, subject, ticks, "A friend was injured", -5,
                            "A bonded animal was injured", -6, -3);
                    } else {
                        Notify(state, subject, ticks, "A friend recovered", 5,
                            "A bonded animal recovered", 5, 3);
                    }
                }
                if (now.Breakdown != before.Breakdown) {
                    NotifyEmpaths(state, subject, ticks,
                        now.Breakdown ? "A friend suffered a breakdown" : "A friend regained composure",
                        now.Breakdown ? -7 : 4);
                }
                Last[subject.Id] = now;
            }
            foreach (uint id in Last.Keys.Where(id => !aliveIds.Contains(id)).ToArray()) {
                Last.Remove(id);
            }
        }

        internal static void OnBeingDied(GameState state, BeingData subject) {
            if (state == null || subject == null) return;
            Last.Remove(subject.Id);
            Notify(state, subject, state.Ticks, "A friend died", -12,
                "A bonded animal died", -12, -8);
        }

        private static void Notify(GameState state, BeingData subject, long ticks,
                string empathReason, int empathValue, string petReason,
                int specificPetValue, int generalPetValue) {
            NotifyEmpaths(state, subject, ticks, empathReason, empathValue);
            if (!subject.IsFriendly || !TraitDefinitionRegistry.IsPetSpecies(subject.Species)) return;
            foreach (Being being in state.Beings.Alive) {
                BeingData observer = being?.Data;
                if (observer?.Mind?.Traits == null || observer == subject
                        || !IsCloseBond(observer, subject)) continue;
                int affinity = TraitDefinitionRegistry.AffinityValue(observer.Mind.Traits,
                    subject.Species);
                if (affinity <= 0) continue;
                int value = affinity >= 4 ? specificPetValue : generalPetValue;
                MoodService.Add(state, observer, ticks, Consts.TicksPer6Hours,
                    petReason, value, value < 0 ? IconId.CMoodBad : IconId.CMoodGood);
            }
        }

        private static void NotifyEmpaths(GameState state, BeingData subject,
                long ticks, string reason, int value) {
            foreach (Being being in state.Beings.Alive) {
                BeingData observer = being?.Data;
                if (observer?.Mind?.Traits?.HasTrait(ExpandedTraitIds.EmpathH) != true
                        || observer == subject || !IsCloseBond(observer, subject)) continue;
                MoodService.Add(state, observer, ticks, Consts.TicksPer6Hours,
                    reason, value, value < 0 ? IconId.CMoodBad : IconId.CMoodGood);
            }
        }

        private static bool IsCloseBond(BeingData observer, BeingData subject) {
            if (observer?.Mind?.Bonds == null || subject?.Mind == null) return false;
            return observer.Mind.Bonds.Opinions.TryGetValue(subject.Mind.Id, out BeingOpinion opinion)
                && (opinion.Flags & CloseBond) != BeingOpinionFlags.None;
        }
    }
}
