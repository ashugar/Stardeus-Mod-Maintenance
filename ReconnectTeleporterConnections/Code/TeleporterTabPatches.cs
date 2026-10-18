using System;
using System.Linq;
using System.Reflection;
using Game;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using Game.Systems.UIUX.Codex.Pages;
using Game.UI;
using Game.Utils;
using HarmonyLib;
using KL.Grid;
using KL.Pool;
using KL.Utils;
using UnityEngine;

namespace ReconnectTeleporterConnections;

[HarmonyPatch(typeof(TeleporterTab), nameof(TeleporterTab.LoadContent))]
internal static class TeleporterTabContentPatch
{
    private static readonly FieldInfo LifetimeField = AccessTools.Field(
        typeof(BaseEntityTab), "lifetime");

    private static bool Prefix(TeleporterTab __instance, WGroup group,
        TeleporterComp ___comp, GameState ___S, Tile ___tile,
        ref bool __result)
    {
        ReconnectTeleporterConnectionsSys system =
            ReconnectTeleporterConnectionsSys.Instance;
        Lifetime lifetime = LifetimeField?.GetValue(__instance) as Lifetime;
        if (system == null || lifetime == null || group == null
            || ___comp?.Entity?.IsActive != true || ___tile == null)
        {
            return true;
        }

        try
        {
            Build(__instance, lifetime, group, ___comp, ___S, ___tile,
                system);
            __result = true;
            return false;
        }
        catch (Exception exception)
        {
            D.LogEx(exception,
                "Reconnect Teleporter Connections failed to enrich the Teleporter tab");
            return true;
        }
    }

    private static void Build(TeleporterTab tab, Lifetime lifetime,
        WGroup group, TeleporterComp source, GameState state, Tile tile,
        ReconnectTeleporterConnectionsSys system)
    {
        WContentBlock block = UISpawn.Block(lifetime, group, 0);
        RectTransform container = block.Container;
        int rowIndex = 0;

        WIconText2 selected = UISpawn.Widget<WIconText2>(lifetime,
            UIPrefabId.WContent2Text2, container, rowIndex++);
        selected.Load(IconId.CLabel, "This Teleporter",
            ReconnectTeleporterConnectionsSys.DisplayIdentity(source));

        WText currentHeader = UISpawn.Widget<WText>(lifetime,
            UIPrefabId.WContent2Header, container, rowIndex++);
        currentHeader.Load("Current Destination");

        TeleporterComp current = source.Outbound;
        if (current != null)
        {
            WContentButton2Text2 linked =
                UISpawn.Widget<WContentButton2Text2>(lifetime,
                    UIPrefabId.WContent2Button2Text2,
                    container, rowIndex++);
            linked.Item.Load(IconId.CTargetSpot,
                ReconnectTeleporterConnectionsSys.DisplayIdentity(current),
                T.Linked,
                () => EntityUtils.SelectAndFocus(current.Tile));
            linked.Button.Load(T.Unlink, () =>
            {
                source.UnsetOutbound();
                Sounds.PlayEnergyOff(current.Tile.Position);
                state.UIQuery.ReloadEntityTab.Ask(tab);
                system.RefreshUi();
            });
        }
        else
        {
            WIconText2 unlinked = UISpawn.Widget<WIconText2>(lifetime,
                UIPrefabId.WContent2Text2, container, rowIndex++);
            unlinked.Load(IconId.CNotConfigured,
                "Current destination", T.NotLinked);
        }

        WText availableHeader = UISpawn.Widget<WText>(lifetime,
            UIPrefabId.WContent2Header, container, rowIndex++);
        availableHeader.Load("Available Destinations");

        TeleporterComp[] candidates = system.ActiveTeleporters()
            .Where(candidate => candidate != source
                && candidate != current
                && candidate.CanTeleport
                && !Pos.AreAdjacent(tile.PosIdx, candidate.Tile.PosIdx))
            .OrderBy(candidate =>
                ReconnectTeleporterConnectionsSys.DisplayIdentity(candidate),
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate =>
                Pos.SqrDistance(candidate.Tile.PosIdx, tile.PosIdx))
            .ToArray();

        foreach (TeleporterComp candidate in candidates)
        {
            TeleporterComp captured = candidate;
            float distance = Pos.Distance(candidate.Tile.PosIdx, tile.PosIdx);
            WContentButton2Text2 candidateRow =
                UISpawn.Widget<WContentButton2Text2>(lifetime,
                    UIPrefabId.WContent2Button2Text2,
                    container, rowIndex++);
            candidateRow.Item.Load(candidate.Entity.Definition.Preview,
                ReconnectTeleporterConnectionsSys.DisplayIdentity(candidate),
                distance.ToString("0.#") + " tiles",
                () => EntityUtils.SelectAndFocus(captured.Tile));
            candidateRow.Button.Load(T.LinkVerb, () =>
            {
                source.SetOutbound(captured);
                Sounds.PlayEnergyOn(captured.Tile.Position);
                state.UIQuery.ReloadEntityTab.Ask(tab);
                system.RefreshUi();
            });
            var tip = UIUtils.AddTip(lifetime, candidateRow,
                "Replace the current outbound destination with "
                + ReconnectTeleporterConnectionsSys.DisplayIdentity(candidate)
                + ".");
            tip.HoverFun = hovered =>
                EntityUtils.ShowEntityLink(null, captured.Tile, hovered);
        }

        if (candidates.Length == 0)
        {
            WIconText empty = UISpawn.Widget<WIconText>(lifetime,
                UIPrefabId.WContent2Text, container, rowIndex);
            empty.Load(IconId.CInfo,
                "No other powered, constructed Teleporter is currently eligible.");
        }
    }
}

[HarmonyPatch(typeof(TeleporterTab), nameof(TeleporterTab.LoadFooter))]
internal static class TeleporterTabFooterPatch
{
    private static readonly FieldInfo LifetimeField = AccessTools.Field(
        typeof(BaseEntityTab), "lifetime");

    private static bool Prefix(TeleporterTab __instance, WGroup group,
        TeleporterComp ___comp, ref bool __result)
    {
        ReconnectTeleporterConnectionsSys system =
            ReconnectTeleporterConnectionsSys.Instance;
        Lifetime lifetime = LifetimeField?.GetValue(__instance) as Lifetime;
        if (system == null || !system.HasConnectionPanel
            || lifetime == null || group == null
            || ___comp?.Entity?.IsActive != true)
        {
            return true;
        }

        try
        {
            WButtonIconText2 mapButton = UISpawn.Widget<WButtonIconText2>(
                lifetime, UIPrefabId.WContent4Button,
                group.Container, 0);
            mapButton.Load(IconId.CTargetSpot, "Connection Map", null,
                () => system.OpenConnectionMap(___comp));
            UIUtils.AddTip(lifetime, mapButton,
                "Open a graphical view of every constructed Teleporter and its outbound connection.");

            WButtonIconText2 howButton = UISpawn.Widget<WButtonIconText2>(
                lifetime, UIPrefabId.WContent4Button,
                group.Container, 1);
            howButton.Load(IconId.CInfo, T.HowItWorks, null, () =>
            {
                A.S.UIQuery.ShowCodexEntry.Ask(
                    CodexConceptPage.FindById(TeleportersPage.PageId));
            });

            __result = true;
            return false;
        }
        catch (Exception exception)
        {
            D.LogEx(exception,
                "Reconnect Teleporter Connections failed to enrich the Teleporter footer");
            return true;
        }
    }
}
