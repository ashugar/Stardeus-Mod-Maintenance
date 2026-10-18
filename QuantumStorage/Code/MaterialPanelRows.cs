using System;
using System.Collections.Generic;

namespace Game.Systems.QuantumStorage
{
    // Presentation-only bookkeeping. Never owns or mutates inventory records.
    // Separate from Unity so that the complete reuse/rebuild decision can be tested.
    internal sealed class MaterialPanelRows<TKey, TRow>
    {
        private sealed class Entry
        {
            internal TRow Row;
            internal int Quantity;
        }

        private readonly Dictionary<TKey, Entry> entries = new Dictionary<TKey, Entry>();
        private readonly List<TKey> order = new List<TKey>();

        internal MaterialPanelRows(IList<KeyValuePair<TKey, int>> contents, IList<TRow> rows)
        {
            if (contents.Count != rows.Count) { throw new ArgumentException("Material row count mismatch."); }
            for (int i = 0; i < contents.Count; i++)
            {
                var item = contents[i];
                entries.Add(item.Key, new Entry { Row = rows[i], Quantity = item.Value });
                order.Add(item.Key);
            }
        }

        internal bool TryUpdate(IList<KeyValuePair<TKey, int>> contents,
            Func<TRow, bool> valid, Action<TRow, int> quantity, Action<TRow, int> position)
        {
            if (contents.Count != entries.Count) { return false; }
            bool reorder = false;
            var seen = new HashSet<TKey>();
            // Validate the WHOLE list before touching any widgets. Structural changes,
            // stale widgets or unexpected duplicate keys fall back to Core's rebuild.
            for (int i = 0; i < contents.Count; i++)
            {
                var key = contents[i].Key;
                if (!seen.Add(key) || !entries.TryGetValue(key, out var entry) || !valid(entry.Row)) { return false; }
                reorder |= !EqualityComparer<TKey>.Default.Equals(order[i], key);
            }
            for (int i = 0; i < contents.Count; i++)
            {
                var item = contents[i];
                var entry = entries[item.Key];
                if (entry.Quantity != item.Value)
                {
                    quantity(entry.Row, item.Value);
                    entry.Quantity = item.Value;
                }
                if (reorder) { position(entry.Row, i); }
                order[i] = item.Key;
            }
            return true;
        }
    }
}
