using ColonistAndBotsAutoUpgrade.Models;
using Game.Data;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public static class BeingScanner
    {
        public static BeingSnapshot Scan(
          GameState gameState,
          Being being,
          ISet<uint> allocatedPartIds = null
        )
        {
            return BeingSnapshot.FromBeing(
                gameState,
                being,
                allocatedPartIds
            );
        }
    }
}
