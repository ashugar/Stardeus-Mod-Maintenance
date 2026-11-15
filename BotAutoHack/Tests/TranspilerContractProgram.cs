using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.AI.Combat;
using HarmonyLib;
using KL.Randomness;
using KL.Utils;
using UnityEngine;

internal static class TranspilerContractProgram {
    private const string PatchTypeName
        = "Game.Systems.BotAutoHack.ConditionHackedResolveIntrusionPatch";
    private const string ContractTypeName
        = "Game.Systems.BotAutoHack.HackOutcomePolicyContract";
    private const string DiagnosticOwner
        = "jleas.stardeus.botautohack.contract-test";

    private static int assertions;

    private static int Main(string[] args) {
        if (args.Length != 3) {
            Console.Error.WriteLine(
                "Usage: TranspilerContractHarness <Managed> <AUMF directory> <BotAutoHack.dll>");
            return 2;
        }

        string[] roots = { args[0], args[1], Path.GetDirectoryName(args[2]) };
        ResolveEventHandler resolver = (sender, eventArgs) => {
            string file = new AssemblyName(eventArgs.Name).Name + ".dll";
            foreach (string root in roots) {
                string path = Path.Combine(root, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;

        Harmony harmony = null;
        try {
            CurveDef.All.Clear();
            Curve conversionCurve = new Curve {
                Points = new[] {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0.05f),
                    new Vector2(3f, 0.2f),
                    new Vector2(7f, 0.35f)
                }
            };
            CurveDef.All.Add(
                CurveIdH.Beings_DaysSinceHackConversion_ConvertChance,
                conversionCurve);

            Assembly bot = Assembly.LoadFrom(args[2]);
            Type contract = bot.GetType(ContractTypeName, true);
            Type patch = bot.GetType(PatchTypeName, true);
            MethodInfo validate = contract.GetMethod(
                "Validate", BindingFlags.NonPublic | BindingFlags.Static);
            validate.Invoke(null, null);
            MethodBase target = (MethodBase)contract.GetProperty(
                "TargetMethod", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
            Check(target != null, "Exact ResolveIntrusion target resolved");

            List<CodeInstruction> original
                = PatchProcessor.GetOriginalInstructions(target);
            MethodInfo transpiler = patch.GetMethod(
                "Transpiler", BindingFlags.NonPublic | BindingFlags.Static);
            PropertyInfo replacementProperty = patch.GetProperty(
                "ReplacementCount", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("ReplacementCount property unavailable");
            List<CodeInstruction> transformed
                = InvokeTranspiler(transpiler, original);
            int replacement = (int)replacementProperty.GetValue(null);
            Check(replacement == 1, "Exactly one Core failure branch replaced");
            Check(transformed.Count < original.Count,
                "Audited log block replaced by smaller conversion block");
            Check(transformed.Count(x => x.operand is MethodInfo m
                    && m.Name == "PromoteHarmlessBranch") == 1,
                "Exactly one promoted-conversion helper call emitted");
            Check(transformed.Count(x => x.operand is MethodInfo m
                    && m.DeclaringType?.FullName == "KL.Randomness.Rng"
                    && m.Name == "Chance") == 2,
                "Both native Core RNG draws remain");

            FieldInfo failChance = AccessTools.Field(
                typeof(Tunables), nameof(Tunables.HackFailChance));
            MethodInfo chance = AccessTools.Method(
                typeof(Rng), nameof(Rng.Chance), new[] { typeof(float) });
            FieldInfo failedText = AccessTools.Field(
                typeof(T), nameof(T.HackFailed_V1));
            FieldInfo replacementText = AccessTools.Field(
                typeof(T), nameof(T.VerbHacking));
            MethodInfo curveGet = AccessTools.Method(
                typeof(CurveDef), nameof(CurveDef.FastGet),
                new[] { typeof(int) });
            FieldInfo timestamp = AccessTools.Field(
                typeof(CombatSys), nameof(CombatSys.LastHackConversionTick));

            float originalFailChance = (float)failChance.GetValue(null);
            try {
                failChance.SetValue(null, 0.31f);
                ExpectContractRejected(validate,
                    "Changed Core harmless-failure tunable is rejected");
            } finally {
                failChance.SetValue(null, originalFailChance);
            }

            Vector2 originalFinalPoint = conversionCurve.Points[3];
            try {
                conversionCurve.Points[3] = new Vector2(7f, 0.34f);
                ExpectContractRejected(validate,
                    "Changed Core conversion curve is rejected");
            } finally {
                conversionCurve.Points[3] = originalFinalPoint;
            }
            validate.Invoke(null, null);
            Check(true, "Core tunable and curve restore to the accepted contract");

            int firstChance = original.FindIndex(code =>
                code.operand is MethodInfo method && method == chance);
            int secondChance = original.FindIndex(firstChance + 1, code =>
                code.operand is MethodInfo method && method == chance);
            int failureTextIndex = original.FindIndex(code =>
                code.operand is FieldInfo field && field == failedText);
            int failureReturn = original.FindIndex(
                failureTextIndex, code => code.opcode == OpCodes.Ret);
            int blockStart = firstChance + 2;
            bool outsidePreserved = true;
            for (int i = 0; i < blockStart; i++) {
                outsidePreserved &= ReferenceEquals(original[i], transformed[i]);
            }
            int suffixStart = blockStart + 5;
            for (int source = failureReturn + 1, result = suffixStart;
                    source < original.Count; source++, result++) {
                outsidePreserved &= ReferenceEquals(
                    original[source], transformed[result]);
            }
            Check(outsidePreserved,
                "Every instruction outside the promoted block remains unchanged");

            List<CodeInstruction> padded = Clone(original);
            padded.Insert(0, new CodeInstruction(OpCodes.Nop));
            Check(InvokeTranspiler(transpiler, padded).Any(code =>
                    code.operand is MethodInfo method
                    && method.Name == "PromoteHarmlessBranch"),
                "Harmless outside-seam no-op is accepted");

            List<CodeInstruction> longBranch = Clone(original);
            int firstBranch = firstChance + 1;
            longBranch[firstBranch].opcode
                = longBranch[firstBranch].opcode == OpCodes.Brfalse_S
                    ? OpCodes.Brfalse : OpCodes.Brfalse_S;
            Check(InvokeTranspiler(transpiler, longBranch).Count > 0,
                "Equivalent short or long false branch is accepted");

            List<CodeInstruction> renumberedLocals = RemapLocals(
                original, new Dictionary<int, int> {
                    { 0, 2 }, { 1, 0 }, { 2, 1 }
                });
            Check(InvokeTranspiler(transpiler, renumberedLocals).Any(code =>
                    code.operand is MethodInfo method
                    && method.Name == "PromoteHarmlessBranch"),
                "Role-consistent local renumbering is accepted");

            List<CodeInstruction> labeledEntryNop = Clone(original);
            DynamicMethod entryLabelOwner = new DynamicMethod(
                "GuardEntryLabel", typeof(void), Type.EmptyTypes);
            CodeInstruction entryNop = new CodeInstruction(OpCodes.Nop);
            entryNop.labels.Add(
                entryLabelOwner.GetILGenerator().DefineLabel());
            labeledEntryNop.Insert(firstBranch + 1, entryNop);
            ExpectRejected(transpiler, replacementProperty, labeledEntryNop,
                "Incoming label immediately before the failure block is rejected");

            List<CodeInstruction> wrongText = Clone(original);
            wrongText[failureTextIndex].operand = replacementText;
            ExpectRejected(transpiler, replacementProperty, wrongText,
                "Changed failure text is rejected");

            List<CodeInstruction> wrongTunable = Clone(original);
            int failChanceIndex = wrongTunable.FindIndex(code =>
                code.operand is FieldInfo field && field == failChance);
            ReplaceInstruction(wrongTunable, failChanceIndex,
                new CodeInstruction(OpCodes.Ldc_R4, 0.3f));
            ExpectRejected(transpiler, replacementProperty, wrongTunable,
                "Changed first-draw tunable source is rejected");

            List<CodeInstruction> duplicateFirstDraw = Clone(original);
            duplicateFirstDraw.InsertRange(0,
                Clone(original.Take(firstBranch + 1)));
            ExpectRejected(transpiler, replacementProperty,
                duplicateFirstDraw,
                "Duplicate first RNG branch is rejected");

            List<CodeInstruction> extraFailureSideEffect = Clone(original);
            extraFailureSideEffect.Insert(
                failureReturn, new CodeInstruction(OpCodes.Ldnull));
            ExpectRejected(transpiler, replacementProperty,
                extraFailureSideEffect,
                "Extra failure-branch side effect is rejected");

            List<CodeInstruction> redirectedFailure = Clone(original);
            redirectedFailure[firstBranch].operand
                = redirectedFailure[secondChance + 1].operand;
            ExpectRejected(transpiler, replacementProperty,
                redirectedFailure,
                "Redirected failure continuation is rejected");

            List<CodeInstruction> labeledFailure = Clone(original);
            DynamicMethod labelOwner = new DynamicMethod(
                "GuardLabel", typeof(void), Type.EmptyTypes);
            labeledFailure[failureTextIndex].labels.Add(
                labelOwner.GetILGenerator().DefineLabel());
            ExpectRejected(transpiler, replacementProperty, labeledFailure,
                "Incoming failure-block label is rejected");

            List<CodeInstruction> protectedFailure = Clone(original);
            protectedFailure[failureTextIndex].blocks.Add(
                new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            ExpectRejected(transpiler, replacementProperty, protectedFailure,
                "Failure block inside an exception region is rejected");

            List<CodeInstruction> wrongCurve = Clone(original);
            int curveGetIndex = wrongCurve.FindIndex(code =>
                code.operand is MethodInfo method && method == curveGet);
            ReplaceInstruction(wrongCurve, curveGetIndex - 1,
                new CodeInstruction(OpCodes.Ldc_I4,
                    CurveIdH.Beings_DaysSinceHackConversion_ConvertChance + 1));
            ExpectRejected(transpiler, replacementProperty, wrongCurve,
                "Changed conversion curve id is rejected");

            List<CodeInstruction> wrongCooldownScale = Clone(original);
            int ticksPerDayIndex = wrongCooldownScale.FindIndex(code =>
                code.opcode == OpCodes.Ldc_R4
                && code.operand is float value
                && Math.Abs(value - Consts.TicksPerDay) <= 0.00001f);
            ReplaceInstruction(wrongCooldownScale, ticksPerDayIndex,
                new CodeInstruction(OpCodes.Ldc_R4,
                    (float)Consts.TicksPerDay + 1f));
            ExpectRejected(transpiler, replacementProperty,
                wrongCooldownScale,
                "Changed days-since-conversion scale is rejected");

            List<CodeInstruction> missingSecondDraw = Clone(original);
            ReplaceInstruction(missingSecondDraw, secondChance,
                new CodeInstruction(OpCodes.Nop));
            ExpectRejected(transpiler, replacementProperty,
                missingSecondDraw,
                "Missing second RNG draw is rejected");

            List<CodeInstruction> divertedConversion = Clone(original);
            if (!(divertedConversion[secondChance + 1].operand
                    is Label burnoutTarget)) {
                throw new Exception("Second RNG burnout target was not found");
            }
            divertedConversion.Insert(secondChance + 2,
                new CodeInstruction(OpCodes.Br_S, burnoutTarget));
            ExpectRejected(transpiler, replacementProperty,
                divertedConversion,
                "Conversion path diverted directly to burnout is rejected");

            List<CodeInstruction> missingTimestamp = Clone(original);
            int timestampIndex = missingTimestamp.FindIndex(code =>
                code.operand is FieldInfo field && field == timestamp
                && code.opcode == OpCodes.Stfld);
            ReplaceInstruction(missingTimestamp, timestampIndex,
                new CodeInstruction(OpCodes.Nop));
            ExpectRejected(transpiler, replacementProperty,
                missingTimestamp,
                "Missing conversion timestamp update is rejected");

            List<CodeInstruction> missingFaction = Clone(original);
            int factionIndex = missingFaction.FindIndex(code =>
                code.operand is MethodInfo method
                && method.Name == "SetFaction"
                && method.DeclaringType?.FullName == "Game.Systems.Mental.Mind");
            ReplaceInstruction(missingFaction, factionIndex,
                new CodeInstruction(OpCodes.Nop));
            ExpectRejected(transpiler, replacementProperty,
                missingFaction,
                "Missing faction conversion is rejected");

            List<CodeInstruction> missingBurnout = Clone(original);
            int woundIndex = missingBurnout.FindIndex(code =>
                code.operand is MethodInfo method
                && method.Name == "TryAddOrUpdateWound"
                && method.DeclaringType?.FullName
                    == "Game.Systems.BodyParts.BodyPartSlot");
            ReplaceInstruction(missingBurnout, woundIndex,
                new CodeInstruction(OpCodes.Nop));
            ExpectRejected(transpiler, replacementProperty,
                missingBurnout,
                "Missing burnout wound is rejected");

            List<CodeInstruction> invertedAlive = Clone(original);
            int isAliveIndex = invertedAlive.FindIndex(code =>
                code.operand is MethodInfo method
                && method.Name == "get_IsAlive"
                && method.DeclaringType?.FullName
                    == "Game.Systems.BodyParts.Body");
            int aliveBranchIndex = isAliveIndex + 1;
            OpCode aliveOpcode = invertedAlive[aliveBranchIndex].opcode;
            OpCode invertedOpcode = aliveOpcode == OpCodes.Brtrue_S
                ? OpCodes.Brfalse_S : OpCodes.Brfalse;
            ReplaceInstruction(invertedAlive, aliveBranchIndex,
                new CodeInstruction(invertedOpcode,
                    invertedAlive[aliveBranchIndex].operand));
            ExpectRejected(transpiler, replacementProperty, invertedAlive,
                "Inverted alive/death branch is rejected");

            List<CodeInstruction> missingDeath = Clone(original);
            int deathIndex = missingDeath.FindIndex(code =>
                code.operand is MethodInfo method
                && method.Name == "OnDeath"
                && method.DeclaringType?.FullName == "Game.Systems.Mental.Mind");
            ReplaceInstruction(missingDeath, deathIndex,
                new CodeInstruction(OpCodes.Nop));
            ExpectRejected(transpiler, replacementProperty,
                missingDeath,
                "Missing burnout death notification is rejected");

            harmony = new Harmony(DiagnosticOwner);
            harmony.CreateClassProcessor(patch).Patch();
            Patches info = Harmony.GetPatchInfo(target);
            Check(info != null && info.Owners.Contains(DiagnosticOwner),
                "Harmony applied the guarded transpiler to the exact target");
            patch.GetMethod("VerifyOwnedPatch",
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { DiagnosticOwner });
            Check(true, "Installed transpiler owner verified exactly once");
            harmony.UnpatchAll(DiagnosticOwner);
            harmony = null;
            Check(Harmony.GetPatchInfo(target)?.Owners.Contains(DiagnosticOwner) != true,
                "Harmony removed the diagnostic patch owner");

            Console.WriteLine("PASS: " + assertions
                + " assertions. Runtime gameplay acceptance remains in-game.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(ex);
            return 1;
        } finally {
            harmony?.UnpatchAll(DiagnosticOwner);
            AppDomain.CurrentDomain.AssemblyResolve -= resolver;
        }
    }

    private static void Check(bool result, string message) {
        assertions++;
        if (!result) throw new Exception(message);
    }

    private static List<CodeInstruction> InvokeTranspiler(
            MethodInfo transpiler,
            IEnumerable<CodeInstruction> instructions) {
        return ((IEnumerable<CodeInstruction>)transpiler.Invoke(
            null, new object[] { instructions })).ToList();
    }

    private static List<CodeInstruction> Clone(
            IEnumerable<CodeInstruction> instructions) {
        return instructions.Select(instruction => {
            CodeInstruction copy = new CodeInstruction(
                instruction.opcode, instruction.operand);
            copy.labels.AddRange(instruction.labels);
            copy.blocks.AddRange(instruction.blocks);
            return copy;
        }).ToList();
    }

    private static void ReplaceInstruction(
            List<CodeInstruction> instructions,
            int index,
            CodeInstruction replacement) {
        if (index < 0 || index >= instructions.Count) {
            throw new Exception("Mutation target was not found");
        }
        replacement.labels.AddRange(instructions[index].labels);
        replacement.blocks.AddRange(instructions[index].blocks);
        instructions[index] = replacement;
    }

    private static List<CodeInstruction> RemapLocals(
            IEnumerable<CodeInstruction> instructions,
            Dictionary<int, int> map) {
        List<CodeInstruction> result = Clone(instructions);
        for (int i = 0; i < result.Count; i++) {
            if (!TryGetLocal(result[i], out bool load, out int current)
                    || !map.TryGetValue(current, out int replacementIndex)) {
                continue;
            }
            CodeInstruction replacement = new CodeInstruction(
                load ? OpCodes.Ldloc_S : OpCodes.Stloc_S,
                replacementIndex);
            replacement.labels.AddRange(result[i].labels);
            replacement.blocks.AddRange(result[i].blocks);
            result[i] = replacement;
        }
        return result;
    }

    private static bool TryGetLocal(
            CodeInstruction code,
            out bool load,
            out int index) {
        load = true;
        index = -1;
        if (code.opcode == OpCodes.Ldloc_0) { index = 0; return true; }
        if (code.opcode == OpCodes.Ldloc_1) { index = 1; return true; }
        if (code.opcode == OpCodes.Ldloc_2) { index = 2; return true; }
        if (code.opcode == OpCodes.Ldloc_3) { index = 3; return true; }
        if (code.opcode == OpCodes.Stloc_0) {
            load = false; index = 0; return true;
        }
        if (code.opcode == OpCodes.Stloc_1) {
            load = false; index = 1; return true;
        }
        if (code.opcode == OpCodes.Stloc_2) {
            load = false; index = 2; return true;
        }
        if (code.opcode == OpCodes.Stloc_3) {
            load = false; index = 3; return true;
        }
        if (code.opcode != OpCodes.Ldloc
                && code.opcode != OpCodes.Ldloc_S
                && code.opcode != OpCodes.Stloc
                && code.opcode != OpCodes.Stloc_S) {
            return false;
        }
        load = code.opcode == OpCodes.Ldloc
            || code.opcode == OpCodes.Ldloc_S;
        if (code.operand is LocalVariableInfo variable) {
            index = variable.LocalIndex;
            return true;
        }
        try {
            index = Convert.ToInt32(code.operand);
            return true;
        } catch {
            return false;
        }
    }

    private static void ExpectRejected(
            MethodInfo transpiler,
            PropertyInfo replacementProperty,
            List<CodeInstruction> instructions,
            string message) {
        bool rejected = false;
        try {
            InvokeTranspiler(transpiler, instructions);
        } catch (TargetInvocationException ex)
                when (ex.InnerException is InvalidOperationException) {
            rejected = true;
        }
        Check(rejected && (int)replacementProperty.GetValue(null) == 0,
            message);
    }

    private static void ExpectContractRejected(
            MethodInfo validate,
            string message) {
        bool rejected = false;
        try {
            validate.Invoke(null, null);
        } catch (TargetInvocationException ex)
                when (ex.InnerException is InvalidOperationException) {
            rejected = true;
        }
        Check(rejected, message);
    }
}
