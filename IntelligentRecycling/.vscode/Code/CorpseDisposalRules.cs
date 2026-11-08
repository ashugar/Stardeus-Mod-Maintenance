namespace Game.Systems.IntelligentRecycling {
    internal enum CorpseDisposalKind { Biological = 1, Robotic = 2 }

    // Kept independent of Unity so the production admission/ownership rules can
    // be exercised by the portable regression harness.
    internal static class CorpseDisposalRules {
        internal static bool SlotBlocks(bool removed, bool installed,
                bool removable, bool welded, bool holdsMind) {
            return !removed && installed && !welded
                && (removable || holdsMind);
        }

        internal static bool Matches(int game, uint being, uint goal,
                CorpseDisposalKind kind, int recordedGame, uint recordedBeing,
                uint recordedGoal, CorpseDisposalKind recordedKind) {
            return being != 0 && goal != 0 && game == recordedGame
                && being == recordedBeing && goal == recordedGoal
                && kind == recordedKind;
        }

        internal static bool Allows(bool ready, bool master, bool category,
                bool dead, bool beingAlive, bool bodyAlive, bool crawler,
                bool active, bool unavailable, bool partsRemain,
                bool surgery, bool foreignGoal, bool foreignCarrier,
                bool foreignReservation, bool compatibleProcessor) {
            return ready && master && category && dead && !beingAlive
                && !bodyAlive && !crawler && active && !unavailable
                && !partsRemain && !surgery && !foreignGoal
                && !foreignCarrier && !foreignReservation
                && compatibleProcessor;
        }
    }
}
