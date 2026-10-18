using System.Collections.Generic;
using System.Linq;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public sealed class UpgradeCandidate
    {
        public BeingSnapshot Snapshot { get; set; }

        public List<UpgradeSlot> Slots { get; } =
          new List<UpgradeSlot>();

        public bool HasUpgrades =>
          Slots.Any(s => s.NeedsUpgrade);
    }
}
