using System;
using System.Globalization;
using System.Text;

namespace ReconnectTeleporterConnections;

internal sealed class TrackedTeleporter
{
    internal int Id;
    internal int EntityId;
    internal int Position;
    internal int DesiredOutboundId;
    internal string Label;
    internal bool HasGeneratedLabel;

    internal bool IsMissing => EntityId == 0;

    internal string Serialize()
    {
        string encodedLabel = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(Label ?? string.Empty));
        return string.Join("|",
            Id.ToString(CultureInfo.InvariantCulture),
            EntityId.ToString(CultureInfo.InvariantCulture),
            Position.ToString(CultureInfo.InvariantCulture),
            DesiredOutboundId.ToString(CultureInfo.InvariantCulture),
            HasGeneratedLabel ? "1" : "0",
            encodedLabel);
    }

    internal static bool TryDeserialize(string serialized,
        out TrackedTeleporter record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(serialized)) return false;
        string[] parts = serialized.Split('|');
        if (parts.Length != 6
            || !int.TryParse(parts[0], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int id)
            || !int.TryParse(parts[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int entityId)
            || !int.TryParse(parts[2], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int position)
            || !int.TryParse(parts[3], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int desiredOutboundId))
        {
            return false;
        }

        try
        {
            record = new TrackedTeleporter
            {
                Id = id,
                EntityId = entityId,
                Position = position,
                DesiredOutboundId = desiredOutboundId,
                HasGeneratedLabel = parts[4] == "1",
                Label = Encoding.UTF8.GetString(
                    Convert.FromBase64String(parts[5]))
            };
            return id > 0 && position >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class RecoveryHistoryEntry
{
    internal long Sequence;
    internal long Tick;
    internal int PrimaryRecordId;
    internal int RelatedRecordId;
    internal string Category;
    internal string Message;

    internal string Serialize()
    {
        string encodedCategory = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(Category ?? string.Empty));
        string encodedMessage = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(Message ?? string.Empty));
        return string.Join("|", "2",
            Sequence.ToString(CultureInfo.InvariantCulture),
            Tick.ToString(CultureInfo.InvariantCulture),
            PrimaryRecordId.ToString(CultureInfo.InvariantCulture),
            RelatedRecordId.ToString(CultureInfo.InvariantCulture),
            encodedCategory,
            encodedMessage);
    }

    internal static bool TryDeserialize(string serialized,
        out RecoveryHistoryEntry entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(serialized)) return false;
        string[] parts = serialized.Split('|');
        if (parts.Length == 7 && parts[0] == "2")
        {
            return TryDeserializeVersion2(parts, out entry);
        }

        // Version 1 entries stored only tick and message. Keep every one of
        // those events visible after upgrading an existing save.
        int separator = serialized.IndexOf('|');
        if (separator <= 0
            || !long.TryParse(serialized.Substring(0, separator),
                NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long tick))
        {
            return false;
        }

        try
        {
            string message = Encoding.UTF8.GetString(Convert.FromBase64String(
                serialized.Substring(separator + 1)));
            entry = new RecoveryHistoryEntry
            {
                Tick = tick,
                Category = InferLegacyCategory(message),
                Message = message
            };
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string InferLegacyCategory(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Recorded event";
        string normalized = message.ToLowerInvariant();
        if (normalized.Contains("destroyed")
            || normalized.Contains("destruction")
            || normalized.Contains("recovered")
            || normalized.Contains("restored")
            || normalized.Contains("pending recovery"))
        {
            return "Recovery";
        }
        if (normalized.Contains("relocated")) return "Relocation";
        if (normalized.Contains("deconstruct")) return "Removal";
        if (normalized.Contains("label") || normalized.Contains("renamed"))
            return "Label";
        if (normalized.Contains("linked") || normalized.Contains("unlinked"))
            return "Connection";
        return "Recorded event";
    }

    private static bool TryDeserializeVersion2(string[] parts,
        out RecoveryHistoryEntry entry)
    {
        entry = null;
        if (!long.TryParse(parts[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long sequence)
            || !long.TryParse(parts[2], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long tick)
            || !int.TryParse(parts[3], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int primaryRecordId)
            || !int.TryParse(parts[4], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int relatedRecordId))
        {
            return false;
        }

        try
        {
            entry = new RecoveryHistoryEntry
            {
                Sequence = sequence,
                Tick = tick,
                PrimaryRecordId = primaryRecordId,
                RelatedRecordId = relatedRecordId,
                Category = Encoding.UTF8.GetString(
                    Convert.FromBase64String(parts[5])),
                Message = Encoding.UTF8.GetString(
                    Convert.FromBase64String(parts[6]))
            };
            return sequence >= 0 && tick >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
