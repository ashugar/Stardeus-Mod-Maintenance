extern alias Harmony;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Harmony::HarmonyLib;

internal static class HiveRankingChecks {
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private delegate bool Compare(bool enabled, float previousTier, float candidateTier,
        float previousDistance, float candidateDistance);

    internal static void Run(Assembly core, Assembly mod, string[] companion, Action<bool, string> check) {
        var patch = mod.GetType("Game.Systems.AutoHauling.HiveRefillRankingPatch", true)!;
        var target = (MethodInfo)patch.GetMethod("ResolveTarget", PrivateStatic)!.Invoke(null, null)!;
        var transpiler = patch.GetMethod("Transpiler", PrivateStatic)!;
        var tier = core.GetType("Game.Systems.RangeCheck.RangeCheckHit", true)!.GetField("Priority")!;
        List<CodeInstruction> Rewrite(List<CodeInstruction> code, ILGenerator generator) =>
            ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { code, generator })!).ToList();
        // Read with the installed Harmony instruction reader directly, but
        // neither query its native detour registry nor instantiate its
        // Mono-oriented generator on the diagnostic process's modern CLR.
        var copier = typeof(PatchProcessor).Assembly.GetType("HarmonyLib.MethodCopier", true)!;
        var constructor = copier.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(MethodBase), typeof(ILGenerator), typeof(LocalBuilder[]) }, null)!;
        var finalize = copier.GetMethod("Finalize",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        List<CodeInstruction> ReadOriginal(out ILGenerator il) {
            il = new DynamicMethod("ReadHiveSelector", typeof(void), Type.EmptyTypes).GetILGenerator();
            var reader = constructor.Invoke(new object?[] { target, il, null });
            return (List<CodeInstruction>)finalize.Invoke(reader, new object?[] { null, null, false })!;
        }
        var original = ReadOriginal(out var generator);
        var patched = Rewrite(original, generator);
        check(patched.Count == original.Count + 34 && original.SequenceEqual(patched.Where(original.Contains)),
            "installed selector matches both sites; every native instruction remains in order");
        check(patched.Count(i => i.opcode == OpCodes.Stfld && Equals(i.operand, tier)) == 1
            && patched.Count(i => i.opcode == OpCodes.Beq) == original.Count(i => i.opcode == OpCodes.Beq) + 2
            && patched.Count(i => i.opcode == OpCodes.Bgt) == original.Count(i => i.opcode == OpCodes.Bgt) + 2,
            "injected local tier tag and both comparison blocks");

        var transitions = Enumerable.Range(0, patched.Count)
            .Where(i => patched[i].opcode == OpCodes.Bgt && i > 2 && Equals(patched[i - 2].operand, tier))
            .ToArray();
        check(transitions.Length == 2, "both priority branches identified");
        var comparisons = transitions.Select(end => ReplayComparison(patched, end)).ToArray();
        foreach (var compare in comparisons) {
            check(compare(false, 0, 1, 2, 10) && !compare(false, 1, 0, 10, 2),
                "off: exact native distance wins regardless of tier");
            check(!compare(false, 0, 1, 5, 5) && !compare(false, 1, 0, float.NaN, 5),
                "off: native equal-distance and unordered comparisons do not keep the incumbent");
            check(!compare(true, 0, 1, 2, 10) && compare(true, 1, 0, 10, 2),
                "on: refill wins over ordinary work in either iteration order");
            check(compare(true, 1, 1, 2, 10) && !compare(true, 1, 1, 10, 2)
                && !compare(true, 1, 1, 5, 5) && compare(true, 0, 0, 2, 10),
                "on: native nearest and tie ordering inside each tier");
        }
        // Reduce the native loop to an incumbent ordinary hit and a farther
        // refill already marked workable by another Hive. A failed reach/work
        // gate keeps the incumbent even after the candidate passes early rank.
        bool SelectFarRefill(bool enabled, bool reachable, bool canWork) =>
            !comparisons[0](enabled, 0, 1, 2, 10)
            && reachable && canWork
            && !comparisons[1](enabled, 0, 1, 2, 10);
        check(SelectFarRefill(true, true, true) && !SelectFarRefill(false, true, true),
            "two-site replay: an already-workable refill passes the nearer ordinary hit only with opt-in");
        check(!SelectFarRefill(true, false, true) && !SelectFarRefill(true, true, false),
            "two-site replay: failed native reach or work gate retains ordinary fallback");
        var noFinal = ReadOriginal(out var alteredGenerator);
        int finalBranch = noFinal.FindLastIndex(i => i.opcode == OpCodes.Blt || i.opcode == OpCodes.Blt_S);
        noFinal[finalBranch].opcode = OpCodes.Bgt;
        var snapshot = noFinal.Select(i => new CodeInstruction(i)).ToList();
        check(Rewrite(noFinal, alteredGenerator).SequenceEqual(noFinal)
            && noFinal.Zip(snapshot).All(pair => pair.First.opcode == pair.Second.opcode
                && Equals(pair.First.operand, pair.Second.operand) && pair.First.labels.SequenceEqual(pair.Second.labels)),
            "mismatched native comparison leaves all incoming instructions intact");
        var existingTier = ReadOriginal(out var existingGenerator);
        existingTier.Add(new CodeInstruction(OpCodes.Ldfld, tier));
        check(Rewrite(existingTier, existingGenerator).SequenceEqual(existingTier),
            "unexpected use of the local ranking field refuses the patch");
        if (companion.Length != 0) {
            var other = AssemblyLoadContext.Default.LoadFromAssemblyPath(companion[0])
                .GetType(companion[1], true)!.GetMethod("Transpiler", PrivateStatic | BindingFlags.Public)!;
            var baseCode = ReadOriginal(out var combinedGenerator);
            var preprocessed = ((IEnumerable<CodeInstruction>)other.Invoke(null, new object[] { baseCode })!).ToList();
            check(preprocessed.Count == baseCode.Count
                && preprocessed.Zip(baseCode).Count(pair => !Equals(pair.First.operand, pair.Second.operand)) == 1,
                "installed companion enumeration hook applied");
            check(Rewrite(preprocessed, combinedGenerator).Count == preprocessed.Count + 34,
                "both ranking sites compose with the installed companion hook");
        }

        CheckClassifier(core, patch, check);
    }

    // Execute the exact injected decision branches in isolation. Only replace
    // the data loads by explicit test arguments and emit the original strict
    // distance branch with its two specimen distances. No job/tick is run.
    private static Compare ReplayComparison(List<CodeInstruction> code, int end) {
        var body = new DynamicMethod("ReplayHiveComparison", typeof(bool),
            new[] { typeof(bool), typeof(float), typeof(float), typeof(float), typeof(float) });
        var il = body.GetILGenerator();
        var distance = il.DefineLabel();
        var keep = il.DefineLabel();
        var replace = il.DefineLabel();
        for (int offset = 0; offset < 12; offset++) {
            var ins = code[end - 10 + offset];
            switch (offset) {
                case 0: il.Emit(OpCodes.Ldarg_0); break;
                case 2: case 4: case 7: break; // addresses lowered to tier arguments
                case 3: case 8: il.Emit(OpCodes.Ldarg_1); break;
                case 5: il.Emit(OpCodes.Ldarg_2); break;
                case 1: case 6: il.Emit(ins.opcode, distance); break;
                case 9: il.Emit(ins.opcode, (float)ins.operand); break;
                case 10: il.Emit(ins.opcode, keep); break;
                case 11: il.Emit(ins.opcode, replace); break;
            }
        }
        il.MarkLabel(distance);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        var nativeDistance = code.Skip(end + 2).First(i => i.opcode == OpCodes.Blt || i.opcode == OpCodes.Blt_S);
        il.Emit(nativeDistance.opcode, keep);
        il.MarkLabel(replace);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(keep);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        return (Compare)body.CreateDelegate(typeof(Compare));
    }

    private static void CheckClassifier(Assembly core, Type patch, Action<bool, string> check) {
        var hiveType = core.GetType("Game.Systems.Nanobots.NanobotHiveComp", true)!;
        var storageType = core.GetType("Game.Components.RefillableStorageComp", true)!;
        var tileType = core.GetType("Game.Data.Tile", true)!;
        var goalType = core.GetType("Game.Systems.AI.AIGoal", true)!;
        var entityType = tileType.BaseType!;
        var constants = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Game.Constants"));
        var job = (core.GetType("Game.CodeGen.JobTypeIdH")
            ?? constants.GetType("Game.CodeGen.JobTypeIdH", true)!).GetField("Hauling")!.GetRawConstantValue();
        var classifier = patch.GetMethod("GetRefillTier", PrivateStatic)!;
        float Tier(object? goal) => (float)classifier.Invoke(null, new[] { goal })!;
        object goal = RuntimeHelpers.GetUninitializedObject(goalType);
        SetInstanceField(goalType.GetField("JobIdH")!, goal, job);
        object storage = RuntimeHelpers.GetUninitializedObject(storageType);
        SetInstanceField(goalType.GetField("Target")!, goal, storage);
        check(Tier(goal) == 0 && Tier(null) == 0, "missing destination and null goal are not promoted");
        var tile = RuntimeHelpers.GetUninitializedObject(tileType);
        SetInstanceField(storageType.BaseType!.GetField("entity",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!, storage, tile);
        check(Tier(goal) == 0, "generic refill storage is not promoted");
        var hive = RuntimeHelpers.GetUninitializedObject(hiveType);
        var components = entityType.GetField("Components")!;
        var slots = Array.CreateInstance(components.FieldType.GetElementType()!, 1);
        slots.SetValue(hive, 0);
        SetInstanceField(components, tile, slots);
        check(Tier(goal) == 0, "even on a Hive, another component is not its fuel");
        SetInstanceField(hiveType.GetField("Fuel")!, hive, storage);
        check(Tier(goal) == 1, "Hauling to the actual Hive fuel component is promoted");
        SetInstanceField(goalType.GetField("JobIdH")!, goal, 0);
        check(Tier(goal) == 0, "non-Hauling work for the same component is not promoted");
    }

    // FieldInfo.SetValue can eagerly initialize the target class (whose
    // unrelated definition hashes call Unity). Set only an instance field in
    // this isolated fixture without requesting arbitrary type initialization.
    internal static void SetInstanceField(FieldInfo field, object target, object? value) {
        if (field.IsStatic) throw new ArgumentException("Expected an instance field.");
        var body = new DynamicMethod("BindTestInstance", typeof(void),
            new[] { typeof(object), typeof(object) }, typeof(HiveRankingChecks).Module, true);
        var il = body.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, field.DeclaringType!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(field.FieldType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, field.FieldType);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        ((Action<object, object?>)body.CreateDelegate(typeof(Action<object, object?>)))(target, value);
    }
}
