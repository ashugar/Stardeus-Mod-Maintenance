using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.CodeGen;
using Game.Data;
using HarmonyLib;
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
            CurveDef.All.Add(
                CurveIdH.Beings_DaysSinceHackConversion_ConvertChance,
                new Curve {
                    Points = new[] {
                        new Vector2(0f, 0f),
                        new Vector2(1f, 0.05f),
                        new Vector2(3f, 0.2f),
                        new Vector2(7f, 0.35f)
                    }
                });

            Assembly bot = Assembly.LoadFrom(args[2]);
            Type contract = bot.GetType(ContractTypeName, true);
            Type patch = bot.GetType(PatchTypeName, true);
            contract.GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
            MethodBase target = (MethodBase)contract.GetProperty(
                "TargetMethod", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
            Check(target != null, "Exact ResolveIntrusion target resolved");

            List<CodeInstruction> original
                = PatchProcessor.GetOriginalInstructions(target);
            MethodInfo transpiler = patch.GetMethod(
                "Transpiler", BindingFlags.NonPublic | BindingFlags.Static);
            List<CodeInstruction> transformed
                = ((IEnumerable<CodeInstruction>)transpiler.Invoke(
                    null, new object[] { original })).ToList();
            int replacement = (int)patch.GetProperty(
                "ReplacementCount", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
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

            harmony = new Harmony(DiagnosticOwner);
            harmony.CreateClassProcessor(patch).Patch();
            Patches info = Harmony.GetPatchInfo(target);
            Check(info != null && info.Owners.Contains(DiagnosticOwner),
                "Harmony applied the guarded transpiler to the exact target");
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
}
