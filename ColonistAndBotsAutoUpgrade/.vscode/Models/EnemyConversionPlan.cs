using Game.Components;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Mental;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public enum EnemyConversionRoute
    {
        None = 0,
        ObeyChip = 1,
        CoreSwap = 2
    }

    public enum CARAUOperationPurpose
    {
        Upgrade = 0,
        EnemyConversion = 1
    }

    public sealed class EnemyConversionPlan
    {
        public Being Being { get; set; }
        public BeingData Target => Being?.Data;
        public EnemyConversionRoute Route { get; set; }
        public BodyPartSlot Slot { get; set; }
        public ObjData ReservedPart { get; set; }
        public Mind OriginalMind { get; set; }
        public Mind IncomingMind => ReservedPart?.Mind;
        public string Reason { get; set; }

        public bool IsQueueable =>
            Being != null &&
            Route != EnemyConversionRoute.None &&
            Slot != null &&
            ReservedPart != null;

        public string RouteName => Route == EnemyConversionRoute.ObeyChip
            ? "Obey Chip"
            : Route == EnemyConversionRoute.CoreSwap
                ? "Core Swap"
                : "Unavailable";

        public uint BeingId => Target?.Id ?? 0;
        public uint ReservedPartId => ReservedPart?.Id ?? 0;
        public uint OriginalMindId => OriginalMind?.Id ?? 0;
        public uint IncomingMindId => IncomingMind?.Id ?? 0;

        public bool SameMappingAs(EnemyConversionPlan other)
        {
            return other != null &&
                   BeingId == other.BeingId &&
                   Route == other.Route &&
                   string.Equals(
                       Slot?.Id,
                       other.Slot?.Id,
                       System.StringComparison.Ordinal
                   ) &&
                   ReservedPartId == other.ReservedPartId &&
                   OriginalMindId == other.OriginalMindId &&
                   IncomingMindId == other.IncomingMindId;
        }
    }

    public sealed class EnemyConversionQueueResult
    {
        public bool Success { get; set; }
        public EnemyConversionPlan Plan { get; set; }
        public string Message { get; set; }
        public Game.Systems.Creatures.Surgeries.SurgicalOperation Operation
          { get; set; }
    }
}
