using System;
using System.Globalization;
using System.Text;

namespace Game.Systems.AutoHauling {
    internal enum HaulDomain {
        Material,
        Object
    }

    internal enum HardExclusion {
        None,
        MaterialQuality,
        IntelligentRecycling,
        BeingOrMind
    }

    internal sealed class HaulItemOption {
        internal HaulDomain Domain;
        internal string ItemKey;
        internal string ItemId;
        internal string Name;
        internal string CategoryKey;
        internal string CategoryName;
        internal string Icon;
        internal bool QualityBearing;

        internal string DomainName => Domain == HaulDomain.Material
            ? "Materials"
            : "Objects";
    }

    internal sealed class TrackedHaulGoal {
        internal uint GoalId;
        internal uint ObjectId;
        internal ushort LastFailedTotal;
        internal string ItemName;
        internal string ItemId;
        internal string Position;
    }

    internal sealed class HaulFailureRecord {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal long Tick;
        internal uint GoalId;
        internal string ItemName;
        internal string ItemId;
        internal string Position;
        internal string Reason;
        internal string State;
        internal int Attempt;
        internal bool Retrying;
        internal int RepeatCount = 1;

        internal string Serialize() {
            return string.Join("|", new[] {
                Tick.ToString(Invariant),
                GoalId.ToString(Invariant),
                Encode(ItemName),
                Encode(ItemId),
                Encode(Position),
                Encode(Reason),
                Encode(State),
                Attempt.ToString(Invariant),
                Retrying ? "1" : "0",
                RepeatCount.ToString(Invariant)
            });
        }

        internal static bool TryDeserialize(string value,
                out HaulFailureRecord record) {
            record = null;
            if (string.IsNullOrEmpty(value)) {
                return false;
            }
            string[] fields = value.Split('|');
            if (fields.Length != 10
                    || !long.TryParse(fields[0], NumberStyles.Integer,
                        Invariant, out long tick)
                    || !uint.TryParse(fields[1], NumberStyles.Integer,
                        Invariant, out uint goalId)
                    || !int.TryParse(fields[7], NumberStyles.Integer,
                        Invariant, out int attempt)
                    || !int.TryParse(fields[9], NumberStyles.Integer,
                        Invariant, out int repeatCount)
                    || (fields[8] != "0" && fields[8] != "1")) {
                return false;
            }
            try {
                record = new HaulFailureRecord {
                    Tick = Math.Max(0L, tick),
                    GoalId = goalId,
                    ItemName = Decode(fields[2]),
                    ItemId = Decode(fields[3]),
                    Position = Decode(fields[4]),
                    Reason = Decode(fields[5]),
                    State = Decode(fields[6]),
                    Attempt = Math.Max(1, attempt),
                    Retrying = fields[8] == "1",
                    RepeatCount = Math.Max(1, repeatCount)
                };
                return true;
            } catch (FormatException) {
                return false;
            }
        }

        private static string Encode(string value) {
            return Convert.ToBase64String(
                Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value) {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
    }

    internal struct ForbiddenLooseSummary {
        internal int Allowed;
    }
}
