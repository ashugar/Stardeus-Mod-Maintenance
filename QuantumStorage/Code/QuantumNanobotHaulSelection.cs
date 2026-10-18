using Game.Data;

namespace Game.Systems.QuantumStorage
{
    // Ranking only: callers retain ownership of eligibility, reservations and work budgets.
    internal static class QuantumNanobotHaulSelection
    {
        public static bool IsBetterDestination(int priority, float distanceSquared, int entityId,
            int bestPriority, float bestDistanceSquared, int bestEntityId)
        {
            // Use Core's strict storage priority, then distance. Stable IDs break exact ties;
            // approximate ties could otherwise make a farther endpoint beat a nearer one.
            return EntityUtils.IsBetterStorage(priority, distanceSquared, bestPriority, bestDistanceSquared) ||
                (priority == bestPriority && distanceSquared == bestDistanceSquared && entityId < bestEntityId);
        }

        public static float TripScore(float hiveToSourceSquared, float sourceToDestinationSquared)
        {
            // Priority chooses an object's destination, not precedence between unrelated jobs.
            // The removed Core weighted-priority helper no longer contributes to this score.
            return hiveToSourceSquared + sourceToDestinationSquared;
        }

        public static bool IsBetterCandidate(float score, uint goalId, float bestScore, uint bestGoalId)
        {
            return score < bestScore || (score == bestScore && goalId < bestGoalId);
        }
    }
}
