using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Game.Constants;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Corrects Core's Nanobot tooltip wording without changing the flag or
    /// any eligibility, reachability, reservation, or scheduling behavior.
    /// NanobotWorkable represents broader current eligibility than physical
    /// reachability, so a false value cannot safely be labelled unreachable.
    /// </summary>
    internal static class NanobotTooltipCorrection {
        private const string UnavailableTranslationKey =
            "optimizationmod.nanobot.notcurrentlyavailable";

        internal static bool IsTranspilerActive { get; private set; }

        internal static void Reset() {
            IsTranspilerActive = false;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions) {
            var codes = new List<CodeInstruction>(instructions);
            FieldInfo misleadingText = AccessTools.Field(
                typeof(T), nameof(T.NanobotUnreachable));
            MethodInfo replacementText = AccessTools.DeclaredMethod(
                typeof(NanobotTooltipCorrection), nameof(UnavailableText));

            var matches = new List<int>();
            for (int i = 0; i < codes.Count; i++) {
                CodeInstruction code = codes[i];
                if (code.opcode == OpCodes.Ldsfld
                        && object.Equals(code.operand, misleadingText)) {
                    matches.Add(i);
                }
            }

            if (misleadingText == null || replacementText == null
                    || matches.Count != 1) {
                IsTranspilerActive = false;
                D.Err("Optimization Mod skipped Nanobot tooltip correction: "
                    + "expected one Core NanobotUnreachable text load, found "
                    + "{0}. Vanilla tooltip wording remains active.",
                    matches.Count);
                return codes;
            }

            // Mutate the existing instruction so labels and exception blocks
            // remain attached to the exact same point in Core's method.
            CodeInstruction target = codes[matches[0]];
            target.opcode = OpCodes.Call;
            target.operand = replacementText;
            IsTranspilerActive = true;
            return codes;
        }

        private static string UnavailableText() {
            return UnavailableTranslationKey.T();
        }
    }
}
