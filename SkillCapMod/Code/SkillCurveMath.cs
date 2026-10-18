using System;
using KL.Utils;

namespace SkillCapMod;

/// <summary>
/// Adapts Core's configured skill curves for effective levels beyond their final control point.
/// Core owns the curve data; the mod only extends the last configured trend when necessary.
/// </summary>
internal static class SkillCurveMath
{
    /// <summary>
    /// Evaluates a Core <see cref="Curve"/> normally while the effective level is inside its
    /// configured range. Above the final point, it continues the final segment's slope so a skill
    /// never becomes completely flat merely because the original Core curve ended at a low level.
    /// Degenerate curves and invalid floating-point results fall back to Core's final value.
    /// </summary>
    internal static float EvaluateExtended(Curve curve, double effectiveLevel)
    {
        if (curve?.Points == null || curve.Points.Length == 0)
        {
            return 0f;
        }

        var points = curve.Points;
        var last = points[points.Length - 1];
        if (effectiveLevel <= last.x || points.Length == 1)
        {
            return curve.Evaluate((float)effectiveLevel);
        }

        var previous = points[points.Length - 2];
        double width = last.x - previous.x;
        if (width <= 0d)
        {
            return last.y;
        }
        double slope = (last.y - previous.y) / width;
        double result = last.y + slope * (effectiveLevel - last.x);
        if (double.IsNaN(result) || double.IsInfinity(result))
        {
            return last.y;
        }
        return (float)Math.Max(0d, Math.Min(float.MaxValue, result));
    }
}
