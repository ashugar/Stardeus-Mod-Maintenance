using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Data.Factions;
using Game.Systems;
using Game.Systems.Conditions;
using Game.Systems.Creatures;
using Game.Utils;
using HarmonyLib;
using KL.Randomness;
using KL.Utils;

namespace Game.Systems.BotAutoHack {
    /// <summary>
    /// Validates the exact Core outcome contract that Bot Auto Hack overrides.
    /// The mod fails closed when Core changes instead of advertising stale odds.
    /// </summary>
    internal static class HackOutcomePolicyContract {
        private const string ResolveIntrusionName = "ResolveIntrusion";
        private const string ExpectedMethodIlSha256
            = "02361C1907C10F2F0C240BB1A8E6A24AF76C2227B36AEE6ED869FE1BB1639866";
        private const float ChanceTolerance = 0.00001f;

        internal static MethodInfo TargetMethod { get; private set; }

        internal static void Validate() {
            TargetMethod = ResolveTargetMethod();
            ValidateMethodBody(TargetMethod);
            RequireChance(
                "Core harmless-failure branch",
                Tunables.HackFailChance,
                HackConversionFormula.PromotedBranchChance);

            Curve curve = CurveDef.FastGet(
                CurveIdH.Beings_DaysSinceHackConversion_ConvertChance);
            if (curve == null) {
                throw new InvalidOperationException(
                    "Core hack-conversion curve is unavailable.");
            }
            RequireChance("Core curve at 0 days", curve.Evaluate(0f),
                HackConversionFormula.Day0CoreCurveChance);
            RequireChance("Core curve at 1 day", curve.Evaluate(1f),
                HackConversionFormula.Day1CoreCurveChance);
            RequireChance("Core curve at 3 days", curve.Evaluate(3f),
                HackConversionFormula.Day3CoreCurveChance);
            RequireChance("Core curve at 7 days", curve.Evaluate(7f),
                HackConversionFormula.Day7CoreCurveChance);
        }

        private static MethodInfo ResolveTargetMethod() {
            MethodInfo method = typeof(ConditionHacked).GetMethod(
                ResolveIntrusionName,
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] {
                    typeof(GameState),
                    typeof(BeingData),
                    typeof(Rng),
                    typeof(long)
                },
                null);
            if (method == null || method.ReturnType != typeof(void)) {
                throw new MissingMethodException(
                    typeof(ConditionHacked).FullName,
                    ResolveIntrusionName
                        + "(GameState, BeingData, Rng, long)");
            }
            return method;
        }

        private static void ValidateMethodBody(MethodInfo method) {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null || il.Length == 0) {
                throw new InvalidOperationException(
                    "Core ResolveIntrusion method body is unavailable.");
            }
            string actual;
            using (SHA256 sha = SHA256.Create()) {
                actual = BitConverter.ToString(sha.ComputeHash(il))
                    .Replace("-", string.Empty);
            }
            if (!string.Equals(actual, ExpectedMethodIlSha256,
                    StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    "Core ResolveIntrusion calculation changed. Expected IL "
                    + ExpectedMethodIlSha256 + ", found " + actual + ".");
            }
        }

        private static void RequireChance(
                string label, float actual, float expected) {
            if (Math.Abs(actual - expected) > ChanceTolerance) {
                throw new InvalidOperationException(
                    label + " changed. Expected " + expected
                    + ", found " + actual + ".");
            }
        }
    }

    /// <summary>
    /// Keeps Core's actual first 30% RNG draw and branch. Only the true branch's
    /// harmless log-and-return body becomes a conversion; the false branch
    /// continues through Core's native curve and consequences without another
    /// inserted or removed draw.
    /// </summary>
    [HarmonyPatch]
    internal static class ConditionHackedResolveIntrusionPatch {
        internal static int ReplacementCount { get; private set; }

        private static MethodBase TargetMethod() {
            return HackOutcomePolicyContract.TargetMethod
                ?? throw new InvalidOperationException(
                    "Hack outcome contract was not validated before patching.");
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes
                = new List<CodeInstruction>(instructions);
            FieldInfo failChance = AccessTools.Field(
                typeof(Tunables), nameof(Tunables.HackFailChance));
            MethodInfo chance = AccessTools.Method(
                typeof(Rng), nameof(Rng.Chance), new[] { typeof(float) });
            FieldInfo failedText = AccessTools.Field(
                typeof(T), nameof(T.HackFailed_V1));
            MethodInfo promote = AccessTools.Method(
                typeof(ConditionHackedResolveIntrusionPatch),
                nameof(PromoteHarmlessBranch));

            int match = -1;
            int matches = 0;
            for (int i = 0; i <= codes.Count - 4; i++) {
                if (codes[i].opcode == OpCodes.Ldarg_2
                        && codes[i + 1].LoadsField(failChance)
                        && codes[i + 2].Calls(chance)
                        && IsBranchFalse(codes[i + 3].opcode)) {
                    match = i;
                    matches++;
                }
            }
            if (matches != 1) {
                throw new InvalidOperationException(
                    "Expected exactly one Core harmless-failure RNG branch; "
                    + "found " + matches + ".");
            }

            int blockStart = match + 4;
            int blockEnd = FindAuditedFailureBlockEnd(
                codes, blockStart, failedText);
            if (!(codes[match + 3].operand is Label falseTarget)
                    || blockEnd + 1 >= codes.Count
                    || !codes[blockEnd + 1].labels.Contains(falseTarget)) {
                throw new InvalidOperationException(
                    "Core harmless-failure branch target changed.");
            }

            List<Label> movedLabels = new List<Label>();
            for (int i = blockStart; i <= blockEnd; i++) {
                movedLabels.AddRange(codes[i].labels);
                if (i != blockStart && i != blockEnd
                        && codes[i].blocks.Count > 0) {
                    throw new InvalidOperationException(
                        "Core harmless-failure exception layout changed.");
                }
            }

            List<CodeInstruction> replacement = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Call, promote),
                new CodeInstruction(OpCodes.Ret)
            };
            replacement[0].labels.AddRange(movedLabels);
            replacement[0].blocks.AddRange(codes[blockStart].blocks);
            if (blockEnd != blockStart) {
                replacement[replacement.Count - 1].blocks.AddRange(
                    codes[blockEnd].blocks);
            }

            codes.RemoveRange(blockStart, blockEnd - blockStart + 1);
            codes.InsertRange(blockStart, replacement);
            ReplacementCount = 1;
            return codes;
        }

        private static int FindAuditedFailureBlockEnd(
                List<CodeInstruction> codes,
                int blockStart,
                FieldInfo failedText) {
            bool sawFailureText = false;
            bool sawFailureLog = false;
            for (int i = blockStart; i < codes.Count; i++) {
                CodeInstruction code = codes[i];
                sawFailureText |= code.LoadsField(failedText);
                if (code.operand is MethodInfo method
                        && method.DeclaringType == typeof(LogSys)
                        && method.Name == nameof(LogSys.AddLine)) {
                    sawFailureLog = true;
                }
                if (code.opcode == OpCodes.Ret) {
                    if (!sawFailureText || !sawFailureLog) {
                        throw new InvalidOperationException(
                            "Core harmless-failure log block changed.");
                    }
                    return i;
                }
            }
            throw new InvalidOperationException(
                "Core harmless-failure branch no longer returns where expected.");
        }

        private static bool IsBranchFalse(OpCode opcode) {
            return opcode == OpCodes.Brfalse || opcode == OpCodes.Brfalse_S;
        }

        private static void PromoteHarmlessBranch(
                GameState s, BeingData victim, long ticks) {
            s.Sys.Combat.LastHackConversionTick = ticks;
            victim.Mind?.SetFaction(FactionDef.Player);
        }
    }
}
