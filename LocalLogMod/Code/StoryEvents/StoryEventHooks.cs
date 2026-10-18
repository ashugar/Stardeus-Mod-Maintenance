using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using Game.Data;
using Game.Data.Space;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Mental;
using Game.Systems.Story;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.StoryEvents {
    [HarmonyPatch]
    internal static class StoryEventHooks {
        private const int MaxTextLength = 600;
        private static readonly Regex RichText = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex("\\s+", RegexOptions.Compiled);
        private static readonly Regex WordBoundary = new Regex("([a-z0-9])([A-Z])", RegexOptions.Compiled);

        [HarmonyPatch(typeof(StoryContext), nameof(StoryContext.StartEvent))]
        [HarmonyPrefix]
        private static void BeforeStart(StoryContext __instance, StoryEventData data) {
            SessionLog.Guard("story.start", () => SessionLog.Write("StoryEvents",
                "event_start " + Summary(data, EventCall(data, "OnStart"), __instance?.Ticks ?? 0)));
        }

        [HarmonyPatch(typeof(StoryContext), nameof(StoryContext.StartEvent))]
        [HarmonyPostfix]
        private static void AfterStart(StoryContext __instance, StoryEventData data) {
            SessionLog.Guard("story.started", () => SessionLog.Write("StoryEvents",
                "event_initialized " + Summary(data, EventCall(data, "OnStart"), __instance?.Ticks ?? 0)));
        }

        [HarmonyPatch(typeof(StoryEventData), nameof(StoryEventData.SetState))]
        [HarmonyPrefix]
        private static void BeforeStateChange(StoryEventData __instance, out StoryEventState __state) {
            __state = __instance?.State ?? StoryEventState.Undefined;
        }

        [HarmonyPatch(typeof(StoryEventData), nameof(StoryEventData.SetState))]
        [HarmonyPostfix]
        private static void AfterStateChange(StoryEventData __instance, StoryEventState __state) {
            SessionLog.Guard("story.state", () => {
                if (__instance == null || __state == __instance.State) { return; }
                SessionLog.Write("StoryEvents", "state_change from=" + __state + " to=" + __instance.State
                    + " " + Summary(__instance, typeof(StoryEventData).FullName + ".SetState", __instance.StartTick));
            });
        }

        [HarmonyPatch(typeof(StoryEventData), nameof(StoryEventData.CreateLogEntry))]
        [HarmonyPostfix]
        private static void LogEntryCreated(StoryEventData __instance) {
            SessionLog.Guard("story.description", () => SessionLog.Write("StoryEvents",
                "description_created " + Summary(__instance,
                    typeof(StoryEventData).FullName + ".CreateLogEntry", __instance?.StartTick ?? 0)));
        }

        [HarmonyPatch(typeof(StoryEventData), nameof(StoryEventData.HideLogEntry))]
        [HarmonyPostfix]
        private static void LogEntryHidden(StoryEventData __instance) {
            SessionLog.Guard("story.hidden", () => SessionLog.Write("StoryEvents",
                "description_hidden " + Summary(__instance,
                    typeof(StoryEventData).FullName + ".HideLogEntry", __instance?.StartTick ?? 0)));
        }

        [HarmonyPatch(typeof(StoryContext), nameof(StoryContext.OnLoad))]
        [HarmonyPostfix]
        private static void ContextLoaded(StoryContext __instance) {
            SessionLog.Guard("story.load", () => {
                if (__instance == null) { return; }
                SessionLog.Write("StoryEvents", "context_loaded ongoing=" + __instance.OngoingEvents.Count
                    + " past=" + __instance.PastEvents.Count);
                foreach (StoryEventData data in __instance.OngoingEvents) {
                    SessionLog.Write("StoryEvents", "event_restored "
                        + Summary(data, EventCall(data, "OnLoad"), __instance.Ticks));
                }
            });
        }

        private static string Summary(StoryEventData data, string call, long contextTick) {
            if (data == null) { return "eventData=none call=unknown description=\"Unknown story event\""; }
            StoryEventDef def = data.Def;
            Type implementation = def?.Event?.GetType();
            string implementationName = implementation?.FullName ?? "unknown";
            string assemblyName = implementation?.Assembly?.GetName()?.Name ?? "unknown";
            string outcome = def == null ? "unknown" : def.Outcome.ToString();
            return "eventData=" + data.Id
                + " definition=" + Clean(def?.Id ?? "custom")
                + " implementation=" + Clean(implementationName)
                + " call=" + Clean(call)
                + " sourceAssembly=" + Clean(assemblyName)
                + " outcome=" + outcome
                + " state=" + data.State
                + " seed=" + data.Seed
                + " startTick=" + data.StartTick
                + " endTick=" + data.EndTick
                + " contextTick=" + contextTick
                + " points=" + (def?.Points.ToString() ?? "unknown")
                + " pointsReserved=" + data.PointsReserved
                + " pointsCostActual=" + data.PointsCostActual
                + " flags=" + (def == null ? "unknown" : Clean(def.Flags.ToString()))
                + " dataFlags=" + Clean(data.Flags.ToString())
                + " eventFlagsActual=" + Clean(data.EventFlagsActual.ToString())
                + " logHidden=" + data.LogEntry.Hide
                + " failure=" + Quote(data.FailReason)
                + " description=" + Quote(Description(data))
                + " " + Targets(data);
        }

        private static string EventCall(StoryEventData data, string method) {
            Type implementation = data?.Def?.Event?.GetType();
            return (implementation?.FullName ?? "unknown") + "." + method;
        }

        private static string Description(StoryEventData data) {
            if (data.LogEntry.IsValid) {
                string title = Clean(data.LogEntry.Title);
                string description = Clean(data.LogEntry.Desc);
                if (title.Length > 0 && description.Length > 0) { return title + ": " + description; }
                if (title.Length > 0) { return title; }
                if (description.Length > 0) { return description; }
            }
            string name = data.Def?.EventId ?? data.Def?.Id ?? "Unknown story event";
            string readable = Humanize(name);
            return data.Def == null ? readable : readable + " (" + data.Def.Outcome + " story event)";
        }

        private static string Targets(StoryEventData data) {
            return "targetTile=" + SessionLog.Entity(data.TargetTile)
                + " targetBeing=" + Being(data.TargetBeing)
                + " targetObj=" + Obj(data.TargetObj)
                + " targetSpace=" + Space(data.TargetSO)
                + " targetOther=" + Other(data.TargetOther)
                + " faction=" + SafeValue(data.Faction)
                + " participants=" + Participants(data);
        }

        private static string Being(BeingData data) {
            if (data == null) { return "none"; }
            return "[data:" + data.Id + ",name:" + Clean(data.Mind?.Name?.DisplayName)
                + ",species:" + Clean(data.Species?.Id) + ",entity:" + SessionLog.Entity(data.Being) + "]";
        }

        private static string Obj(ObjData data) {
            if (data == null) { return "none"; }
            return data.Obj == null ? "[data:" + Clean(data.ToString()) + "]" : SessionLog.Entity(data.Obj);
        }

        private static string Space(SpaceObject target) {
            if (target == null) { return "none"; }
            return "[id:" + target.Id + ",name:" + Clean(target.Name) + "]";
        }

        private static string Other(object target) {
            if (target == null) { return "none"; }
            if (target is Entity entity) { return SessionLog.Entity(entity); }
            if (target is BeingData being) { return Being(being); }
            if (target is ObjData obj) { return Obj(obj); }
            if (target is SpaceObject space) { return Space(space); }
            if (target is IEnumerable items && !(target is string)) {
                var result = new StringBuilder("[type:");
                result.Append(Clean(target.GetType().FullName));
                result.Append(",items:");
                int count = 0;
                foreach (object item in items) {
                    if (count > 0) { result.Append('|'); }
                    if (count == 12) { result.Append("..."); break; }
                    result.Append(ReferenceEquals(item, target) ? "self" : SafeValue(item));
                    count++;
                }
                if (count == 0) { result.Append("empty"); }
                result.Append(']');
                return result.ToString();
            }
            return "[type:" + Clean(target.GetType().FullName) + ",value:" + SafeValue(target) + "]";
        }

        private static string Participants(StoryEventData data) {
            if (data.Participants == null || data.Participants.Count == 0) { return "none"; }
            var result = new StringBuilder("[");
            int count = Math.Min(data.Participants.Count, 12);
            for (int i = 0; i < count; i++) {
                if (i > 0) { result.Append('|'); }
                Mind mind = data.Participants[i];
                result.Append(Clean(mind?.Name?.DisplayName));
            }
            if (data.Participants.Count > count) {
                result.Append("|+");
                result.Append(data.Participants.Count - count);
            }
            result.Append(']');
            return result.ToString();
        }

        private static string Humanize(string value) {
            string clean = Clean(value).Replace('/', ' ').Replace('_', ' ');
            clean = clean.Replace("StoryEvent", "");
            return Spaces.Replace(WordBoundary.Replace(clean, "$1 $2"), " ").Trim();
        }

        private static string SafeValue(object value) {
            if (value == null) { return "none"; }
            try { return Clean(value.ToString()); }
            catch { return "[type:" + Clean(value.GetType().FullName) + "]"; }
        }

        private static string Quote(string value) => "\"" + Clean(value).Replace('"', '\'') + "\"";

        private static string Clean(string value) {
            if (string.IsNullOrEmpty(value)) { return ""; }
            string clean = Spaces.Replace(RichText.Replace(value, " "), " ").Trim();
            if (clean.Length > MaxTextLength) { clean = clean.Substring(0, MaxTextLength) + "..."; }
            return clean;
        }
    }
}
