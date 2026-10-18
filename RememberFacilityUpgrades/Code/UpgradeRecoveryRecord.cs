using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RememberFacilityUpgrades
{

internal sealed class UpgradeRecoveryRecord
{
    private const string RecordVersion = "1";

    public int Position { get; set; }
    public int Layer { get; set; }
    public int EntityId { get; set; }
    public string DefinitionId { get; set; }
    public string[] UpgradeIds { get; set; } = Array.Empty<string>();
    public bool AwaitingRebuild { get; set; }

    public string Key => RememberFacilityUpgradesSys.MakeKey(
        Layer, Position, DefinitionId);

    public string Serialize()
    {
        string upgrades = string.Join(",", UpgradeIds.Select(Encode));
        return string.Join("|",
            RecordVersion,
            Position.ToString(CultureInfo.InvariantCulture),
            Layer.ToString(CultureInfo.InvariantCulture),
            EntityId.ToString(CultureInfo.InvariantCulture),
            AwaitingRebuild ? "1" : "0",
            Encode(DefinitionId),
            upgrades);
    }

    public static bool TryDeserialize(string serialized,
        out UpgradeRecoveryRecord record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(serialized)) return false;

        string[] fields = serialized.Split(new[] { '|' }, 7,
            StringSplitOptions.None);
        if (fields.Length != 7 || fields[0] != RecordVersion
            || !int.TryParse(fields[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int position)
            || !int.TryParse(fields[2], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int layer)
            || !int.TryParse(fields[3], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int entityId))
        {
            return false;
        }

        try
        {
            string definitionId = Decode(fields[5]);
            if (string.IsNullOrWhiteSpace(definitionId)) return false;
            string[] upgrades = string.IsNullOrEmpty(fields[6])
                ? Array.Empty<string>()
                : fields[6].Split(new[] { ',' },
                        StringSplitOptions.None)
                    .Select(Decode)
                    .ToArray();

            record = new UpgradeRecoveryRecord
            {
                Position = position,
                Layer = layer,
                EntityId = entityId,
                AwaitingRebuild = fields[4] == "1",
                DefinitionId = definitionId,
                UpgradeIds = upgrades
            };
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string Decode(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
}
