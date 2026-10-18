using Game;
using KL.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AutoFactory
{
    internal readonly struct ProductionGoalTableValue
    {
        internal ProductionGoalTableValue(int goal, int priority)
        {
            Goal = Math.Max(0, goal);
            Priority = Math.Max(0, Math.Min(100, priority));
        }

        internal int Goal { get; }
        internal int Priority { get; }
    }

    internal sealed class ProductionGoalTable
    {
        internal ProductionGoalTable(string name,
            IReadOnlyDictionary<string, ProductionGoalTableValue> values)
        {
            Name = name;
            Values = values.ToDictionary(pair => pair.Key,
                pair => pair.Value, StringComparer.Ordinal);
        }

        internal string Name { get; }
        internal IReadOnlyDictionary<string, ProductionGoalTableValue> Values
        {
            get;
        }
    }

    internal readonly struct ProductionGoalTableSummary
    {
        internal ProductionGoalTableSummary(string name, int configuredCount)
        {
            Name = name;
            ConfiguredCount = configuredCount;
        }

        internal string Name { get; }
        internal int ConfiguredCount { get; }
    }

    // Purpose: Stores named Production Goal snapshots in Core's global settings
    // so one catalog is available to every playthrough.
    internal sealed class ProductionGoalTableStore
    {
        private const string PreferenceKey = "autofactory.productionGoalTables";
        private const string FormatVersion = "1";
        private readonly Dictionary<string, ProductionGoalTable> tables =
            new(StringComparer.OrdinalIgnoreCase);

        internal ProductionGoalTableStore()
        {
            The.Prefs.RegisterString(PreferenceKey, string.Empty);
            Deserialize(The.Prefs.GetStringAutoHash(PreferenceKey,
                string.Empty));
        }

        internal IReadOnlyList<ProductionGoalTableSummary> Summaries => tables
            .Values
            .OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
            .Select(table => new ProductionGoalTableSummary(table.Name,
                table.Values.Count))
            .ToArray();

        internal bool Contains(string name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && tables.ContainsKey(name.Trim());
        }

        internal bool TryGet(string name, out ProductionGoalTable table)
        {
            table = null;
            return !string.IsNullOrWhiteSpace(name)
                && tables.TryGetValue(name.Trim(), out table);
        }

        internal bool TryCreate(string name,
            IReadOnlyDictionary<string, ProductionGoalTableValue> values,
            out string savedName, out string error)
        {
            savedName = NormalizeName(name);
            if (!ValidateName(savedName, out error))
            {
                return false;
            }
            if (tables.ContainsKey(savedName))
            {
                error = "A Production Goals Table named \"" + savedName
                    + "\" already exists. Load it before using Save.";
                return false;
            }

            tables[savedName] = new ProductionGoalTable(savedName, values);
            try
            {
                Save();
            }
            catch (Exception exception)
            {
                tables.Remove(savedName);
                D.LogEx(exception,
                    "Auto Factory could not create a Production Goals Table");
                error = "The Production Goals Table could not be saved.";
                return false;
            }
            error = null;
            return true;
        }

        internal bool TryUpdate(string name,
            IReadOnlyDictionary<string, ProductionGoalTableValue> values,
            out string error)
        {
            if (!TryGet(name, out var existing))
            {
                error = "No Production Goals Table is loaded. Enter a name "
                    + "and choose Create Production Goals Table.";
                return false;
            }

            var replacement = new ProductionGoalTable(existing.Name, values);
            tables[existing.Name] = replacement;
            try
            {
                Save();
            }
            catch (Exception exception)
            {
                tables[existing.Name] = existing;
                D.LogEx(exception,
                    "Auto Factory could not update a Production Goals Table");
                error = "The Production Goals Table could not be saved.";
                return false;
            }
            error = null;
            return true;
        }

        internal bool TryDelete(string name, out string deletedName,
            out string error)
        {
            deletedName = null;
            if (!TryGet(name, out var existing))
            {
                error = "Production Goals Table \"" + name
                    + "\" no longer exists.";
                return false;
            }

            tables.Remove(existing.Name);
            try
            {
                Save();
            }
            catch (Exception exception)
            {
                tables[existing.Name] = existing;
                D.LogEx(exception,
                    "Auto Factory could not delete a Production Goals Table");
                error = "The Production Goals Table could not be deleted.";
                return false;
            }

            deletedName = existing.Name;
            error = null;
            return true;
        }

        private static string NormalizeName(string name)
        {
            return name?.Trim() ?? string.Empty;
        }

        private static bool ValidateName(string name, out string error)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Enter a label before creating a Production Goals Table.";
                return false;
            }
            if (name.Length > 64)
            {
                error = "Production Goals Table labels are limited to 64 characters.";
                return false;
            }
            error = null;
            return true;
        }

        private void Save()
        {
            var plainText = new StringBuilder();
            plainText.Append("V|").Append(FormatVersion).Append('\n');
            foreach (var table in tables.Values.OrderBy(
                         value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                plainText.Append("T|").Append(Encode(table.Name)).Append('\n');
                foreach (var pair in table.Values.OrderBy(
                             value => value.Key, StringComparer.Ordinal))
                {
                    plainText.Append("E|").Append(Encode(pair.Key)).Append('|')
                        .Append(pair.Value.Goal).Append('|')
                        .Append(pair.Value.Priority).Append('\n');
                }
            }
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                plainText.ToString()));
            The.Prefs.SetString(PreferenceKey, encoded);
            The.SaveGlobalSettings();
        }

        private void Deserialize(string encoded)
        {
            tables.Clear();
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return;
            }

            try
            {
                var plainText = Encoding.UTF8.GetString(
                    Convert.FromBase64String(encoded));
                ProductionGoalTableBuilder current = null;
                foreach (var line in plainText.Split(new[] { '\n' },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = line.TrimEnd('\r').Split('|');
                    if (fields.Length == 2 && fields[0] == "V")
                    {
                        if (fields[1] != FormatVersion)
                        {
                            D.Warn("Auto Factory ignored Production Goals "
                                + "Tables with unsupported format version {0}.",
                                fields[1]);
                            tables.Clear();
                            return;
                        }
                        continue;
                    }
                    if (fields.Length == 2 && fields[0] == "T")
                    {
                        Commit(current);
                        var name = NormalizeName(Decode(fields[1]));
                        current = ValidateName(name, out _)
                            ? new ProductionGoalTableBuilder(name)
                            : null;
                        continue;
                    }
                    if (current != null && fields.Length == 4
                        && fields[0] == "E"
                        && int.TryParse(fields[2], out var goal)
                        && int.TryParse(fields[3], out var priority))
                    {
                        var key = Decode(fields[1]);
                        if (!string.IsNullOrWhiteSpace(key)
                            && (goal > 0 || priority > 0))
                        {
                            current.Values[key] = new ProductionGoalTableValue(
                                goal, priority);
                        }
                    }
                }
                Commit(current);
            }
            catch (Exception exception)
            {
                tables.Clear();
                D.LogEx(exception,
                    "Auto Factory could not read saved Production Goals Tables");
            }
        }

        private void Commit(ProductionGoalTableBuilder builder)
        {
            if (builder != null && !tables.ContainsKey(builder.Name))
            {
                tables[builder.Name] = new ProductionGoalTable(builder.Name,
                    builder.Values);
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private sealed class ProductionGoalTableBuilder
        {
            internal ProductionGoalTableBuilder(string name)
            {
                Name = name;
            }

            internal string Name { get; }
            internal Dictionary<string, ProductionGoalTableValue> Values { get; }
                = new(StringComparer.Ordinal);
        }
    }
}
