using System;
using System.Collections.Generic;
using System.Linq;
using OrbotronUpgrade;

internal static class Program
{
    private static int assertions;
    private static int cases;
    private static uint nextId;

    private sealed class Node
    {
        internal readonly uint Id = ++nextId;
        internal object Mind = new object();
        internal object Jobs = new object();
        internal uint MindId;
        internal uint DataId;
        internal string Name;
        internal bool Eligible = true;
        internal bool Complete;
        internal bool Current = true;
        internal int Checks;
        internal int Calls;
        internal Action OnRestore;
        internal Action OnCheck;
        internal Func<RecoveryResult> Return;
        internal readonly List<BatchJob> Missing = new List<BatchJob> { new BatchJob("Construction", "Construction", false, false) };
        internal Node(string name = null) { MindId = Id; DataId = Id; Name = name ?? ("Mind " + Id); }
    }

    private static BatchCandidate Inspect(Node node)
    {
        // Fixtures supply native eligibility outcomes; they do not reimplement or
        // certify Core's Orbotron, ownership, body, faction or Unity checks.
        if (!node.Eligible) return BatchCandidate.Excluded;
        if (node.Complete) return BatchCandidate.AlreadyComplete;
        return BatchCandidate.For(new BatchTarget(node, node.Mind, node.Jobs, node.DataId, node.MindId, node.Name, node.Missing,
            () => { node.Checks++; node.OnCheck?.Invoke(); return node.Current && !node.Complete; },
            () =>
            {
                node.Calls++; node.OnRestore?.Invoke();
                var result = node.Return != null ? node.Return()
                    : new RecoveryResult(RecoveryStatus.Committed, "Done", node.Missing.Select(job => job.Id).ToArray());
                if (result != null && (result.Status == RecoveryStatus.Committed || result.Status == RecoveryStatus.CommittedRefreshFailed)) node.Complete = true;
                return result;
            }));
    }

    private static BatchRecoveryPlan<Node> Plan(List<Node> selected, Func<bool> extra = null)
    {
        var snapshot = new SelectionSnapshot<Node>(selected);
        bool created = BatchRecoveryPlan<Node>.TryCreate(snapshot, Inspect,
            () => (extra == null || extra()) && snapshot.Matches(selected), out var plan, out var error);
        Check(created, "Fixture preview must be valid: " + error);
        return plan;
    }

    private static void Check(bool expected, string message)
    { assertions++; if (!expected) throw new InvalidOperationException(message); }
    private static void Case(string name, Action run)
    { run(); cases++; Console.WriteLine("PASS " + name); }
    private static RecoveryResult Outcome(RecoveryStatus status, Node node) => new RecoveryResult(status, status.ToString(),
        status == RecoveryStatus.Committed || status == RecoveryStatus.CommittedRefreshFailed ? node.Missing.Select(job => job.Id).ToArray() : null);

    private static void Main()
    {
        Case("mixed acquisition, duplicates and nulls", () =>
        {
            var a = new Node(); var b = new Node(); var excluded = new Node { Eligible = false }; var complete = new Node { Complete = true };
            var selected = new List<Node> { a, excluded, null, a, b, complete, null };
            var plan = Plan(selected);
            Check(plan.Selection.Members.Count == 5 && plan.Targets.Count == 2, "Membership must deduplicate references.");
            Check(plan.CompleteCount == 1 && plan.ExcludedCount == 2, "Complete/excluded counts must be distinct.");
            Check(a.Calls == 0 && b.Calls == 0 && a.Checks == 0, "Acquisition must never execute or revalidate captured plans.");
            var result = plan.Execute();
            Check(result.RestoredMinds == 2 && result.AddedJobs == 2, "Count actual unique commits.");
            Check(a.Calls == 1 && b.Calls == 1 && excluded.Calls == 0 && complete.Calls == 0, "Only captured target callbacks run once.");
        });
        Case("empty, null and complete-only previews", () =>
        {
            foreach (var list in new[] { new List<Node>(), new List<Node> { null }, new List<Node> { new Node { Complete = true } }, new List<Node> { new Node { Eligible = false } } })
                Check(!BatchRecoveryPlan<Node>.TryCreate(new SelectionSnapshot<Node>(list), Inspect, () => true, out _, out _), "No eligible missing target means no preview.");
            Check(!BatchRecoveryPlan<Node>.TryCreate(null, Inspect, () => true, out _, out _), "Null snapshot must refuse.");
        });
        Case("copied membership, reorder and replacement", () =>
        {
            var a = new Node(); var b = new Node(); var selected = new List<Node> { a, b };
            var plan = Plan(selected); selected.Reverse();
            Check(plan.Selection.Members[0] == a && plan.Selection.Matches(selected), "Snapshot owns its ordered copy; reorder is the same scope.");
            Check(plan.Execute().RestoredMinds == 2, "A reorder must retain the original work.");
            a = new Node(); b = new Node(); selected = new List<Node> { a, b }; plan = Plan(selected); selected[1] = new Node();
            var refused = plan.Execute();
            Check(a.Calls == 0 && b.Calls == 0 && refused.NotAttempted == 2 && refused.StopReason != null, "Replacement must invalidate the whole confirmation before writes.");
        });
        Case("removing unrelated selected member cancels scope", () =>
        {
            var a = new Node(); var other = new Node { Eligible = false }; var selected = new List<Node> { a, other };
            var plan = Plan(selected); selected.Remove(other);
            Check(plan.Execute().NotAttempted == 1 && a.Calls == 0, "The full selection includes non-target members.");
        });
        Case("fixed subset never adds a later eligible mind", () =>
        {
            var a = new Node(); var other = new Node { Eligible = false }; var complete = new Node { Complete = true };
            var plan = Plan(new List<Node> { a, other, complete }); other.Eligible = true; complete.Complete = false;
            Check(plan.Execute().RestoredMinds == 1 && other.Calls == 0 && complete.Calls == 0, "Only the preview's fixed targets may run.");
        });
        Case("conflicting mind/jobs/persistent identities fail closed", () =>
        {
            for (int variant = 0; variant < 5; variant++)
            {
                var a = new Node(); var b = new Node();
                switch (variant) { case 0: b.Mind = a.Mind; break; case 1: b.Jobs = a.Jobs; break; case 2: b.MindId = a.MindId; break; case 3: b.DataId = a.DataId; break; default: b.MindId = 0; break; }
                Check(!BatchRecoveryPlan<Node>.TryCreate(new SelectionSnapshot<Node>(new[] { a, b }), Inspect, () => true, out _, out _), "Conflicting aliases cannot pick a target.");
                Check(a.Calls == 0 && b.Calls == 0, "Conflicting acquisition must not mutate.");
            }
        });
        Case("mismatched entity and malformed preview", () =>
        {
            var a = new Node(); var b = new Node(); var snapshot = new SelectionSnapshot<Node>(new[] { a });
            Check(!BatchRecoveryPlan<Node>.TryCreate(snapshot, _ => Inspect(b), () => true, out _, out _), "An inspector cannot redirect to an unselected entity.");
            a.Missing.Add(a.Missing[0]);
            Check(!BatchRecoveryPlan<Node>.TryCreate(snapshot, Inspect, () => true, out _, out _), "Duplicate missing IDs refuse.");
            Check(!BatchRecoveryPlan<Node>.TryCreate(snapshot, _ => null, () => true, out _, out _), "Null inspection refuses.");
            Check(!BatchRecoveryPlan<Node>.TryCreate(snapshot, _ => throw new Exception("inspection"), () => true, out _, out _), "Inspection exception refuses without a plan.");
        });
        Case("cancel, replaced preview and duplicate confirmation", () =>
        {
            var a = new Node(); var selected = new List<Node> { a }; var old = Plan(selected); old.Cancel(); var replacement = Plan(selected);
            Check(!old.CanConfirm && !old.Execute().ShowCompletion && a.Calls == 0, "Canceled/replaced confirmation is inert.");
            var first = replacement.Execute(); var again = replacement.Execute();
            Check(first.ShowCompletion && !again.ShowCompletion && a.Calls == 1, "One confirmation has one execution/completion.");
        });
        Case("stale full preflight starts none", () =>
        {
            var a = new Node(); var b = new Node(); var plan = Plan(new List<Node> { a, b }); b.Current = false;
            var result = plan.Execute();
            Check(a.Calls == 0 && b.Calls == 0 && result.NotAttempted == 2 && result.RestoredMinds == 0, "A stale confirmed target blocks all initial work.");
            Check(!plan.CanConfirm && !plan.Execute().ShowCompletion, "Refused confirmation stays consumed.");
        });
        Case("scope disposal/readiness before and during execution", () =>
        {
            bool current = true; var a = new Node(); var b = new Node(); var plan = Plan(new List<Node> { a, b }, () => current); current = false;
            Check(plan.Execute().NotAttempted == 2 && a.Calls == 0, "Disposed/unready scope before entry starts none.");
            current = true; a = new Node(); b = new Node(); a.OnRestore = () => current = false; plan = Plan(new List<Node> { a, b }, () => current);
            var result = plan.Execute();
            Check(result.RestoredMinds == 1 && result.AddedJobs == 1 && result.NotAttempted == 1 && b.Calls == 0, "Scope loss retains earlier commit and stops the remainder.");
        });
        Case("pending target drift is refused and later valid targets continue", () =>
        {
            var a = new Node(); var b = new Node(); var c = new Node(); a.OnRestore = () => b.Current = false;
            var result = Plan(new List<Node> { a, b, c }).Execute();
            Check(result.RestoredMinds == 2 && result.Count(RecoveryStatus.Rejected) == 1 && b.Calls == 0 && c.Calls == 1, "Later target must be rechecked without rechecking old committed plans.");
        });
        Case("all six result categories and actual addition counts", () =>
        {
            var statuses = new[] { RecoveryStatus.Committed, RecoveryStatus.NoChanges, RecoveryStatus.Rejected, RecoveryStatus.RolledBack, RecoveryStatus.CommittedRefreshFailed };
            var nodes = statuses.Select(_ => new Node()).ToList(); nodes.Add(new Node());
            for (int i = 0; i < statuses.Length; i++) { var node = nodes[i]; var status = statuses[i]; node.Return = () => Outcome(status, node); }
            nodes[0].Missing.Add(new BatchJob("Mining", "Mining", false, true));
            var result = Plan(nodes).Execute();
            Check(result.RestoredMinds == 2 && result.AddedJobs == 3 && result.NotAttempted == 1, "Count committed refresh-failed additions and stop afterward.");
            foreach (var status in statuses) Check(result.Count(status) == 1, "Preserve category " + status);
            Check(nodes[5].Calls == 0 && BatchRecoveryText.Title(result).Contains("refresh failed"), "Severe failure stops and remains visible.");
            var a = new Node(); var failed = new Node(); var untouched = new Node(); failed.Return = () => Outcome(RecoveryStatus.PartialFailure, failed);
            result = Plan(new List<Node> { a, failed, untouched }).Execute();
            Check(result.Count(RecoveryStatus.PartialFailure) == 1 && result.RestoredMinds == 1 && result.NotAttempted == 1, "Incomplete cleanup is not a commit or clean rollback.");
            Check(untouched.Calls == 0 && result.NeedsRebuild && BatchRecoveryText.Result(result).Contains("untouched save"), "Incomplete cleanup keeps recovery guidance.");
        });
        Case("clean refusal and rollback continue without global rollback", () =>
        {
            var a = new Node(); var rejected = new Node(); var rolled = new Node(); var b = new Node();
            rejected.Return = () => Outcome(RecoveryStatus.Rejected, rejected); rolled.Return = () => Outcome(RecoveryStatus.RolledBack, rolled);
            var result = Plan(new List<Node> { a, rejected, rolled, b }).Execute();
            Check(result.RestoredMinds == 2 && result.AddedJobs == 2 && result.NotAttempted == 0 && b.Calls == 1, "Clean local failures must not block valid targets.");
            Check(result.Count(RecoveryStatus.Rejected) == 1 && result.Count(RecoveryStatus.RolledBack) == 1, "Both failures stay visible.");
        });
        Case("consumed before validation and nested different-target execution", () =>
        {
            var a = new Node(); var b = new Node(); var selected = new List<Node> { a }; var plan = Plan(selected); var other = Plan(new List<Node> { b });
            bool consumedOnCheck = false, blockedSingle = false; BatchRecoveryReport nested = null;
            a.OnCheck = () => consumedOnCheck = plan.IsConsumed;
            a.OnRestore = () =>
            {
                nested = other.Execute();
                blockedSingle = !RecoveryInvocation.TryEnter(out var lease); lease?.Dispose();
                Check(!plan.Execute().ShowCompletion, "Reentrant same confirmation is inert.");
            };
            Check(plan.Execute().RestoredMinds == 1 && consumedOnCheck, "Consumption precedes all checks.");
            Check(blockedSingle && nested != null && !nested.ShowCompletion && b.Calls == 0, "Shared invocation gate blocks nested different-target entry.");
            Check(!RecoveryInvocation.IsActive, "Invocation gate must be released.");
            Check(Plan(new List<Node> { b }).Execute().RestoredMinds == 1, "Fresh deliberate confirmation can run afterward.");
        });
        Case("exceptions/null/invalid results stop with unconfirmed outcome", () =>
        {
            for (int variant = 0; variant < 5; variant++)
            {
                var a = new Node(); var uncertain = new Node(); var tail = new Node();
                switch (variant)
                {
                    case 0: uncertain.Return = () => throw new Exception("external callback"); break;
                    case 1: uncertain.Return = () => null; break;
                    case 2: uncertain.Return = () => new RecoveryResult((RecoveryStatus)999, "bad enum"); break;
                    case 3: uncertain.Return = () => new RecoveryResult(RecoveryStatus.Committed, "wrong job", new[] { "Unplanned" }); break;
                    default: uncertain.Return = () => new RecoveryResult(RecoveryStatus.Rejected, "invalid additions", new[] { "Construction" }); break;
                }
                var result = Plan(new List<Node> { a, uncertain, tail }).Execute();
                Check(result.RestoredMinds == 1 && result.UnknownTarget == uncertain.Name && result.NotAttempted == 1 && tail.Calls == 0, "Unknown outcome cannot imply unchanged/current commit.");
                Check(result.NeedsRebuild && BatchRecoveryText.Title(result) == "Recovery incomplete" && !RecoveryInvocation.IsActive, "Unknown result is visible and gate released.");
            }
        });
        Case("scope/validation exceptions are contained", () =>
        {
            bool throwing = false; var a = new Node(); var plan = Plan(new List<Node> { a }, () => throwing ? throw new Exception("scope") : true);
            throwing = true;
            Check(!plan.CanConfirm, "Native validation predicate must fail closed on query exceptions.");
            var result = plan.Execute();
            Check(a.Calls == 0 && result.NotAttempted == 1 && result.StopReason != null && !RecoveryInvocation.IsActive, "Scope exception starts none and releases gate.");
            a = new Node(); plan = Plan(new List<Node> { a }); a.OnCheck = () => throw new Exception("validate"); result = plan.Execute();
            Check(a.Calls == 0 && result.StopReason != null && result.UnknownTarget == null && !RecoveryInvocation.IsActive, "Read-only preflight exception is not a fabricated transaction result.");
        });
        Case("immutable target descriptions and bounded1000-selection summaries", () =>
        {
            var nodes = Enumerable.Range(0, 1000).Select(i => new Node(new string('x', 500) + i)).ToList();
            var plan = Plan(nodes); nodes[0].Missing.Add(new BatchJob("AfterPreview", "After preview", false, false));
            Check(plan.Targets[0].Missing.Count == 1, "Descriptions own their missing-job copy.");
            var preview = BatchRecoveryText.Preview(plan);
            Check(preview.Length < 4000 && preview.Contains("1000 missing job records") && preview.Contains("992 more"), "Preview must summarize the whole batch with capped examples.");
            nodes[0].Return = () => Outcome(RecoveryStatus.RolledBack, nodes[0]);
            // Keep actual fixture callbacks consistent with their confirmed description.
            nodes[0].Missing.RemoveAt(1);
            foreach (var node in nodes) node.Return = () => Outcome(RecoveryStatus.RolledBack, node);
            var result = plan.Execute(); var text = BatchRecoveryText.Result(result);
            Check(result.Count(RecoveryStatus.RolledBack) == 1000 && result.NotAttempted == 0, "All targets remain in actual work despite bounded text.");
            Check(text.Length < 4000 && text.Contains("994 more refusal details"), "Result details must be bounded with omitted count.");
        });
        Console.WriteLine("PASS " + cases + " coordinator scenarios; " + assertions + " assertions. Native eligibility, Unity, Harmony and save-file gameplay are outside this portable harness.");
    }
}
