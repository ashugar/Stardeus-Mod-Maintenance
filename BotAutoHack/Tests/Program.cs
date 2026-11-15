using System;
using Game.Systems.BotAutoHack;

internal static class Program {
    private static int assertions;

    private static void Main() {
        CheckAnchor(0f, 0.30f, 0.70f, "0 days");
        CheckAnchor(0.05f, 0.335f, 0.665f, "1 day");
        CheckAnchor(0.20f, 0.44f, 0.56f, "3 days");
        CheckAnchor(0.35f, 0.545f, 0.455f, "7+ days");

        bool rejected = false;
        try {
            HackConversionFormula.ConversionChance(1.01f);
        } catch (ArgumentOutOfRangeException) {
            rejected = true;
        }
        Check(rejected, "Out-of-range Core curve chance must be rejected");

        Console.WriteLine("PASS: " + assertions
            + " assertions. Gameplay acceptance remains in-game.");
    }

    private static void CheckAnchor(
            float coreCurve,
            float expectedConversion,
            float expectedBurnout,
            string label) {
        float conversion = HackConversionFormula.ConversionChance(coreCurve);
        float burnout = HackConversionFormula.BurnoutChance(coreCurve);
        CheckClose(conversion, expectedConversion, label + " conversion");
        CheckClose(burnout, expectedBurnout, label + " burnout");
        CheckClose(conversion + burnout, 1f, label + " outcome total");
    }

    private static void CheckClose(float actual, float expected, string label) {
        Check(Math.Abs(actual - expected) < 0.000001f,
            label + ": expected " + expected + ", found " + actual);
    }

    private static void Check(bool result, string message) {
        assertions++;
        if (!result) {
            throw new Exception(message);
        }
    }
}
