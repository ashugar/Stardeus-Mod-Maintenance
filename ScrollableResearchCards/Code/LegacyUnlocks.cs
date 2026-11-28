using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Game.Data;
using Game.Research;
using Game.Systems.UIUX;
using Game.UI;
using HarmonyLib;
using UnityEngine.UI;

namespace Game.Systems.ScrollableResearchCards
{
    internal static class LegacyUnlocks
    {
        private static readonly Type Node = typeof(WResearchNode);
        private static readonly Type UnlockList = typeof(List<(Def, IResearchUnlockEffect)>);

        internal static MethodInfo RequireLegacyMethod()
        {
            var create = AccessTools.DeclaredMethod(Node, "CreateUnlocks", Type.EmptyTypes);
            var load = AccessTools.DeclaredMethod(Node, nameof(WResearchNode.Load),
                new[] { typeof(Researchable), typeof(ResearchProgress), typeof(WText) });
            if (create == null || load == null || create.IsStatic || load.IsStatic ||
                create.ReturnType != typeof(void) || load.ReturnType != typeof(void))
            {
                throw new InvalidOperationException("Core research card method signatures are unrecognized");
            }

            var loadCode = PatchProcessor.GetOriginalInstructions(load);
            var code = PatchProcessor.GetOriginalInstructions(create);
            if (loadCode.Concat(code).Any(ReferencesScrolling))
                throw new InvalidOperationException("Core research card code already references unlock scrolling");

            if (loadCode.Count(i => Calls(i, create)) != 1)
                throw new InvalidOperationException("Core Load no longer calls the legacy unlock renderer exactly once");

            var getUnlocks = AccessTools.DeclaredMethod(typeof(ResearchTree),
                nameof(ResearchTree.GetUnlockableIconsFor), new[] { typeof(Def) });
            if (getUnlocks == null || getUnlocks.IsStatic || getUnlocks.ReturnType != UnlockList ||
                !MatchesLegacyOverflow(code, getUnlocks))
            {
                throw new InvalidOperationException("Core unlock code is unrecognized (expected the native unlock list, >12 threshold and +N tile after 11 icons followed by loop exit)");
            }
            return create;
        }

        internal static bool ReferencesScrolling(CodeInstruction instruction)
        {
            if (!(instruction.operand is MemberInfo member))
                return false;

            return (member is Type type && IsScrollType(type)) ||
                IsScrollType(member.DeclaringType) ||
                (member is FieldInfo field && IsScrollType(field.FieldType)) ||
                (member is MethodInfo method && (IsScrollType(method.ReturnType) ||
                    (method.IsGenericMethod && method.GetGenericArguments().Any(IsScrollType))));
        }

        private static bool IsScrollType(Type type)
        {
            return type != null && (typeof(ScrollRect).IsAssignableFrom(type) ||
                typeof(IReactsToScroll).IsAssignableFrom(type));
        }

        internal static bool MatchesLegacyOverflow(List<CodeInstruction> code, MethodInfo getUnlocks)
        {
            var count = AccessTools.PropertyGetter(UnlockList, "Count");
            var getEnumerator = AccessTools.DeclaredMethod(UnlockList, "GetEnumerator", Type.EmptyTypes);
            var enumerator = getEnumerator.ReturnType;
            var moveNext = AccessTools.DeclaredMethod(enumerator, "MoveNext", Type.EmptyTypes);
            var current = AccessTools.PropertyGetter(enumerator, "Current");
            var extraPrefab = AccessTools.DeclaredField(Node, "unlockExtraPrefab");
            var iconPrefab = AccessTools.DeclaredField(Node, "unlockPrefab");
            var container = AccessTools.DeclaredField(Node, "unlocksContainer");
            var extra = AccessTools.DeclaredField(Node, "extraUnlocksText");
            var setText = AccessTools.DeclaredMethod(typeof(WText), "SetText", new[] { typeof(string), typeof(bool) });
            var rect = AccessTools.PropertyGetter(typeof(WBase), "Rect");
            var format = AccessTools.DeclaredMethod(typeof(string), "Format", new[] { typeof(string), typeof(object) });

            int read = code.FindIndex(i => Calls(i, getUnlocks));
            if (read < 0 || read + 1 >= code.Count || code.Count(i => Calls(i, getUnlocks)) != 1)
                return false;
            int list = Local(code[read + 1], store: true);
            if (list < 0)
                return false;

            int threshold = Find(code, read + 2, i => Loads(i, list), i => Calls(i, count),
                i => i.LoadsConstant(12), i => i.opcode == OpCodes.Cgt, i => Local(i, store: true) >= 0);
            if (threshold < 0)
                return false;
            int overflow = Local(code[threshold + 4], store: true);

            int loop = Find(code, threshold + 5, i => Loads(i, list), i => Calls(i, getEnumerator));
            if (loop < 0)
                return false;
            int next = code.FindIndex(loop + 2, i => Calls(i, moveNext));
            int entry = code.FindIndex(loop + 2, i => Calls(i, current));
            if (entry < 0 || next <= entry || next + 1 >= code.Count ||
                !IsBranch(code[next + 1], OpCodes.Brtrue, OpCodes.Brtrue_S))
                return false;

            int branch = Find(code, entry + 1, i => Loads(i, overflow), i => i.opcode == OpCodes.And,
                i => IsBranch(i, OpCodes.Brfalse, OpCodes.Brfalse_S));
            if (branch < entry + 8 || branch >= next)
                return false;
            int ordinal = Local(code[branch - 3], store: false);
            if (ordinal < 0 || !Loads(code[branch - 7], ordinal) ||
                !code[branch - 6].LoadsConstant(1) || code[branch - 5].opcode != OpCodes.Add ||
                Local(code[branch - 4], store: true) != ordinal ||
                !code[branch - 2].LoadsConstant(11) || code[branch - 1].opcode != OpCodes.Cgt)
                return false;

            int tile = branch + 3;
            int icon = Destination(code, code[branch + 2]);
            if (icon <= tile || icon >= next)
                return false;
            int spawn = Find(code, tile, i => Field(i, OpCodes.Ldfld, extraPrefab), IsThis,
                i => Field(i, OpCodes.Ldfld, container), i => Calls(i, rect),
                i => Spawns<WText>(i), i => Field(i, OpCodes.Stfld, extra));
            int caption = Find(code, tile, i => Field(i, OpCodes.Ldfld, extra),
                i => i.opcode == OpCodes.Ldstr && Equals(i.operand, "+{0}"),
                i => Loads(i, list), i => Calls(i, count), i => i.LoadsConstant(11),
                i => i.opcode == OpCodes.Sub,
                i => i.opcode == OpCodes.Box && Equals(i.operand, typeof(int)),
                i => Calls(i, format), i => i.LoadsConstant(1), i => Calls(i, setText));
            int nativeIcon = Find(code, icon, i => Field(i, OpCodes.Ldfld, iconPrefab), IsThis,
                i => Field(i, OpCodes.Ldfld, container), i => Calls(i, rect), i => Spawns<WImage>(i));
            if (spawn < tile || caption < spawn + 6 || caption + 10 >= icon ||
                nativeIcon < icon || nativeIcon >= next ||
                !IsBranch(code[icon - 1], OpCodes.Leave, OpCodes.Leave_S))
                return false;

            int exit = Destination(code, code[icon - 1]);
            int back = Destination(code, code[next + 1]);
            return exit > next + 2 && code[exit].opcode == OpCodes.Ret &&
                IsBranch(code[next + 2], OpCodes.Leave, OpCodes.Leave_S) &&
                Destination(code, code[next + 2]) == exit && back > loop && back <= entry;
        }

        private static int Find(List<CodeInstruction> code, int start, params Func<CodeInstruction, bool>[] pattern)
        {
            for (int i = start; i <= code.Count - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && pattern[j](code[i + j]))
                    j++;
                if (j == pattern.Length)
                    return i;
            }
            return -1;
        }

        private static bool Spawns<T>(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(UISpawn) && method.Name == "WidgetPermanent" &&
                method.IsGenericMethod && method.GetGenericArguments().SequenceEqual(new[] { typeof(T) }) &&
                method.ReturnType == typeof(T) &&
                method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(T), typeof(UnityEngine.RectTransform) });
        }

        private static int Destination(List<CodeInstruction> code, CodeInstruction branch)
        {
            return branch.operand is Label label ? code.FindIndex(i => i.labels.Contains(label)) : -1;
        }

        private static bool Calls(CodeInstruction instruction, MethodInfo method)
        {
            return method != null && instruction.Calls(method);
        }

        private static bool Field(CodeInstruction instruction, OpCode opcode, FieldInfo field)
        {
            return field != null && instruction.opcode == opcode && Equals(instruction.operand, field);
        }

        private static bool IsThis(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldarg_0;
        private static bool IsBranch(CodeInstruction i, OpCode full, OpCode shortForm) =>
            i.opcode == full || i.opcode == shortForm;

        private static bool Loads(CodeInstruction instruction, int index) =>
            Local(instruction, store: false) == index;

        private static int Local(CodeInstruction instruction, bool store)
        {
            var opcode = instruction.opcode;
            if (opcode == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (opcode == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (opcode == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (opcode == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            if (opcode != (store ? OpCodes.Stloc : OpCodes.Ldloc) &&
                opcode != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S))
                return -1;
            return instruction.operand is LocalVariableInfo local
                ? local.LocalIndex
                : Convert.ToInt32(instruction.operand);
        }
    }
}
