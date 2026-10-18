using System;
using Game;
using Game.CodeGen;
using Game.Commands;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Tools;
using Game.Utils;
using KL.Grid;
using KL.Text;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private bool TryOrderBackgroundRelocation(Tile source, int destination,
        Facing.Type targetRotation, int powerConnectorPosition,
        Action<Tile> onInstantRelocated,
        out ReinstallableComp target)
    {
        target = null;
        ReinstallableComp relocation = source?.Reinstallable;
        if (!CanBeginBackgroundRelocation(source, relocation)) return false;

        Tile prototype = EntityUtils.CreatePrototype<Tile>(source.Definition);
        if (prototype?.Reinstallable == null) return false;
        prototype.Transform.Rotation = targetRotation;
        if (powerConnectorPosition > 0 && prototype.ENode != null)
            prototype.ENode.TryConnectPosAfterBuild = powerConnectorPosition;

        // Repeat the live placement check immediately before issuing the Core
        // command. Destination discovery can precede this call by several
        // scheduler operations, so its earlier result is not authoritative.
        if (!CanPlannerPlaceTile(destination, prototype,
                source.Definition.LayerId, source)) return false;

        ToolPlaceTiles.ClearDebrisFor(prototype, destination, null);

        // Core intentionally relocates immediately in Sandbox instant-build
        // mode and when a multi-tile object is placed over part of itself.
        if (A.InstantBuild || RelocationOverlapsSource(source, prototype, destination))
        {
            source.Transform.RelocateToInstant(destination, targetRotation,
                onInstantRelocated);
            return true;
        }

        // This is the noninteractive half of Core's ToolPlaceTiles.Reinstall
        // path. The target ReinstallableComp creates Core's ordinary Relocate
        // goal during placement; no player tool, selection, panel, or sound is
        // touched by the background planner.
        prototype.Reinstallable.SourceId = relocation.EntityId;
        var command = new CmdPlaceTile(destination, prototype,
            warn: false, instant: false, skipValidation: true, afterPlace: tile =>
            {
                relocation.TargetId = tile.Id;
                relocation.ShowInfoIcon(IconId.CMove, T.RelocationPending);
            });
        command.Execute(S);
        if (!command.IsSuccess) return false;

        target = relocation.Target ?? command.PlacedTile?.Reinstallable;
        return target?.Entity?.IsActive == true;
    }

    private bool CanBeginBackgroundRelocation(Tile source,
        ReinstallableComp relocation)
    {
        if (source?.IsActive != true || relocation == null
                || !relocation.CanOrderRelocationNow
                || relocation.IsPending
                || source.Constructable?.IsAllWorkDone != true) return false;

        if (relocation.IsAdvanced && !A.InstantBuild
                && !S.BB.Get(BBKeyH.RelocationAdvanced, false)) return false;

        if (source.TryGetComponent<IWithFaction>(out var withFaction)
                && withFaction.Faction?.IsPlayer == false) return false;

        return true;
    }

    private static bool RelocationOverlapsSource(Tile source, Tile prototype,
        int destination)
    {
        foreach (int position in TileFootprint(prototype, destination))
        {
            Tile existing = EntityUtils.ParentObjAt(position);
            if (existing?.Id == source.Id) return true;
        }
        return false;
    }
}
