namespace SuperCleaning;

internal sealed class BotActivityBreakdown
{
    internal string Key { get; }
    internal string Label { get; }
    internal int Count { get; set; }

    internal BotActivityBreakdown(string key, string label)
    {
        Key = key;
        Label = label;
    }
}
