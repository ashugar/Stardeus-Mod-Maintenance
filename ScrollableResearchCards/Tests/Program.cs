using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

internal static class Program
{
    private static void Main(string[] args)
    {
        string managed = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
        {
            string file = Path.Combine(managed, new AssemblyName(request.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        Run(args[1]);
    }

    private static void Run(string file)
    {
        const BindingFlags internalStatic = BindingFlags.Static | BindingFlags.NonPublic;
        const string modNamespace = "Game.Systems.ScrollableResearchCards.";
        var mod = Assembly.LoadFrom(file);
        var guard = mod.GetType(modNamespace + "LegacyUnlocks", true);
        var create = (MethodInfo)guard.GetMethod("RequireLegacyMethod", internalStatic).Invoke(null, null);
        Check(create.Name == "CreateUnlocks", "installed Core's legacy renderer was recognized");

        var original = PatchProcessor.GetOriginalInstructions(create);
        var getUnlocks = original.Select(i => i.operand).OfType<MethodInfo>()
            .Single(m => m.Name == "GetUnlockableIconsFor");
        var matches = guard.GetMethod("MatchesLegacyOverflow", internalStatic);
        bool Recognizes(List<CodeInstruction> code) => (bool)matches.Invoke(null, new object[] { code, getUnlocks });
        List<CodeInstruction> Clone() => original.Select(i => new CodeInstruction(i)).ToList();

        Check(Recognizes(original), "actual IL matches");
        var changed = Clone();
        changed.First(i => i.LoadsConstant(12)).operand = (sbyte)13;
        Check(!Recognizes(changed), "unknown threshold falls back");

        changed = Clone();
        changed.First(i => Equals(i.operand, "+{0}")).operand = "{0}";
        Check(!Recognizes(changed), "unknown tile falls back");

        changed = Clone();
        changed.First(i => i.opcode == OpCodes.Leave).opcode = OpCodes.Br;
        Check(!Recognizes(changed), "unknown loop exit falls back");

        var scrolling = guard.GetMethod("ReferencesScrolling", internalStatic);
        var scrollRect = Assembly.Load("UnityEngine.UI").GetType("UnityEngine.UI.ScrollRect", true);
        Check((bool)scrolling.Invoke(null, new object[] { new CodeInstruction(OpCodes.Ldtoken, scrollRect) }),
            "native scroll evidence is detected separately");
        Check(!(bool)scrolling.Invoke(null, new object[] { new CodeInstruction(OpCodes.Ldtoken, typeof(string)) }),
            "unrelated evidence is ignored");

        var system = mod.GetType(modNamespace + "ScrollableResearchCardsSys", true);
        var firstWarning = system.GetMethod("FirstWarning", internalStatic);
        foreach (string reason in new[] { "Core research card code already references unlock scrolling",
            "Core unlock code is unrecognized" })
        {
            object[] warning = { "Core compatibility", reason };
            Check((bool)firstWarning.Invoke(null, warning), "first diagnostic is reported for " + reason);
            Check(!(bool)firstWarning.Invoke(null, warning), "repeated diagnostic is suppressed for " + reason);
        }

        var pane = mod.GetType(modNamespace + "ResearchUnlockPane", true);
        int columns = (int)pane.GetField("Columns", internalStatic).GetRawConstantValue();
        int capacity = (int)pane.GetField("Capacity", internalStatic).GetRawConstantValue();
        var lastRow = pane.GetMethod("LastRow", internalStatic);
        foreach (var sample in new[] { (1, 0), (12, 0), (13, 1), (16, 1), (17, 2), (91, 20) })
        {
            int row = (int)lastRow.Invoke(null, new object[] { sample.Item1 });
            Check(row == sample.Item2, sample.Item1 + " unlocks have expected last row offset");
            Check(row * columns <= sample.Item1 - 1 && row * columns + capacity >= sample.Item1,
                sample.Item1 + " unlocks can reach the last icon");
        }
    }

    private static void Check(bool result, string test)
    {
        if (!result) throw new Exception("FAIL " + test);
        Console.WriteLine("PASS " + test);
    }
}

