using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Game.CodeGen;
using Game.Components;
using Game.Systems.AI;
using Game.Systems.Nanobots;
using Game.Systems.RangeCheck;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.AutoHauling {
    internal static class HiveRefillRankingPatch {
        private const string Owner = "AutoHauling.HiveRefillRanking";
        private static bool rankingSitesApplied;

        internal static bool IsAvailable => rankingSitesApplied;

        internal static void Install() {
            try {
                var target = ResolveTarget();
                if (target == null) {
                    D.Warn("[Auto Hauling] Hive refill ranking unavailable: "
                        + "native Hive selection method not found.");
                    return;
                }
                var transpiler = new HarmonyMethod(AccessTools.DeclaredMethod(
                    typeof(HiveRefillRankingPatch), nameof(Transpiler))) {
                    priority = HarmonyLib.Priority.Last
                };
                rankingSitesApplied = false;
                new Harmony(Owner).Patch(target, transpiler: transpiler);
                if (!rankingSitesApplied) {
                    Remove();
                    D.Warn("[Auto Hauling] Hive refill ranking unavailable: "
                        + "native ranking instructions did not match.");
                }
            } catch (Exception ex) {
                Remove();
                D.LogEx(ex, "[Auto Hauling] Hive refill ranking unavailable.");
            }
        }

        internal static void Remove() {
            rankingSitesApplied = false;
            try {
                var target = ResolveTarget();
                if (target != null) {
                    new Harmony(Owner).Unpatch(target,
                        HarmonyPatchType.Transpiler, Owner);
                }
            } catch (Exception ex) {
                D.LogEx(ex, "[Auto Hauling] Could not remove the disabled "
                    + "Hive refill ranking hook.");
            }
        }

        private static MethodInfo ResolveTarget() {
            var method = AccessTools.DeclaredMethod(typeof(NanobotSys),
                "OnTick", new[] { typeof(long) });
            return method != null && !method.IsStatic
                && method.ReturnType == typeof(void) ? method : null;
        }

        private static bool IsEnabled(NanobotSys hiveSystem) {
            var state = hiveSystem.S;
            return rankingSitesApplied && state?.Sys?.Known != null
                && state.Sys.Known.TryGetValue(AutoHaulingSys.SysId, out var sys)
                && sys is AutoHaulingSys autoHauling
                && autoHauling.UsesHiveRefillRanking(state);
        }

        private static float GetRefillTier(AIGoal goal) {
            // Identify the actual Hive fuel component, not a material ID, text
            // label, or another device that happens to have a refill goal.
            return goal?.JobIdH == JobTypeIdH.Hauling
                && goal.Target is RefillableStorageComp storage
                && storage.Entity != null
                && storage.Entity.TryGetComponent<NanobotHiveComp>(out var hive)
                && ReferenceEquals(hive.Fuel, storage) ? 1f : 0f;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = instructions.ToList();
            rankingSitesApplied = false;
            var hitType = typeof(RangeCheckHit);
            var tier = AccessTools.DeclaredField(hitType, nameof(RangeCheckHit.Priority));
            var distance = AccessTools.DeclaredPropertyGetter(hitType, nameof(RangeCheckHit.SqrDist));
            var selector = typeof(NanobotSys);
            var closestHits = AccessTools.DeclaredField(selector, "closestHits");
            var dictionary = typeof(Dictionary<int, RangeCheckHit>);
            var workItem = AccessTools.DeclaredField(selector, "nanobotWorkItems")
                ?.FieldType.GetGenericArguments().LastOrDefault();
            var goalGetter = AccessTools.DeclaredPropertyGetter(workItem, "Goal");
            var reach = AccessTools.DeclaredMethod(workItem, "CanHiveReach",
                new[] { typeof(NanobotHiveComp) });
            var work = AccessTools.DeclaredMethod(workItem, "CanHiveWork",
                new[] { typeof(NanobotHiveComp) });
            var workable = AccessTools.DeclaredPropertyGetter(typeof(AIGoal),
                nameof(AIGoal.IsNanobotWorkable));
            var tryGet = dictionary.GetMethod("TryGetValue");
            var set = dictionary.GetProperty("Item").GetSetMethod();
            var clear = dictionary.GetMethod("Clear");
            if (tier == null || distance == null || closestHits?.FieldType != dictionary
                    || goalGetter?.ReturnType != typeof(AIGoal)
                    || reach == null || work == null || workable == null
                    || code.Any(i => Equals(i.operand, tier))) {
                return code;
            }

            var distances = Sites(code, distance);
            var workableSites = Sites(code, workable);
            var reachSites = Sites(code, reach);
            var workSites = Sites(code, work);
            if (distances.Length != 4 || workableSites.Length != 1
                    || reachSites.Length != 1 || workSites.Length != 1) {
                return code;
            }
            int tagAt = workableSites[0] - 2;
            int early = distances[0] - 1;
            int final = distances[3] - 1;
            int reachAt = reachSites[0];
            int workAt = workSites[0];
            int finish = final + 4;
            var clears = Sites(code, clear).Where(i => i >= 2
                && Equals(code[i - 1].operand, closestHits)).ToArray();
            if (clears.Length != 2 || tagAt < 2 || finish + 7 >= code.Count
                    || !(clears[0] < tagAt && tagAt + 11 == early
                        && early + 3 == distances[1] && early + 7 == reachAt
                        && reachAt < workAt && workAt + 3 == distances[2]
                        && distances[2] + 9 == final && finish < clears[1])) {
                return code;
            }

            // Match the flags shortcut, both distance branches and their
            // destinations before changing either. Local indexes are not fixed.
            var candidate = ReadLocal(code[distances[1] - 1], true, hitType);
            var first = ReadLocal(code[early], true, hitType);
            var last = ReadLocal(code[final], true, hitType);
            var item = ReadLocal(code[tagAt], false, workItem);
            var sqDist = code[distances[2] + 1].operand as LocalBuilder;
            var loop = code[early + 4].operand;
            int loopAt = clears[0] + 1;
            if (candidate == null || first == null || last == null || item == null
                    || sqDist?.LocalType != typeof(float)
                    || !code[tagAt + 1].Calls(goalGetter)
                    || !code[early - 2].Calls(tryGet)
                    || !code[final - 2].Calls(tryGet)
                    || !Equals(ReadLocal(code[early - 3], true, hitType), first)
                    || !Equals(ReadLocal(code[final - 3], true, hitType), last)
                    || !Equals(code[early - 6].operand, closestHits)
                    || !Equals(code[final - 6].operand, closestHits)
                    || !Equals(ReadLocal(code[distances[2] - 1], true, hitType), candidate)
                    || !Equals(ReadLocal(code[final + 2], false, typeof(float)), sqDist)
                    || !BranchTo(code[early + 4], code[loopAt], OpCodes.Blt, OpCodes.Blt_S)
                    || !BranchTo(code[final + 3], code[loopAt], OpCodes.Blt, OpCodes.Blt_S)
                    || !BranchTo(code[tagAt - 1], code[loopAt], OpCodes.Brfalse, OpCodes.Brfalse_S)
                    || !BranchTo(code[tagAt + 3], code[reachAt - 2], OpCodes.Brfalse, OpCodes.Brfalse_S)
                    || !BranchTo(code[early - 1], code[reachAt - 2], OpCodes.Brfalse, OpCodes.Brfalse_S)
                    || !BranchTo(code[reachAt + 1], code[loopAt], OpCodes.Brfalse, OpCodes.Brfalse_S)
                    || !BranchTo(code[workAt + 1], code[loopAt], OpCodes.Brfalse, OpCodes.Brfalse_S)
                    || !BranchTo(code[final - 1], code[finish], OpCodes.Brfalse, OpCodes.Brfalse_S)
                    || !Equals(code[finish + 1].operand, closestHits)
                    || !Equals(ReadLocal(code[finish + 4], false, hitType), candidate)
                    || !code[finish + 5].Calls(set)
                    || !BranchTo(code[finish + 6], code[loopAt], OpCodes.Br, OpCodes.Br_S)
                    || code.Skip(clears[0] - 2).Take(finish + 7 - (clears[0] - 2))
                        .Any(i => i.blocks.Count != 0)) {
                return code;
            }

            // Only this managed loop uses the tag. Native distances, queue
            // geometry, eligibility/goal flags and dispatch remain intact.
            // When off, skip classification and run the original comparisons.
            var enabled = generator.DeclareLocal(typeof(bool));
            var nativeTag = generator.DefineLabel();
            var rerank = AccessTools.DeclaredMethod(typeof(HiveRefillRankingPatch), nameof(IsEnabled));
            var classify = AccessTools.DeclaredMethod(typeof(HiveRefillRankingPatch), nameof(GetRefillTier));
            InsertComparison(code, generator, final, finish, enabled, last, candidate, tier, loop);
            InsertComparison(code, generator, early, reachAt - 2, enabled, first, candidate, tier, loop);
            InsertBefore(code, tagAt, nativeTag,
                new CodeInstruction(OpCodes.Ldloc, enabled),
                new CodeInstruction(OpCodes.Brfalse, nativeTag),
                new CodeInstruction(OpCodes.Ldloca, candidate),
                new CodeInstruction(OpCodes.Ldloc, item),
                new CodeInstruction(OpCodes.Callvirt, goalGetter),
                new CodeInstruction(OpCodes.Call, classify),
                new CodeInstruction(OpCodes.Stfld, tier));
            InsertBefore(code, clears[0] - 2, generator.DefineLabel(),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, rerank),
                new CodeInstruction(OpCodes.Stloc, enabled));
            rankingSitesApplied = true;
            return code;
        }

        private static int[] Sites(List<CodeInstruction> code, MethodInfo method) =>
            Enumerable.Range(0, code.Count).Where(i => code[i].Calls(method)).ToArray();

        private static LocalBuilder ReadLocal(CodeInstruction instruction, bool address, Type type) {
            var op = instruction.opcode;
            return (address ? op == OpCodes.Ldloca || op == OpCodes.Ldloca_S
                            : op == OpCodes.Ldloc || op == OpCodes.Ldloc_S)
                && instruction.operand is LocalBuilder local && local.LocalType == type
                ? local : null;
        }

        private static bool BranchTo(CodeInstruction from, CodeInstruction to,
                OpCode longOp, OpCode shortOp) =>
            (from.opcode == longOp || from.opcode == shortOp)
                && from.operand is Label label && to.labels.Contains(label);

        private static void InsertComparison(List<CodeInstruction> code,
                ILGenerator generator, int start, int fallThrough, LocalBuilder enabled,
                LocalBuilder previous, LocalBuilder candidate, FieldInfo tier, object loop) {
            var nativeDistance = generator.DefineLabel();
            var takeCandidate = generator.DefineLabel();
            code[fallThrough].labels.Add(takeCandidate);
            InsertBefore(code, start, nativeDistance,
                new CodeInstruction(OpCodes.Ldloc, enabled),
                new CodeInstruction(OpCodes.Brfalse, nativeDistance),
                new CodeInstruction(OpCodes.Ldloca, previous),
                new CodeInstruction(OpCodes.Ldfld, tier),
                new CodeInstruction(OpCodes.Ldloca, candidate),
                new CodeInstruction(OpCodes.Ldfld, tier),
                new CodeInstruction(OpCodes.Beq, nativeDistance),
                new CodeInstruction(OpCodes.Ldloca, previous),
                new CodeInstruction(OpCodes.Ldfld, tier),
                new CodeInstruction(OpCodes.Ldc_R4, 0f),
                new CodeInstruction(OpCodes.Bgt, loop),
                new CodeInstruction(OpCodes.Br, takeCandidate));
        }

        private static void InsertBefore(List<CodeInstruction> code, int index,
                Label native, params CodeInstruction[] added) {
            added[0].labels.AddRange(code[index].labels);
            code[index].labels.Clear();
            code[index].labels.Add(native);
            code.InsertRange(index, added);
        }

    }
}
