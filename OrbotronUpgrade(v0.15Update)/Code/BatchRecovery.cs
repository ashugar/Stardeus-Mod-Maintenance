using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace OrbotronUpgrade
{
    // These transient units are also source-linked by the portable regression harness.
    // Native eligibility, preview and mutation stay in RecoveryTarget/RecoveryService.
    internal sealed class ReferenceIdentity<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceIdentity<T> Instance = new ReferenceIdentity<T>();
        public bool Equals(T left, T right) => ReferenceEquals(left, right);
        public int GetHashCode(T value) => ReferenceEquals(value, null) ? 0 : RuntimeHelpers.GetHashCode(value);
    }

    internal sealed class SelectionSnapshot<T> where T : class
    {
        internal readonly ReadOnlyCollection<T> Members;
        private readonly HashSet<T> membership;
        internal SelectionSnapshot(IEnumerable<T> selected)
        {
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            membership = new HashSet<T>(ReferenceIdentity<T>.Instance);
            var copy = new List<T>();
            foreach (var entity in selected) if (membership.Add(entity)) copy.Add(entity);
            Members = copy.AsReadOnly();
        }
        internal bool Matches(IEnumerable<T> selected)
        {
            if (selected == null) return false;
            var current = new HashSet<T>(ReferenceIdentity<T>.Instance);
            foreach (var entity in selected)
                if (!membership.Contains(entity)) return false;
                else current.Add(entity);
            return membership.SetEquals(current);
        }
        internal int CountMatching(Func<T, bool> eligible)
        {
            if (eligible == null) throw new ArgumentNullException(nameof(eligible));
            int count = 0;
            foreach (var entity in Members)
                if (!ReferenceEquals(entity, null) && eligible(entity)) count++;
            return count;
        }
    }

    // Shared by external single and batch restore entry points, never by their
    // internal per-target calls. No game/selection reference is retained here.
    internal static class RecoveryInvocation
    {
        private static readonly object sync = new object();
        private static bool active;
        internal static bool IsActive { get { lock (sync) return active; } }
        internal static bool TryEnter(out IDisposable lease)
        {
            lock (sync)
            {
                if (active) { lease = null; return false; }
                active = true; lease = new Lease(); return true;
            }
        }
        private sealed class Lease : IDisposable
        {
            private bool disposed;
            public void Dispose()
            {
                lock (sync)
                    if (!disposed) { disposed = true; active = false; }
            }
        }
    }

    internal sealed class BatchJob
    {
        internal readonly string Id;
        internal readonly string Name;
        internal readonly bool PermanentIncapability;
        internal readonly bool LacksAbilities;
        internal BatchJob(string id, string name, bool permanent, bool lacks)
        { Id = id; Name = name ?? id; PermanentIncapability = permanent; LacksAbilities = lacks; }
    }

    internal sealed class BatchTarget
    {
        internal readonly object Entity;
        internal readonly object Mind;
        internal readonly object Jobs;
        internal readonly uint DataId;
        internal readonly uint MindId;
        internal readonly string Name;
        internal readonly ReadOnlyCollection<BatchJob> Missing;
        internal readonly Func<bool> IsCurrent;
        internal readonly Func<RecoveryResult> Restore;
        internal BatchTarget(object entity, object mind, object jobs, uint dataId, uint mindId,
            string name, IList<BatchJob> missing, Func<bool> isCurrent, Func<RecoveryResult> restore)
        {
            Entity = entity; Mind = mind; Jobs = jobs; DataId = dataId; MindId = mindId;
            Name = string.IsNullOrEmpty(name) ? "Orbotron" : name;
            Missing = new List<BatchJob>(missing ?? new BatchJob[0]).AsReadOnly();
            IsCurrent = isCurrent; Restore = restore;
        }
    }

    internal sealed class BatchCandidate
    {
        internal readonly bool Complete;
        internal readonly BatchTarget Target;
        private BatchCandidate(bool complete, BatchTarget target) { Complete = complete; Target = target; }
        internal static readonly BatchCandidate Excluded = new BatchCandidate(false, null);
        internal static readonly BatchCandidate AlreadyComplete = new BatchCandidate(true, null);
        internal static BatchCandidate For(BatchTarget target) => new BatchCandidate(false, target);
    }

    internal sealed class BatchRecoveryPlan<T> where T : class
    {
        internal readonly SelectionSnapshot<T> Selection;
        internal readonly ReadOnlyCollection<BatchTarget> Targets;
        internal readonly int CompleteCount;
        internal readonly int ExcludedCount;
        private readonly Func<bool> scopeCurrent;
        private bool consumed;
        private bool cancelled;
        internal bool IsConsumed => consumed;
        internal bool CanConfirm
        {
            get
            {
                if (consumed || RecoveryInvocation.IsActive) return false;
                try { return ScopeCurrent(); } catch { return false; }
            }
        }
        internal bool ScopeCurrent() => !cancelled && scopeCurrent();
        internal void Cancel() { cancelled = true; consumed = true; }

        private BatchRecoveryPlan(SelectionSnapshot<T> selection, List<BatchTarget> targets,
            int complete, int excluded, Func<bool> scope)
        { Selection = selection; Targets = targets.AsReadOnly(); CompleteCount = complete; ExcludedCount = excluded; scopeCurrent = scope; }

        internal static bool TryCreate(SelectionSnapshot<T> selection, Func<T, BatchCandidate> inspect,
            Func<bool> scope, out BatchRecoveryPlan<T> plan, out string error)
        {
            plan = null; error = null;
            try
            {
                if (selection == null || inspect == null || scope == null || !scope())
                    throw new InvalidOperationException("The selection or game changed. Select the group again.");
                var targets = new List<BatchTarget>();
                var minds = new HashSet<object>(ReferenceIdentity<object>.Instance);
                var jobs = new HashSet<object>(ReferenceIdentity<object>.Instance);
                var mindIds = new HashSet<uint>();
                var dataIds = new HashSet<uint>();
                int complete = 0, excluded = 0;
                foreach (var entity in selection.Members)
                {
                    var candidate = ReferenceEquals(entity, null) ? BatchCandidate.Excluded : inspect(entity);
                    if (candidate == null) throw new InvalidOperationException("A selected being could not be inspected.");
                    var target = candidate.Target;
                    if (target == null) { if (candidate.Complete) complete++; else excluded++; continue; }
                    if (!ReferenceEquals(entity, target.Entity) || target.Mind == null || target.Jobs == null
                        || target.MindId == 0 || target.DataId == 0 || target.Missing.Count == 0
                        || target.IsCurrent == null || target.Restore == null || !minds.Add(target.Mind)
                        || !jobs.Add(target.Jobs) || !mindIds.Add(target.MindId) || !dataIds.Add(target.DataId))
                        throw new InvalidOperationException("Selected mind identities overlap or changed. Select the group again.");
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var job in target.Missing)
                        if (job == null || string.IsNullOrEmpty(job.Id) || !ids.Add(job.Id))
                            throw new InvalidOperationException("A missing-job preview is inconsistent.");
                    targets.Add(target);
                }
                if (!scope()) throw new InvalidOperationException("The selection or game changed. Select the group again.");
                if (targets.Count == 0) throw new InvalidOperationException("No eligible Orbotron minds in this selection have missing configured jobs.");
                plan = new BatchRecoveryPlan<T>(selection, targets, complete, excluded, scope);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal BatchRecoveryReport Execute()
        {
            var report = new BatchRecoveryReport(Targets.Count, CompleteCount, ExcludedCount);
            if (consumed) { report.ShowCompletion = false; return report; }
            consumed = true; // Before scope, target or external callbacks, including refusal.
            if (!RecoveryInvocation.TryEnter(out var lease)) { report.ShowCompletion = false; return report; }
            using (lease)
            {
                try
                {
                    if (!ScopeCurrent()) { report.StopReason = "The selection or game changed. Open recovery again."; return report; }
                    // Read-only consent preflight: a known stale target starts no work.
                    foreach (var target in Targets)
                        if (!target.IsCurrent())
                        { report.StopReason = "A confirmed mind changed. Open recovery again; no recovery was started."; return report; }
                    foreach (var target in Targets)
                    {
                        if (!ScopeCurrent()) { report.StopReason = "The selection or game changed; remaining minds were not attempted."; break; }
                        if (!target.IsCurrent())
                        {
                            report.Record(target, new RecoveryResult(RecoveryStatus.Rejected, "This mind changed after the preview and was left unchanged."));
                            continue;
                        }
                        RecoveryResult result;
                        try { result = target.Restore(); }
                        catch (Exception ex)
                        { report.Unknown(target, "The recovery outcome could not be confirmed. " + ex.Message); break; }
                        if (!ValidResult(target, result))
                        { report.Unknown(target, "The recovery outcome could not be confirmed because an unexpected result was returned."); break; }
                        report.Record(target, result); // Record commits before any further callback.
                        if (result.Status == RecoveryStatus.PartialFailure || result.Status == RecoveryStatus.CommittedRefreshFailed) break;
                        if (!ScopeCurrent()) { report.StopReason = "The selection or game changed; remaining minds were not attempted."; break; }
                    }
                }
                catch (Exception ex) { report.StopReason = "Recovery stopped while rechecking the selection. " + ex.Message; }
            }
            return report;
        }

        private static bool ValidResult(BatchTarget target, RecoveryResult result)
        {
            if (result == null || result.AddedIds == null) return false;
            switch (result.Status)
            {
                case RecoveryStatus.Committed:
                case RecoveryStatus.CommittedRefreshFailed:
                    if (result.AddedIds.Count != target.Missing.Count) return false;
                    var expected = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var job in target.Missing) expected.Add(job.Id);
                    foreach (var id in result.AddedIds) if (id == null || !expected.Remove(id)) return false;
                    return expected.Count == 0;
                case RecoveryStatus.Rejected:
                case RecoveryStatus.NoChanges:
                case RecoveryStatus.RolledBack:
                case RecoveryStatus.PartialFailure:
                    return result.AddedIds.Count == 0;
                default: return false;
            }
        }
    }

    internal sealed class BatchTargetResult
    {
        internal readonly BatchTarget Target;
        internal readonly RecoveryResult Result;
        internal BatchTargetResult(BatchTarget target, RecoveryResult result) { Target = target; Result = result; }
    }

    internal sealed class BatchRecoveryReport
    {
        internal readonly int TargetCount;
        internal readonly int CompleteCount;
        internal readonly int ExcludedCount;
        internal BatchRecoveryReport(int targets, int complete, int excluded)
        { TargetCount = targets; CompleteCount = complete; ExcludedCount = excluded; }
        private readonly List<BatchTargetResult> results = new List<BatchTargetResult>();
        internal ReadOnlyCollection<BatchTargetResult> Results => results.AsReadOnly();
        internal bool ShowCompletion = true;
        internal string StopReason;
        internal string UnknownTarget;
        internal int AttemptedCount => results.Count + (UnknownTarget == null ? 0 : 1);
        internal int NotAttempted => TargetCount - AttemptedCount;
        internal int Count(RecoveryStatus status) { int count = 0; foreach (var item in results) if (item.Result.Status == status) count++; return count; }
        internal int RestoredMinds => Count(RecoveryStatus.Committed) + Count(RecoveryStatus.CommittedRefreshFailed);
        internal int AddedJobs { get { int count = 0; foreach (var item in results) if (item.Result.Status == RecoveryStatus.Committed || item.Result.Status == RecoveryStatus.CommittedRefreshFailed) count += item.Result.AddedIds.Count; return count; } }
        internal bool NeedsRebuild => RestoredMinds > 0 || Count(RecoveryStatus.PartialFailure) > 0 || UnknownTarget != null;
        internal void Record(BatchTarget target, RecoveryResult result) => results.Add(new BatchTargetResult(target, result));
        internal void Unknown(BatchTarget target, string reason) { UnknownTarget = target.Name; StopReason = reason + " Use the untouched save before retrying."; }
    }

    internal static class BatchRecoveryText
    {
        private const int NameLimit = 8;
        private const int JobLimit = 24;
        private const int DetailLimit = 6;
        internal static string Short(string text, int limit) => string.IsNullOrEmpty(text) ? "Orbotron" : text.Length <= limit ? text : text.Substring(0, limit - 3) + "...";
        internal static string Preview<T>(BatchRecoveryPlan<T> plan) where T : class
        {
            var text = new StringBuilder();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            int total = 0, incapable = 0, lacks = 0;
            foreach (var target in plan.Targets) foreach (var job in target.Missing)
            {
                if (!counts.ContainsKey(job.Id)) { counts.Add(job.Id, 0); names.Add(job.Id, job.Name); order.Add(job.Id); }
                counts[job.Id]++; total++;
                if (job.PermanentIncapability) incapable++; else if (job.LacksAbilities) lacks++;
            }
            text.Append("Restore ").Append(total).Append(" missing job records on ").Append(plan.Targets.Count).Append(" Orbotron minds.\n")
                .Append("Selected: ").Append(plan.Selection.Members.Count).Append(". Already complete: ").Append(plan.CompleteCount)
                .Append(". Other or unavailable beings skipped: ").Append(plan.ExcludedCount).Append(".\n\nMinds: ");
            for (int i = 0; i < plan.Targets.Count && i < NameLimit; i++)
            { if (i > 0) text.Append(", "); text.Append(Short(plan.Targets[i].Name, 48)); }
            if (plan.Targets.Count > NameLimit) text.Append(" and ").Append(plan.Targets.Count - NameLimit).Append(" more");
            text.Append(".\n\nMissing jobs (number of minds):\n");
            for (int i = 0; i < order.Count && i < JobLimit; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(Short(names[order[i]], 40)).Append(" (").Append(counts[order[i]]).Append(')');
            }
            if (order.Count > JobLimit) text.Append(", and ").Append(order.Count - JobLimit).Append(" more job types");
            text.Append(".\n");
            text.Append("\nNative restrictions: ").Append(incapable).Append(" permanently incapable; ").Append(lacks).Append(" need working body parts.\n")
                .Append("\nCapable missing skills start at level 1. Existing skills, progress and priorities are kept. New public jobs start off; a missing internal Task stays enabled. Only eligible Orbotron minds are changed.\n\nRestored skills can affect passive combat bonuses. Unknown past experience is not recovered. Save separately before applying. Earlier successful restorations remain if a later mind cannot be restored.");
            return text.ToString();
        }
        internal static string Title(BatchRecoveryReport report)
        {
            if (report.UnknownTarget != null || report.Count(RecoveryStatus.PartialFailure) > 0) return "Recovery incomplete";
            if (report.Count(RecoveryStatus.CommittedRefreshFailed) > 0) return "Jobs restored; refresh failed";
            if (report.RestoredMinds == 0) return "Jobs unchanged";
            return report.NotAttempted > 0 || report.Count(RecoveryStatus.Rejected) > 0 || report.Count(RecoveryStatus.RolledBack) > 0
                ? "Recovery completed with skips" : "Jobs restored";
        }
        internal static string Result(BatchRecoveryReport report)
        {
            var text = new StringBuilder();
            text.Append("Restored ").Append(report.RestoredMinds).Append(" Orbotron minds, adding ").Append(report.AddedJobs).Append(" missing job records.\n")
                .Append("Already complete: ").Append(report.CompleteCount).Append(". Other or unavailable beings skipped: ").Append(report.ExcludedCount).Append(".\n")
                .Append("Unchanged during recovery: ").Append(report.Count(RecoveryStatus.NoChanges)).Append(". Refused: ").Append(report.Count(RecoveryStatus.Rejected))
                .Append(". Cleanly rolled back: ").Append(report.Count(RecoveryStatus.RolledBack)).Append(".\n")
                .Append("Incomplete cleanup: ").Append(report.Count(RecoveryStatus.PartialFailure)).Append(". Restored but refresh failed: ").Append(report.Count(RecoveryStatus.CommittedRefreshFailed))
                .Append(". Not attempted: ").Append(report.NotAttempted).Append(".\n");
            if (report.UnknownTarget != null) text.Append("\nUnconfirmed outcome for ").Append(Short(report.UnknownTarget, 60)).Append(".\n");
            if (report.StopReason != null) text.Append('\n').Append(Short(report.StopReason, 360)).Append('\n');
            int shown = 0, details = 0;
            // Severe outcomes first, never hidden behind a long list of successful minds.
            for (int pass = 0; pass < 2; pass++) foreach (var item in report.Results)
            {
                bool severe = item.Result.Status == RecoveryStatus.PartialFailure || item.Result.Status == RecoveryStatus.CommittedRefreshFailed;
                bool failed = item.Result.Status == RecoveryStatus.Rejected || item.Result.Status == RecoveryStatus.RolledBack;
                if ((pass == 0 && !severe) || (pass == 1 && !failed)) continue;
                details++;
                if (shown++ < DetailLimit) text.Append("\n• ").Append(Short(item.Target.Name, 48)).Append(": ").Append(Short(item.Result.Message, 220));
            }
            if (details > DetailLimit) text.Append("\n...and ").Append(details - DetailLimit).Append(" more refusal details.");
            if (report.Count(RecoveryStatus.PartialFailure) > 0) text.Append("\n\nCleanup is incomplete. Use the untouched save before retrying.");
            if (report.Count(RecoveryStatus.CommittedRefreshFailed) > 0) text.Append("\n\nRestored jobs were retained. Save and reload before using them.");
            text.Append("\n\nExisting skill records and priorities were not reinitialized. Successful additions are retained.");
            return text.ToString();
        }
    }
}
