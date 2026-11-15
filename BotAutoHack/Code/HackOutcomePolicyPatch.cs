using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Data.Factions;
using Game.Systems;
using Game.Systems.AI.Combat;
using Game.Systems.BodyParts;
using Game.Systems.Conditions;
using Game.Systems.Creatures;
using Game.Systems.Mental;
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
        private const float ChanceTolerance = 0.00001f;

        internal static MethodInfo TargetMethod { get; private set; }

        internal static void Validate() {
            TargetMethod = ResolveTargetMethod();
            ConditionHackedResolveIntrusionPatch.ValidateCoreContract(
                PatchProcessor.GetOriginalInstructions(TargetMethod));
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

        private sealed class ContractMatch {
            internal int BlockStart;
            internal int BlockEnd;
        }

        private static MethodBase TargetMethod() {
            return HackOutcomePolicyContract.TargetMethod
                ?? throw new InvalidOperationException(
                    "Hack outcome contract was not validated before patching.");
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions) {
            ReplacementCount = 0;
            List<CodeInstruction> codes
                = new List<CodeInstruction>(instructions);
            ContractMatch match = AnalyzeContract(codes);
            MethodInfo promote = AccessTools.Method(
                typeof(ConditionHackedResolveIntrusionPatch),
                nameof(PromoteHarmlessBranch));

            List<CodeInstruction> replacement = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Call, promote),
                new CodeInstruction(OpCodes.Ret)
            };

            codes.RemoveRange(
                match.BlockStart,
                match.BlockEnd - match.BlockStart + 1);
            codes.InsertRange(match.BlockStart, replacement);
            ReplacementCount = 1;
            return codes;
        }

        internal static void ValidateCoreContract(
                IEnumerable<CodeInstruction> instructions) {
            AnalyzeContract(new List<CodeInstruction>(instructions));
        }

        internal static void VerifyOwnedPatch(string owner) {
            MethodInfo expected = AccessTools.Method(
                typeof(ConditionHackedResolveIntrusionPatch),
                nameof(Transpiler));
            Patches info = Harmony.GetPatchInfo(
                HackOutcomePolicyContract.TargetMethod);
            int owned = info?.Transpilers.Count(patch =>
                patch.owner == owner && patch.PatchMethod == expected) ?? 0;
            if (owned != 1 || ReplacementCount != 1) {
                throw new InvalidOperationException(
                    "Bot Auto Hack outcome transpiler was not installed exactly once.");
            }
        }

        private static ContractMatch AnalyzeContract(
                List<CodeInstruction> codes) {
            if (codes == null || codes.Count == 0) {
                throw new InvalidOperationException(
                    "Core ResolveIntrusion instructions are unavailable.");
            }

            FieldInfo failChance = RequireMember(AccessTools.Field(
                typeof(Tunables), nameof(Tunables.HackFailChance)),
                "HackFailChance");
            MethodInfo chance = RequireMember(AccessTools.Method(
                typeof(Rng), nameof(Rng.Chance), new[] { typeof(float) }),
                "Rng.Chance(float)");
            FieldInfo stateSystems = RequireMember(AccessTools.Field(
                typeof(GameState), nameof(GameState.Sys)), "GameState.Sys");
            FieldInfo logField = RequireMember(AccessTools.Field(
                typeof(GameSystems), nameof(GameSystems.Log)), "GameSystems.Log");
            FieldInfo failedText = RequireMember(AccessTools.Field(
                typeof(T), nameof(T.HackFailed_V1)), "T.HackFailed_V1");
            MethodInfo linkBeing = RequireMember(AccessTools.Method(
                typeof(Texts), nameof(Texts.LinkToBeing),
                new[] { typeof(BeingData), typeof(string) }),
                "Texts.LinkToBeing(BeingData, string)");
            MethodInfo addLine = RequireMember(AccessTools.Method(
                typeof(LogSys), nameof(LogSys.AddLine),
                new[] { typeof(string), typeof(bool) }),
                "LogSys.AddLine(string, bool)");

            List<int> semantic = SemanticIndices(codes, 0);
            int first = FindUniqueSequence(codes, semantic, 0,
                code => LoadsArgument(code, 2),
                code => IsField(code, OpCodes.Ldsfld, failChance),
                code => IsCall(code, OpCodes.Callvirt, chance),
                code => IsBranchFalse(code.opcode));
            if (first < 0) {
                throw new InvalidOperationException(
                    "Expected exactly one Core harmless-failure RNG branch.");
            }

            int branchIndex = semantic[first + 3];
            if (!(codes[branchIndex].operand is Label falseTarget)) {
                throw new InvalidOperationException(
                    "Core harmless-failure branch target changed.");
            }

            Func<CodeInstruction, bool>[] logBlock = {
                code => LoadsArgument(code, 0),
                code => IsField(code, OpCodes.Ldfld, stateSystems),
                code => IsField(code, OpCodes.Ldfld, logField),
                code => IsField(code, OpCodes.Ldsfld, failedText),
                code => LoadsArgument(code, 1),
                code => code.opcode == OpCodes.Ldnull,
                code => IsCall(code, OpCodes.Call, linkBeing),
                code => IsTranslationFormatCall(code),
                code => LoadsInt32(code, 0),
                code => IsCall(code, OpCodes.Callvirt, addLine),
                code => code.opcode == OpCodes.Ret
            };
            int logStart = first + 4;
            if (!SequenceMatches(codes, semantic, logStart, logBlock)) {
                throw new InvalidOperationException(
                    "Core harmless-failure log-and-return block changed.");
            }
            int blockStart = semantic[logStart];
            int blockEnd = semantic[logStart + logBlock.Length - 1];
            for (int i = branchIndex + 1; i < blockStart; i++) {
                if (!IsSafeNop(codes[i])) {
                    throw new InvalidOperationException(
                        "Core harmless-failure entry layout changed.");
                }
            }
            for (int i = blockStart; i <= blockEnd; i++) {
                if (codes[i].labels.Count != 0 || codes[i].blocks.Count != 0) {
                    throw new InvalidOperationException(
                        "Core harmless-failure control-flow layout changed.");
                }
                if (codes[i].opcode == OpCodes.Nop && !IsSafeNop(codes[i])) {
                    throw new InvalidOperationException(
                        "Core harmless-failure no-op layout changed.");
                }
            }

            int falseTargetIndex = FindLabelTarget(codes, falseTarget);
            if (falseTargetIndex <= blockEnd) {
                throw new InvalidOperationException(
                    "Core harmless-failure branch target changed.");
            }
            for (int i = blockEnd + 1; i < falseTargetIndex; i++) {
                if (!IsSafeNop(codes[i])) {
                    throw new InvalidOperationException(
                        "Core harmless-failure continuation moved.");
                }
            }

            ValidateFalsePath(codes, falseTargetIndex, chance,
                stateSystems);
            return new ContractMatch {
                BlockStart = blockStart,
                BlockEnd = blockEnd
            };
        }

        private static void ValidateFalsePath(
                List<CodeInstruction> codes,
                int start,
                MethodInfo chance,
                FieldInfo stateSystems) {
            FieldInfo combatField = RequireMember(AccessTools.Field(
                typeof(GameSystems), nameof(GameSystems.Combat)),
                "GameSystems.Combat");
            FieldInfo lastConversion = RequireMember(AccessTools.Field(
                typeof(CombatSys), nameof(CombatSys.LastHackConversionTick)),
                "CombatSys.LastHackConversionTick");
            MethodInfo curveGet = RequireMember(AccessTools.Method(
                typeof(CurveDef), nameof(CurveDef.FastGet),
                new[] { typeof(int) }), "CurveDef.FastGet(int)");
            MethodInfo curveEvaluate = RequireMember(AccessTools.Method(
                typeof(Curve), nameof(Curve.Evaluate),
                new[] { typeof(float) }), "Curve.Evaluate(float)");
            MethodInfo flipTarget = RequireMember(AccessTools.Method(
                typeof(ConditionHacked), "FlipTargetFor",
                new[] { typeof(BeingData) }),
                "ConditionHacked.FlipTargetFor(BeingData)");
            MethodInfo setFaction = RequireMember(AccessTools.Method(
                typeof(Mind), nameof(Mind.SetFaction),
                new[] { typeof(FactionDef), typeof(bool) }),
                "Mind.SetFaction(FactionDef, bool)");
            FieldInfo verbHacking = RequireMember(AccessTools.Field(
                typeof(T), nameof(T.VerbHacking)), "T.VerbHacking");
            MethodInfo namedFor = RequireMember(AccessTools.Method(
                typeof(NamedEntity), nameof(NamedEntity.For),
                new[] { typeof(string) }), "NamedEntity.For(string)");
            MethodInfo addWound = RequireMember(AccessTools.Method(
                typeof(BodyPartSlot), nameof(BodyPartSlot.TryAddOrUpdateWound),
                new[] { typeof(INamedEntity), typeof(DamageType),
                    typeof(Severity), typeof(long), typeof(float),
                    typeof(WoundFlags), typeof(int) }),
                "BodyPartSlot.TryAddOrUpdateWound");
            FieldInfo bodyField = RequireMember(AccessTools.Field(
                typeof(BeingData), nameof(BeingData.Body)), "BeingData.Body");
            FieldInfo mindField = RequireMember(AccessTools.Field(
                typeof(BeingData), nameof(BeingData.Mind)), "BeingData.Mind");
            FieldInfo mindSlotField = RequireMember(AccessTools.Field(
                typeof(Body), nameof(Body.MindSlot)), "Body.MindSlot");
            MethodInfo isAlive = RequireMember(AccessTools.PropertyGetter(
                typeof(Body), nameof(Body.IsAlive)), "Body.IsAlive");
            FieldInfo deathText = RequireMember(AccessTools.Field(
                typeof(T), nameof(T.CondHacked)), "T.CondHacked");
            MethodInfo onDeath = RequireMember(AccessTools.Method(
                typeof(Mind), nameof(Mind.OnDeath),
                new[] { typeof(string), typeof(long) }),
                "Mind.OnDeath(string, long)");

            List<int> semantic = SemanticIndices(codes, start);
            int combat = FindUniqueSequence(codes, semantic, 0,
                code => LoadsArgument(code, 0),
                code => IsField(code, OpCodes.Ldfld, stateSystems),
                code => IsField(code, OpCodes.Ldfld, combatField),
                code => IsStoreLocal(code, out _));
            if (combat < 0 || !TryGetLocalIndex(
                    codes[semantic[combat + 3]], false, out int combatLocal)) {
                throw new InvalidOperationException(
                    "Core hack-conversion combat state changed.");
            }

            int days = FindUniqueSequence(codes, semantic, combat + 4,
                code => LoadsLocal(code, combatLocal),
                code => IsField(code, OpCodes.Ldfld, lastConversion),
                code => LoadsInt32(code, 0),
                code => code.opcode == OpCodes.Conv_I8,
                code => IsBranchLessOrEqual(code.opcode),
                code => LoadsArgument(code, 3),
                code => LoadsLocal(code, combatLocal),
                code => IsField(code, OpCodes.Ldfld, lastConversion),
                code => code.opcode == OpCodes.Sub,
                code => code.opcode == OpCodes.Conv_R4,
                code => LoadsFloat32(code, Consts.TicksPerDay),
                code => code.opcode == OpCodes.Div,
                code => IsUnconditionalBranch(code.opcode),
                code => LoadsFloat32(code, float.MaxValue),
                code => IsStoreLocal(code, out _));
            if (days < 0
                    || !(codes[semantic[days + 4]].operand
                        is Label noPriorConversionTarget)
                    || !(codes[semantic[days + 12]].operand
                        is Label daysReadyTarget)
                    || FindLabelTarget(codes, noPriorConversionTarget)
                        != semantic[days + 13]
                    || FindLabelTarget(codes, daysReadyTarget)
                        != semantic[days + 14]
                    || !TryGetLocalIndex(codes[semantic[days + 14]],
                        false, out int daysLocal)) {
                throw new InvalidOperationException(
                    "Core hack-conversion cooldown calculation changed.");
            }

            int curve = FindUniqueSequence(codes, semantic, days + 15,
                code => LoadsInt32(code,
                    CurveIdH.Beings_DaysSinceHackConversion_ConvertChance),
                code => IsCall(code, OpCodes.Call, curveGet),
                code => LoadsLocal(code, daysLocal),
                code => IsCall(code, OpCodes.Callvirt, curveEvaluate),
                code => IsStoreLocal(code, out _));
            if (curve < 0 || !TryGetLocalIndex(
                    codes[semantic[curve + 4]], false, out int chanceLocal)) {
                throw new InvalidOperationException(
                    "Core hack-conversion curve path changed.");
            }

            int chanceCalls = codes.Count(code =>
                IsCall(code, OpCodes.Callvirt, chance));
            if (chanceCalls != 2) {
                throw new InvalidOperationException(
                    "Core hack-conversion RNG draw count changed.");
            }
            int secondDraw = FindUniqueSequence(codes, semantic, curve + 5,
                code => LoadsArgument(code, 2),
                code => LoadsLocal(code, chanceLocal),
                code => IsCall(code, OpCodes.Callvirt, chance),
                code => IsBranchFalse(code.opcode));
            if (secondDraw < 0
                    || !(codes[semantic[secondDraw + 3]].operand
                        is Label burnoutTarget)) {
                throw new InvalidOperationException(
                    "Core hack-conversion second RNG branch changed.");
            }

            int conversionStart = secondDraw + 4;
            Func<CodeInstruction, bool>[] conversionBlock = {
                code => LoadsLocal(code, combatLocal),
                code => LoadsArgument(code, 3),
                code => IsField(code, OpCodes.Stfld, lastConversion),
                code => LoadsArgument(code, 1),
                code => IsField(code, OpCodes.Ldfld, mindField),
                code => code.opcode == OpCodes.Dup,
                code => IsBranchTrue(code.opcode),
                code => code.opcode == OpCodes.Pop,
                code => code.opcode == OpCodes.Ret,
                code => LoadsArgument(code, 1),
                code => IsCall(code, OpCodes.Call, flipTarget),
                code => LoadsInt32(code, 0),
                code => IsCall(code, OpCodes.Call, setFaction),
                code => code.opcode == OpCodes.Ret
            };
            if (!SequenceMatches(codes, semantic, conversionStart,
                    conversionBlock)
                    || !(codes[semantic[conversionStart + 6]].operand
                        is Label conversionTarget)
                    || FindLabelTarget(codes, conversionTarget)
                        != semantic[conversionStart + 9]) {
                throw new InvalidOperationException(
                    "Core hack-conversion faction path changed.");
            }

            int burnoutStart = FindLabelTarget(codes, burnoutTarget);
            int burnoutSemantic = conversionStart + conversionBlock.Length;
            if (burnoutSemantic >= semantic.Count
                    || burnoutStart != semantic[burnoutSemantic]) {
                throw new InvalidOperationException(
                    "Core hack-conversion burnout target changed.");
            }
            List<int> burnout = SemanticIndices(codes, burnoutStart);
            Func<CodeInstruction, bool>[] burnoutBlock = {
                code => LoadsArgument(code, 1),
                code => IsField(code, OpCodes.Ldfld, bodyField),
                code => IsField(code, OpCodes.Ldfld, mindSlotField),
                code => code.opcode == OpCodes.Dup,
                code => IsBranchTrue(code.opcode),
                code => code.opcode == OpCodes.Pop,
                code => IsUnconditionalBranch(code.opcode),
                code => IsField(code, OpCodes.Ldsfld, verbHacking),
                code => IsCall(code, OpCodes.Call, namedFor),
                code => LoadsInt32(code, (int)DamageType.Energy),
                code => LoadsInt32(code, (int)Severity.Irreparable),
                code => LoadsArgument(code, 3),
                code => LoadsFloat32(code, 1f),
                code => LoadsInt32(code, 0),
                code => LoadsInt32(code, ConditionIdH.Hacked),
                code => IsCall(code, OpCodes.Call, addWound),
                code => code.opcode == OpCodes.Pop,
                code => LoadsArgument(code, 1),
                code => IsField(code, OpCodes.Ldfld, bodyField),
                code => IsCall(code, OpCodes.Callvirt, isAlive),
                code => IsBranchTrue(code.opcode),
                code => LoadsArgument(code, 1),
                code => IsField(code, OpCodes.Ldfld, mindField),
                code => code.opcode == OpCodes.Dup,
                code => IsBranchTrue(code.opcode),
                code => code.opcode == OpCodes.Pop,
                code => code.opcode == OpCodes.Ret,
                code => IsField(code, OpCodes.Ldsfld, deathText),
                code => LoadsArgument(code, 3),
                code => IsCall(code, OpCodes.Call, onDeath),
                code => code.opcode == OpCodes.Ret
            };
            if (!SequenceMatches(codes, burnout, 0, burnoutBlock)
                    || !(codes[burnout[4]].operand is Label woundTarget)
                    || FindLabelTarget(codes, woundTarget) != burnout[7]
                    || !(codes[burnout[6]].operand is Label afterWoundTarget)
                    || FindLabelTarget(codes, afterWoundTarget) != burnout[17]
                    || !(codes[burnout[20]].operand is Label aliveTarget)
                    || FindLabelTarget(codes, aliveTarget) != burnout[30]
                    || !(codes[burnout[24]].operand is Label deathTarget)
                    || FindLabelTarget(codes, deathTarget) != burnout[27]) {
                throw new InvalidOperationException(
                    "Core hack-conversion burnout death path changed.");
            }
        }

        private static List<int> SemanticIndices(
                List<CodeInstruction> codes, int start) {
            List<int> result = new List<int>();
            for (int i = Math.Max(0, start); i < codes.Count; i++) {
                if (!IsSafeNop(codes[i])) {
                    result.Add(i);
                }
            }
            return result;
        }

        private static int FindUniqueSequence(
                List<CodeInstruction> codes,
                List<int> indices,
                int start,
                params Func<CodeInstruction, bool>[] predicates) {
            int found = -1;
            int count = 0;
            for (int i = Math.Max(0, start);
                    i <= indices.Count - predicates.Length; i++) {
                if (!SequenceMatches(codes, indices, i, predicates)) {
                    continue;
                }
                found = i;
                count++;
            }
            return count == 1 ? found : -1;
        }

        private static bool SequenceMatches(
                List<CodeInstruction> codes,
                List<int> indices,
                int start,
                params Func<CodeInstruction, bool>[] predicates) {
            if (start < 0 || start + predicates.Length > indices.Count) {
                return false;
            }
            for (int i = 0; i < predicates.Length; i++) {
                if (!predicates[i](codes[indices[start + i]])) {
                    return false;
                }
            }
            return true;
        }

        private static int FindLabelTarget(
                List<CodeInstruction> codes, Label label) {
            int found = -1;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].labels.Contains(label)) continue;
                if (found >= 0) {
                    throw new InvalidOperationException(
                        "Core hack-conversion branch target is ambiguous.");
                }
                found = i;
            }
            if (found < 0) {
                throw new InvalidOperationException(
                    "Core hack-conversion branch target is missing.");
            }
            return found;
        }

        private static bool IsSafeNop(CodeInstruction code) {
            return code.opcode == OpCodes.Nop
                && code.operand == null
                && code.labels.Count == 0
                && code.blocks.Count == 0;
        }

        private static bool LoadsArgument(CodeInstruction code, int index) {
            if (index == 0 && code.opcode == OpCodes.Ldarg_0) return true;
            if (index == 1 && code.opcode == OpCodes.Ldarg_1) return true;
            if (index == 2 && code.opcode == OpCodes.Ldarg_2) return true;
            if (index == 3 && code.opcode == OpCodes.Ldarg_3) return true;
            if (code.opcode != OpCodes.Ldarg
                    && code.opcode != OpCodes.Ldarg_S) return false;
            if (code.operand is ParameterInfo parameter) {
                return parameter.Position == index;
            }
            try { return Convert.ToInt32(code.operand) == index; }
            catch { return false; }
        }

        private static bool IsLoadLocal(
                CodeInstruction code, out int index) {
            return TryGetLocalIndex(code, true, out index);
        }

        private static bool IsStoreLocal(
                CodeInstruction code, out int index) {
            return TryGetLocalIndex(code, false, out index);
        }

        private static bool LoadsLocal(CodeInstruction code, int index) {
            return TryGetLocalIndex(code, true, out int actual)
                && actual == index;
        }

        private static bool TryGetLocalIndex(
                CodeInstruction code, bool load, out int index) {
            index = -1;
            if (load) {
                if (code.opcode == OpCodes.Ldloc_0) { index = 0; return true; }
                if (code.opcode == OpCodes.Ldloc_1) { index = 1; return true; }
                if (code.opcode == OpCodes.Ldloc_2) { index = 2; return true; }
                if (code.opcode == OpCodes.Ldloc_3) { index = 3; return true; }
                if (code.opcode != OpCodes.Ldloc
                        && code.opcode != OpCodes.Ldloc_S) return false;
            } else {
                if (code.opcode == OpCodes.Stloc_0) { index = 0; return true; }
                if (code.opcode == OpCodes.Stloc_1) { index = 1; return true; }
                if (code.opcode == OpCodes.Stloc_2) { index = 2; return true; }
                if (code.opcode == OpCodes.Stloc_3) { index = 3; return true; }
                if (code.opcode != OpCodes.Stloc
                        && code.opcode != OpCodes.Stloc_S) return false;
            }
            if (code.operand is LocalBuilder builder) {
                index = builder.LocalIndex;
                return true;
            }
            if (code.operand is LocalVariableInfo variable) {
                index = variable.LocalIndex;
                return true;
            }
            try {
                index = Convert.ToInt32(code.operand);
                return true;
            } catch { return false; }
        }

        private static bool IsField(
                CodeInstruction code, OpCode opcode, FieldInfo field) {
            return code.opcode == opcode && Equals(code.operand, field);
        }

        private static bool IsCall(
                CodeInstruction code, OpCode opcode, MethodInfo method) {
            return code.opcode == opcode && Equals(code.operand, method);
        }

        private static bool IsTranslationFormatCall(CodeInstruction code) {
            if (code.opcode != OpCodes.Call
                    || !(code.operand is MethodInfo method)
                    || method.DeclaringType?.FullName != "Game.Utils.Translations"
                    || method.Name != "F"
                    || method.ReturnType != typeof(string)) {
                return false;
            }
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 2
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == typeof(object);
        }

        private static bool LoadsInt32(CodeInstruction code, int value) {
            if (value == -1 && code.opcode == OpCodes.Ldc_I4_M1) return true;
            if (value >= 0 && value <= 8) {
                OpCode[] constants = { OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1,
                    OpCodes.Ldc_I4_2, OpCodes.Ldc_I4_3, OpCodes.Ldc_I4_4,
                    OpCodes.Ldc_I4_5, OpCodes.Ldc_I4_6, OpCodes.Ldc_I4_7,
                    OpCodes.Ldc_I4_8 };
                if (code.opcode == constants[value]) return true;
            }
            if (code.opcode != OpCodes.Ldc_I4
                    && code.opcode != OpCodes.Ldc_I4_S) return false;
            try { return Convert.ToInt32(code.operand) == value; }
            catch { return false; }
        }

        private static bool LoadsFloat32(CodeInstruction code, float value) {
            return code.opcode == OpCodes.Ldc_R4
                && code.operand is float actual
                && Math.Abs(actual - value) <= 0.00001f;
        }

        private static bool IsBranchFalse(OpCode opcode) {
            return opcode == OpCodes.Brfalse || opcode == OpCodes.Brfalse_S;
        }

        private static bool IsBranchTrue(OpCode opcode) {
            return opcode == OpCodes.Brtrue || opcode == OpCodes.Brtrue_S;
        }

        private static bool IsBranchLessOrEqual(OpCode opcode) {
            return opcode == OpCodes.Ble || opcode == OpCodes.Ble_S;
        }

        private static bool IsUnconditionalBranch(OpCode opcode) {
            return opcode == OpCodes.Br || opcode == OpCodes.Br_S;
        }

        private static T RequireMember<T>(T member, string label)
                where T : class {
            if (member == null) {
                throw new MissingMemberException(
                    "Required Core member is unavailable: " + label + ".");
            }
            return member;
        }

        private static void PromoteHarmlessBranch(
                GameState s, BeingData victim, long ticks) {
            s.Sys.Combat.LastHackConversionTick = ticks;
            victim.Mind?.SetFaction(FactionDef.Player);
        }
    }
}
