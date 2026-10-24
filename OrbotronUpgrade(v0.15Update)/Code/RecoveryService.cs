using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Game.Systems.Mental;
using KL.Utils;

namespace OrbotronUpgrade
{
    internal static class RecoveryContract
    {
        // The copied flag and new-slot contracts were reviewed against rc344.3226.
        internal static readonly Guid SupportedGameModule = new Guid("bf5d4d92-ad65-4f59-a00e-14a764a9eceb");
        internal static bool IsSupported => typeof(Jobs).Module.ModuleVersionId == SupportedGameModule;
    }

    internal sealed class RecoveryEntry
    {
        internal readonly JobType Job;
        internal readonly JobAffinityFlags Flags;
        internal readonly bool LacksAbilities;
        internal readonly byte Level;
        internal readonly byte Priority;
        internal RecoveryEntry(JobType job, JobAffinityFlags flags, bool lacks)
        {
            Job = job; Flags = flags; LacksAbilities = lacks;
            bool permanent = (flags & JobAffinityFlags.Incapable) != 0;
            Level = permanent ? (byte)0 : (byte)1;
            Priority = permanent ? (byte)0 : job.Id == "Task" ? (byte)3 : (byte)0;
        }
    }

    internal sealed class RecoveryPlan
    {
        internal readonly Mind Mind;
        internal readonly Jobs Jobs;
        internal readonly JobType[] ConfiguredJobs;
        internal readonly Func<int, bool> HasAbility;
        internal readonly Dictionary<int, JobSlot> ExistingSlots;
        internal readonly ReadOnlyCollection<RecoveryEntry> Entries;
        internal RecoveryPlan(Mind mind, Jobs jobs, JobType[] configured, Func<int, bool> abilities,
            Dictionary<int, JobSlot> existing, List<RecoveryEntry> entries)
        { Mind = mind; Jobs = jobs; ConfiguredJobs = configured; HasAbility = abilities; ExistingSlots = existing; Entries = entries.AsReadOnly(); }
    }

    // Deliberately internal narrow execution seams. Production supplies native target/refresh
    // callbacks; the independent validator can inject a failure after a REAL native insertion.
    internal sealed class RecoveryCallbacks
    {
        internal Func<bool> TargetStillValid;
        internal Action<RecoveryPlan, IList<JobSlot>> RefreshCommitted;
        internal Action<RecoveryPlan, IList<JobSlot>> InvalidateRolledBack;
        internal Action<int, JobSlot> AfterInsert;
    }

    internal static class RecoveryService
    {
        private static readonly HashSet<Jobs> executing = new HashSet<Jobs>();

        internal static bool TryBuildPlan(Mind mind, Jobs jobs, IList<JobType> configuredJobs,
            Func<int, bool> hasAbility, out RecoveryPlan plan, out string error)
        {
            plan = null; error = null;
            try
            {
                if (!RecoveryContract.IsSupported) throw new InvalidOperationException("This Core build has not been reviewed for recovery.");
                if (mind == null || jobs == null || !ReferenceEquals(mind.Jobs, jobs) || !ReferenceEquals(jobs.Owner, mind))
                    throw new InvalidOperationException("The mind's existing jobs and ownership do not match.");
                if (configuredJobs == null || configuredJobs.Count == 0 || hasAbility == null)
                    throw new InvalidOperationException("The current starter job configuration is unavailable.");
                // Native persistence leaves Roles null for a valid empty backstory, and native
                // late-slot affinities treat absent role/trait collections as no sources.
                var roles = mind.Experience?.Roles;
                var traits = mind.Traits?.Slots;
                var seen = new HashSet<int>();
                var configured = new JobType[configuredJobs.Count];
                var entries = new List<RecoveryEntry>();
                var existing = new Dictionary<int, JobSlot>();
                foreach (var pair in jobs.JobSlots)
                {
                    if (pair.Value == null || pair.Value.Job == null || pair.Value.Job.IdHash != pair.Key || !ReferenceEquals(pair.Value.Owner, mind))
                        throw new InvalidOperationException("An existing job record is malformed; it was left unchanged.");
                    existing.Add(pair.Key, pair.Value);
                }
                // Validate all source references even when a malformed source affects a present job.
                if (roles != null) foreach (var role in roles.Values)
                {
                    if (role == null) throw new InvalidOperationException("A role record is missing.");
                    ValidateJobs(role.LovesJobs); ValidateJobs(role.IncapableJobs);
                }
                if (traits != null) foreach (var slot in traits)
                {
                    if (slot?.Trait == null) throw new InvalidOperationException("A trait record is missing.");
                    ValidateJobs(slot.Trait.LovesJobs); ValidateJobs(slot.Trait.IncapableJobs);
                }
                for (int i = 0; i < configured.Length; i++)
                {
                    var job = configuredJobs[i];
                    if (job == null || string.IsNullOrEmpty(job.Id) || job.IdHash != Hashes.S(job.Id) || !seen.Add(job.IdHash))
                        throw new InvalidOperationException("Starter job identities are missing, duplicated or inconsistent.");
                    if ((job.Id == "Task" && (!job.Internal || !job.Unskilled))
                        || (job.Internal && job.Id != "Task"))
                        throw new InvalidOperationException("An unsupported internal starter job was found.");
                    configured[i] = job;
                    if (jobs.TryJob(job.IdHash, out _)) continue;
                    var flags = JobAffinityFlags.None;
                    if (roles != null) foreach (var role in roles.Values)
                    {
                        if (JobAffinities.Contains(role.LovesJobs, job)) flags |= JobAffinityFlags.Loves;
                        if (JobAffinities.Contains(role.IncapableJobs, job)) flags |= JobAffinityFlags.Incapable;
                    }
                    if (traits != null) foreach (var slot in traits)
                    {
                        if (JobAffinities.Contains(slot.Trait.LovesJobs, job)) flags |= JobAffinityFlags.Loves;
                        if (JobAffinities.Contains(slot.Trait.IncapableJobs, job)) flags |= JobAffinityFlags.Incapable;
                    }
                    if (job.Unskilled || (flags & JobAffinityFlags.Incapable) != 0) flags &= ~JobAffinityFlags.Loves;
                    entries.Add(new RecoveryEntry(job, flags, LacksAbility(job, hasAbility)));
                }
                plan = new RecoveryPlan(mind, jobs, configured, hasAbility, existing, entries);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static void ValidateJobs(JobType[] jobs)
        {
            if (jobs == null) return;
            foreach (var job in jobs)
                if (job == null || string.IsNullOrEmpty(job.Id) || job.IdHash != Hashes.S(job.Id))
                    throw new InvalidOperationException("A role or trait job reference is malformed.");
        }

        private static bool LacksAbility(JobType job, Func<int, bool> hasAbility)
        {
            var abilities = job.EnabledByAbilitiesH;
            if (abilities == null || abilities.Length == 0) return false;
            bool any = false;
            foreach (var ability in abilities)
            {
                bool has = hasAbility(ability);
                if (has) any = true;
                else if (!job.RequireAnyAbilities) return true;
            }
            return job.RequireAnyAbilities && !any;
        }

        internal static bool Equivalent(RecoveryPlan left, RecoveryPlan right)
        {
            if (left == null || right == null || !ReferenceEquals(left.Mind, right.Mind) || !ReferenceEquals(left.Jobs, right.Jobs)
                || left.ConfiguredJobs.Length != right.ConfiguredJobs.Length || left.Entries.Count != right.Entries.Count
                || left.ExistingSlots.Count != right.ExistingSlots.Count) return false;
            foreach (var pair in left.ExistingSlots)
                if (!right.ExistingSlots.TryGetValue(pair.Key, out var value) || !ReferenceEquals(pair.Value, value)) return false;
            for (int i = 0; i < left.ConfiguredJobs.Length; i++)
                if (!ReferenceEquals(left.ConfiguredJobs[i], right.ConfiguredJobs[i])) return false;
            for (int i = 0; i < left.Entries.Count; i++)
            {
                var a = left.Entries[i]; var b = right.Entries[i];
                if (!ReferenceEquals(a.Job, b.Job) || a.Flags != b.Flags || a.LacksAbilities != b.LacksAbilities
                    || a.Level != b.Level || a.Priority != b.Priority) return false;
            }
            return true;
        }

        internal static RecoveryResult Execute(RecoveryPlan plan, RecoveryCallbacks callbacks)
        {
            if (plan == null || callbacks?.TargetStillValid == null)
                return new RecoveryResult(RecoveryStatus.Rejected, "Recovery has no valid selected target.");
            lock (executing)
                if (!executing.Add(plan.Jobs)) return new RecoveryResult(RecoveryStatus.Rejected, "Recovery is already running for this mind.");
            var inserted = new List<JobSlot>();
            var addedIds = new List<string>();
            try
            {
                if (!callbacks.TargetStillValid()) return new RecoveryResult(RecoveryStatus.Rejected, "The selection or game state changed. Open recovery again.");
                if (!TryBuildPlan(plan.Mind, plan.Jobs, plan.ConfiguredJobs, plan.HasAbility, out var fresh, out var error))
                    return new RecoveryResult(RecoveryStatus.Rejected, error);
                if (!Equivalent(plan, fresh)) return new RecoveryResult(RecoveryStatus.Rejected, "The missing jobs or restrictions changed. Open recovery again.");
                if (plan.Entries.Count == 0) return new RecoveryResult(RecoveryStatus.NoChanges, "No configured jobs are missing.");
                try
                {
                    for (int i = 0; i < plan.Entries.Count; i++)
                    {
                        var entry = plan.Entries[i];
                        if (plan.Jobs.TryJob(entry.Job.IdHash, out _)) throw new InvalidOperationException("A planned job is now present; it was not initialized.");
                        int count = plan.Jobs.JobSlots.Count;
                        var slot = plan.Jobs.AddJob(entry.Job, 1);
                        bool returnedIsStored = plan.Jobs.TryJob(entry.Job.IdHash, out var stored) && ReferenceEquals(slot, stored);
                        if (returnedIsStored && !plan.ExistingSlots.ContainsKey(entry.Job.IdHash) && ReferenceEquals(slot.Job, entry.Job))
                            inserted.Add(slot); // Track an actual returned new reference before later invariant checks can fail.
                        if (!returnedIsStored
                            || plan.Jobs.JobSlots.Count != count + 1 || !ReferenceEquals(slot.Owner, plan.Mind) || !ReferenceEquals(slot.Job, entry.Job))
                            throw new InvalidOperationException("Native job insertion returned an unexpected record.");
                        slot.Flags = entry.Flags;
                        slot.LacksAbilities = entry.LacksAbilities;
                        slot.Priority = entry.Priority;
                        slot.LevelProgress = 0f; slot.XpToday = 0f;
                        if (entry.Level == 0) slot.Level = 0; // Only permanent native incapability, never lost parts.
                        callbacks.AfterInsert?.Invoke(i, slot);
                        if (!plan.Jobs.TryJob(entry.Job.IdHash, out stored) || !ReferenceEquals(slot, stored)
                            || slot.Level != entry.Level || slot.Priority != entry.Priority || slot.Flags != entry.Flags
                            || slot.LacksAbilities != entry.LacksAbilities || slot.LevelProgress != 0f || slot.XpToday != 0f)
                            throw new InvalidOperationException("A new job changed during initialization.");
                        addedIds.Add(entry.Job.Id);
                    }
                    if (plan.Jobs.JobSlots.Count != plan.ExistingSlots.Count + inserted.Count)
                        throw new InvalidOperationException("Unexpected job entries appeared during recovery.");
                    foreach (var old in plan.ExistingSlots)
                        if (!plan.Jobs.JobSlots.TryGetValue(old.Key, out var current) || !ReferenceEquals(current, old.Value))
                            throw new InvalidOperationException("An existing job reference changed during recovery.");
                }
                catch (Exception ex)
                {
                    bool clean = true;
                    for (int i = inserted.Count - 1; i >= 0; i--)
                    {
                        var slot = inserted[i];
                        if (plan.Jobs.JobSlots.TryGetValue(slot.Job.IdHash, out var current))
                        {
                            if (ReferenceEquals(current, slot)) plan.Jobs.JobSlots.Remove(slot.Job.IdHash);
                            else clean = false; // Never delete a foreign replacement.
                        }
                    }
                    if (plan.Jobs.JobSlots.Count != plan.ExistingSlots.Count) clean = false;
                    foreach (var old in plan.ExistingSlots)
                        if (!plan.Jobs.JobSlots.TryGetValue(old.Key, out var current) || !ReferenceEquals(current, old.Value)) clean = false;
                    try { if (inserted.Count > 0) callbacks.InvalidateRolledBack?.Invoke(plan, inserted); }
                    catch { clean = false; }
                    return new RecoveryResult(clean ? RecoveryStatus.RolledBack : RecoveryStatus.PartialFailure,
                        clean ? "Recovery stopped; its new jobs were removed. " + ex.Message
                              : "Recovery stopped with incomplete cleanup. Use the untouched save before retrying. " + ex.Message);
                }
                // Structural commit: observers may see the additions. Do not undo them if refresh fails.
                try { callbacks.RefreshCommitted?.Invoke(plan, inserted); }
                catch (Exception ex) { return new RecoveryResult(RecoveryStatus.CommittedRefreshFailed, "Jobs were restored, but their native refresh failed. Save and reload before using restored jobs. " + ex.Message, addedIds); }
                return new RecoveryResult(RecoveryStatus.Committed, "Missing jobs restored. Existing skills and priorities were kept.", addedIds);
            }
            catch (Exception ex) { return new RecoveryResult(RecoveryStatus.Rejected, "Recovery was not started. " + ex.Message); }
            finally { lock (executing) executing.Remove(plan.Jobs); }
        }
    }
}
