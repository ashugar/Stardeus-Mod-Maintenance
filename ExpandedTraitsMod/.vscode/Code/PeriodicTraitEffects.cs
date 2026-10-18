using System.Collections.Generic;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Story;
using Game.Utils;
using KL.Utils;

namespace ExpandedTraitsMod {
    internal static class PeriodicTraitEffects {
        private static readonly Dictionary<uint, long> SuitStarted = new();
        internal static void Reset() => SuitStarted.Clear();
        internal static void Apply(GameState state, long ticks) {
            if (ticks % Consts.TicksPer15Minutes != 0) return;
            var occupancy = new Dictionary<int, int>();
            foreach (Being being in state.Beings.Alive) {
                if (being?.Data?.IsFriendly != true) continue;
                int area = state.Sys.Areas.At(being.PosIdx);
                if (area > 0) occupancy[area] = occupancy.TryGetValue(area, out int n) ? n + 1 : 1;
            }
            bool danger = state.Sys.Firex.IsAnythingOnFire
                || state.Sys.Combat.IsHostileCombatInProgress
                || state.Sys.Sec.NumActiveSirens > 0;
            foreach (Being being in state.Beings.Alive) {
                BeingData data = being?.Data;
                if (data?.IsFriendly != true || data.Mind?.Health == null) continue;
                ApplySocial(state, data, ticks);
                ApplyClaustrophobia(state, data, ticks, occupancy);
                bool personalDanger = danger || (data.Being != null
                    && !data.Safety.IsActuallySafeAt(data.Being.PosIdx));
                ApplyDanger(state, data, ticks, personalDanger);
            }
        }

        private static void ApplySocial(GameState state, BeingData data, long ticks) {
            if (data.Mind.Bonds == null) return;
            if (data.Mind.Traits.HasTrait(ExpandedTraitIds.IntrovertH)) {
                int recent = data.Mind.Bonds.SocialInteractionsSince(ticks - Consts.TicksPer6Hours, out _);
                if (recent >= 3) {
                    MoodService.Add(state, data, ticks, Consts.TicksPer4Hours,
                        "Socially drained", -3, IconId.CMoodBad);
                } else if (recent == 0) {
                    MoodService.Add(state, data, ticks, Consts.TicksPer4Hours,
                        "Enjoyed quiet time", 3, IconId.CMoodGood);
                }
            } else if (data.Mind.Traits.HasTrait(ExpandedTraitIds.ExtrovertH)) {
                int recent = data.Mind.Bonds.SocialInteractionsSince(ticks - Consts.TicksPer6Hours, out long last);
                if (recent > 0) {
                    MoodService.Add(state, data, ticks, Consts.TicksPer4Hours,
                        "Socially fulfilled", 3, IconId.CWave);
                } else if (last == 0 || last < ticks - Consts.TicksPerDay) {
                    MoodService.Add(state, data, ticks, Consts.TicksPer4Hours,
                        "Feeling isolated", -5, IconId.CMoodBad);
                }
            }
        }

        private static void ApplyClaustrophobia(GameState state, BeingData data, long ticks,
                Dictionary<int, int> occupancy) {
            if (!data.Mind.Traits.HasTrait(ExpandedTraitIds.ClaustrophobicH) || data.Being == null) return;
            bool wearingSuit = data.Inventory?.Equipment.TryGetValue(
                InventorySlotIdH.Suit, out InventoryItemSlot suit) == true
                && suit.ObjData != null;
            if (!wearingSuit) {
                SuitStarted.Remove(data.Id);
            } else if (!SuitStarted.TryGetValue(data.Id, out long started)) {
                SuitStarted[data.Id] = ticks;
            } else if (ticks - started >= Consts.TicksPer4Hours) {
                MoodService.Add(state, data, ticks, Consts.TicksPer2Hours,
                    "Confined in a suit", -2, IconId.CMoodBad);
            }
            int areaId = state.Sys.Areas.At(data.Being.PosIdx);
            if (areaId <= 0) {
                MoodService.Add(state, data, ticks, Consts.TicksPer4Hours,
                    "Room to breathe", 3, IconId.CMoodGood);
                return;
            }
            int size = state.Sys.Areas.AreaById(areaId).Count;
            occupancy.TryGetValue(areaId, out int people);
            if (size <= 8 || (people >= 4 && people * 5 > size)) {
                MoodService.Add(state, data, ticks, Consts.TicksPer4Hours,
                    "Feeling confined", -5, IconId.CMoodBad);
            } else if (size >= 30) {
                MoodService.Add(state, data, ticks, Consts.TicksPer4Hours,
                    "Room to breathe", 3, IconId.CMoodGood);
            }
        }

        private static void ApplyDanger(GameState state, BeingData data, long ticks, bool danger) {
            if (!danger) return;
            if (data.Mind.Traits.HasTrait(ExpandedTraitIds.ParanoidH)) {
                MoodService.Add(state, data, ticks, Consts.TicksPer2Hours,
                    "Worried about ship security", -6, IconId.CWarning);
            }
            if (data.Mind.Traits.HasTrait(ExpandedTraitIds.DaredevilH)) {
                MoodService.Add(state, data, ticks, Consts.TicksPer2Hours,
                    "Thrilled by danger", 3, IconId.CAttackTarget);
            } else if (data.Mind.Traits.HasTrait(ExpandedTraitIds.CautiousH)) {
                MoodService.Add(state, data, ticks, Consts.TicksPer2Hours,
                    "Alarmed by danger", -4, IconId.CWarning);
            }
        }
    }
}
