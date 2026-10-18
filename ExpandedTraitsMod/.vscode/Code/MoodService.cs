using Game.Constants;
using Game.Data;
using Game.Systems.Creatures;
using Game.Systems.Mental;

namespace ExpandedTraitsMod {
    internal static class MoodService {
        internal static void Add(GameState state, BeingData being, long ticks,
                int duration, string reason, int value, string icon = IconId.CMoodGood) {
            if (state == null || being?.Mind?.Health == null || being.HasStateFlag(BeingState.Dead)) return;
            int pos = being.Being?.PosIdx ?? being.LastPosition.Idx;
            being.Mind.Health.AddEffect(state,
                MentalEffect.CreateWithIcon(pos, icon, ticks, duration, reason, value),
                autoLog: false);
        }
    }
}

