using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Systems.QuantumStorage.Relays
{
    // Pure, game-independent policy. Adapters supply Core's local contents and
    // reservation floors; this layer never owns a task, reservation, or item.
    internal sealed class RelayVertex
    {
        public int Id, X, Y;
        public string Kind;
        public int Range;
        public bool Active;
    }

    internal sealed class RelayBin<T>
    {
        public int Id, Capacity;
        public Dictionary<T, int> Contents = new Dictionary<T, int>();
        public Dictionary<T, int> Pinned = new Dictionary<T, int>();
    }

    internal static class RelayPolicy
    {
        internal static List<List<int>> Components(IList<RelayVertex> vertices)
        {
            // Spatial buckets avoid an all-pairs scan on large colonies.
            const int cellSize = 12;
            var buckets = new Dictionary<(int, int), List<int>>();
            var parent = Enumerable.Range(0, vertices.Count).ToArray();
            int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            int Cell(int x) => (int)Math.Floor(x / (double)cellSize);
            for (int i = 0; i < vertices.Count; i++)
            {
                var v = vertices[i];
                if (!v.Active) { continue; }
                int cx = Cell(v.X), cy = Cell(v.Y), radius = (v.Range + cellSize - 1) / cellSize;
                for (int x = cx - radius; x <= cx + radius; x++)
                for (int y = cy - radius; y <= cy + radius; y++)
                {
                    if (!buckets.TryGetValue((x, y), out var peers)) { continue; }
                    foreach (int j in peers)
                    {
                        var p = vertices[j];
                        long dx = (long)v.X - p.X, dy = (long)v.Y - p.Y;
                        int range = Math.Min(v.Range, p.Range);
                        if (v.Kind == p.Kind && dx * dx + dy * dy <= (long)range * range)
                        { parent[Root(i)] = Root(j); }
                    }
                }
                if (!buckets.TryGetValue((cx, cy), out var bucket)) { buckets[(cx, cy)] = bucket = new List<int>(); }
                bucket.Add(i);
            }
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < vertices.Count; i++)
            {
                if (!vertices[i].Active) { continue; }
                int key = Root(i);
                if (!groups.TryGetValue(key, out var group)) { groups[key] = group = new List<int>(); }
                group.Add(i);
            }
            return groups.Values.OrderBy(g => vertices[g[0]].Id).ToList();
        }

        // Build the whole plan before any mutation. A reservation pin is an
        // absolute lower bound, not extra stock. Failure leaves all bins intact.
        internal static bool TryBalance<T>(IList<RelayBin<T>> bins, IComparer<T> comparer,
            out List<Dictionary<T, int>> plan, int claimBin = -1, T claimType = default, int claimAmount = 0)
        {
            plan = new List<Dictionary<T, int>>();
            var totals = new Dictionary<T, int>();
            var used = new int[bins.Count];
            for (int i = 0; i < bins.Count; i++)
            {
                var b = bins[i];
                foreach (var p in b.Contents)
                {
                    if (p.Value < 0) { return false; }
                    totals.TryGetValue(p.Key, out int n);
                    if ((long)n + p.Value > int.MaxValue) { return false; }
                    totals[p.Key] = n + p.Value;
                }
                var target = new Dictionary<T, int>();
                foreach (var p in b.Pinned)
                {
                    int actual = b.Contents.TryGetValue(p.Key, out int count) ? count : 0;
                    if (p.Value < 0 || p.Value > actual) { return false; }
                    if (p.Value > 0) { target[p.Key] = p.Value; used[i] += p.Value; }
                }
                if (i == claimBin && claimAmount > 0)
                {
                    target.TryGetValue(claimType, out int pinned);
                    if (pinned > int.MaxValue - claimAmount) { return false; }
                    target[claimType] = pinned + claimAmount;
                    used[i] += claimAmount;
                }
                if (used[i] > b.Capacity) { return false; }
                plan.Add(target);
            }
            foreach (var target in plan)
            foreach (var p in target)
            {
                if (!totals.TryGetValue(p.Key, out int n) || n < p.Value) { return false; }
                totals[p.Key] = n - p.Value;
            }
            foreach (var type in totals.Keys.OrderBy(t => t, comparer))
            {
                int remaining = totals[type];
                while (remaining > 0)
                {
                    int bestCount = int.MaxValue, nextCount = int.MaxValue;
                    var lowest = new List<int>();
                    for (int i = 0; i < bins.Count; i++)
                    {
                        if (used[i] >= bins[i].Capacity) { continue; }
                        int count = plan[i].TryGetValue(type, out int c) ? c : 0;
                        if (count < bestCount) { nextCount = bestCount; bestCount = count; lowest.Clear(); lowest.Add(i); }
                        else if (count == bestCount) { lowest.Add(i); }
                        else { nextCount = Math.Min(nextCount, count); }
                    }
                    if (lowest.Count == 0) { return false; }
                    // Advance all least-stocked bins by whole rounds. Work scales
                    // with bin/level changes rather than individual stored units.
                    int step = Math.Min(remaining / lowest.Count, nextCount - bestCount);
                    foreach (int i in lowest) { step = Math.Min(step, bins[i].Capacity - used[i]); }
                    if (step > 0)
                    {
                        foreach (int i in lowest) { plan[i][type] = bestCount + step; used[i] += step; remaining -= step; }
                    }
                    else
                    {
                        foreach (int i in lowest.OrderBy(i => used[i]).ThenBy(i => bins[i].Id).ToArray())
                        {
                            plan[i][type] = bestCount + 1; used[i]++; remaining--;
                            if (remaining == 0) { break; }
                        }
                    }
                }
            }
            return true;
        }
    }
}
