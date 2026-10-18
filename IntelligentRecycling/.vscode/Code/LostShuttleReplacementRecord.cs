using System;
using System.Globalization;
using System.Text;
using Game.Constants;

namespace Game.Systems.IntelligentRecycling {
    internal enum LostShuttleReplacementPhase {
        Deconstructing = 1,
        AwaitingBlueprint = 2,
        AwaitingConstruction = 3
    }

    internal sealed class LostShuttleReplacementRecord {
        private const string RecordVersion = "1";

        internal int OriginalEntityId;
        internal int CurrentEntityId;
        internal uint DeconstructionGoalId;
        internal int Position;
        internal int Layer;
        internal Facing.Type Rotation;
        internal string DefinitionId;
        internal string[] UpgradeIds = Array.Empty<string>();
        internal LostShuttleReplacementPhase Phase;
        internal long NextActionTick;
        internal bool UpgradeRetryLogged;

        internal string Key => IntelligentRecyclingSys.MakeLostShuttleKey(
            Layer, Position, DefinitionId);

        internal string Serialize() {
            string[] encodedUpgrades = new string[UpgradeIds?.Length ?? 0];
            for (int i = 0; i < encodedUpgrades.Length; i++) {
                encodedUpgrades[i] = Encode(UpgradeIds[i]);
            }
            return string.Join("|",
                RecordVersion,
                OriginalEntityId.ToString(CultureInfo.InvariantCulture),
                CurrentEntityId.ToString(CultureInfo.InvariantCulture),
                DeconstructionGoalId.ToString(CultureInfo.InvariantCulture),
                Position.ToString(CultureInfo.InvariantCulture),
                Layer.ToString(CultureInfo.InvariantCulture),
                ((int) Rotation).ToString(CultureInfo.InvariantCulture),
                ((int) Phase).ToString(CultureInfo.InvariantCulture),
                NextActionTick.ToString(CultureInfo.InvariantCulture),
                Encode(DefinitionId),
                string.Join(",", encodedUpgrades));
        }

        internal static bool TryDeserialize(string serialized,
                out LostShuttleReplacementRecord record) {
            record = null;
            if (string.IsNullOrWhiteSpace(serialized)) {
                return false;
            }

            string[] fields = serialized.Split(new[] { '|' }, 11,
                StringSplitOptions.None);
            if (fields.Length != 11 || fields[0] != RecordVersion
                    || !int.TryParse(fields[1], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int originalEntityId)
                    || !int.TryParse(fields[2], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int currentEntityId)
                    || !uint.TryParse(fields[3], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out uint goalId)
                    || !int.TryParse(fields[4], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int position)
                    || !int.TryParse(fields[5], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int layer)
                    || !int.TryParse(fields[6], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int rotation)
                    || !int.TryParse(fields[7], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int phase)
                    || !long.TryParse(fields[8], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long nextActionTick)
                    || phase < (int) LostShuttleReplacementPhase.Deconstructing
                    || phase > (int) LostShuttleReplacementPhase.AwaitingConstruction
                    || rotation < (int) Facing.Type.Down
                    || rotation > (int) Facing.Type.Right) {
                return false;
            }

            try {
                string definitionId = Decode(fields[9]);
                if (string.IsNullOrWhiteSpace(definitionId)) {
                    return false;
                }
                string[] upgrades;
                if (string.IsNullOrEmpty(fields[10])) {
                    upgrades = Array.Empty<string>();
                } else {
                    string[] encoded = fields[10].Split(new[] { ',' },
                        StringSplitOptions.None);
                    upgrades = new string[encoded.Length];
                    for (int i = 0; i < encoded.Length; i++) {
                        upgrades[i] = Decode(encoded[i]);
                    }
                }

                record = new LostShuttleReplacementRecord {
                    OriginalEntityId = originalEntityId,
                    CurrentEntityId = currentEntityId,
                    DeconstructionGoalId = goalId,
                    Position = position,
                    Layer = layer,
                    Rotation = (Facing.Type) rotation,
                    DefinitionId = definitionId,
                    UpgradeIds = upgrades,
                    Phase = (LostShuttleReplacementPhase) phase,
                    NextActionTick = Math.Max(0L, nextActionTick)
                };
                return true;
            } catch (FormatException) {
                return false;
            }
        }

        private static string Encode(string value) {
            return string.IsNullOrEmpty(value)
                ? string.Empty
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value) {
            return string.IsNullOrEmpty(value)
                ? null
                : Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
    }
}
