using System;
using System.Collections.Generic;
using System.Linq;

namespace TraitAndClothingCompatibility {
    public sealed class TraitChange {
        public string Id;
        public string Kind;
        public string Previous;
        public string Current;
    }

    public static class TraitChangeTracker {
        public static List<TraitChange> Compare(IDictionary<string, string> previous,
                IDictionary<string, string> current, ISet<string> shippedIds, bool firstRun) {
            var result = new List<TraitChange>();
            foreach (var pair in current.OrderBy(p => p.Key, StringComparer.Ordinal)) {
                if (!previous.TryGetValue(pair.Key, out string old)) {
                    if (!firstRun || !shippedIds.Contains(pair.Key))
                        result.Add(new TraitChange { Id = pair.Key, Kind = "New trait", Current = pair.Value });
                } else if (old != pair.Value) {
                    result.Add(new TraitChange { Id = pair.Key, Kind = "Trait changed", Previous = old, Current = pair.Value });
                }
            }
            foreach (var pair in previous.OrderBy(p => p.Key, StringComparer.Ordinal)) {
                if (!current.ContainsKey(pair.Key))
                    result.Add(new TraitChange { Id = pair.Key, Kind = "Trait removed", Previous = pair.Value });
            }
            return result;
        }
    }
}
