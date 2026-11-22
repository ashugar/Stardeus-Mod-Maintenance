using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Game.Systems.QuantumStorage.Relays;

static class Program
{
    static int assertions;
    static void Must(bool condition, string label)
    {
        assertions++; if (!condition) throw new Exception("Material query contract failed: " + label);
    }
    static void Compare(World w, string type, params int[] amounts)
    {
        foreach (int amount in amounts)
        {
            bool expected = Oracle.HasEnough(w, type, amount);
            Must(w.Query(type, amount) == expected, "slow physical-record oracle");
            Must(w.Query(type, amount, true) == expected, "broad Relay-aware fallback oracle");
            Must(w.Depth == 0 && w.Begun == w.Ended, "attempt cleanup");
        }
    }
    static void Main()
    {
        PhysicalReservations(); ProvidersAndPolicies(); CandidateDomain(); TopologyAndNesting(); EndpointScope(); RandomizedOracle();
        Console.WriteLine("Material query production engine and endpoint scope passed: " + assertions + " assertions. Fixture coverage: physical-record oracle, provider semantics, reservation edits, identity, eligibility, narrowing, retry/refusal, nested queries and scope cleanup. Actual game adapter/topology/Harmony/runtime remain separate checks.");
    }
    static void PhysicalReservations()
    {
        var w = new World(); var pool = new object();
        var a = w.Add(new Endpoint(Provider.Relay, pool).Put("steel", 40));
        var b = w.Add(new Endpoint(Provider.Relay, pool).Put("steel", 60));
        Reservation ra = a.Reserve("steel", 10), rb = b.Reserve("steel", 20);
        var stockA = a.Stock; var stockB = b.Stock; var ownerA = ra.Owner; var ownerB = rb.Owner;
        Compare(w, "steel", 0, 1, 69, 70, 71);
        Must(a.Stock["steel"] == 40 && b.Stock["steel"] == 60, "pooled reads leave physical stock");
        Must(ReferenceEquals(stockA, a.Stock) && ReferenceEquals(stockB, b.Stock) && !ReferenceEquals(stockA, stockB), "physical dictionary identities");
        Must(ReferenceEquals(ra, a.Reservations[0]) && ReferenceEquals(rb, b.Reservations[0]) && ReferenceEquals(ra.Owner, ownerA) && ReferenceEquals(rb.Owner, ownerB) && ReferenceEquals(ra.Storage, a) && ReferenceEquals(rb.Storage, b), "native reservation identities and ownership");
        ra.Amount = 35; Compare(w, "steel", 45, 46); // direct public-field write, no invalidation callback
        var extra = a.Reserve("steel", 5); Compare(w, "steel", 40, 41);
        a.Reservations.Remove(extra); Compare(w, "steel", 45, 46); // cancellation
        b.Stock["steel"] -= 10; rb.Amount -= 10; Compare(w, "steel", 45, 46); // take + reservation adjustment in the same logical tick
        a.Reserve("copper", 999); a.Reserve("steel", -100); a.Reserve("steel", 0); Compare(w, "steel", 45, 46);
        a.Reservations.Remove(ra); Compare(w, "steel", 80, 81);
        rb.Amount = 1000; w.ResetCounts(); Must(!w.Query("steel", 1), "fully reserved pool unavailable");
        Must(w.Reads == 1, "fully reserved pool is evaluated once");
        Must(ReferenceEquals(rb, b.Reservations[0]) && ReferenceEquals(rb.Owner, ownerB) && ReferenceEquals(rb.Storage, b), "mutation preserves record identity");
    }
    static void ProvidersAndPolicies()
    {
        var empty = new World(); Compare(empty, "steel", -1, 0, 1);
        Must(!empty.Query("steel", 0), "zero requested amount still requires positive native availability");
        var forbiddenOnly = new World(); var forbiddenPile = forbiddenOnly.Add(new Endpoint(Provider.Pile).Put("steel", 9)); forbiddenPile.Forbidden = true;
        Must(!forbiddenOnly.Query("steel", 1), "Core Forbidden respected for null/respecting worker");
        forbiddenOnly.RespectsForbidden = false; Must(forbiddenOnly.Query("steel", 9) && !forbiddenOnly.Query("steel", 10), "Core worker intentionally ignoring Forbidden remains eligible");
        var large = new World(); large.Add(new Endpoint(Provider.Ordinary).Put("steel", int.MaxValue - 1)); large.Add(new Endpoint(Provider.Ordinary).Put("steel", int.MaxValue - 1));
        Must(large.Query("steel", int.MaxValue), "existing long accumulation avoids integer overflow");
        var zero = new World(); var z = zero.Add(new Endpoint(Provider.Ordinary).Put("steel", 0)); Compare(zero, "steel", -1, 0, 1);
        z.Put("steel", 10); z.Reserved["steel"] = 30; Compare(zero, "steel", -1, 0, 1);
        z.Put("steel", 21); Compare(zero, "steel", 21, 22); // ordinary Core >20 shortcut deliberately retained
        var w = new World(); var relay = new object();
        var bad = w.Add(new Endpoint(Provider.Relay, relay).Put("steel", 40)); bad.Reachable = false;
        var peer = w.Add(new Endpoint(Provider.Relay, relay).Put("steel", 60));
        Must(w.Query("steel", 100), "rejected first peer cannot hide eligible peer");
        Must(bad.Reads == 0 && peer.Reads == 1, "read only admitted peer");
        peer.Safe = false; Compare(w, "steel", 1, 100);
        peer.Safe = true; peer.StoreOnly = true; Compare(w, "steel", 1, 100);
        bad.Reachable = true; bad.StoreOnly = true; Compare(w, "steel", 1, 100);
        bad.StoreOnly = false; peer.StoreOnly = false;
        var ordinary = w.Add(new Endpoint(Provider.Ordinary).Put("steel", 25)); ordinary.Reserved["steel"] = 100;
        var q = new AdvancedPool(); q.Contents["steel"] = 40; q.Reserved["steel"] = 15;
        w.Add(new Endpoint(Provider.Advanced, q)); w.Add(new Endpoint(Provider.Advanced, q));
        var pile = w.Add(new Endpoint(Provider.Pile).Put("steel", 7)); pile.Forbidden = true;
        Compare(w, "steel", 150, 151); //100 Relay +25 ordinary +25 advanced
        w.RespectsForbidden = false; Compare(w, "steel", 157, 158);
        w.RespectsForbidden = true; pile.Forbidden = false; Compare(w, "steel", 157, 158);
        bad.Reserve("steel", 10); peer.Reserve("steel", 20);
        w.LocalDepth = true; Compare(w, "steel", 127, 128); //30+40 local Relay,25 ordinary,25 advanced,7 pile
        bad.Active = false; Compare(w, "steel", 127, 128); //LocalDepth preserves local behavior independent of pooled access
        w.LocalDepth = false; Compare(w, "steel", 97, 98); //offline owner's40 excluded; active peer60-20 +25+25+7
    }
    static void CandidateDomain()
    {
        var w = new World(); w.Add(new Endpoint(Provider.Ordinary).Put("steel", 1));
        for (int i = 0; i < 5000; i++) w.Add(new Endpoint(Provider.Pile).Put("other" + i, 1));
        w.Add(new Endpoint(Provider.Pile).Put("steel", 1));
        w.ResetCounts(); Must(!w.Query("steel", 3), "narrowed empty answer"); int narrowed = w.Candidates;
        w.ResetCounts(); Must(!w.Query("steel", 3, true), "broad fallback answer"); int broad = w.Candidates;
        Must(narrowed == 2 && broad == 5002, "unrelated pile types excluded without changing answer");
    }
    static void TopologyAndNesting()
    {
        var w = new World(); var p = new object();
        var a = w.Add(new Endpoint(Provider.Relay, p).Put("steel", 40));
        var b = w.Add(new Endpoint(Provider.Relay, p).Put("steel", 60));
        Compare(w, "steel", 100, 101);
        b.Active = false; Compare(w, "steel", 40, 41); b.Active = true; Compare(w, "steel", 100, 101);
        b.Pool = new object(); b.Reachable = false; Compare(w, "steel", 40, 41); //same-tick/paused bridge split represented by actual map change
        b.Pool = p; Compare(w, "steel", 100, 101);
        w.ResetCounts(); w.AfterAvailable = (world, _) => { world.AfterAvailable = null; b.Active = false; };
        Must(!w.Query("steel", 100), "direct topology change before early-success publication discards whole attempt");
        Must(w.Begun == 2 && w.Ended == 2 && w.Depth == 0, "stable retry after direct mutation");
        b.Active = true; w.ResetCounts(); w.AfterAvailable = (world, _) => { b.X++; };
        Must(!w.Query("steel", 100), "continually unstable topology conservatively refuses");
        Must(w.Begun == 3 && w.Ended == 3, "retry bound is three attempts");
        w.AfterAvailable = null; w.ResetCounts();
        var sentinel = new Exception("fixture provider failure"); w.AfterAvailable = (_, _) => throw sentinel;
        try { w.Query("steel", 1); Must(false, "provider exception propagated"); }
        catch (Exception error) { Must(ReferenceEquals(error, sentinel), "original provider exception identity"); }
        Must(w.Depth == 0 && w.Begun == w.Ended, "provider failure cleanup");
        w.AfterAvailable = null; w.ResetCounts(); bool nested = false;
        w.AfterAvailable = (world, _) => { world.AfterAvailable = null; nested = world.Query("steel", 100); };
        Must(w.Query("steel", 100) && nested && w.Depth == 0, "nested query owns independent seen/snapshot state");
        w.ResetCounts(); w.AfterAvailable = (world, _) => { world.AfterAvailable = null; b.Active = false; Must(!world.Query("steel", 100), "nested changed-topology query"); };
        Must(!w.Query("steel", 100) && w.Depth == 0, "nested mutation invalidates outer accumulated result");
        w.AfterAvailable = null; b.Active = true; w.ResetCounts();
        Endpoint old = w.All[1]; w.AfterAvailable = (world, _) => { world.AfterAvailable = null; world.All[1] = new Endpoint(Provider.Relay, p).Put("steel", 5); };
        Must(!w.Query("steel", 100) && w.Begun == 2 && !ReferenceEquals(old, w.All[1]), "same-count identity replacement retries");
        w.AfterAvailable = null; w.ResetCounts(); w.FailBegin = true;
        Must(!w.Query("steel", 1) && w.Begun == 3 && w.Begun == w.Ended && w.Depth == 0, "failed attempt setup cleanup/refusal");
    }
    static void EndpointScope()
    {
        EndpointReadScope<Endpoint>.Reset(); var a = new Endpoint(Provider.Relay); var b = new Endpoint(Provider.Relay);
        var outer = EndpointReadScope<Endpoint>.Enter(a); Must(ReferenceEquals(EndpointReadScope<Endpoint>.Current, a), "endpoint scope enters");
        var ordinary = EndpointReadScope<Endpoint>.Enter(null); EndpointReadScope<Endpoint>.Exit(ordinary);
        Must(ReferenceEquals(EndpointReadScope<Endpoint>.Current, a), "non-Relay nested call preserves outer endpoint");
        var inner = EndpointReadScope<Endpoint>.Enter(b); Must(ReferenceEquals(EndpointReadScope<Endpoint>.Current, b), "nested endpoint replaces only current scope");
        EndpointReadScope<Endpoint>.Exit(inner); Must(ReferenceEquals(EndpointReadScope<Endpoint>.Current, a), "nested endpoint restores outer");
        EndpointReadScope<Endpoint>.Exit(default); Must(ReferenceEquals(EndpointReadScope<Endpoint>.Current, a), "default state no-op");
        Exception sentinel = new Exception("fixture scope failure"), received = null;
        var duringThrow = EndpointReadScope<Endpoint>.Enter(b);
        try { throw sentinel; } catch (Exception error) { received = error; } finally { EndpointReadScope<Endpoint>.Exit(duringThrow); }
        Must(ReferenceEquals(received, sentinel) && ReferenceEquals(EndpointReadScope<Endpoint>.Current, a), "exception identity and scope restoration");
        bool threadWasEmpty = false; var thread = new Thread(() => { threadWasEmpty = EndpointReadScope<Endpoint>.Current == null; }); thread.Start(); thread.Join();
        Must(threadWasEmpty, "thread-local endpoint view");
        var late = EndpointReadScope<Endpoint>.Enter(b); EndpointReadScope<Endpoint>.Reset();
        EndpointReadScope<Endpoint>.Exit(late); EndpointReadScope<Endpoint>.Exit(outer);
        Must(EndpointReadScope<Endpoint>.Current == null, "unload reset invalidates outstanding tokens");
        // Contents-only source view uses the exact current endpoint; an unrelated endpoint stays pooled.
        a.Put("steel", 40); b.Put("copper", 60); var pooled = new Dictionary<string, int> { ["steel"] = 40, ["copper"] = 60 };
        Dictionary<string, int> Read(Endpoint e) => ReferenceEquals(EndpointReadScope<Endpoint>.Current, e) ? e.Stock : pooled;
        outer = EndpointReadScope<Endpoint>.Enter(a);
        Must(ReferenceEquals(Read(a), a.Stock) && !Read(a).ContainsKey("copper") && ReferenceEquals(Read(b), pooled), "contents-only local type view does not create remote-only source");
        EndpointReadScope<Endpoint>.Exit(outer);
    }
    static void RandomizedOracle()
    {
        var rng = new Random(184193);
        for (int scenario = 0; scenario < 1000; scenario++)
        {
            var w = new World { LocalDepth = rng.Next(5) == 0, RespectsForbidden = rng.Next(2) == 0 };
            var pools = new[] { new object(), new object() };
            var advanced = new[] { new AdvancedPool(), new AdvancedPool() };
            foreach (var q in advanced) foreach (string type in new[] { "steel", "copper" }) { q.Contents[type] = rng.Next(80); q.Reserved[type] = rng.Next(100); }
            for (int i = 0; i < rng.Next(2, 25); i++)
            {
                var kind = (Provider)rng.Next(4); object pool = kind == Provider.Relay ? pools[rng.Next(2)] : kind == Provider.Advanced ? advanced[rng.Next(2)] : null;
                var e = w.Add(new Endpoint(kind, pool).Put("steel", rng.Next(80)).Put("copper", rng.Next(80)));
                e.Active = rng.Next(5) != 0; e.StoreOnly = rng.Next(8) == 0; e.Forbidden = rng.Next(2) == 0; e.Reachable = rng.Next(6) != 0; e.Safe = rng.Next(6) != 0;
                e.Reserved["steel"] = rng.Next(100); e.Reserved["copper"] = rng.Next(100);
                e.Reserve("steel", rng.Next(-10, 100)); e.Reserve("steel", rng.Next(-10, 40)); e.Reserve("copper", rng.Next(-10, 100));
                // A real loose pile has one type; remove the other fixture key.
                if (kind == Provider.Pile) e.Stock.Remove("steel");
            }
            Compare(w, "steel", -1, 0, 1, 20, 100, 200, 1000);
            Compare(w, "copper", -1, 0, 1, 20, 100, 200, 1000);
        }
    }
}
