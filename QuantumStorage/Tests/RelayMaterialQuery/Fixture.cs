using System;
using System.Collections.Generic;
using System.Linq;
using Game.Systems.QuantumStorage;

enum Provider { Ordinary, Relay, Advanced, Pile }
sealed class Reservation
{
    internal string Type;
    internal int Amount;
    internal object Owner = new object();
    internal Endpoint Storage;
}
sealed class AdvancedPool
{
    internal readonly Dictionary<string, int> Contents = new Dictionary<string, int>();
    internal readonly Dictionary<string, int> Reserved = new Dictionary<string, int>();
}
sealed class Endpoint
{
    internal Provider Kind;
    internal object Pool;
    internal readonly Dictionary<string, int> Stock = new Dictionary<string, int>();
    internal readonly Dictionary<string, int> Reserved = new Dictionary<string, int>();
    internal readonly List<Reservation> Reservations = new List<Reservation>();
    internal bool Active = true, StoreOnly, Forbidden, Reachable = true, Safe = true;
    internal int X, Range = 12;
    internal string Network = "materials";
    internal string PileType;
    internal int Reads;
    internal Endpoint(Provider kind, object pool = null) { Kind = kind; Pool = pool; }
    internal Endpoint Put(string type, int count) { Stock[type] = count; if (Kind == Provider.Pile) PileType = type; return this; }
    internal Reservation Reserve(string type, int amount)
    {
        var r = new Reservation { Type = type, Amount = amount, Storage = this };
        Reservations.Add(r); return r;
    }
}
sealed class World
{
    internal readonly List<Endpoint> All = new List<Endpoint>();
    internal bool RespectsForbidden = true, LocalDepth, FailBegin;
    internal int Begun, Ended, Depth, Candidates, Keys, Reads, Validations;
    internal Action<World, Endpoint> AfterAvailable;
    internal Endpoint Add(Endpoint endpoint) { All.Add(endpoint); return endpoint; }
    internal bool Query(string type, int amount, bool broad = false)
    {
        var source = new Source(this, type, broad);
        return MaterialQueryEngine.HasEnough<Endpoint, Source>(ref source, amount);
    }
    internal void ResetCounts()
    {
        Begun = Ended = Candidates = Keys = Reads = Validations = 0;
        foreach (var e in All) e.Reads = 0;
    }
    internal readonly struct Stamp
    {
        internal readonly Endpoint Endpoint;
        internal readonly object Pool;
        internal readonly bool Active;
        internal readonly int X, Range;
        internal readonly string Network;
        internal readonly Provider Kind;
        internal Stamp(Endpoint e) { Endpoint = e; Pool = e.Pool; Active = e.Active; X = e.X; Range = e.Range; Network = e.Network; Kind = e.Kind; }
        internal bool Matches(Endpoint e) => ReferenceEquals(Endpoint, e) && ReferenceEquals(Pool, e.Pool) && Active == e.Active && X == e.X && Range == e.Range && Network == e.Network && Kind == e.Kind;
    }
    internal Stamp[] Capture() => All.Select(e => new Stamp(e)).ToArray();
    internal bool Matches(Stamp[] snapshot)
    {
        if (snapshot == null || snapshot.Length != All.Count) return false;
        for (int i = 0; i < All.Count; i++) if (!snapshot[i].Matches(All[i])) return false;
        return true;
    }
}
struct Source : IMaterialQuerySource<Endpoint>
{
    private readonly World world;
    private readonly string type;
    private Endpoint[] candidates;
    private readonly bool broad;
    private World.Stamp[] snapshot;
    private bool entered;
    internal Source(World world, string type, bool broad)
    {
        this.world = world; this.type = type;
        this.broad = broad; candidates = null;
        snapshot = null; entered = false;
    }
    public bool BeginAttempt()
    {
        world.Begun++; world.Depth++; entered = true;
        string requestedType = type;
        candidates = (broad ? world.All : world.All.Where(e => e.Kind != Provider.Pile).Concat(world.All.Where(e => e.Kind == Provider.Pile && e.PileType == requestedType))).ToArray();
        snapshot = world.Capture(); return !world.FailBegin;
    }
    public int Count => candidates.Length;
    public Endpoint Candidate(int index) { world.Candidates++; return candidates[index]; }
    public bool IsEligible(Endpoint e) => !e.StoreOnly && !(e.Kind == Provider.Pile && e.Forbidden && world.RespectsForbidden) && e.Reachable && e.Safe;
    public object PoolKey(Endpoint e)
    {
        world.Keys++;
        if (e.Kind == Provider.Relay) return !world.LocalDepth && e.Active ? e.Pool ?? e : e;
        return e.Kind == Provider.Advanced ? e.Pool ?? e : e;
    }
    public int Available(Endpoint e)
    {
        world.Reads++; e.Reads++;
        int n;
        if (e.Kind == Provider.Relay)
        {
            if (world.LocalDepth) n = Math.Max(0, Stored(e) - PositiveReservations(e));
            else if (!e.Active) n = 0;
            else
            {
                long stock = 0, reserved = 0;
                foreach (var member in world.All)
                    if (member.Kind == Provider.Relay && member.Active && ReferenceEquals(member.Pool, e.Pool))
                    { stock += Stored(member); reserved += PositiveReservations(member); }
                n = (int)Math.Max(0, stock - reserved);
            }
        }
        else if (e.Kind == Provider.Advanced)
        {
            var q = (AdvancedPool)e.Pool;
            q.Contents.TryGetValue(type, out int stored); q.Reserved.TryGetValue(type, out int reserved);
            n = Math.Max(0, stored - reserved);
        }
        else
        {
            int stored = Stored(e); e.Reserved.TryGetValue(type, out int reserved);
            n = e.Kind == Provider.Ordinary && stored > 20 ? stored : stored - reserved;
        }
        world.AfterAvailable?.Invoke(world, e);
        return n;
    }
    private int Stored(Endpoint e) => e.Stock.TryGetValue(type, out int count) ? count : 0;
    private int PositiveReservations(Endpoint e)
    {
        int n = 0; foreach (var r in e.Reservations) if (r.Type == type && r.Amount > 0) n += r.Amount;
        return n;
    }
    public bool ValidateAttempt() { world.Validations++; return world.Matches(snapshot); }
    public void EndAttempt() { world.Ended++; if (entered) { world.Depth--; entered = false; } snapshot = null; }
}
static class Oracle
{
    // Independent broad physical-record calculation. It does not call the engine,
    // Source eligibility/key/provider methods, or fixture's narrowed candidate domain.
    internal static bool HasEnough(World w, string type, int wanted)
    {
        var admitted = new Dictionary<object, long>();
        foreach (var endpoint in w.All)
        {
            if (endpoint.StoreOnly || !endpoint.Reachable || !endpoint.Safe) continue;
            if (endpoint.Kind == Provider.Pile && endpoint.Forbidden && w.RespectsForbidden) continue;
            object key = endpoint;
            if (endpoint.Kind == Provider.Advanced) key = endpoint.Pool;
            if (endpoint.Kind == Provider.Relay && !w.LocalDepth && endpoint.Active) key = endpoint.Pool;
            if (admitted.ContainsKey(key)) continue;
            long available = 0;
            if (endpoint.Kind == Provider.Relay)
            {
                foreach (var owner in w.All)
                {
                    bool include = w.LocalDepth ? ReferenceEquals(owner, endpoint) : endpoint.Active && owner.Kind == Provider.Relay && owner.Active && ReferenceEquals(owner.Pool, endpoint.Pool);
                    if (!include) continue;
                    foreach (var pair in owner.Stock) if (pair.Key == type) available += pair.Value;
                    foreach (var record in owner.Reservations) if (record.Type == type && record.Amount > 0) available -= record.Amount;
                }
                available = Math.Max(0, available);
            }
            else if (endpoint.Kind == Provider.Advanced)
            {
                var q = (AdvancedPool)endpoint.Pool;
                foreach (var pair in q.Contents) if (pair.Key == type) available += pair.Value;
                foreach (var pair in q.Reserved) if (pair.Key == type) available -= pair.Value;
                available = Math.Max(0, available);
            }
            else
            {
                foreach (var pair in endpoint.Stock) if (pair.Key == type) available += pair.Value;
                if (endpoint.Kind != Provider.Ordinary || available <= 20)
                    foreach (var pair in endpoint.Reserved) if (pair.Key == type) available -= pair.Value;
            }
            admitted.Add(key, available);
        }
        long total = 0; bool anyPositive = false;
        foreach (long value in admitted.Values) if (value > 0) { anyPositive = true; total += value; }
        return anyPositive && total >= wanted;
    }
}
