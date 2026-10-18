using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using Game;
using Game.Constants;
using Game.Data;
using Game.Input;
using Game.Systems.Creatures;
using Game.Systems.Mental;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace SkillCapMod;

/// <summary>
/// Installs the guarded Job Matrix tooltip integration separately from the required skill patches.
/// This class intentionally has no Harmony patch attributes: <see cref="SkillCapSys"/> calls
/// <see cref="TryInstall"/> only after its required <c>PatchAll</c> succeeds, and an unfamiliar Core
/// implementation therefore leaves the native tooltip intact without disabling the mod.
/// </summary>
internal static class SkillCapOptionalPatches
{
    internal const string HarmonyId = "jleas.stardeus.skillcapmod.optional.jobmatrix";

    private static readonly Guid ReviewedGameModule =
        new("59f15392-56b9-48f4-960a-81ba580dce1b");

    private const string ReviewedOuterIl =
        "D359170098F77845EE4F5AC0276CCDC874186616646F76C3A805A4C7618339F6";

    private const string ReviewedClosureIl =
        "E004176D93E1E8F78AAED098AA6EAD05A60514597EA15B69C4460D64D86F3B9F";

    /// <summary>
    /// Verifies the exact reviewed Core method and generated tooltip closure before installing the
    /// optional postfix. Every failure is caught here and reduced to one warning, so required skill
    /// progression remains active while Core keeps ownership of the unmodified tooltip delegate.
    /// </summary>
    internal static bool TryInstall(Harmony harmony)
    {
        try
        {
            MethodInfo target = FindAndValidateTarget();
            ValidateReviewedImplementation(target);
            MethodInfo postfix = typeof(SkillCapOptionalPatches).GetMethod(nameof(Postfix),
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("postfix method missing");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix)
            {
                priority = HarmonyLib.Priority.Last
            });
            return true;
        }
        catch (Exception ex)
        {
            // A defensive cleanup keeps the optional owner fail-closed even if Harmony reports an
            // exception after beginning its single patch operation. Cleanup failures stay isolated.
            try
            {
                harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // Required patches use another owner and must never be affected by optional cleanup.
            }
            D.Warn("Skill Cap Mod Job Matrix hover patch skipped; native tooltip retained ({0}).",
                ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Resolves only the private instance overload reviewed for Core rc335.3217. The redundant
    /// shape checks make a visibility, return-type, or parameter change fail closed even if a future
    /// reflection binder happens to return another method.
    /// </summary>
    private static MethodInfo FindAndValidateTarget()
    {
        Type[] signature =
        {
            typeof(BeingData),
            typeof(JobSlot),
            typeof(WJobMatrixPriority)
        };
        MethodInfo target = typeof(JobMatrixHandler).GetMethod("AddCellTooltip",
            BindingFlags.Instance | BindingFlags.NonPublic, null, signature, null);
        if (target == null) throw new InvalidOperationException("exact target signature missing");
        ParameterInfo[] parameters = target.GetParameters();
        if (!target.IsPrivate || target.IsStatic || target.ReturnType != typeof(void)
            || parameters.Length != signature.Length
            || parameters.Where((parameter, index) => parameter.ParameterType != signature[index]).Any())
        {
            throw new InvalidOperationException("exact target shape changed");
        }
        if (target.Module.ModuleVersionId != ReviewedGameModule)
            throw new InvalidOperationException("Game.dll module is not the reviewed build");
        if (!string.Equals(HashRawIl(target), ReviewedOuterIl, StringComparison.Ordinal))
            throw new InvalidOperationException("outer method IL changed");
        return target;
    }

    /// <summary>
    /// Uses Harmony's view of the original, unpatched instructions to find the compiler-generated
    /// delegate by its single <c>ldftn</c>, then validates both its raw body and stable semantic
    /// landmarks. No compiler-generated method name is trusted as an identity check.
    /// </summary>
    private static void ValidateReviewedImplementation(MethodInfo target)
    {
        List<CodeInstruction> outer = PatchProcessor.GetOriginalInstructions(target);
        MethodInfo[] closures = outer
            .Where(instruction => instruction.opcode == OpCodes.Ldftn)
            .Select(instruction => instruction.operand as MethodInfo)
            .Where(method => method != null)
            .ToArray();
        if (closures.Length != 1)
            throw new InvalidOperationException("tooltip closure shape changed");

        MethodInfo setTooltip = typeof(WJobMatrixPriority).GetMethod(nameof(WJobMatrixPriority.SetTooltip),
            BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Func<string>) }, null)
            ?? throw new InvalidOperationException("SetTooltip signature missing");
        if (CountCalls(outer, setTooltip) != 1)
            throw new InvalidOperationException("SetTooltip call shape changed");

        MethodInfo closure = closures[0];
        if (closure.ReturnType != typeof(string) || closure.GetParameters().Length != 0)
            throw new InvalidOperationException("tooltip closure signature changed");
        if (!string.Equals(HashRawIl(closure), ReviewedClosureIl, StringComparison.Ordinal))
            throw new InvalidOperationException("tooltip closure IL changed");

        List<CodeInstruction> closureInstructions = PatchProcessor.GetOriginalInstructions(closure);
        MethodInfo levelGetter = typeof(JobSlot).GetProperty(nameof(JobSlot.Level))?.GetGetMethod()
            ?? throw new InvalidOperationException("Level getter missing");
        MethodInfo shownProgressGetter = typeof(JobSlot)
            .GetProperty(nameof(JobSlot.ShownProgress))?.GetGetMethod()
            ?? throw new InvalidOperationException("ShownProgress getter missing");
        if (CountCalls(closureInstructions, levelGetter) != 2
            || CountCalls(closureInstructions, shownProgressGetter) != 2)
        {
            throw new InvalidOperationException("tooltip skill reads changed");
        }
    }

    /// <summary>
    /// Counts calls to one exact metadata method, accepting either <c>call</c> or
    /// <c>callvirt</c>. Module plus metadata token avoids loose name-only matching.
    /// </summary>
    private static int CountCalls(IEnumerable<CodeInstruction> instructions, MethodInfo expected) =>
        instructions.Count(instruction =>
            (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
            && instruction.operand is MethodInfo called
            && called.Module == expected.Module
            && called.MetadataToken == expected.MetadataToken);

    /// <summary>
    /// Hashes the CLR method body's untouched byte stream. This intentionally differs from the
    /// semantic checks above: the hash pins the reviewed rc335.3217 implementation byte-for-byte,
    /// while the instruction checks document the particular tooltip behavior relied upon.
    /// </summary>
    private static string HashRawIl(MethodInfo method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException("method has no IL body");
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(il)).Replace("-", string.Empty);
    }

    /// <summary>
    /// Runs last after Core's private <c>AddCellTooltip</c> has assigned its native delegate, then
    /// replaces only that delegate. Positional arguments deliberately bind the exact reviewed
    /// signature without depending on Core parameter names.
    /// </summary>
    private static void Postfix(BeingData __0, JobSlot __1, WJobMatrixPriority __2)
    {
        __2.SetTooltip(() => BuildTooltip(__0, __1));
    }

    /// <summary>
    /// Reproduces Core rc335.3217's current Job Matrix tooltip ordering and styling. The only
    /// substituted values are level, visible progress, and next level, all read from a mutation-free
    /// immutable snapshot and formatted through <see cref="SkillCapMath.Format"/>.
    /// </summary>
    private static string BuildTooltip(BeingData being, JobSlot slot)
    {
        StringBuilder sb = Caches.StringBuilder;
        sb.Clear();
        sb.Append(being.Name);
        sb.Append(' ');
        sb.Append(GlyphMap.ArrowRight);
        sb.Append(' ');
        sb.AppendLine(Texts.Accented(slot.Job.NameT));
        if (slot.Incapable)
        {
            sb.Append(Texts.Red(T.AdjIncapable));
            JobAffinities.AppendIncapableReasons(slot, sb);
            sb.AppendLine();
            return sb.ToString();
        }
        if (slot.Loves)
        {
            sb.Append(Texts.Green(T.VerbLoves));
            JobAffinities.AppendLovesReasons(slot, sb);
            sb.AppendLine();
        }
        if (JobAffinities.HasPartialAbilities(slot))
        {
            JobAffinities.AppendPartialAbilities(slot, sb);
            sb.AppendLine();
        }
        if (!slot.Job.Unskilled)
        {
            SkillDisplaySnapshot display = SkillCapRuntime.DisplaySnapshot(slot);
            string level = T.JobLevel_V1.F(SkillCapMath.Format(display.Level));
            if (display.Progress >= 0.01d)
            {
                string progress = Units.Percentage((float)display.Progress);
                progress = $"({progress}{GlyphMap.ArrowRight}{SkillCapMath.Format(display.NextLevel)})";
                sb.AppendFormat("{0} {1}", level, Texts.Grey(progress));
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(level);
            }
        }
        sb.Append(JobPriorityUI.TooltipLines(slot));
        return sb.ToString();
    }
}
