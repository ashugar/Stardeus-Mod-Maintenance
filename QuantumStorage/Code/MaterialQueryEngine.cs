using System.Collections.Generic;

namespace Game.Systems.QuantumStorage
{
    // The adapter supplies native policy/provider calls; this loop owns no game state.
    internal interface IMaterialQuerySource<T>
    {
        bool BeginAttempt();
        int Count { get; }
        T Candidate(int index);
        bool IsEligible(T storage);
        object PoolKey(T storage);
        int Available(T storage);
        bool ValidateAttempt();
        void EndAttempt();
    }

    internal static class MaterialQueryEngine
    {
        internal static bool HasEnough<T, TSource>(ref TSource source, int amount)
            where TSource : struct, IMaterialQuerySource<T>
        {
#if QUANTUM_RELAY_QUERY_BASELINE
            if (RelayQueryFocus.TryEnterEngine())
            {
                try { return HasEnoughFocused<T, TSource>(ref source, amount); }
                finally { RelayQueryFocus.LeaveEngine(); }
            }
#endif
            var seen = new HashSet<object>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                seen.Clear();
                try
                {
                    if (!source.BeginAttempt()) { continue; }
                    long total = 0;
                    bool enough = false;
                    for (int i = 0; i < source.Count; i++)
                    {
                        T storage = source.Candidate(i);
                        if (!source.IsEligible(storage)) { continue; }
                        object key = source.PoolKey(storage);
                        if (!seen.Add(key)) { continue; }
                        int available = source.Available(storage);
                        // Native HasEnough never admits zero/negative availability,
                        // even for a zero or negative requested amount.
                        if (available < 1) { continue; }
                        total += available;
                        if (total >= amount) { enough = true; break; }
                    }
                    if (source.ValidateAttempt()) { return enough; }
                }
                finally { source.EndAttempt(); }
            }
            // Repeated synchronous topology changes cannot yield a coherent pool
            // answer. Never fall through to Core's non-deduplicated pooled view.
            return false;
        }

#if QUANTUM_RELAY_QUERY_BASELINE
        // This detail-only loop preserves every native/source call and its order.
        // It counts the outer engine and clocks at most one candidate per query.
        private static bool HasEnoughFocused<T, TSource>(ref TSource source, int amount)
            where TSource : struct, IMaterialQuerySource<T>
        {
            HashSet<object> seen;
            long token = RelayQueryFocus.BeginStage(0);
            try { seen = new HashSet<object>(); }
            finally { RelayQueryFocus.EndStage(0, token); }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                seen.Clear();
                RelayQueryFocus.Attempt();
                try
                {
                    bool begun;
                    token = RelayQueryFocus.BeginStage(1);
                    try { begun = source.BeginAttempt(); }
                    finally { RelayQueryFocus.EndStage(1, token); }
                    if (!begun) { RelayQueryFocus.Retry(); continue; }
                    long total = 0;
                    bool enough = false;
                    long scanToken = RelayQueryFocus.BeginStage(8);
                    try
                    {
                    for (int i = 0; ; i++)
                    {
                        // Consume exactly the Count read made by the ordinary
                        // loop condition, never another metadata/provider read.
                        int count = source.Count;
                        RelayQueryFocus.CountRead(count);
                        if (i >= count) break;
                        bool witness = RelayQueryFocus.Visit();
                        T storage;
                        token = RelayQueryFocus.BeginStage(4, witness);
                        try { storage = source.Candidate(i); }
                        finally { RelayQueryFocus.EndStage(4, token); }
                        bool eligible;
                        token = RelayQueryFocus.BeginStage(5, witness);
                        try { eligible = source.IsEligible(storage); }
                        finally { RelayQueryFocus.EndStage(5, token); }
                        RelayQueryFocus.Eligible(eligible);
                        if (!eligible) continue;
                        bool added;
                        RelayQueryFocus.BeforePool();
                        object key;
                        token = RelayQueryFocus.BeginStage(6, witness);
                        try { RelayQueryFocus.ArmPoolProbe(witness); key = source.PoolKey(storage); }
                        finally { RelayQueryFocus.LeavePoolProbe(); RelayQueryFocus.EndStage(6, token); }
                        token = RelayQueryFocus.BeginStage(9, witness);
                        try { added = seen.Add(key); }
                        finally { RelayQueryFocus.EndStage(9, token); }
                        RelayQueryFocus.Pool(!added);
                        if (!added) continue;
                        int available;
                        RelayQueryFocus.BeforeAvailable();
                        token = RelayQueryFocus.BeginStage(7, witness);
                        try { available = source.Available(storage); }
                        finally { RelayQueryFocus.EndStage(7, token); }
                        RelayQueryFocus.Available(available);
                        if (available < 1) continue;
                        total += available;
                        if (total >= amount) { enough = true; break; }
                    }
                    }
                    finally { RelayQueryFocus.EndStage(8, scanToken); }
                    bool valid;
                    token = RelayQueryFocus.BeginStage(2);
                    try { valid = source.ValidateAttempt(); }
                    finally { RelayQueryFocus.EndStage(2, token); }
                    if (valid) return enough;
                    RelayQueryFocus.Retry();
                }
                finally
                {
                    token = RelayQueryFocus.BeginStage(3);
                    try { source.EndAttempt(); }
                    finally { RelayQueryFocus.EndStage(3, token); }
                }
            }
            return false;
        }
#endif
    }
}
