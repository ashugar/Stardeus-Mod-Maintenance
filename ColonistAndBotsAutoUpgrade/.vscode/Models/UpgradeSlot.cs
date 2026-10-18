using Game.Systems.BodyParts;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public sealed class UpgradeSlot
    {
        public BodyPartSlot Slot { get; set; }

        public BodyPartDef InstalledPart { get; set; }

        public List<BodyPartDef> CompatibleParts { get; } =
          new List<BodyPartDef>();

        public BodyPartDef BestPart { get; set; }

        public bool NeedsUpgrade { get; set; }

        public int InstalledTier =>
          InstalledPart?.Tier ?? 0;

        public int BestTier =>
          BestPart?.Tier ?? 0;

        public string UpgradeStatusText
        {
            get
            {
                if (BestPart == null)
                {
                    return "Nothing Better Found";
                }

                if (InstalledPart == null)
                {
                    return "Missing";
                }

                if (NeedsUpgrade)
                {
                    return $"Upgrade → {BestPart.NameT} (T:{BestTier})";
                }

                return "Best Installed";
            }
        }

    }
}