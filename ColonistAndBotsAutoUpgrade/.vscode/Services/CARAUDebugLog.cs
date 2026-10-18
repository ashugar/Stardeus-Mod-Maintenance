using System.Collections.Generic;
using System.Text;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public sealed class CARAUDebugEntry
    {
        public string GameTime { get; set; }
        public CARAUDebugCategory Category { get; set; }
        public string Message { get; set; }
    }

    public static class CARAUDebugLog
    {
        private const int MaxEntries = 1000;

        private static readonly List<CARAUDebugEntry> entries = new();

        public static IReadOnlyList<CARAUDebugEntry> Entries => entries;

        public static void Add(CARAUDebugCategory category, string message)
        {
            if (entries.Count >= MaxEntries)
            {
                entries.RemoveAt(FindEvictionIndex());
            }

            entries.Add(new CARAUDebugEntry
            {
                GameTime = GetGameTime(),
                Category = category,
                Message = message
            });
        }

        private static int FindEvictionIndex()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (!IsEnabled(entries[i].Category))
                {
                    return i;
                }
            }

            return 0;
        }

        public static void Clear()
        {
            entries.Clear();
        }

        public static string BuildText()
        {
            var sb = new StringBuilder();

            sb.AppendLine(CARAUVersion.DisplayTitle);
            sb.AppendLine(
                "Debug Categories: " +
                $"Error={IsEnabled(CARAUDebugCategory.Error)} | " +
                $"Warning={IsEnabled(CARAUDebugCategory.Warning)} | " +
                $"UI={IsEnabled(CARAUDebugCategory.UI)} | " +
                $"Selection={IsEnabled(CARAUDebugCategory.Selection)} | " +
                $"Decision={IsEnabled(CARAUDebugCategory.Decision)} | " +
                $"Queue={IsEnabled(CARAUDebugCategory.Queue)} | " +
                $"Inventory={IsEnabled(CARAUDebugCategory.Inventory)} | " +
                $"Intent={IsEnabled(CARAUDebugCategory.Intent)} | " +
                $"Cleanup={IsEnabled(CARAUDebugCategory.Cleanup)}"
            );
            sb.AppendLine($"Stardeus Version: {CARAUVersion.StardeusCurrent}");
            sb.AppendLine();

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                if (!CARAUSettings.IsDebugCategoryEnabled(entry.Category))
                {
                    continue;
                }

                sb.AppendLine(
                    $"{entry.GameTime} CARAU {entry.Category} | " +
                    entry.Message
                );
            }

            return sb.ToString();
        }

        private static bool IsEnabled(CARAUDebugCategory category)
        {
            return CARAUSettings.IsDebugCategoryEnabled(category);
        }

        private static string GetGameTime()
        {
            try
            {
                var clock = Game.The.R?.State?.Clock;
                if (clock == null)
                {
                    return "----|--:--";
                }

                var ticks = clock.Ticks;
                var day = ticks / 1440;
                var ticksToday = ticks % 1440;
                var hour = ticksToday / 60;
                var minute = ticksToday % 60;

                return $"{day}|{hour:00}:{minute:00}";
            }
            catch
            {
                return "----|--:--";
            }
        }
    }
}
