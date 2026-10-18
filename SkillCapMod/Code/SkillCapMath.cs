using System;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SkillCapMod;

/// <summary>
/// Contains the numeric rules that do not depend on a running Stardeus game.
/// Keeping parsing and scaling separate from Core makes the risky boundary—the conversion
/// back to Core's byte-sized <c>JobSlot.Level</c>—easy to see and unit test.
/// </summary>
internal static class SkillCapMath
{
    // Core currently stops ordinary training at 25 even though JobSlot.Level is a byte.
    internal const int VanillaCap = 25;

    // Levels are unchanged through this point. Diminishing returns begin above it.
    internal const int SoftPlateauLevel = 50;

    // Core's JobSlot.Level field and several Core APIs can represent at most 255.
    internal const int CoreProjectionMaximum = byte.MaxValue;

    // BigInteger stores the true level. 10^308 is the user-facing "effectively unlimited" cap.
    internal static readonly BigInteger EffectivelyUnlimited = BigInteger.Pow(10, 308);
    internal static readonly BigInteger DefaultCap = new BigInteger(VanillaCap);

    // The parser accepts integer and scientific forms, but TryParseLevel later rejects any
    // scientific value that does not resolve to a whole number.
    private static readonly Regex NumberPattern = new(
        @"^\+?(?<whole>\d+)(?:\.(?<fraction>\d*))?(?:[eE](?<exponent>[+-]?\d+))?$",
        RegexOptions.CultureInvariant);
    private static readonly double PlateauLog10 = Math.Log10(SoftPlateauLevel);
    private const double MaximumLog10 = 308d;

    /// <summary>
    /// Parses the main-menu setting into a <see cref="BigInteger"/> true level.
    /// Core's preference store is string-based, so this method validates the string before any
    /// value reaches the game. Scientific notation is expanded exactly rather than through a
    /// floating-point type, which avoids rounding very large levels.
    /// </summary>
    internal static bool TryParseLevel(string text, out BigInteger level, out string error)
    {
        level = BigInteger.Zero;
        error = null;
        string value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "Enter a whole-number level from 1 through 1e308.";
            return false;
        }

        Match match = NumberPattern.Match(value);
        if (!match.Success)
        {
            error = "Use a whole number or scientific notation, such as 50 or 1e308.";
            return false;
        }

        string whole = match.Groups["whole"].Value;
        string fraction = match.Groups["fraction"].Success
            ? match.Groups["fraction"].Value
            : string.Empty;
        if (!int.TryParse(match.Groups["exponent"].Success
                ? match.Groups["exponent"].Value
                : "0", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                out int exponent) || exponent < -1000 || exponent > 1000)
        {
            error = "The scientific-notation exponent must be between -1000 and 1000.";
            return false;
        }

        string digits = (whole + fraction).TrimStart('0');
        if (digits.Length == 0)
        {
            error = "Maximum level must be at least 1.";
            return false;
        }
        if (digits.Length > 309)
        {
            error = "Maximum level cannot exceed 1e308.";
            return false;
        }

        BigInteger significand = BigInteger.Parse(
            digits, NumberStyles.Integer, CultureInfo.InvariantCulture);
        int decimalShift = exponent - fraction.Length;
        if (decimalShift >= 0)
        {
            if (digits.Length + decimalShift > 309)
            {
                error = "Maximum level cannot exceed 1e308.";
                return false;
            }
            level = significand * BigInteger.Pow(10, decimalShift);
        }
        else
        {
            int divisorPower = -decimalShift;
            if (divisorPower > 1000)
            {
                error = "Maximum level must be a whole number.";
                return false;
            }
            BigInteger divisor = BigInteger.Pow(10, divisorPower);
            level = BigInteger.DivRem(significand, divisor, out BigInteger remainder);
            if (!remainder.IsZero)
            {
                error = "Maximum level must resolve to a whole number.";
                return false;
            }
        }

        if (level < BigInteger.One)
        {
            error = "Maximum level must be at least 1.";
            return false;
        }
        if (level > EffectivelyUnlimited)
        {
            error = "Maximum level cannot exceed 1e308.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Produces the stable string written to Core's global preferences. The largest supported
    /// value is kept as <c>1e308</c> so the settings file does not need a 309-digit entry.
    /// </summary>
    internal static string Canonical(BigInteger level) =>
        level == EffectivelyUnlimited
            ? "1e308"
            : level.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Produces compact UI text for a true level. Core's native skill labels were designed for
    /// byte values, so large values are shortened to scientific notation before being displayed.
    /// </summary>
    internal static string Format(BigInteger level)
    {
        string digits = level.ToString(CultureInfo.InvariantCulture);
        if (digits.Length <= 6)
        {
            return level.ToString("N0", CultureInfo.InvariantCulture);
        }

        int shown = Math.Min(4, digits.Length);
        string fraction = digits.Substring(1, shown - 1).TrimEnd('0');
        return fraction.Length == 0
            ? $"{digits[0]}e{digits.Length - 1}"
            : $"{digits[0]}.{fraction}e{digits.Length - 1}";
    }

    /// <summary>
    /// Converts the true, arbitrary-size level to the effective scale used by Core formulas.
    /// Levels through 50 are unchanged. Above 50, equal multiplication of the true level
    /// produces equal gains, with 1e308 mapping to 255—the largest value Core can store.
    /// This lets patched Core formulas keep improving without allowing astronomical true levels
    /// to overflow floats, curve inputs, or byte-oriented callers.
    /// </summary>
    internal static double EffectiveLevel(BigInteger level)
    {
        if (level <= SoftPlateauLevel)
        {
            return (double)BigInteger.Max(BigInteger.Zero, level);
        }

        double log10 = BigInteger.Log(level, 10d);
        double fraction = (log10 - PlateauLog10) / (MaximumLog10 - PlateauLog10);
        return SoftPlateauLevel
            + (CoreProjectionMaximum - SoftPlateauLevel) * Math.Max(0d, Math.Min(1d, fraction));
    }

    /// <summary>
    /// Projects a true level into Core's byte field. Unpatched Core code and other mods can still
    /// read a useful, monotonic <c>JobSlot.Level</c>, while this mod retains the exact level in its
    /// sidecar state and save component.
    /// </summary>
    internal static byte Project(BigInteger level)
    {
        double effective = EffectiveLevel(level);
        return (byte)Math.Max(0, Math.Min(byte.MaxValue, (int)Math.Floor(effective)));
    }

}
