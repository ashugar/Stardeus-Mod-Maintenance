using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Systems.QuantumStorage.Relays
{
    // Pure identity-preserving adapter over the SAME quantity allocator used by
    // material Relays. Produces an ownership plan; never clones or edits records.
    internal sealed class RelayRecord<T>
    {
        internal T Record;
        internal int Owner;
        internal string Kind;
        internal bool Pinned;
    }

    internal static class RelayObjectPolicy
    {
        internal static bool TryPlan<T>(IList<RelayBin<string>> bins, IList<RelayRecord<T>> records,
            out Dictionary<T, int> owners)
        {
            owners = null;
            var byId = bins.ToDictionary(b => b.Id);
            foreach (var b in bins) { b.Contents = new Dictionary<string, int>(); b.Pinned = new Dictionary<string, int>(); }
            var unique = new HashSet<T>();
            foreach (var r in records)
            {
                if (!unique.Add(r.Record) || r.Kind == null || !byId.TryGetValue(r.Owner, out var bin)) { return false; }
                bin.Contents.TryGetValue(r.Kind, out int count); bin.Contents[r.Kind] = count + 1;
                if (r.Pinned) { bin.Pinned.TryGetValue(r.Kind, out count); bin.Pinned[r.Kind] = count + 1; }
            }
            if (!RelayPolicy.TryBalance(bins, StringComparer.Ordinal, out var plan)) { return false; }
            var result = new Dictionary<T, int>();
            // Keep pinned identities first, then retain as many existing owners
            // as the allocation allows. Only whole surplus records change owner.
            foreach (var r in records.OrderByDescending(r => r.Pinned))
            {
                int i = -1;
                for (int j = 0; j < bins.Count; j++) { if (bins[j].Id == r.Owner) { i = j; break; } }
                if (plan[i].TryGetValue(r.Kind, out int n) && n > 0)
                { result.Add(r.Record, r.Owner); plan[i][r.Kind] = n - 1; }
                else if (r.Pinned) { return false; }
            }
            foreach (var r in records)
            {
                if (result.ContainsKey(r.Record)) { continue; }
                for (int i = 0; i < bins.Count; i++)
                {
                    if (!plan[i].TryGetValue(r.Kind, out int n) || n < 1) { continue; }
                    result.Add(r.Record, bins[i].Id); plan[i][r.Kind] = n - 1; break;
                }
                if (!result.ContainsKey(r.Record)) { return false; }
            }
            owners = result;
            return true;
        }
    }
}
