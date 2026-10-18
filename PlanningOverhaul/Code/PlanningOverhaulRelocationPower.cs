using Game;
using Game.CodeGen;
using Game.Components;
using Game.Data;
using Game.Systems.Crafting;
using KL.Grid;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private bool RequiresPoweredRelocationDestination(Tile source)
    {
        return source?.ENode != null
            && source.TryGetComponent(out MultiCrafterComp crafter)
            && crafter.CurrentOrder != null;
    }

    private bool TryPrepareRelocationPower(Tile source, int destination,
        out int connectorPosition)
    {
        // This is a read-only eligibility probe, deliberately repeated just
        // before ordering a move. Discovery may short-circuit unused candidates;
        // it must never reserve a connection or interrupt a production order.
        connectorPosition = 0;
        if (!RequiresPoweredRelocationDestination(source)) return true;

        ElectricNodeComp sourceNode = source.ENode;
        int connectedCount = S.Sys.Electricity.ConnectionsFor(
            sourceNode.EntityId, Caches.IntArray128);
        for (int i = 0; i < connectedCount; i++)
        {
            ElectricNodeComp connected = S.Components.Find<ElectricNodeComp>(
                Caches.IntArray128[i]);
            if (IsPoweredRelocationConnector(connected, sourceNode,
                    destination, alreadyConnected: true))
                return true;
        }

        ElectricNodeComp best = null;
        int bestDistance = int.MaxValue;
        foreach (ElectricNodeComp candidate in
                 S.Components.AllByType<ElectricNodeComp>())
        {
            if (!IsPoweredRelocationConnector(candidate, sourceNode,
                    destination, alreadyConnected: false)) continue;
            int distance = Pos.SqrDistance(destination, candidate.PosIdx);
            if (distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }

        if (best == null) return false;
        connectorPosition = best.PosIdx;
        return true;
    }

    private bool IsPoweredRelocationConnector(ElectricNodeComp connector,
        ElectricNodeComp sourceNode, int destination, bool alreadyConnected)
    {
        if (connector?.Entity?.IsActive != true
                || connector == sourceNode
                || !connector.IsConnector
                || connector.Tile?.IsConstructed != true
                || connector.Tile.Health?.IsHeavilyDamaged == true
                || !connector.IsConnected
                || !connector.IsReachable
                || !S.Sys.Electricity.IsGridProducing(connector.Node.GridId)
                || connector.Tile.Reinstallable?.IsPending == true
                || connector.Tile.Goal?.JobIdH == JobTypeIdH.Demolition
                || (!alreadyConnected && connector.Node.FreeConnections
                    <= PendingRelocationPowerReservations(connector.PosIdx)
                        + PendingConstructionSupportPowerReservations(
                            connector.PosIdx)))
            return false;

        return Pos.SqrDistance(destination, connector.PosIdx)
            <= connector.RangeSqr;
    }

    private int PendingRelocationPowerReservations(int connectorPosition)
    {
        int reserved = 0;
        foreach (int targetId in relocationTargets.Keys)
        {
            ReinstallableComp target = S.Components.Find<ReinstallableComp>(
                targetId);
            if (target?.Entity?.IsActive == true
                    && target.IsPendingQuick
                    && target.Tile?.ENode?.TryConnectPosAfterBuild
                        == connectorPosition)
                reserved++;
        }
        return reserved;
    }
}
