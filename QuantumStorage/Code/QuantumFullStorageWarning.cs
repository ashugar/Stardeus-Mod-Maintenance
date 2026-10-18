using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Inventory;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.QuantumStorage
{
    internal static class QuantumFullStorageWarning
    {
        // Only correct the two totals consumed by Core's warning. Nothing here
        // changes the inventory list, endpoint capacity, contents or reservations.
        internal static void CorrectTotals(GameState state, List<IMatStorage> storages,
            ref int capacity, ref int free)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(state);
            if (system == null || !system.HasAttachedNetworks || storages == null)
            {
                return;
            }

            long totalCapacity = 0;
            long totalFree = 0;
            HashSet<QuantumStorageNetwork> counted = null;
            for (int i = storages.Count - 1; i >= 0; i--)
            {
                // Match FullStorageUI.Check's eligibility exactly. In particular,
                // do not add power, priority, filter or reachability restrictions.
                if (storages[i] is not MatStorageComp storage ||
                    storage.StorageType == StorageType.TakeOnly)
                {
                    continue;
                }

                if (system.TryGetNetwork(storage, out QuantumStorageNetwork network))
                {
                    counted ??= new HashSet<QuantumStorageNetwork>();
                    if (!counted.Add(network))
                    {
                        continue;
                    }
                }

                totalCapacity += storage.MaxCapacity;
                totalFree += Math.Max(0, storage.AvailableCapacity);
            }

            // No eligible Quantum endpoint in this particular Core list: leave
            // Core's original totals untouched, even if other networks exist.
            if (counted == null)
            {
                return;
            }

            // Core only tests capacity == 0, free <= 0 and free >= 100. Saturation
            // preserves all three decisions without changing its local types.
            capacity = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, totalCapacity));
            free = (int)Math.Min(int.MaxValue, totalFree);
        }
    }

    [HarmonyPatch(typeof(FullStorageUI), nameof(FullStorageUI.Check),
        new Type[] { typeof(List<IMatStorage>) })]
    internal static class FullStorageUIQuantumTotalsPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            FieldInfo state = AccessTools.Field(typeof(FullStorageUI), "S");
            FieldInfo notification = AccessTools.Field(typeof(FullStorageUI), "fullNotif");
            MethodInfo maxCapacity = AccessTools.PropertyGetter(typeof(MatStorageComp), nameof(MatStorageComp.MaxCapacity));
            MethodInfo availableCapacity = AccessTools.PropertyGetter(typeof(MatStorageComp), nameof(MatStorageComp.AvailableCapacity));
            int boundary = -1;
            for (int i = 4; i + 3 < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Ldloc_0 &&
                    (code[i + 1].opcode == OpCodes.Brtrue || code[i + 1].opcode == OpCodes.Brtrue_S) &&
                    code[i + 2].opcode == OpCodes.Ldarg_0 &&
                    code[i + 3].opcode == OpCodes.Ldflda && Equals(code[i + 3].operand, notification))
                {
                    if (boundary >= 0)
                    {
                        boundary = -1;
                        break;
                    }
                    boundary = i;
                }
            }

            // Validate the inspected rc320 aggregation shape. A future mismatch
            // disables only this warning correction, not shared storage startup.
            if (state == null || notification == null || maxCapacity == null || availableCapacity == null || boundary < 0 ||
                code[0].opcode != OpCodes.Ldc_I4_0 || code[1].opcode != OpCodes.Stloc_0 ||
                code[2].opcode != OpCodes.Ldc_I4_0 || code[3].opcode != OpCodes.Stloc_1 ||
                code.Take(boundary).Count(c => c.Calls(maxCapacity)) != 1 ||
                code.Take(boundary).Count(c => c.Calls(availableCapacity)) != 1 ||
                code[boundary].blocks.Count != 0)
            {
                D.Warn("Quantum Storage could not match Core's full-storage warning totals; only the warning correction is inactive.");
                return code;
            }

            var correction = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, state),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldloca_S, (byte)0),
                new CodeInstruction(OpCodes.Ldloca_S, (byte)1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(QuantumFullStorageWarning),
                    nameof(QuantumFullStorageWarning.CorrectTotals), new[]
                    {
                        typeof(GameState), typeof(List<IMatStorage>),
                        typeof(int).MakeByRefType(), typeof(int).MakeByRefType()
                    }))
            };
            correction[0].labels.AddRange(code[boundary].labels);
            code[boundary].labels.Clear();
            code.InsertRange(boundary, correction);
            // Core retains its notification handle, text, fanfare, zero-capacity
            // removal and 100-free-space hysteresis without a replacement UI.
            return code;
        }
    }
}
