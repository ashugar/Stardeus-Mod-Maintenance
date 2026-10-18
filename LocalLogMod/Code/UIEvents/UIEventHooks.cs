using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.UIUX;
using Game.Tools;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.UIEvents {
    [HarmonyPatch]
    internal static class UIEventHooks {
        [HarmonyPatch(typeof(GoalHoverBoost), "BoostHoveredGoals")]
        [HarmonyPrefix]
        private static void BeforeHoverBoost(FreeSelectContext ctx) {
            var goals = new StringBuilder("[");
            for (int i = 0; i < ctx.HoveredGoals.Count; i++) {
                if (i > 0) { goals.Append('|'); }
                AIGoal goal = ctx.HoveredGoals[i];
                goals.Append(goal?.Id).Append(':').Append(goal?.Name)
                    .Append(':').Append(goal?.State)
                    .Append(":priority=").Append(goal?.GoalPriority)
                    .Append(":target=").Append(SessionLog.Entity(goal?.Target?.Entity));
            }
            Write("hover_boost position=" + ctx.HoveredPos + " goals=" + goals.Append(']'));
        }

        [HarmonyPatch(typeof(FreeSelectContext), nameof(FreeSelectContext.DeselectAny))]
        [HarmonyPrefix]
        private static void BeforeDeselectAny(FreeSelectContext __instance, out string __state) {
            __state = Selection(__instance) + " caller=" + CallerChain();
        }

        [HarmonyPatch(typeof(FreeSelectContext), nameof(FreeSelectContext.DeselectAny))]
        [HarmonyPostfix]
        private static void AfterDeselectAny(bool __result, string __state) {
            if (__result) { Write("deselect_any " + __state); }
        }

        [HarmonyPatch(typeof(FreeSelectContext), nameof(FreeSelectContext.TryDeselect))]
        [HarmonyPrefix]
        private static void BeforeTryDeselect(FreeSelectContext __instance, out string __state) {
            __state = Selection(__instance) + " caller=" + CallerChain();
        }

        [HarmonyPatch(typeof(FreeSelectContext), nameof(FreeSelectContext.TryDeselect))]
        [HarmonyPostfix]
        private static void AfterTryDeselect(bool __result, string __state) {
            if (__result) { Write("try_deselect " + __state); }
        }

        [HarmonyPatch(typeof(FreeSelectContext), nameof(FreeSelectContext.DeselectIfSelected))]
        [HarmonyPrefix]
        private static void BeforeDeselectIfSelected(FreeSelectContext __instance, Entity entity,
                out string __state) {
            __state = "entity=" + EntityText(entity) + " " + Selection(__instance)
                + " caller=" + CallerChain();
        }

        [HarmonyPatch(typeof(FreeSelectContext), nameof(FreeSelectContext.DeselectIfSelected))]
        [HarmonyPostfix]
        private static void AfterDeselectIfSelected(bool __result, string __state) {
            if (__result) { Write("deselect_if_selected " + __state); }
        }

        [HarmonyPatch(typeof(FreeSelectContext), "OnEntityRemoved")]
        [HarmonyPrefix]
        private static void BeforeEntityRemoved(FreeSelectContext __instance, Entity entity) {
            if (__instance.IsSelected(entity)) {
                Write("entity_removed_selected entity=" + EntityText(entity)
                    + " " + Selection(__instance) + " caller=" + CallerChain());
            }
        }

        [HarmonyPatch(typeof(EntitySelectionHandler), "OnMultiSelect")]
        [HarmonyPrefix]
        private static void OnMultiSelect(List<Being> beings, List<Obj> objs, List<Tile> tiles) {
            Write("multi_select beings=" + Entities(beings)
                + " objs=" + Entities(objs) + " tiles=" + Entities(tiles)
                + " caller=" + CallerChain());
        }

        [HarmonyPatch(typeof(EntityPanelHandler), "ReloadEntityIfSelected")]
        [HarmonyPostfix]
        private static void AfterReload(Entity entity, bool __result) {
            if (__result) {
                Write("panel_reload_if_selected result=True entity=" + EntityText(entity)
                    + " caller=" + CallerChain());
            }
        }

        [HarmonyPatch(typeof(EntityPanelWidget), nameof(EntityPanelWidget.ReloadPanel))]
        [HarmonyPrefix]
        private static void BeforePanelReload(EntityPanelWidget __instance, out string __state) {
            __state = "showing=" + __instance.IsShowing + " entity=" + PanelEntity(__instance)
                + " caller=" + CallerChain();
        }

        [HarmonyPatch(typeof(EntityPanelWidget), nameof(EntityPanelWidget.ReloadPanel))]
        [HarmonyPostfix]
        private static void AfterPanelReload(EntityPanelWidget __instance, bool __result, string __state) {
            Write("panel_reload result=" + __result + " showingAfter=" + __instance.IsShowing
                + " entityAfter=" + PanelEntity(__instance) + " " + __state);
        }

        [HarmonyPatch(typeof(EntityPanelHandler), "HidePanel")]
        [HarmonyPostfix]
        private static void AfterPanelHide(bool __result) {
            if (__result) { Write("panel_hidden caller=" + CallerChain()); }
        }

        private static string PanelEntity(EntityPanelWidget widget) {
            if (widget.CurrentBeing?.Being != null) { return SessionLog.Entity(widget.CurrentBeing.Being); }
            if (widget.CurrentObj?.Obj != null) { return SessionLog.Entity(widget.CurrentObj.Obj); }
            return SessionLog.Entity(widget.CurrentTile);
        }

        private static string Selection(FreeSelectContext ctx) {
            return "selectedBeings=" + Entities(ctx.SelectedBeings)
                + " selectedObjs=" + Entities(ctx.SelectedObjs)
                + " selectedTiles=" + Entities(ctx.SelectedTiles);
        }

        private static string Entities<T>(IList<T> entities) where T : Entity {
            if (entities == null || entities.Count == 0) { return "[]"; }
            var text = new StringBuilder("[");
            for (int i = 0; i < entities.Count; i++) {
                if (i > 0) { text.Append('|'); }
                text.Append(EntityText(entities[i]));
                if (i == 7 && entities.Count > 8) {
                    text.Append("|+").Append(entities.Count - 8);
                    break;
                }
            }
            return text.Append(']').ToString();
        }

        private static string EntityText(Entity entity) {
            if (entity is Tile tile) {
                Entity parent = EntityUtils.ParentObj(tile);
                if (parent != null) { return SessionLog.Entity(parent); }
            }
            return SessionLog.Entity(entity);
        }

        internal static string CallerChain() {
            var text = new StringBuilder();
            var frames = new StackTrace(2, false).GetFrames();
            if (frames == null) { return "unknown"; }
            int added = 0;
            foreach (StackFrame frame in frames) {
                MethodBase method = frame.GetMethod();
                Type type = method?.DeclaringType;
                string typeName = type?.FullName ?? string.Empty;
                if (typeName.StartsWith("LocalLogMod.", StringComparison.Ordinal)
                        || typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal)
                        || typeName.StartsWith("KL.Signals.", StringComparison.Ordinal)
                        || typeName.StartsWith("System.", StringComparison.Ordinal)
                        || typeName.StartsWith("MonoMod.", StringComparison.Ordinal)
                        || typeName.Contains("DMD<")
                        || method.Name.IndexOf("_Patch", StringComparison.Ordinal) >= 0) { continue; }
                if (added++ > 0) { text.Append('>'); }
                text.Append(typeName).Append('.').Append(method.Name);
                if (added == 6) { break; }
            }
            return text.Length == 0 ? "unknown" : text.ToString();
        }

        private static void Write(string detail) {
            SessionLog.Guard("ui.events", () => SessionLog.Write("UIEvents", detail));
        }
    }
}
