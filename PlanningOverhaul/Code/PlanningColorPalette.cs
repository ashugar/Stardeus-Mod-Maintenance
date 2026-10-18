using System;
using System.Reflection;
using Game;
using Game.Constants;
using Game.Data;
using Game.Systems.Plan;
using Game.Tools;
using HarmonyLib;
using KL.Utils;
using UnityEngine;

namespace PlanningOverhaul;

internal enum PlanColorKey
{
    Room = 0,
    BuildingType = 1
}

internal static class PlanningColorPalette
{
    internal const int IndoorStagingBrush = 7;
    internal const int OutdoorStagingBrush = 8;
    internal const int OutdoorEdgeStagingBrush = 9;
    internal const int FirstPlanBrush = 16;

    private const int HueSteps = 32;
    private const int SaturationSteps = 3;
    private const int LuminositySteps = 4;
    internal const int PlanColorCount = HueSteps * SaturationSteps * LuminositySteps;
    internal const int BufferSize = FirstPlanBrush + PlanColorCount;

    private static readonly Color32[] Colors = BuildColors();
    private static readonly FieldInfo ColorsRField = AccessTools.Field(typeof(PlanningSys), "colorsR");
    private static readonly FieldInfo ColorsGField = AccessTools.Field(typeof(PlanningSys), "colorsG");
    private static readonly FieldInfo ColorsBField = AccessTools.Field(typeof(PlanningSys), "colorsB");
    private static readonly FieldInfo ColorsADataField = AccessTools.Field(typeof(PlanningSys), "colorsAData");
    private static readonly FieldInfo OverlayShaderField = AccessTools.Field(typeof(PlanningSys), "overlayShader");
    private static readonly FieldInfo OverlayKernelField = AccessTools.Field(typeof(PlanningSys), "overlayKernel");

    internal static int BrushForOrdinal(int ordinal)
    {
        int normalized = Math.Max(0, ordinal) % PlanColorCount;
        return FirstPlanBrush + normalized;
    }

    internal static bool IsPlanningOverhaulBrush(int brush) =>
        brush >= IndoorStagingBrush && brush < BufferSize;

    internal static bool TryGetColor(int brush, out Color color)
    {
        if (brush < 0 || brush >= Colors.Length || Colors[brush].a == 0)
        {
            color = default;
            return false;
        }
        Color32 value = Colors[brush];
        value.a = (byte)Mathf.RoundToInt(255f * Tunables.PlanningToolAlpha);
        color = value;
        return true;
    }

    internal static Color OpaqueColor(int brush)
    {
        if (brush < 0 || brush >= Colors.Length || Colors[brush].a == 0)
            return Color.white;
        Color32 value = Colors[brush];
        value.a = byte.MaxValue;
        return value;
    }

    internal static void ExpandRendererPalette(PlanningSys system)
    {
        if (system == null || !Consts.HasComputeShaders) return;
        try
        {
            var shader = OverlayShaderField?.GetValue(system) as ComputeShader;
            if (shader == null || OverlayKernelField == null) return;
            int kernel = (int)OverlayKernelField.GetValue(system);
            float[] red = new float[BufferSize];
            float[] green = new float[BufferSize];
            float[] blue = new float[BufferSize];

            // Preserve the exact five Core Planning brushes and its no-build color.
            for (int brush = 1; brush <= 5; brush++)
            {
                int[] core = Tunable.IntSet("PlanningColor" + brush);
                if (core == null || core.Length < 3) continue;
                red[brush] = core[0];
                green[brush] = core[1];
                blue[brush] = core[2];
            }
            red[6] = 1f;

            for (int brush = IndoorStagingBrush; brush < Colors.Length; brush++)
            {
                Color32 color = Colors[brush];
                if (color.a == 0) continue;
                red[brush] = color.r;
                green[brush] = color.g;
                blue[brush] = color.b;
            }

            ReplaceBuffer(system, ColorsRField, shader, kernel, "colorsR", red,
                "PlanColorsR");
            ReplaceBuffer(system, ColorsGField, shader, kernel, "colorsG", green,
                "PlanColorsG");
            ReplaceBuffer(system, ColorsBField, shader, kernel, "colorsB", blue,
                "PlanColorsB");
        }
        catch (Exception ex)
        {
            D.LogEx(ex, "Planning Overhaul could not expand the Planning color palette");
        }
    }

    internal static void RevealMultiTileGhosts(PlanningSys system)
    {
        if (system == null || ColorsADataField?.GetValue(system) is not float[] alphaData)
            return;
        PlanningOverhaulSys.Instance?.ApplyMultiTileGhostAlpha(alphaData);
    }

    private static void ReplaceBuffer(PlanningSys system, FieldInfo field,
        ComputeShader shader, int kernel, string shaderName, float[] values,
        string bufferName)
    {
        if (field == null) return;
        if (field.GetValue(system) is ComputeBuffer old)
        {
            old.Release();
            The.Alloc.Unregister(old);
        }
        var replacement = new ComputeBuffer(values.Length, sizeof(float))
        {
            name = bufferName
        };
        replacement.SetData(values);
        The.Alloc.Register(replacement);
        field.SetValue(system, replacement);
        shader.SetBuffer(kernel, shaderName, replacement);
    }

    private static Color32[] BuildColors()
    {
        var result = new Color32[BufferSize];

        // These are the default Paint swatches from Core's TileTint palette.
        result[IndoorStagingBrush] = new Color32(243, 39, 39, 255);
        result[OutdoorStagingBrush] = new Color32(17, 155, 17, 255);
        result[OutdoorEdgeStagingBrush] = new Color32(242, 126, 11, 255);

        float[] saturations = { 0.58f, 0.74f, 0.9f };
        float[] luminosities = { 0.68f, 0.79f, 0.9f, 1f };
        const float goldenHueStep = 0.61803398875f;
        for (int ordinal = 0; ordinal < PlanColorCount; ordinal++)
        {
            int hueOrdinal = ordinal % HueSteps;
            int band = ordinal / HueSteps;
            float hue = (hueOrdinal * goldenHueStep) % 1f;
            float saturation = saturations[band % SaturationSteps];
            float luminosity = luminosities[(band / SaturationSteps) % LuminositySteps];
            Color color = Color.HSVToRGB(hue, saturation, luminosity);
            result[FirstPlanBrush + ordinal] = color;
        }
        return result;
    }
}

[HarmonyPatch]
internal static class PlanningExpandedRendererPalettePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(PlanningSys), "PrepareCompute");

    private static void Postfix(PlanningSys __instance)
    {
        PlanningColorPalette.ExpandRendererPalette(__instance);
    }
}

[HarmonyPatch]
internal static class PlanningMultiTileGhostAlphaPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(PlanningSys), "UpdateOverlayData");

    private static void Postfix(PlanningSys __instance)
    {
        PlanningColorPalette.RevealMultiTileGhosts(__instance);
    }
}

[HarmonyPatch(typeof(ToolPlan), nameof(ToolPlan.GetColor))]
internal static class PlanningExpandedToolColorPatch
{
    private static bool Prefix(int num, ref Color __result)
    {
        if (!PlanningColorPalette.TryGetColor(num, out Color color)) return true;
        __result = color;
        return false;
    }
}
