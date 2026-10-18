namespace SuperCleaning;

internal sealed class DirtTrendSample
{
    internal long Ticks { get; }
    internal int DirtCount { get; }
    internal int Delta { get; }
    internal int IntervalMinutes { get; }

    internal DirtTrendSample(long ticks, int dirtCount, int delta,
        int intervalMinutes)
    {
        Ticks = ticks;
        DirtCount = dirtCount;
        Delta = delta;
        IntervalMinutes = intervalMinutes;
    }
}
