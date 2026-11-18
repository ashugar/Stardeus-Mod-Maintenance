using System;

namespace MessagePack
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class MessagePackObjectAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class KeyAttribute : Attribute
    {
        public KeyAttribute(int index) { IntKey = index; }
        public int IntKey { get; }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class IgnoreMemberAttribute : Attribute { }
}

namespace ColonistAndBotsAutoUpgrade.Models
{
    public enum UpgradeActionKind { Upgrade = 0 }
    public enum CARAUOperationPurpose { Upgrade = 0, EnemyConversion = 1 }
    public enum EnemyConversionRoute { None = 0, ObeyChip = 1, CoreSwap = 2 }

    public enum CARAUDebugCategory
    {
        Error,
        Warning,
        UI,
        Selection,
        Decision,
        Queue,
        Inventory,
        Intent,
        Cleanup
    }
}

namespace Game.Data
{
    public sealed class Being { }
}

namespace Game.Systems.BodyParts
{
    public sealed class BodyPartSlot { }
}

namespace Game.Systems.Creatures.Surgeries
{
    public sealed class SurgicalOperation { }
}
