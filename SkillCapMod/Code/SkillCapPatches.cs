using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Game;
using Game.Constants;
using Game.Data;
using Game.Input;
using Game.Systems.Creatures;
using Game.Systems.Mental;
using Game.Systems.Stats;
using Game.UI;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace SkillCapMod;

/// <summary>
/// Watches all writes through Core's JobSlot.Level setter. A postfix is intentional: Core first
/// performs its normal field assignment and stat invalidation, then the mod mirrors an external
/// byte write into the exact sidecar state.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.Level), MethodType.Setter)]
internal static class LevelSetterPatch
{
    /// <summary>
    /// Harmony supplies the Core instance and setter argument. Writes originating from this mod's
    /// own projection are rejected by SkillCapRuntime's recursion guard.
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, byte value) =>
        SkillCapRuntime.ExternalLevelSet(__instance, value);
}

/// <summary>
/// Establishes vanilla fallback state as Core reconstructs each JobSlot from its byte-based
/// JobSlotData. The mod's exact save component is applied later, after the whole save is loaded.
/// </summary>
[HarmonyPatch(typeof(JobSlotData), nameof(JobSlotData.TryDeserialize))]
internal static class JobSlotDeserializePatch
{
    /// <summary>Captures only slots Core successfully deserialized.</summary>
    [HarmonyPostfix]
    private static void Postfix(bool __result, ref JobSlot slot)
    {
        if (__result) SkillCapRuntime.CaptureCoreState(slot);
    }
}

/// <summary>
/// Replaces Core's byte-capped training loop with the extended implementation. The full parameter
/// signature keeps this patch bound to the intended overload if Core later adds another one.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.TrainSkill), new[] { typeof(float), typeof(bool) })]
internal static class TrainSkillPatch
{
    /// <summary>
    /// Runs extended training and returns false so Harmony skips Core's original level-25 loop.
    /// </summary>
    [HarmonyPrefix]
    private static bool Prefix(JobSlot __instance, float xp, bool isTraining)
    {
        SkillCapRuntime.Train(__instance, xp, isTraining);
        return false;
    }
}

/// <summary>
/// Replaces Core's byte-based skill-decay loop while retaining its public JobSlot.Forget entrypoint.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.Forget), new[] { typeof(float) })]
internal static class ForgetSkillPatch
{
    /// <summary>Runs extended decay and prevents Core from applying the same XP loss a second time.</summary>
    [HarmonyPrefix]
    private static bool Prefix(JobSlot __instance, float xp)
    {
        SkillCapRuntime.Forget(__instance, xp);
        return false;
    }
}

/// <summary>
/// Corrects the efficiency returned by Core's UseSkill. Core still performs its native internal,
/// ability, and incapability checks and calls the patched TrainSkill; only its byte-based result is
/// replaced afterward.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.UseSkill), new[] { typeof(float) })]
internal static class UseSkillPatch
{
    /// <summary>
    /// Preserves Core's zero/error results and internal-job result, replacing only a successful
    /// skilled-use result with the extended curve evaluation.
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, ref float __result)
    {
        if (__result > 0f && !__instance.IsInternal)
            __result = SkillCapRuntime.Efficiency(__instance);
    }
}

/// <summary>
/// Corrects direct callers of Core's SkillEfficiency method, which otherwise evaluates the job's
/// curve with the projected byte level.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.SkillEfficiency))]
internal static class SkillEfficiencyPatch
{
    /// <summary>Retains Core's zero result for incapable jobs and replaces only valid efficiency.</summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, ref float __result)
    {
        if (!__instance.Incapable) __result = SkillCapRuntime.Efficiency(__instance);
    }
}

/// <summary>
/// Extends Core's job-selection cost formula so higher effective skill continues reducing cost.
/// Core's priority, incapability, and unskilled rules remain authoritative.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.ActionCost))]
internal static class ActionCostPatch
{
    /// <summary>
    /// Leaves Core's sentinel or unskilled result untouched when the job is not eligible; otherwise
    /// recomputes the same formula with the effective level and clamps the cost above zero.
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, ref float __result)
    {
        if (__instance.Incapable || __instance.Priority < 1 || __instance.Job.Unskilled) return;
        float priorityCost = 6000f - __instance.Priority * 1000f;
        __result = Math.Max(1f, priorityCost - (float)SkillCapRuntime.EffectiveLevel(__instance) * 10f);
    }
}

/// <summary>
/// Extends Core's weighted SkillValue property, which is used when comparing job proficiency.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.SkillValue), MethodType.Getter)]
internal static class SkillValuePatch
{
    /// <summary>Preserves Core's incapable result and substitutes effective level for its byte.</summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, ref float __result)
    {
        if (!__instance.Incapable)
            __result = (float)(1d + SkillCapRuntime.EffectiveLevel(__instance)) * __instance.Job.Importance;
    }
}

/// <summary>
/// Extends Core's integer SkillOrder property while keeping its negative sentinel values for
/// incapable and unskilled jobs.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.SkillOrder), MethodType.Getter)]
internal static class SkillOrderPatch
{
    /// <summary>Replaces only non-negative skilled results with the effective integer level.</summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, ref int __result)
    {
        if (__result >= 0 && !__instance.Job.Unskilled)
            __result = SkillCapRuntime.EffectiveInteger(__instance);
    }
}

/// <summary>
/// Replaces Core's manual level adjustment, which clamps to JobSlot.MaxLevel and casts to byte.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.AdjustLevel), new[] { typeof(sbyte) })]
internal static class AdjustLevelPatch
{
    /// <summary>
    /// Applies the signed adjustment to exact state, preserves Core's "do not lower below one"
    /// behavior for negative edits, and skips the original byte-limited method.
    /// </summary>
    [HarmonyPrefix]
    private static bool Prefix(JobSlot __instance, sbyte adj)
    {
        ExtendedSkillState state = SkillCapRuntime.State(__instance);
        BigInteger target = state.Level + adj;
        if (adj < 0 && target < BigInteger.One) target = BigInteger.One;
        SkillCapRuntime.Set(__instance, target, state.Progress);
        return false;
    }
}

/// <summary>
/// Corrects Core's progress property, whose native cap test is hard-coded to level 25.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.ShownProgress), MethodType.Getter)]
internal static class ShownProgressPatch
{
    /// <summary>Shows zero at the configured cap and exact progress everywhere else.</summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, ref float __result) =>
        __result = SkillCapRuntime.AtCap(__instance)
            ? 0f
            : (float)SkillCapRuntime.State(__instance).Progress;
}

/// <summary>
/// Corrects Core's Jobs.LevelOf convenience API so callers receive the effective level rather than
/// the compatibility byte stored in JobSlot.Level.
/// </summary>
[HarmonyPatch(typeof(Jobs), nameof(Jobs.LevelOf), new[] { typeof(int) })]
internal static class JobsLevelOfPatch
{
    /// <summary>Uses Core's own JobSlots dictionary lookup and replaces a found slot's result.</summary>
    [HarmonyPostfix]
    private static void Postfix(Jobs __instance, int jobId, ref int __result)
    {
        if (__instance.JobSlots.TryGetValue(jobId, out JobSlot slot))
            __result = SkillCapRuntime.EffectiveInteger(slot);
    }
}

/// <summary>
/// Rebuilds Core's multi-skill tooltip because several embedded level/progress fragments cannot be
/// corrected reliably with one string replacement. Ordering, colors, icons, love/incapable state,
/// unskilled markers, and important-job emphasis mirror Core's implementation.
/// </summary>
[HarmonyPatch(typeof(Jobs), nameof(Jobs.Tooltip))]
internal static class JobsTooltipPatch
{
    /// <summary>
    /// Produces the complete tooltip with exact formatted levels, assigns Harmony's result, and
    /// returns false to skip Core's byte-based formatter.
    /// </summary>
    [HarmonyPrefix]
    private static bool Prefix(Jobs __instance, ref string __result, string prepend, int[] important)
    {
        var sb = Caches.StringBuilder3;
        sb.Clear();
        bool needsBreak = false;
        if (prepend != null)
        {
            sb.Append(prepend);
            needsBreak = true;
        }

        var importantIds = new HashSet<int>(important ?? Array.Empty<int>());
        foreach (bool wantImportant in new[] { true, false })
        {
            foreach (JobSlot skill in __instance.JobSlots.Values)
            {
                if (skill.Job.Internal || importantIds.Contains(skill.Job.IdHash) != wantImportant) continue;
                if (needsBreak) sb.AppendLine();
                needsBreak = true;
                if (wantImportant) sb.Append(Texts.Accented(GlyphMap.TriangleRight)).Append(' ');
                if (skill.Incapable)
                {
                    sb.Append(Texts.Red(Texts.Strike(skill.Job.NameT)));
                    continue;
                }

                string jobName = skill.Loves ? Texts.Green(skill.Job.NameT) : skill.Job.NameT;
                if (skill.Job.Unskilled)
                {
                    sb.AppendFormat("{0}: {1}", jobName, Texts.GreenPlus);
                    continue;
                }

                ExtendedSkillState state = SkillCapRuntime.State(skill);
                sb.Append(jobName).Append(": ").Append(SkillCapMath.Format(state.Level));
                if (SkillCapRuntime.AtCap(skill))
                {
                    sb.Append(" <b>").Append(T.MaxedOut).Append("</b>");
                }
                else if (state.Progress >= 0.01d)
                {
                    string progress = $" ({Units.Percentage((float)state.Progress)}{GlyphMap.ArrowRight}"
                        + $"{SkillCapMath.Format(state.Level + BigInteger.One)})";
                    sb.Append(Texts.Grey(progress));
                }
            }
        }
        __result = sb.ToString();
        return false;
    }
}

/// <summary>Corrects Core's compact single-job tooltip with the exact level.</summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.JobTooltip), MethodType.Getter)]
internal static class JobTooltipPatch
{
    /// <summary>
    /// Leaves Core's incapable and unskilled glyphs intact; only skilled level text is replaced.
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, ref string __result)
    {
        if (__instance.Incapable || __instance.Job.Unskilled) return;
        string name = __instance.Loves ? Texts.Green(__instance.Job.NameT) : __instance.Job.NameT;
        __result = $"{name}: {Texts.Accented(SkillCapMath.Format(SkillCapRuntime.Level(__instance)))}";
    }
}

/// <summary>
/// Corrects the level output used by Core job-list widgets while preserving Core's name styling and
/// tooltip choice.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.GetNameAndLevelText))]
internal static class NameAndLevelPatch
{
    /// <summary>Replaces only the skilled <c>lvlText</c> out parameter with exact formatted text.</summary>
    [HarmonyPostfix]
    private static void Postfix(JobSlot __instance, ref string jobName, ref string lvlText, ref string tip)
    {
        if (__instance.Incapable || __instance.Job.Unskilled) return;
        lvlText = SkillCapMath.Format(SkillCapRuntime.Level(__instance));
    }
}

/// <summary>
/// Rebuilds Core's detailed skilled-job tooltip so cap and progress decisions use exact state.
/// Incapable and unskilled tooltips are delegated back to Core unchanged.
/// </summary>
[HarmonyPatch(typeof(JobSlot), nameof(JobSlot.Tooltip))]
internal static class SlotTooltipPatch
{
    /// <summary>
    /// Returns true when Core should format the tooltip; otherwise assigns an extended tooltip and
    /// returns false. Core's love reasons and daily-learning saturation warning are preserved.
    /// </summary>
    [HarmonyPrefix]
    private static bool Prefix(JobSlot __instance, ref string __result)
    {
        if (__instance.Incapable || __instance.Job.Unskilled) return true;
        ExtendedSkillState state = SkillCapRuntime.State(__instance);
        var sb = Caches.StringBuilder;
        sb.Clear();
        sb.AppendLine(Texts.Accented(__instance.Job.NameT));
        if (__instance.Loves)
        {
            sb.Append(T.VerbLoves);
            JobAffinities.AppendLovesReasons(__instance, sb);
            sb.AppendLine();
        }
        sb.Append("Level: ").Append(SkillCapMath.Format(state.Level));
        if (SkillCapRuntime.AtCap(__instance))
        {
            sb.Append(" (maximum)");
        }
        else
        {
            sb.AppendLine();
            sb.Append(T.JobLevelProgress_V1.F(Units.PercentageWithFraction2((float)state.Progress)));
            if (__instance.XpToday >= Tunables.SkillDailySoftCap)
            {
                sb.AppendLine();
                sb.Append(Texts.Grey(T.JobLearningSaturated));
            }
        }
        __result = sb.ToString();
        return false;
    }
}

/// <summary>
/// Replaces the point where Core converts a skill into a being-stat bonus. Core normally feeds the
/// projected JobSlot.Level into each JobStatSource curve; this patch feeds the effective level.
/// </summary>
[HarmonyPatch(typeof(SkillStatSource), nameof(SkillStatSource.GetEffectFor))]
internal static class SkillStatEffectPatch
{
    /// <summary>
    /// Repeats Core's stat/source/job validation, evaluates Core's own configured curve at the
    /// effective level, fills the original out parameters, and skips the byte-based method.
    /// Harmony's triple-underscore parameters expose the named private fields from SkillStatSource.
    /// </summary>
    [HarmonyPrefix]
    private static bool Prefix(Stat stat, ref bool __result, ref float toAdd, ref float limit,
        BeingData ___owner, JobType ___job, JobStatSource ___source)
    {
        limit = 0f;
        toAdd = 0f;
        if (stat.Id != ___source.StatIdH) { __result = false; return false; }
        Jobs jobs = ___owner.Mind?.Jobs;
        if (jobs == null || !jobs.TryJob(___job.IdHash, out JobSlot slot))
        {
            __result = false;
            return false;
        }
        toAdd = ___source.Curve.Evaluate((float)SkillCapRuntime.EffectiveLevel(slot));
        __result = toAdd != 0f;
        return false;
    }
}
