using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Crafting;
using Game.Systems.Ships;
using KL.Utils;

namespace LocalLogMod.Logging {
    internal static class SessionLog {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, StringBuilder> Pending = new Dictionary<string, StringBuilder>();
        private static readonly HashSet<string> ReportedErrors = new HashSet<string>();
        private static string root;
        private static bool started;
        private static bool failed;
        private static long currentTick;
        internal static int TimingWarningMs { get; private set; } = 8;

        internal static bool Research { get; private set; }
        internal static bool Manufacturing { get; private set; }
        internal static bool Power { get; private set; }
        internal static bool Tasks { get; private set; }
        internal static bool Beings { get; private set; }
        internal static bool CrewUI { get; private set; }
        internal static bool UIEvents { get; private set; }
        internal static bool Audio { get; private set; }
        internal static bool CanRecord => started && !failed;
        internal static bool ShipCombat { get; private set; }
        internal static bool Inventory { get; private set; }
        internal static bool Lifecycle { get; private set; }
        internal static bool StoryEvents { get; private set; }
        internal static bool Fire { get; private set; }
        internal static bool Timings { get; private set; }

        internal static void BeginOnce() {
            if (started) { return; }
            started = true;
            try {
                string library = Path.GetDirectoryName(typeof(SessionLog).Assembly.Location);
                root = Directory.GetParent(library).FullName;
                string settings = File.ReadAllText(Path.Combine(root, "Settings.json"));
                Research = ReadSwitch(settings, "Research");
                Manufacturing = ReadSwitch(settings, "Manufacturing");
                Power = ReadSwitch(settings, "Power");
                Tasks = ReadSwitch(settings, "Tasks");
                Beings = ReadSwitch(settings, "Beings");
                CrewUI = ReadSwitch(settings, "CrewUI");
                UIEvents = ReadSwitch(settings, "UIEvents", true);
                Audio = ReadSwitch(settings, "Audio", true);
                ShipCombat = ReadSwitch(settings, "ShipCombat");
                Inventory = ReadSwitch(settings, "Inventory");
                Lifecycle = ReadSwitch(settings, "Lifecycle");
                StoryEvents = ReadSwitch(settings, "StoryEvents", true);
                Fire = ReadSwitch(settings, "Fire", true);
                Timings = ReadSwitch(settings, "Timings");
                Match warning = Regex.Match(settings, "\"TimingWarningMs\"\\s*:\\s*(\\d+)");
                if (warning.Success && int.TryParse(warning.Groups[1].Value, out int ms)) {
                    TimingWarningMs = Math.Max(1, Math.Min(1000, ms));
                }
                string logs = Path.Combine(root, "Logs");
                Directory.CreateDirectory(logs);
                foreach (string file in Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories)) {
                    File.Delete(file);
                }
                foreach (string category in new[] { "Session", "Research", "Tasks", "Beings", "CrewUI", "UIEvents", "Audio", "ShipCombat", "Inventory", "Lifecycle", "StoryEvents", "Fire", "Timings" }) {
                    File.WriteAllText(Path.Combine(logs, category + ".log"), "");
                }
                Directory.CreateDirectory(Path.Combine(logs, "Manufacturing"));
                Directory.CreateDirectory(Path.Combine(logs, "Power"));
            } catch (Exception ex) {
                failed = true;
                D.Err("Local Log Mod could not prepare its local logs: {0}", ex);
            }
        }

        private static bool ReadSwitch(string text, string name, bool? defaultValue = null) {
            Match match = Regex.Match(text, "\"" + name + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (!match.Success) {
                if (defaultValue.HasValue) { return defaultValue.Value; }
                throw new FormatException("Missing setting: " + name);
            }
            return bool.Parse(match.Groups[1].Value);
        }

        internal static void Write(string category, string detail) {
            WritePath(category + ".log", detail);
        }

        internal static void SetTick(long tick) { currentTick = tick; }

        internal static void Building(string category, Tile tile, string detail) {
            if (tile == null) { WritePath(category + "/unknown.log", detail); return; }
            string name = SafeName(tile.DefinitionId ?? "unknown");
            WritePath(category + "/" + name + "_" + tile.Id.ToString(CultureInfo.InvariantCulture) + ".log",
                "building=" + Entity(tile) + " " + detail);
        }

        private static string SafeName(string value) {
            var result = new StringBuilder(Math.Min(48, value.Length));
            foreach (char c in value) {
                if (result.Length == 48) { break; }
                result.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return result.Length == 0 ? "unknown" : result.ToString();
        }

        private static void WritePath(string relative, string detail) {
            if (!started || failed) { return; }
            lock (Gate) {
                try {
                    string path = Path.Combine(root, "Logs", relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!Pending.TryGetValue(path, out StringBuilder buffer)) {
                        buffer = new StringBuilder();
                        Pending.Add(path, buffer);
                    }
                    buffer.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    buffer.Append(" tick=");
                    buffer.Append(currentTick);
                    buffer.Append(' ');
                    buffer.AppendLine((detail ?? "").Replace('\r', ' ').Replace('\n', ' '));
                    if (buffer.Length >= 8192) { FlushOne(path, buffer); }
                } catch (Exception ex) { Fail("write", ex); }
            }
        }

        internal static void Flush() {
            if (!started || failed) { return; }
            lock (Gate) {
                try {
                    foreach (var entry in Pending) { FlushOne(entry.Key, entry.Value); }
                } catch (Exception ex) { Fail("flush", ex); }
            }
        }

        private static void FlushOne(string path, StringBuilder buffer) {
            if (buffer.Length == 0) { return; }
            File.AppendAllText(path, buffer.ToString(), Encoding.UTF8);
            buffer.Clear();
        }

        private static void Fail(string operation, Exception ex) {
            failed = true;
            D.Err("Local Log Mod {0} failed; logging stopped: {1}", operation, ex);
        }

        internal static void Guard(string hook, Action action) {
            try { action(); }
            catch (Exception ex) {
                lock (Gate) {
                    if (ReportedErrors.Add(hook)) {
                        D.Err("Local Log Mod observer {0} failed: {1}", hook, ex);
                    }
                }
            }
        }

        internal static string Entity(Entity entity) {
            if (entity == null) { return "none"; }
            return entity.DefinitionId + "#" + entity.Id + "@" + entity.PosIdx;
        }

        internal static string Crafter(MultiCrafterComp crafter) => Entity(crafter?.Tile);

        private static readonly FieldInfo CraftableIdField = typeof(CraftingDemand).GetField("craftableId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CraftableField = typeof(CraftingDemand).GetField("craftable",
            BindingFlags.Instance | BindingFlags.NonPublic);

        internal static string Order(CraftingOrder order) {
            if (order == null) { return "none"; }
            return "craftable=" + CraftableId(order)
                + " state=" + order.State + " progress=" + order.Progress.ToString("0.###", CultureInfo.InvariantCulture)
                + " produced=" + order.Demand?.AmountProduced;
        }

        internal static string CraftableId(CraftingOrder order) {
            if (order?.Demand == null) { return "unknown"; }
            return CraftableIdField?.GetValue(order.Demand) as string
                ?? (CraftableField?.GetValue(order.Demand) as Craftable)?.Id
                ?? "unknown";
        }

        internal static string Goal(AIGoal goal) {
            if (goal == null) { return "none"; }
            return "goal=" + goal.Id + " name=" + goal.Name + " job=" + goal.JobIdH
                + " state=" + goal.State + " priority=" + goal.GoalPriority
                + " target=" + Entity(goal.Target?.Entity)
                + " agent=" + Entity(goal.Agent?.Being)
                + " fail=" + goal.FailReason;
        }

        internal static string Ship(NPCShip ship) => ship == null ? "none" : ship.Name + "#" + ship.Id;
    }
}
