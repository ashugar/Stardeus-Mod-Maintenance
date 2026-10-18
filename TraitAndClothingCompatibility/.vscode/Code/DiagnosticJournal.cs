using System;
using System.Collections.Generic;
using System.Linq;

namespace TraitAndClothingCompatibility {
    // Observations are never validation. Only the shipped reference defines what was reviewed.
    public sealed class DiagnosticJournal {
        private Dictionary<string, DiagnosticEntry> active = new Dictionary<string, DiagnosticEntry>(StringComparer.Ordinal);
        public IReadOnlyCollection<DiagnosticEntry> Active => active.Values;

        public List<DiagnosticEntry> Reconcile(IEnumerable<DiagnosticEntry> findings,
                List<DiagnosticEntry> history, string time, int historyLimit) {
            var next = new Dictionary<string, DiagnosticEntry>(StringComparer.Ordinal);
            var added = new List<DiagnosticEntry>();
            foreach (var finding in findings) {
                if (string.IsNullOrEmpty(finding.FindingKey)) throw new ArgumentException("A validation finding needs a stable key.");
                if (next.ContainsKey(finding.FindingKey)) continue;
                if (active.TryGetValue(finding.FindingKey, out var existing)) {
                    next.Add(finding.FindingKey, existing);
                    continue;
                }
                finding.Time = time;
                next.Add(finding.FindingKey, finding);
                // On reload, refresh the saved warning rather than accumulating duplicate history.
                history.RemoveAll(e => e.FindingKey == finding.FindingKey);
                history.Add(finding);
                added.Add(finding);
            }
            active = next;
            while (history.Count > Math.Max(0, historyLimit)) history.RemoveAt(0);
            return added;
        }

        public IEnumerable<DiagnosticEntry> History(IEnumerable<DiagnosticEntry> history) =>
            history.Reverse().Where(e => e.FindingKey == null || !active.ContainsKey(e.FindingKey));

        public void Clear(List<DiagnosticEntry> history) { history.Clear(); Reset(); }
        public void Reset() { active.Clear(); }
    }
}
