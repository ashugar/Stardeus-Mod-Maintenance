using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Game;
using Game.Constants;
using Game.Systems.Mental;
using Game.Systems.Stats;
using Game.UI;
using Game.Utils;

namespace SkillCapMod;

/// <summary>
/// The exact state Core cannot hold in <c>JobSlot</c>: an arbitrary-size level plus double-
/// precision progress. Core's own byte level and float progress remain populated with compatible
/// projected values for unpatched code.
/// </summary>
internal sealed class ExtendedSkillState
{
    internal BigInteger Level;
    internal double Progress;
}

/// <summary>
/// An immutable, display-only copy of one skill. UI code can safely retain this value for the
/// duration of a tooltip build without gaining a reference to the mutable sidecar state.
/// </summary>
internal readonly struct SkillDisplaySnapshot
{
    internal SkillDisplaySnapshot(BigInteger level, double progress, bool atCap)
    {
        Level = level;
        Progress = progress;
        AtCap = atCap;
    }

    internal BigInteger Level { get; }
    internal double Progress { get; }
    internal bool AtCap { get; }
    internal BigInteger NextLevel => Level + BigInteger.One;
}

/// <summary>
/// Bridges the mod's extended skill model and Core's live <see cref="JobSlot"/> objects.
/// Harmony patches call this class instead of duplicating state-management rules.
/// </summary>
internal static class SkillCapRuntime
{
    // ConditionalWeakTable makes a JobSlot's sidecar state disappear when Core discards the slot;
    // it does not keep old creatures or saves alive by itself.
    private static ConditionalWeakTable<JobSlot, ExtendedSkillState> states = new();

    // Writing the projected byte calls Core's Level setter and its Harmony postfix. This guard
    // tells that postfix not to mistake our compatibility write for a new external true level.
    [ThreadStatic] private static bool projecting;

    // The current GameSystem is the safe route from the global settings page into loaded save data.
    internal static SkillCapSys ActiveSystem { get; set; }

    /// <summary>
    /// Gets or lazily creates a slot's extended state. A newly seen slot starts from Core's byte
    /// level and float progress, which supplies a safe fallback for vanilla saves and other mods.
    /// Normalize then enforces the configured cap and refreshes Core's projected fields.
    /// </summary>
    internal static ExtendedSkillState State(JobSlot slot)
    {
        ExtendedSkillState state = states.GetValue(slot, key => new ExtendedSkillState
        {
            Level = key.Level,
            Progress = Math.Max(0d, key.LevelProgress)
        });
        Normalize(slot, state);
        return state;
    }

    /// <summary>
    /// Replaces a slot's exact state, primarily when <see cref="SkillCapSys"/> applies this mod's
    /// save component. The value is normalized, projected back into Core, and announced to Core UI
    /// and stat consumers through <see cref="Refresh"/>.
    /// </summary>
    internal static void Set(JobSlot slot, BigInteger level, double progress)
    {
        ExtendedSkillState state = states.GetValue(slot, _ => new ExtendedSkillState());
        state.Level = BigInteger.Max(BigInteger.Zero, level);
        state.Progress = Math.Max(0d, progress);
        Normalize(slot, state);
        Refresh(slot);
    }

    /// <summary>
    /// Mirrors a write made through Core's <c>JobSlot.Level</c> setter into the sidecar state. This
    /// preserves compatibility with Core tools and other mods that intentionally set a byte level.
    /// Writes made by <see cref="Normalize"/> are ignored via the projection guard.
    /// </summary>
    internal static void ExternalLevelSet(JobSlot slot, byte level)
    {
        if (projecting) return;
        ExtendedSkillState state = states.GetValue(slot, _ => new ExtendedSkillState());
        state.Level = level;
        state.Progress = Math.Max(0d, slot.LevelProgress);
        Normalize(slot, state);
    }

    /// <summary>
    /// Captures the vanilla fallback immediately after Core deserializes a <c>JobSlotData</c>.
    /// Extended save data is applied later, after Core has finished rebuilding all minds and jobs.
    /// </summary>
    internal static void CaptureCoreState(JobSlot slot)
    {
        if (slot == null) return;
        ExtendedSkillState state = states.GetValue(slot, _ => new ExtendedSkillState());
        state.Level = slot.Level;
        state.Progress = Math.Max(0d, slot.LevelProgress);
        Normalize(slot, state);
    }

    /// <summary>Returns the exact level rather than Core's projected byte.</summary>
    internal static BigInteger Level(JobSlot slot) => State(slot).Level;

    /// <summary>
    /// Takes a mutation-free snapshot for optional UI integrations. Unlike <see cref="State"/>,
    /// this method uses only <see cref="ConditionalWeakTable{TKey,TValue}.TryGetValue(TKey,out TValue)"/>:
    /// hovering cannot create a sidecar, normalize a slot, invoke UI callbacks, or write Core's
    /// byte/float compatibility fields.
    /// </summary>
    internal static SkillDisplaySnapshot DisplaySnapshot(JobSlot slot)
    {
        if (states.TryGetValue(slot, out ExtendedSkillState exact))
        {
            BigInteger cap = CapFor(slot);
            BigInteger level = BigInteger.Max(BigInteger.Zero, BigInteger.Min(cap, exact.Level));
            bool atCap = level >= cap;
            double progress = Math.Max(0d, Math.Min(1d, exact.Progress));
            return new SkillDisplaySnapshot(level, atCap ? 0d : progress, atCap);
        }

        // A missing sidecar means Core is still authoritative. Match Core's native
        // ShownProgress rule directly instead of calling that Harmony-patched property.
        BigInteger coreLevel = slot.Level;
        bool coreAtCap = slot.Level >= JobSlot.MaxLevel;
        double coreProgress = coreAtCap ? 0d : slot.LevelProgress;
        return new SkillDisplaySnapshot(coreLevel, coreProgress, coreAtCap);
    }

    /// <summary>Returns the bounded level used by patched Core gameplay formulas.</summary>
    internal static double EffectiveLevel(JobSlot slot) => SkillCapMath.EffectiveLevel(Level(slot));

    /// <summary>Tests the exact level against the applicable cap for UI and training rules.</summary>
    internal static bool AtCap(JobSlot slot) => Level(slot) >= CapFor(slot);

    /// <summary>
    /// Keeps Core's unskilled jobs at their native level-one behavior; trained jobs use the global
    /// cap selected in the main menu.
    /// </summary>
    internal static BigInteger CapFor(JobSlot slot) =>
        slot.Job.Unskilled ? BigInteger.One : SkillCapSettings.MaximumLevel;

    /// <summary>
    /// Relays a settings change to the active save system. There is no active system at the main
    /// menu, so in that case the persisted value simply takes effect when a game starts or loads.
    /// </summary>
    internal static void MaximumLevelChanged() => ActiveSystem?.ApplyMaximumToAll();

    /// <summary>
    /// Re-normalizes one loaded slot after the cap changes. Refresh is only needed if the exact
    /// level was clamped; Normalize already keeps Core's compatibility fields synchronized.
    /// </summary>
    internal static void ApplyMaximum(JobSlot slot)
    {
        ExtendedSkillState state = states.GetValue(slot, key => new ExtendedSkillState
        {
            Level = key.Level,
            Progress = Math.Max(0d, key.LevelProgress)
        });
        BigInteger before = state.Level;
        Normalize(slot, state);
        if (state.Level != before) Refresh(slot);
    }

    /// <summary>
    /// Drops all references when Core unloads the GameSystem. The next runtime starts with fresh
    /// sidecars populated from that save rather than leaking state between games.
    /// </summary>
    internal static void Reset()
    {
        states = new ConditionalWeakTable<JobSlot, ExtendedSkillState>();
        ActiveSystem = null;
    }

    /// <summary>
    /// Reimplements Core's <c>JobSlot.TrainSkill</c> against arbitrary-size levels while preserving
    /// Core's speed tunable, passion multiplier, daily soft cap, training-booth behavior, XP carry,
    /// unskilled-job rule, UI refresh, and level-up log notification.
    /// </summary>
    internal static void Train(JobSlot slot, float inputXp, bool isTraining)
    {
        if (slot == null || inputXp <= 0f || float.IsNaN(inputXp) || float.IsInfinity(inputXp)) return;
        ExtendedSkillState state = State(slot);
        BigInteger cap = CapFor(slot);

        double xp = inputXp * (double)Tunables.SkillLevelUpSpeed;
        if (!isTraining)
        {
            xp *= slot.Loves ? Tunables.SkillLearnMulLoves : Tunables.SkillLearnMulNone;
            if (slot.XpToday >= Tunables.SkillDailySoftCap)
            {
                xp *= Tunables.SkillSaturatedLearnMul;
            }
            slot.XpToday = (float)Math.Min(float.MaxValue, Math.Max(0d, slot.XpToday + xp));
        }
        if (xp <= 0d || double.IsNaN(xp) || double.IsInfinity(xp)) return;

        BigInteger before = state.Level;
        if (isTraining && state.Level.IsZero)
        {
            slot.Flags &= ~JobAffinityFlags.Incapable;
            slot.Priority = 3;
        }

        double requirement = Requirement(state.Level);
        double budget = Math.Max(0d, state.Progress) * requirement + xp;

        // Core's XP curve reaches its final point at level 24. Walk only the varying part,
        // then advance the constant-cost tail in one operation so extreme XP cannot hang.
        while (state.Level < cap && state.Level < 24 && budget >= requirement)
        {
            budget -= requirement;
            state.Level++;
            requirement = Requirement(state.Level);
        }

        if (state.Level < cap && state.Level >= 24 && budget >= requirement)
        {
            double availableAsDouble = Math.Floor(budget / requirement);
            BigInteger available = new BigInteger(availableAsDouble);
            BigInteger room = cap - state.Level;
            BigInteger steps = BigInteger.Min(available, room);
            if (steps > BigInteger.Zero)
            {
                state.Level += steps;
                if (steps == available)
                {
                    budget %= requirement;
                }
                else
                {
                    budget = Math.Min(requirement,
                        Math.Max(0d, budget - (double)steps * requirement));
                }
            }
        }

        state.Progress = state.Level >= cap
            ? Math.Min(1d, budget / requirement)
            : Math.Max(0d, Math.Min(0.999999999999d, budget / Requirement(state.Level)));
        Normalize(slot, state);
        if (state.Level != before)
        {
            Refresh(slot);
            NotifyLevelUp(slot, before, state.Level);
        }
    }

    /// <summary>
    /// Reimplements Core's <c>JobSlot.Forget</c> so skill decay removes progress and then exact
    /// levels without byte underflow. Core's constant-cost high-level tail is processed in bulk;
    /// the small varying portion still walks level by level for the original XP curve.
    /// </summary>
    internal static void Forget(JobSlot slot, float inputXp)
    {
        if (slot == null || inputXp <= 0f || float.IsNaN(inputXp) || float.IsInfinity(inputXp)) return;
        ExtendedSkillState state = State(slot);
        if (state.Level.IsZero) return;

        BigInteger before = state.Level;
        double requirement = Requirement(state.Level);
        double remaining = inputXp - Math.Max(0d, state.Progress) * requirement;
        if (remaining <= 0d)
        {
            state.Progress = -remaining / requirement;
            Normalize(slot, state);
            return;
        }

        state.Progress = 0d;
        if (state.Level > 24)
        {
            BigInteger tailLevels = state.Level - 24;
            BigInteger full = new BigInteger(Math.Floor(remaining / requirement));
            BigInteger steps = BigInteger.Min(tailLevels, full);
            state.Level -= steps;
            remaining -= (double)steps * requirement;
        }

        while (remaining > 0d && state.Level > BigInteger.Zero)
        {
            state.Level--;
            requirement = Requirement(state.Level);
            if (remaining < requirement)
            {
                state.Progress = 1d - remaining / requirement;
                remaining = 0d;
                break;
            }
            remaining -= requirement;
        }
        if (state.Level.IsZero && remaining > 0d) state.Progress = 0d;
        Normalize(slot, state);
        if (state.Level != before) Refresh(slot);
    }

    /// <summary>
    /// Evaluates the job's Core-owned SkillSpeed curve at the effective level. Unskilled jobs retain
    /// Core's fixed efficiency of one.
    /// </summary>
    internal static float Efficiency(JobSlot slot)
    {
        if (slot.Job.Unskilled) return 1f;
        return SkillCurveMath.EvaluateExtended(slot.Job.SkillSpeed, EffectiveLevel(slot));
    }

    /// <summary>
    /// Supplies integer-oriented Core APIs with the floor of the bounded effective level.
    /// </summary>
    internal static int EffectiveInteger(JobSlot slot) =>
        Math.Max(0, Math.Min(byte.MaxValue, (int)Math.Floor(EffectiveLevel(slot))));

    /// <summary>
    /// Asks Core for the XP cost of a level. Values beyond <see cref="int.MaxValue"/> use the last
    /// reachable curve input; Core's XP curve is already constant long before that boundary.
    /// </summary>
    private static double Requirement(BigInteger level)
    {
        int projected = level > int.MaxValue ? int.MaxValue : (int)level;
        return Math.Max(1d, JobSlot.XpToNextLevel(projected));
    }

    /// <summary>
    /// Clamps exact state to this job's cap, then writes a projected byte/float snapshot into Core's
    /// <c>JobSlot</c>. Keeping those fields valid is essential for untouched Core code, serialization,
    /// and other mods, while the projection guard prevents the Level-setter patch from overwriting
    /// the exact sidecar level.
    /// </summary>
    private static void Normalize(JobSlot slot, ExtendedSkillState state)
    {
        BigInteger cap = CapFor(slot);
        state.Level = BigInteger.Max(BigInteger.Zero, BigInteger.Min(cap, state.Level));
        state.Progress = Math.Max(0d, Math.Min(1d, state.Progress));
        byte projectedLevel = SkillCapMath.Project(state.Level);
        projecting = true;
        try
        {
            slot.Level = projectedLevel;
            slot.LevelProgress = (float)state.Progress;
        }
        finally
        {
            projecting = false;
        }
    }

    /// <summary>
    /// Invalidates the same downstream state Core invalidates when a skill changes. The explicit
    /// stat refresh matters when the exact level changes but its floored byte projection does not;
    /// <c>ChangedB</c> tells native UI bindings to redraw.
    /// </summary>
    private static void Refresh(JobSlot slot)
    {
        var being = slot.Owner?.Owner;
        if (being != null && slot.Job.ProvidesStats != null)
        {
            SkillStatSource.Refresh(being, slot.Job);
        }
        slot.ChangedB.Invoke();
    }

    /// <summary>
    /// Reproduces Core's player-facing level-up log behavior using the exact formatted level.
    /// A bulk high-level gain emits one message and adds a "from" suffix instead of flooding the
    /// log with one line per level.
    /// </summary>
    private static void NotifyLevelUp(JobSlot slot, BigInteger before, BigInteger after)
    {
        if (slot.Owner?.Faction == null) return;
        if (!slot.Owner.Faction.IsPlayer && slot.Owner.Owner?.Body.IsForcedToObey != true) return;
        string shown = SkillCapMath.Format(after);
        string suffix = after - before > BigInteger.One ? $" (from {SkillCapMath.Format(before)})" : string.Empty;
        if (slot.Owner.Owner != null)
        {
            string message = T.SkillUp_V3.F(string.Empty, slot.Job.NameT, shown).TrimStart() + suffix;
            A.S.Sys.Log.AddLine(slot.Owner.Owner, Texts.Green(message));
        }
        else
        {
            string message = T.SkillUp_V3.F(slot.Owner.LinkToShortName, slot.Job.NameT, shown) + suffix;
            A.S.Sys.Log.AddLine(Texts.Green(message));
        }
    }
}
