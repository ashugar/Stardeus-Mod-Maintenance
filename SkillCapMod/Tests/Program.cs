using System.Numerics;
using SkillCapMod;

static class Program
{
    /// <summary>
    /// Minimal assertion helper for this dependency-free console test project. Throwing gives the
    /// build a non-zero exit code without adding a test framework that the game mod does not need.
    /// </summary>
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    /// <summary>
    /// Exercises the Core-independent boundary rules: exact parsing, display formatting, the
    /// unchanged level-50 boundary, logarithmic gains, and the projection to Core's byte ceiling.
    /// </summary>
    public static void Main()
    {
        Check(SkillCapMath.TryParseLevel("25", out BigInteger twentyFive, out _)
            && twentyFive == 25, "plain integer parse");
        Check(SkillCapMath.TryParseLevel("1.0e308", out BigInteger unlimited, out _)
            && unlimited == SkillCapMath.EffectivelyUnlimited, "scientific maximum parse");
        Check(SkillCapMath.TryParseLevel("2.5e2", out BigInteger twoFifty, out _)
            && twoFifty == 250, "whole scientific parse");
        Check(!SkillCapMath.TryParseLevel("2.5", out _, out _), "fractional level rejected");
        Check(!SkillCapMath.TryParseLevel("1e309", out _, out _), "above maximum rejected");
        Check(SkillCapMath.Canonical(unlimited) == "1e308", "maximum canonical form");
        Check(SkillCapMath.Format(new BigInteger(1500)) == "1,500",
            "four-digit current level display");
        Check(SkillCapMath.Format(new BigInteger(1501)) == "1,501",
            "four-digit next level display");
        Check(SkillCapMath.Format(BigInteger.Pow(10, 40)) == "1e40", "large level display");
        Check(Math.Abs(SkillCapMath.EffectiveLevel(50) - 50d) < 1e-9,
            "plateau boundary preserved");
        Check(SkillCapMath.EffectiveLevel(100) > 50d && SkillCapMath.EffectiveLevel(100) < 51d,
            "post-50 gain is small and positive");
        Check(Math.Abs(SkillCapMath.EffectiveLevel(SkillCapMath.EffectivelyUnlimited) - 255d) < 1e-9,
            "maximum maps to byte ceiling");
        double a = SkillCapMath.EffectiveLevel(BigInteger.Pow(10, 20));
        double b = SkillCapMath.EffectiveLevel(BigInteger.Pow(10, 21));
        double c = SkillCapMath.EffectiveLevel(BigInteger.Pow(10, 22));
        Check(Math.Abs((b - a) - (c - b)) < 1e-9,
            "equal exponential steps produce equal gains");
        Console.WriteLine("Skill Cap math tests passed.");
    }
}
