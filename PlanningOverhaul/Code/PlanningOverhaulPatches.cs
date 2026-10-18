using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Constants;
using Game.Systems.Plan;
using Game.Systems.UIUX;
using Game.Tools;
using Game.Utils;
using HarmonyLib;
using KL.Clock;
using KL.Utils;
using UnityEngine;

namespace PlanningOverhaul;

[HarmonyPatch(typeof(Clock), nameof(Clock.SetSpeed))]
internal static class PlanningPlacementPausePatch
{
    private static void Prefix(ref ClockSpeed newSpeed)
    {
        if (PlanningOverhaulSys.Instance?.CaptureActive == true
                && newSpeed != ClockSpeed.Stopped)
            newSpeed = ClockSpeed.Stopped;
    }
}

[HarmonyPatch]
internal static class PlanningPlacementValidationPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(ToolPlaceTiles), "IsAbleToPlace",
        new[] { typeof(int), typeof(bool), typeof(bool) });

    private static bool Prefix(ref bool __result)
    {
        PlanningOverhaulSys system = PlanningOverhaulSys.Instance;
        if (system?.CaptureActive != true || system.NativeRelocationActive) return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch]
internal static class PlanningSinglePlacementPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(ToolPlaceTiles), "PlaceTile",
        new[] { typeof(int), typeof(bool) });

    private static bool Prefix(ToolPlaceTiles __instance, int pos,
        ref bool ___justPlacedTile)
    {
        PlanningOverhaulSys system = PlanningOverhaulSys.Instance;
        if (system?.CaptureActive != true || system.NativeRelocationActive) return true;
        system.CapturePlacement(__instance, new[] { pos });
        // Prevent ToolPlaceTiles.OnClickUp from interpreting this planned click
        // as a request to cancel an unfinished native construction order.
        ___justPlacedTile = true;
        return false;
    }
}

[HarmonyPatch]
internal static class PlanningPreserveNativeBuildPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(ToolPlaceTiles), "RemoveCurrentBuildTask",
        new[] { typeof(int) });

    private static bool Prefix(ref bool __result)
    {
        PlanningOverhaulSys system = PlanningOverhaulSys.Instance;
        if (system?.CaptureActive != true || system.NativeRelocationActive) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class PlanningMultiplePlacementPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(ToolPlaceTiles), "PlaceTiles",
        new[] { typeof(List<int>) });

    private static bool Prefix(ToolPlaceTiles __instance, List<int> pos)
    {
        PlanningOverhaulSys system = PlanningOverhaulSys.Instance;
        if (system?.CaptureActive != true || system.NativeRelocationActive) return true;
        system.CapturePlacement(__instance, pos);
        return false;
    }
}

[HarmonyPatch(typeof(ToolPlaceTiles), nameof(ToolPlaceTiles.OnHover))]
internal static class PlanningPlacementGhostRefreshPatch
{
    private static void Postfix(Vector2 coords)
    {
        PlanningOverhaulSys.Instance?.RefreshGhostAt(coords);
    }
}

[HarmonyPatch(typeof(ToolPlan), nameof(ToolPlan.OnClickDown))]
internal static class PlanningPlanGestureStartPatch
{
    private static void Prefix(string ___mode)
    {
        PlanningOverhaulSys.Instance?.OnPlanToolClickDown(___mode);
    }
}

[HarmonyPatch(typeof(ToolPlan), nameof(ToolPlan.OnHover))]
internal static class PlanningPlanGhostRefreshPatch
{
    private static void Postfix(Vector2 coords)
    {
        PlanningOverhaulSys.Instance?.RefreshGhostAt(coords);
    }
}

[HarmonyPatch]
internal static class PlanningPlanDrawPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(ToolPlan), "DrawAt", new[] { typeof(int) });

    private static bool Prefix(int pos, string ___mode)
    {
        return PlanningOverhaulSys.Instance?.OnPlanToolDrawAt(___mode, pos) != true;
    }
}

[HarmonyPatch(typeof(ToolPlan), nameof(ToolPlan.OnClickUp))]
internal static class PlanningPlanGestureEndPatch
{
    private static void Postfix(string ___mode)
    {
        PlanningOverhaulSys.Instance?.OnPlanToolClickUp(___mode);
    }
}

[HarmonyPatch(typeof(ToolPlaceTiles), nameof(ToolPlaceTiles.Deactivate))]
internal static class PlanningPlacementEndPatch
{
    private static void Postfix()
    {
        PlanningOverhaulSys.Instance?.EndCapture("Building placement closed. Your construction plan was preserved.");
    }
}

[HarmonyPatch(typeof(PlanningUI), nameof(PlanningUI.Show))]
internal static class PlanningUiEntryPatch
{
    private static readonly FieldInfo LifetimeField = AccessTools.Field(typeof(PlanningUI), "lifetime");
    private static readonly FieldInfo WidgetField = AccessTools.Field(typeof(PlanningUI), "widget");

    private static void Postfix(PlanningUI __instance)
    {
        try
        {
            var lifetime = LifetimeField?.GetValue(__instance) as Lifetime;
            var widget = WidgetField?.GetValue(__instance) as WPanel;
            if (lifetime == null || widget?.Content?.Container == null) return;
            WContentBlock block = UISpawn.Block(lifetime, widget.Content,
                widget.Content.Container.childCount);
            WButtonIconText2 button = UISpawn.Widget<WButtonIconText2>(lifetime,
                UIPrefabId.WContent2Button, block.Container, 0);
            button.Load(IconId.WPlanning, "Construction Plan", "Rooms, upgrades, and Pulse Wave",
                () => PlanningOverhaulSys.Instance?.ToggleUi());
        }
        catch (Exception ex)
        {
            D.LogEx(ex, "Planning Overhaul could not add its Planning-window button");
        }
    }
}
