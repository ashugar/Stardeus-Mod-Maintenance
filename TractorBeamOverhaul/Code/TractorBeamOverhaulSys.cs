using System;
using System.Collections.Generic;
using System.Reflection;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Systems;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using UnityEngine;

namespace TractorBeamOverhaul;

public sealed class TractorBeamOverhaulSys : GameSystem
{
    public const string SysId = "TractorBeamOverhaulSys";
    public const string ConsumerId = "TractorBeamOverhaul";
    public const string ModVersion = "1.1.1";
    public const string RequiredUtilitiesVersion = "1.4.0";

    private const string HarmonyId =
        "jleas.stardeus.tractorbeamoverhaul.projectilecoordination";

    private readonly List<IWithTrajectory> trajectoryCandidates = new();
    private readonly List<TractorBeamComp> beams = new();
    private readonly List<TargetLoad> targetLoads = new();
    private readonly List<MovableBeamAssignment> movableAssignments = new();
    private readonly Dictionary<int, int> targetIndexByEntityId = new();
    private readonly Dictionary<int, EdgeDiversionRoute> edgeDiversionRoutes =
        new();
    private readonly object[] assignmentArguments = new object[1];

    private Harmony harmony;
    private MethodInfo onWithTrajectoryMethod;
    private MethodInfo meteorInfluenceMethod;
    private MethodInfo asteroidInfluenceMethod;
    private MethodInfo meteorOnRemoveMethod;
    private MethodInfo asteroidOnRemoveMethod;
    private MethodInfo tractorBeamTickProgressMethod;
    private int lastRebalancedThreatEntityId;
    private bool coordinating;
    private bool faulted;
    private bool diversionActive;
    private bool diversionFaulted;

    internal static TractorBeamOverhaulSys Instance { get; private set; }

    public override string Id => SysId;
    public override bool SkipInSandbox => false;

    // Phase 2 keeps coordination permanently enabled. A persistent setting and
    // AUMF interface belong to the later interface phase.
    internal bool CoordinationEnabled => true;

    [SelfInit]
    private static void Register()
    {
        GameSystems.Register(SysId, () => new TractorBeamOverhaulSys());
    }

    protected override void OnInitialize()
    {
        string consumerVersion = typeof(TractorBeamOverhaulSys).Assembly
            .GetName().Version.ToString(3);
        if (!AutoUtilitiesApi.TryRegisterConsumer(
                ConsumerId,
                "Tractor Beam Overhaul",
                consumerVersion,
                RequiredUtilitiesVersion))
        {
            D.Err("[Tractor Beam Overhaul] AUMF {0} registration failed. "
                + "Coordination will remain inactive for this session.",
                RequiredUtilitiesVersion);
            return;
        }

        try
        {
            onWithTrajectoryMethod = AccessTools.Method(
                typeof(TractorBeamComp),
                "OnWithTrajectory",
                new[] { typeof(IWithTrajectory) });
            MethodInfo prefix = AccessTools.Method(
                typeof(TractorBeamSelectionPatch),
                nameof(TractorBeamSelectionPatch.Prefix));
            if (onWithTrajectoryMethod == null || prefix == null)
            {
                throw new MissingMethodException(
                    "TractorBeamComp.OnWithTrajectory(IWithTrajectory) "
                    + "or its coordinator prefix could not be resolved.");
            }

            Instance = this;
            harmony = new Harmony(HarmonyId);
            harmony.Patch(onWithTrajectoryMethod,
                prefix: new HarmonyMethod(prefix));
            D.Warn("[Tractor Beam Overhaul] {0} initialized. "
                + "Stable even-load coordination is active.",
                consumerVersion);
        }
        catch (Exception exception)
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            onWithTrajectoryMethod = null;
            Instance = null;
            D.LogEx(exception, "[Tractor Beam Overhaul] Initialization failed. "
                + "Coordination will remain inactive for this session.");
            return;
        }

        try
        {
            InitializeDiversionPatches();
            D.Warn("[Tractor Beam Overhaul] Meteor/Asteroid nearest-edge "
                + "diversion is active.");
        }
        catch (Exception exception)
        {
            UnpatchDiversionMethods();
            diversionFaulted = true;
            D.LogEx(exception, "[Tractor Beam Overhaul] Meteor/Asteroid "
                + "edge-diversion initialization failed open. Stable "
                + "even-load coordination and Core landing-target "
                + "behavior will remain active for this session.");
        }
    }

    private void InitializeDiversionPatches()
    {
        meteorInfluenceMethod = AccessTools.Method(typeof(MeteorComp),
            nameof(MeteorComp.InfluenceTrajectory),
            new[] { typeof(Vector2), typeof(float) });
        asteroidInfluenceMethod = AccessTools.Method(typeof(AsteroidComp),
            nameof(AsteroidComp.InfluenceTrajectory),
            new[] { typeof(Vector2), typeof(float) });
        meteorOnRemoveMethod = AccessTools.Method(typeof(MeteorComp),
            nameof(MeteorComp.OnRemove));
        asteroidOnRemoveMethod = AccessTools.Method(typeof(AsteroidComp),
            nameof(AsteroidComp.OnRemove));
        tractorBeamTickProgressMethod = AccessTools.Method(
            typeof(TractorBeamComp), nameof(TractorBeamComp.TickProgress),
            new[] { typeof(float) });

        MethodInfo meteorPrefix = AccessTools.Method(
            typeof(TrajectoryDiversionPatch),
            nameof(TrajectoryDiversionPatch.MeteorPrefix));
        MethodInfo asteroidPrefix = AccessTools.Method(
            typeof(TrajectoryDiversionPatch),
            nameof(TrajectoryDiversionPatch.AsteroidPrefix));
        MethodInfo meteorRemovedPostfix = AccessTools.Method(
            typeof(TrajectoryDiversionPatch),
            nameof(TrajectoryDiversionPatch.MeteorRemovedPostfix));
        MethodInfo asteroidRemovedPostfix = AccessTools.Method(
            typeof(TrajectoryDiversionPatch),
            nameof(TrajectoryDiversionPatch.AsteroidRemovedPostfix));
        MethodInfo beamVisualPostfix = AccessTools.Method(
            typeof(TrajectoryDiversionPatch),
            nameof(TrajectoryDiversionPatch.BeamVisualPostfix));

        if (meteorInfluenceMethod == null || asteroidInfluenceMethod == null
            || meteorOnRemoveMethod == null || asteroidOnRemoveMethod == null
            || tractorBeamTickProgressMethod == null || meteorPrefix == null
            || asteroidPrefix == null || meteorRemovedPostfix == null
            || asteroidRemovedPostfix == null || beamVisualPostfix == null)
        {
            throw new MissingMethodException(
                "A required Meteor/Asteroid edge-diversion hook could not "
                + "be resolved.");
        }

        harmony.Patch(meteorInfluenceMethod,
            prefix: new HarmonyMethod(meteorPrefix));
        harmony.Patch(asteroidInfluenceMethod,
            prefix: new HarmonyMethod(asteroidPrefix));
        harmony.Patch(meteorOnRemoveMethod,
            postfix: new HarmonyMethod(meteorRemovedPostfix));
        harmony.Patch(asteroidOnRemoveMethod,
            postfix: new HarmonyMethod(asteroidRemovedPostfix));
        harmony.Patch(tractorBeamTickProgressMethod,
            postfix: new HarmonyMethod(beamVisualPostfix));
        diversionActive = true;
    }

    private void UnpatchDiversionMethods()
    {
        UnpatchMethod(meteorInfluenceMethod);
        UnpatchMethod(asteroidInfluenceMethod);
        UnpatchMethod(meteorOnRemoveMethod);
        UnpatchMethod(asteroidOnRemoveMethod);
        UnpatchMethod(tractorBeamTickProgressMethod);
        meteorInfluenceMethod = null;
        asteroidInfluenceMethod = null;
        meteorOnRemoveMethod = null;
        asteroidOnRemoveMethod = null;
        tractorBeamTickProgressMethod = null;
        diversionActive = false;
        edgeDiversionRoutes.Clear();
    }

    private void UnpatchMethod(MethodInfo method)
    {
        try
        {
            if (method != null)
            {
                harmony?.Unpatch(method, HarmonyPatchType.All, HarmonyId);
            }
        }
        catch
        {
            // Cleanup is best-effort after partial hook initialization.
        }
    }

    internal bool TryGetEdgeDiversionTarget(IWithTrajectory projectile,
        out Vector2 target)
    {
        target = default;
        if (!diversionActive || diversionFaulted
            || !IsEdgeDivertible(projectile))
        {
            return false;
        }

        try
        {
            int entityId = projectile.Entity.Id;
            if (edgeDiversionRoutes.TryGetValue(entityId,
                    out EdgeDiversionRoute route)
                && ReferenceEquals(route.Projectile, projectile))
            {
                target = route.Target;
                return true;
            }

            Vector2 position = projectile.Entity.Position;
            EdgeExitPoint exit = EdgeDiversionPolicy.ChooseNearestExit(
                position.x, position.y, S.GridWidth, S.GridHeight);
            target = new Vector2(exit.X, exit.Y);
            edgeDiversionRoutes[entityId] = new EdgeDiversionRoute(
                projectile, target);
            return true;
        }
        catch (Exception exception)
        {
            FailEdgeDiversion(exception);
            return false;
        }
    }

    internal bool TryGetEdgeDiversionVisual(IWithTrajectory projectile,
        out Vector3 visualTarget)
    {
        visualTarget = default;
        if (!TryGetEdgeDiversionTarget(projectile, out Vector2 target))
        {
            return false;
        }

        float x = Mathf.Clamp(target.x, 0f, S.GridWidth);
        float y = Mathf.Clamp(target.y, 0f, S.GridHeight);
        visualTarget = new Vector3(x, y, -5f);
        return true;
    }

    internal void ApplyEdgeDiversionVisual(IWithTrajectory projectile,
        LineRenderer beamLine)
    {
        try
        {
            if (beamLine != null && beamLine.positionCount >= 3
                && TryGetEdgeDiversionVisual(projectile,
                    out Vector3 visualTarget))
            {
                beamLine.SetPosition(2, visualTarget);
            }
        }
        catch (Exception exception)
        {
            FailEdgeDiversion(exception);
        }
    }

    internal void ForgetEdgeDiversion(IWithTrajectory projectile)
    {
        try
        {
            int entityId = projectile?.Entity?.Id ?? 0;
            if (entityId != 0
                && edgeDiversionRoutes.TryGetValue(entityId,
                    out EdgeDiversionRoute route)
                && ReferenceEquals(route.Projectile, projectile))
            {
                edgeDiversionRoutes.Remove(entityId);
            }
        }
        catch
        {
            // Cleanup is best-effort; stale routes are replaced on ID reuse.
        }
    }

    private void FailEdgeDiversion(Exception exception)
    {
        if (diversionFaulted)
        {
            return;
        }

        diversionFaulted = true;
        edgeDiversionRoutes.Clear();
        D.LogEx(exception, "[Tractor Beam Overhaul] Meteor/Asteroid edge "
            + "diversion failed open. Core landing-target behavior will "
            + "remain active for this session.");
    }

    private static bool IsEdgeDivertible(IWithTrajectory projectile)
    {
        return projectile?.Entity?.IsActive == true
            && (projectile is MeteorComp || projectile is AsteroidComp);
    }

    internal void CoordinateCandidate(TractorBeamComp beam,
        ref IWithTrajectory originalCandidate)
    {
        if (!CoordinationEnabled || faulted || coordinating
            || beam?.S == null || !IsEligible(originalCandidate))
        {
            return;
        }

        int threatEntityId = originalCandidate.Entity.Id;
        bool shouldRebalance = threatEntityId
            != lastRebalancedThreatEntityId;
        if (!shouldRebalance && beam.Projectile != null)
        {
            return;
        }

        coordinating = true;
        try
        {
            trajectoryCandidates.Clear();
            beams.Clear();
            targetLoads.Clear();
            movableAssignments.Clear();
            targetIndexByEntityId.Clear();

            beam.S.Components.FindByInterface(trajectoryCandidates);
            beam.S.Components.FindAll(beams);
            AddCandidateIfMissing(originalCandidate);
            trajectoryCandidates.Sort(CompareCandidates);

            foreach (IWithTrajectory candidate in trajectoryCandidates)
            {
                if (!IsEligible(candidate))
                {
                    continue;
                }

                int entityId = candidate.Entity.Id;
                if (targetIndexByEntityId.ContainsKey(entityId))
                {
                    continue;
                }

                targetIndexByEntityId.Add(entityId, targetLoads.Count);
                targetLoads.Add(new TargetLoad(entityId, 0, 0));
            }

            if (targetLoads.Count == 0)
            {
                return;
            }

            foreach (TractorBeamComp currentBeam in beams)
            {
                IWithTrajectory assigned = currentBeam?.Projectile;
                if (assigned?.Entity == null
                    || !targetIndexByEntityId.TryGetValue(
                        assigned.Entity.Id, out int targetIndex))
                {
                    continue;
                }

                TargetLoad load = targetLoads[targetIndex];
                int movableCount = load.MovableAssignmentCount;
                if (CanReassignAutomaticBeam(currentBeam))
                {
                    movableCount++;
                    movableAssignments.Add(new MovableBeamAssignment(
                        currentBeam, assigned.Entity.Id));
                }

                targetLoads[targetIndex] = load.WithCounts(
                    load.AssignmentCount + 1, movableCount);
            }

            if (shouldRebalance && targetLoads.Count > 1)
            {
                int moved = RebalanceExistingAssignments();
                if (moved > 0)
                {
                    D.Warn("[Tractor Beam Overhaul] Rebalanced {0} "
                        + "automatic beam(s) across {1} eligible incoming "
                        + "object(s).", moved, targetLoads.Count);
                }
            }

            if (beam.Projectile == null
                && CanReassignAutomaticBeam(beam))
            {
                UpdateDistancesFrom(beam);
                int chosenIndex = EvenLoadPolicy
                    .ChooseLeastLoadedTargetIndex(targetLoads);
                if (chosenIndex >= 0)
                {
                    IWithTrajectory chosen = FindCandidate(
                        targetLoads[chosenIndex].EntityId);
                    if (chosen != null)
                    {
                        originalCandidate = chosen;
                    }
                }
            }

            lastRebalancedThreatEntityId = threatEntityId;
        }
        catch (Exception exception)
        {
            faulted = true;
            D.LogEx(exception, "[Tractor Beam Overhaul] Coordination failed "
                + "open. Vanilla targeting will remain active for this "
                + "session.");
        }
        finally
        {
            trajectoryCandidates.Clear();
            beams.Clear();
            targetLoads.Clear();
            movableAssignments.Clear();
            targetIndexByEntityId.Clear();
            coordinating = false;
        }
    }

    private int RebalanceExistingAssignments()
    {
        int moved = 0;

        while (EvenLoadPolicy.TryChooseMove(targetLoads,
            out int donorIndex, out int receiverIndex))
        {
            int donorEntityId = targetLoads[donorIndex].EntityId;
            IWithTrajectory receiver = FindCandidate(
                targetLoads[receiverIndex].EntityId);
            if (receiver == null)
            {
                break;
            }

            int movableIndex = ChooseDonorBeamIndex(
                donorEntityId, receiver);
            if (movableIndex < 0)
            {
                TargetLoad donorWithoutMovable = targetLoads[donorIndex];
                targetLoads[donorIndex] = donorWithoutMovable.WithCounts(
                    donorWithoutMovable.AssignmentCount, 0);
                continue;
            }

            MovableBeamAssignment assignment =
                movableAssignments[movableIndex];
            if (!TryRetargetThroughCore(assignment.Beam, receiver))
            {
                break;
            }

            TargetLoad donor = targetLoads[donorIndex];
            TargetLoad recipient = targetLoads[receiverIndex];
            targetLoads[donorIndex] = donor.WithCounts(
                donor.AssignmentCount - 1,
                donor.MovableAssignmentCount - 1);
            targetLoads[receiverIndex] = recipient.WithCounts(
                recipient.AssignmentCount + 1,
                recipient.MovableAssignmentCount + 1);
            movableAssignments[movableIndex] = assignment.WithTarget(
                recipient.EntityId);
            moved++;
        }

        return moved;
    }

    private int ChooseDonorBeamIndex(int donorEntityId,
        IWithTrajectory receiver)
    {
        int bestIndex = -1;
        float bestDistanceSquared = float.MaxValue;
        int bestBeamEntityId = int.MaxValue;

        for (int i = 0; i < movableAssignments.Count; i++)
        {
            MovableBeamAssignment assignment = movableAssignments[i];
            TractorBeamComp candidate = assignment.Beam;
            if (assignment.TargetEntityId != donorEntityId
                || !CanReassignAutomaticBeam(candidate))
            {
                continue;
            }

            float distanceSquared = Vectors.SqrDistance(
                candidate.Tile.Position, receiver.Entity.Position);
            int beamEntityId = candidate.Entity.Id;
            if (bestIndex < 0 || distanceSquared < bestDistanceSquared
                || distanceSquared == bestDistanceSquared
                    && beamEntityId < bestBeamEntityId)
            {
                bestIndex = i;
                bestDistanceSquared = distanceSquared;
                bestBeamEntityId = beamEntityId;
            }
        }

        return bestIndex;
    }

    private bool TryRetargetThroughCore(TractorBeamComp beam,
        IWithTrajectory replacement)
    {
        IWithTrajectory previous = beam.Projectile;
        if (previous?.Entity?.Id == replacement.Entity.Id)
        {
            return true;
        }

        beam.ReleaseProjectile();
        try
        {
            InvokeCoreAssignment(beam, replacement);
            if (beam.Projectile?.Entity?.Id == replacement.Entity.Id)
            {
                return true;
            }
        }
        catch (Exception exception)
        {
            TryRestoreAssignment(beam, previous);
            throw new InvalidOperationException(
                "Core rejected an automatic Tractor Beam reassignment.",
                exception);
        }

        if (TryRestoreAssignment(beam, previous))
        {
            return false;
        }

        throw new InvalidOperationException(
            "Core did not accept an automatic Tractor Beam reassignment "
            + "and the previous assignment could not be restored.");
    }

    private bool TryRestoreAssignment(TractorBeamComp beam,
        IWithTrajectory previous)
    {
        try
        {
            if (beam.Projectile != null)
            {
                beam.ReleaseProjectile();
            }

            if (!IsEligible(previous))
            {
                return previous == null;
            }

            InvokeCoreAssignment(beam, previous);
            return beam.Projectile?.Entity?.Id == previous.Entity.Id;
        }
        catch
        {
            return false;
        }
    }

    private void InvokeCoreAssignment(TractorBeamComp beam,
        IWithTrajectory candidate)
    {
        assignmentArguments[0] = candidate;
        try
        {
            onWithTrajectoryMethod.Invoke(beam, assignmentArguments);
        }
        finally
        {
            assignmentArguments[0] = null;
        }
    }

    private void UpdateDistancesFrom(TractorBeamComp beam)
    {
        for (int i = 0; i < targetLoads.Count; i++)
        {
            IWithTrajectory candidate = FindCandidate(
                targetLoads[i].EntityId);
            if (candidate == null)
            {
                continue;
            }

            float distanceSquared = Vectors.SqrDistance(
                beam.Tile.Position, candidate.Entity.Position);
            targetLoads[i] = targetLoads[i].WithDistance(
                distanceSquared);
        }
    }

    private void AddCandidateIfMissing(IWithTrajectory candidate)
    {
        int entityId = candidate.Entity.Id;
        foreach (IWithTrajectory existing in trajectoryCandidates)
        {
            if (existing?.Entity?.Id == entityId)
            {
                return;
            }
        }

        trajectoryCandidates.Add(candidate);
    }

    private static int CompareCandidates(IWithTrajectory left,
        IWithTrajectory right)
    {
        int leftId = left?.Entity?.Id ?? int.MaxValue;
        int rightId = right?.Entity?.Id ?? int.MaxValue;
        return leftId.CompareTo(rightId);
    }

    private IWithTrajectory FindCandidate(int entityId)
    {
        foreach (IWithTrajectory candidate in trajectoryCandidates)
        {
            if (candidate?.Entity?.Id == entityId && IsEligible(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool CanReassignAutomaticBeam(TractorBeamComp beam)
    {
        if (beam?.S == null || beam.Entity?.IsActive != true
            || beam.Tile == null || !beam.Tile.IsConstructed
            || !beam.Tile.Outdoors.IsOutdoors
            || !beam.IsAutoTarget
            || beam.Target?.IsValidNow != true)
        {
            return false;
        }

        ElectricNodeComp node = beam.ENode;
        return node != null && node.IsPowerable && !node.HasDeficit
            && (beam.Projectile == null || IsEligible(beam.Projectile));
    }

    private static bool IsEligible(IWithTrajectory candidate)
    {
        return candidate?.Entity?.IsActive == true
            && candidate.CanChangeTrajectory
            && !candidate.WasAligned;
    }

    public override void Unload()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        onWithTrajectoryMethod = null;
        trajectoryCandidates.Clear();
        beams.Clear();
        targetLoads.Clear();
        movableAssignments.Clear();
        targetIndexByEntityId.Clear();
        edgeDiversionRoutes.Clear();
        assignmentArguments[0] = null;
        lastRebalancedThreatEntityId = 0;
        coordinating = false;
        faulted = false;
        diversionActive = false;
        diversionFaulted = false;
        meteorInfluenceMethod = null;
        asteroidInfluenceMethod = null;
        meteorOnRemoveMethod = null;
        asteroidOnRemoveMethod = null;
        tractorBeamTickProgressMethod = null;
    }
}

internal readonly struct EdgeDiversionRoute
{
    public EdgeDiversionRoute(IWithTrajectory projectile, Vector2 target)
    {
        Projectile = projectile;
        Target = target;
    }

    public IWithTrajectory Projectile { get; }
    public Vector2 Target { get; }
}

internal readonly struct MovableBeamAssignment
{
    public MovableBeamAssignment(TractorBeamComp beam, int targetEntityId)
    {
        Beam = beam;
        TargetEntityId = targetEntityId;
    }

    public TractorBeamComp Beam { get; }
    public int TargetEntityId { get; }

    public MovableBeamAssignment WithTarget(int targetEntityId)
    {
        return new MovableBeamAssignment(Beam, targetEntityId);
    }
}

internal static class TractorBeamSelectionPatch
{
    internal static void Prefix(TractorBeamComp __instance,
        ref IWithTrajectory threat)
    {
        TractorBeamOverhaulSys.Instance?.CoordinateCandidate(
            __instance, ref threat);
    }
}

internal static class TrajectoryDiversionPatch
{
    internal static void MeteorPrefix(MeteorComp __instance,
        ref Vector2 target)
    {
        TryRedirect(__instance, ref target);
    }

    internal static void AsteroidPrefix(AsteroidComp __instance,
        ref Vector2 target)
    {
        TryRedirect(__instance, ref target);
    }

    internal static void MeteorRemovedPostfix(MeteorComp __instance)
    {
        TractorBeamOverhaulSys.Instance?.ForgetEdgeDiversion(__instance);
    }

    internal static void AsteroidRemovedPostfix(AsteroidComp __instance)
    {
        TractorBeamOverhaulSys.Instance?.ForgetEdgeDiversion(__instance);
    }

    internal static void BeamVisualPostfix(TractorBeamComp __instance,
        LineRenderer ___beamLine)
    {
        TractorBeamOverhaulSys.Instance?.ApplyEdgeDiversionVisual(
            __instance.Projectile, ___beamLine);
    }

    private static void TryRedirect(IWithTrajectory projectile,
        ref Vector2 target)
    {
        TractorBeamOverhaulSys system = TractorBeamOverhaulSys.Instance;
        if (system != null
            && system.TryGetEdgeDiversionTarget(projectile,
                out Vector2 redirectedTarget))
        {
            target = redirectedTarget;
        }
    }
}
