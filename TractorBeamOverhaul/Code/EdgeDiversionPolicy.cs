using System;

namespace TractorBeamOverhaul;

internal readonly struct EdgeExitPoint
{
    public EdgeExitPoint(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }
    public float Y { get; }
}

internal static class EdgeDiversionPolicy
{
    internal const float ExitMargin = 2f;

    internal static EdgeExitPoint ChooseNearestExit(float x, float y,
        float width, float height)
    {
        if (width <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        float safeX = Clamp(x, 0f, width);
        float safeY = Clamp(y, 0f, height);
        float nearestDistance = safeX;
        EdgeExitPoint nearest = new(-ExitMargin, safeY);

        float rightDistance = width - safeX;
        if (rightDistance < nearestDistance)
        {
            nearestDistance = rightDistance;
            nearest = new EdgeExitPoint(width + ExitMargin, safeY);
        }

        if (safeY < nearestDistance)
        {
            nearestDistance = safeY;
            nearest = new EdgeExitPoint(safeX, -ExitMargin);
        }

        float topDistance = height - safeY;
        if (topDistance < nearestDistance)
        {
            nearest = new EdgeExitPoint(safeX, height + ExitMargin);
        }

        return nearest;
    }

    private static float Clamp(float value, float minimum, float maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }
}
