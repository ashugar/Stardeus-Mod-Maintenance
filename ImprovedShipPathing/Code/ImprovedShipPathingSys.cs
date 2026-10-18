using System;
using System.Reflection;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Constants;
using Game.Data.Space;
using Game.Systems;
using Game.Systems.Ships;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using UnityEngine;

namespace ImprovedShipPathing {
    public sealed class ImprovedShipPathingSys : GameSystem {
        public const string ModVersion = "0.1.1";
        public const string RequiredUtilitiesVersion = "1.4.0";
        private const string HarmonyId = "jleas.stardeus.improvedshippathing";

        internal static ImprovedShipPathingSys Instance;
        private Harmony harmony;
        private bool faulted;
        private long lastSolveTick = long.MinValue;
        private uint lastTargetId;
        private Vector2 lastTargetCourse;
        private Vector2 cachedDestination;
        private bool hasCachedDestination;
        private bool cachedStopAtIntercept;
        private uint lastReportedTargetId;
        private bool lastReportedStopAtIntercept;
        private TargetTrajectoryKind lastReportedTrajectory;

        public override string Id => "ImprovedShipPathingSys";
        public override bool SkipInSandbox => false;

        [SelfInit]
        private static void Register() {
            GameSystems.Register("ImprovedShipPathingSys", () => new ImprovedShipPathingSys());
        }

        protected override void OnInitialize() {
            if (!AutoUtilitiesApi.TryRegisterConsumer("ImprovedShipPathing",
                    "Improved Ship Pathing", ModVersion, RequiredUtilitiesVersion)) return;
            try {
                ValidateCoreContract();
                Instance = this;
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(ImprovedShipPathingSys).Assembly);
                D.Warn("[Improved Ship Pathing] {0} initialized. Moving-ship interception is active for Travel Here and Attack.", ModVersion);
            } catch (Exception ex) {
                Disable(ex);
            }
        }

        private static void ValidateCoreContract() {
            MethodInfo progress = AccessTools.Method(typeof(ShipSys), "OnTickProgress",
                new[] { typeof(float) });
            if (progress == null
                    || AccessTools.Field(typeof(ShipSys), "destinationLoc") == null
                    || AccessTools.Field(typeof(ShipSys), "currentLoc") == null
                    || AccessTools.Field(typeof(ShipSys), "velocity") == null
                    || AccessTools.Field(typeof(ShipSys), "engineForce") == null
                    || AccessTools.Field(typeof(ShipSys), "isApproachFlight") == null
                    || AccessTools.Field(typeof(ShipSys), "hasDestination") == null
                    || AccessTools.Field(typeof(ShipSys), "destination") == null
                    || AccessTools.Field(typeof(ShipSys), "onReachShip") == null) {
                throw new MissingMemberException("Ship flight interception contract is unavailable.");
            }
            if (AccessTools.Method(typeof(NPCShip), nameof(NPCShip.NearbyPosFrom),
                    new[] { typeof(Vector2), typeof(bool).MakeByRefType(), typeof(float) }) == null) {
                throw new MissingMethodException("NPC ship standoff contract is unavailable.");
            }
        }

        internal void UpdateDestination(ShipSys shipSys, ref Vector2 destinationLoc,
                Vector2 currentLoc, Vector2 velocity, float engineForce,
                bool isApproachFlight, bool hasDestination, SpaceObject destination,
                Action onReachShip) {
            if (faulted || IsDeactivated || !hasDestination || isApproachFlight
                    || destination?.Ship == null || destination.Parent != shipSys.Player.Parent) {
                ResetCache();
                return;
            }

            NPCShip target = destination.Ship;
            if (!target.HasLocalTarget || target.IsDerelict) {
                ResetCache();
                return;
            }

            long tick = G.Ticks;
            bool stopAtIntercept = onReachShip == null;
            if (hasCachedDestination && lastSolveTick == tick
                    && lastTargetId == target.Id && lastTargetCourse == target.LocalTarget
                    && cachedStopAtIntercept == stopAtIntercept) {
                destinationLoc = cachedDestination;
                return;
            }

            float motionScale = Tunables.SpaceTravelBaseSpeed * shipSys.SpeedInCurrentLayer;
            if (motionScale <= 0f || shipSys.ShipMass <= 0 || engineForce <= 0f) return;
            float maxVelocity = Equations.EnginePush(engineForce, shipSys.ShipMass,
                Tunables.ShipSpeedMin, Tunables.ShipSpeedMax);
            float maxSpeed = maxVelocity * motionScale;
            float acceleration = Tunables.SpaceTravelAcceleration * motionScale;
            float deceleration = Tunables.SpaceTravelDeceleration * motionScale;

            Vector2 targetPos = target.SO.Location;
            Vector2 nativeStandoff = target.NearbyPosFrom(currentLoc, out _);
            float standoffDistance = Vector2.Distance(targetPos, nativeStandoff);
            bool isOrbit = target.BehaviorSector is NPCShipBehavior_Sector_Orbit;
            TargetTrajectoryKind trajectory = isOrbit
                ? TargetTrajectoryKind.CircularOrbit
                : TargetTrajectoryKind.FiniteLine;
            double orbitDirection = isOrbit ? (target.Id % 2 == 1 ? -1d : 1d) : 0d;
            var input = new InterceptInput(
                currentLoc.x, currentLoc.y,
                velocity.x * motionScale, velocity.y * motionScale,
                maxSpeed, acceleration, deceleration,
                targetPos.x, targetPos.y, target.LocalTarget.x, target.LocalTarget.y,
                target.LocalVelocity, target.WantedVelocity,
                Tunables.SpaceTravelAcceleration, standoffDistance,
                trajectory, orbitDirection, stopAtIntercept);
            if (!InterceptSolver.TrySolve(input, out InterceptSolution intercept)) return;

            var predictedCenter = new Vector2((float)intercept.TargetX, (float)intercept.TargetY);
            Vector2 fromPlayer = predictedCenter - currentLoc;
            if (fromPlayer.sqrMagnitude <= standoffDistance * standoffDistance) return;
            Vector2 approachDirection = fromPlayer.normalized;
            Vector2 aimPoint;
            if (stopAtIntercept) {
                aimPoint = predictedCenter - approachDirection * standoffDistance;
            } else {
                // Attack and other on-reach actions need only cross the target's
                // native standoff envelope. Aim beyond the meeting point far enough
                // that Core does not brake before the callback can fire.
                float stoppingDistance = maxSpeed * maxSpeed / (2f * deceleration) * 0.9f;
                aimPoint = predictedCenter + approachDirection
                    * (standoffDistance + stoppingDistance);
            }
            if (float.IsNaN(aimPoint.x) || float.IsNaN(aimPoint.y)
                    || float.IsInfinity(aimPoint.x) || float.IsInfinity(aimPoint.y)) return;

            destinationLoc = aimPoint;
            cachedDestination = aimPoint;
            hasCachedDestination = true;
            lastSolveTick = tick;
            lastTargetId = target.Id;
            lastTargetCourse = target.LocalTarget;
            cachedStopAtIntercept = stopAtIntercept;
            if (lastReportedTargetId != target.Id
                    || lastReportedStopAtIntercept != stopAtIntercept
                    || lastReportedTrajectory != trajectory) {
                lastReportedTargetId = target.Id;
                lastReportedStopAtIntercept = stopAtIntercept;
                lastReportedTrajectory = trajectory;
                D.Warn("[Improved Ship Pathing] Intercept solution for {0}: {1}, {2}, meeting in {3:0.0} ticks, lead {4:0.00}, player max {5:0.000}, target speed {6:0.000}.",
                    target.Name, trajectory, stopAtIntercept ? "stopped rendezvous" : "fly-through",
                    intercept.Ticks, Vector2.Distance(targetPos, predictedCenter),
                    maxSpeed, target.LocalVelocity);
            }
        }

        private void ResetCache() {
            hasCachedDestination = false;
            lastSolveTick = long.MinValue;
            lastTargetId = 0;
        }

        internal void Disable(Exception ex) {
            faulted = true;
            ResetCache();
            harmony?.UnpatchAll(HarmonyId);
            D.LogEx(ex, "[Improved Ship Pathing] Disabled for this session; Core ship navigation restored.");
        }

        public override void Unload() {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            ResetCache();
            if (Instance == this) Instance = null;
        }
    }

    [HarmonyPatch(typeof(ShipSys), "OnTickProgress", new Type[] { typeof(float) })]
    internal static class MovingShipInterceptPatch {
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        private static void Postfix(ShipSys __instance,
                ref Vector2 ___destinationLoc,
                Vector2 ___currentLoc,
                Vector2 ___velocity,
                float ___engineForce,
                bool ___isApproachFlight,
                bool ___hasDestination,
                SpaceObject ___destination,
                Action ___onReachShip) {
            var mod = ImprovedShipPathingSys.Instance;
            if (mod == null) return;
            try {
                mod.UpdateDestination(__instance, ref ___destinationLoc,
                    ___currentLoc, ___velocity, ___engineForce,
                    ___isApproachFlight, ___hasDestination, ___destination,
                    ___onReachShip);
            } catch (Exception ex) {
                mod.Disable(ex);
            }
        }
    }
}
