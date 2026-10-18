using System;
using System.Collections.Generic;
using System.Reflection;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.Firex;
using Game.Systems.Notify;
using Game.Systems.Ships;
using Game.Systems.Turrets;
using Game.Utils;
using HarmonyLib;
using KL.Clock;
using KL.Randomness;
using KL.Utils;
using UnityEngine;

namespace MissileOverhaul {
    public sealed class MissileOverhaulSys : GameSystem {
        public const string ModVersion = "0.4.0";
        public const string RequiredUtilitiesVersion = "1.4.0";
        private const string HarmonyId = "jleas.stardeus.missileoverhaul";
        private const string StorageTargetTypeId = "SuspectedRaidStorageCapsule";
        private const string StorageRaidWarningKey = "missileoverhaul.warning.suspected_storage_capsule_raid";
        private Harmony harmony;
        private MethodInfo expectedTicks;
        private FieldInfo normalUpsField;
        private bool faulted;
        private int timingFrame = -1;
        private float scaledTicksPerSecond;
        private float normalTicksPerSecond;
        internal bool NativePass;
        internal static MissileOverhaulSys Instance;
        private readonly List<IShipCombatTurretTarget> targets = new();
        private IShipCombatTurretTarget[] targetBuffer = new IShipCombatTurretTarget[16];
        private readonly List<ShipWeapon> launchers = new();
        private readonly Dictionary<IShipCombatTurretTarget, int> flights = new();
        private readonly List<StorageCapsuleComp> storageCapsules = new();
        private readonly Dictionary<int, StorageCapsuleTarget> storageTargets = new();
        private ShipWeaponTargetTypeDef storageCapsuleTargetType;
        private bool suspectedStorageRaidActive;
        private NotificationHandle storageRaidWarning;

        public override string Id => "MissileOverhaulSys";
        public override bool SkipInSandbox => false;
        [SelfInit]
        private static void Register() {
            GameSystems.Register("MissileOverhaulSys", () => new MissileOverhaulSys());
        }

        protected override void OnInitialize() {
            if (!AutoUtilitiesApi.TryRegisterConsumer("MissileOverhaul", "Missile Overhaul",
                    ModVersion, RequiredUtilitiesVersion)) return;
            try {
                expectedTicks = AccessTools.Method(typeof(Clock), "ExpectedTicksPerSecond");
                normalUpsField = AccessTools.Field(typeof(Clock), "upsNormal");
                if (expectedTicks == null || normalUpsField == null)
                    throw new MissingMethodException("Clock flight timing API is unavailable.");
                if (AccessTools.Method(typeof(TurretsSys), "TargetShipCombatTurrets") == null
                        || AccessTools.Method(typeof(ShipWeapon), "ShootNonShipTarget") == null
                        || AccessTools.PropertyGetter(typeof(ShipDevice), "ShouldBeActive") == null)
                    throw new MissingMethodException("Missile defense API is unavailable.");
                storageCapsuleTargetType = ShipWeaponTargetTypeDef.Get(StorageTargetTypeId);
                if (storageCapsuleTargetType?.Id != StorageTargetTypeId)
                    throw new MissingMemberException("Suspected Storage Capsule target definition is unavailable.");
                Instance = this;
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(MissileOverhaulSys).Assembly);
                D.Warn("[Missile Overhaul] {0} initialized. Interceptor and Rocket Pod roles and readiness are active for all ships.", ModVersion);
            } catch (Exception ex) { Disable(ex); }
        }

        internal bool Supports(ShipWeapon weapon) {
            if (faulted || IsDeactivated || weapon?.Device?.Ship == null
                    || !DefensePolicy.Supports(weapon.WeaponDef?.Id)) return false;
            if (!weapon.Device.Ship.IsPlayer) return !weapon.Device.Ship.IsDerelict;
            return weapon.Comp?.Tile?.IsActive == true && weapon.Comp.Tile.IsConstructed
                && weapon.Comp.Tile.Reinstallable?.IsMidRelocation != true
                && weapon.Comp.Turret?.IsRegistered == true;
        }

        internal bool Manages(ShipWeapon weapon) {
            if (!Supports(weapon)) return false;
            bool isPlayer = weapon.Device.Ship.IsPlayer;
            return DefensePolicy.Manages(weapon.WeaponDef.Id, isPlayer,
                !isPlayer || weapon.EffectiveSettings.FireMode == ShipWeaponFireMode.Automatic);
        }

        internal ShipWeapon FindWeapon(ShipDevice device) {
            if (device?.Ship == null) return null;
            if (device.Tile != null) {
                if (device.Tile.TryGetComponent<ShipWeaponComp>(out var comp)
                        && ReferenceEquals(comp.Weapon?.Device, device)) return comp.Weapon;
                return null;
            }
            if (device.Ship.IsPlayer) return null;
            var weapons = device.Ship.CombatState?.Weapons;
            if (weapons == null) return null;
            for (int i = 0; i < weapons.Count; i++)
                if (ReferenceEquals(weapons[i].Device, device)) return weapons[i];
            return null;
        }

        internal static DefenseTargetKind Kind(IShipCombatTurretTarget target) {
            if (target is MeteorComp) return DefenseTargetKind.Meteoroid;
            if (target is HullpiercerComp) return DefenseTargetKind.Hullpiercer;
            if (target is StorageCapsuleTarget) return DefenseTargetKind.SuspectedStorageCapsule;
            return DefenseTargetKind.Other;
        }

        internal bool RoleAllows(ShipWeapon weapon, IShipCombatTurretTarget target) {
            return DefensePolicy.RoleAllows(weapon.WeaponDef.Id, Kind(target));
        }

        private bool Available(ShipWeapon weapon) {
            if (!Manages(weapon) || !weapon.Device.IsOperational
                    || weapon.Device.Ship.AreWeaponsHacked
                    || weapon.Device.Ship.CombatState.Flux.IsOverloaded) return false;
            if (!weapon.Device.Ship.IsPlayer)
                return weapon.WeaponDef.AmmoCapacity <= 0 || weapon.Ammo >= 1;
            return weapon.Comp.Tile.ENode.IsPowerable && !weapon.HasNoStation
                && weapon.Comp.Turret.IsScanning && weapon.Comp.Ammo.CurrentStock >= 1;
        }

        internal bool CanFire(ShipWeapon weapon) {
            weapon.SkippedShotDueToFlux = false;
            if (!Available(weapon) || weapon.Phase != ShipWeaponPhase.Ready) return false;
            var flux = weapon.Device.Ship.CombatState.Flux;
            if (flux.FutureTotalPercent(weapon.WeaponDef.HardFluxPerShot,
                    weapon.WeaponDef.SoftFluxPerShot) >= weapon.EffectiveSettings.ShootFluxThreshold) {
                weapon.SkippedShotDueToFlux = true;
                return false;
            }
            return true;
        }

        private void AddSuspectedStorageCapsules() {
            storageCapsules.Clear();
            S.Components.FindAllMatching(storageCapsules,
                capsule => capsule.IsMidFlight && capsule.CanChangeTrajectory);
            bool wasActive = suspectedStorageRaidActive;
            suspectedStorageRaidActive = DefensePolicy.SuspectedStorageRaidActive(
                wasActive, storageCapsules.Count);
            if (!wasActive && suspectedStorageRaidActive) {
                var obj = (Obj)storageCapsules[0].Entity;
                string warning = StorageRaidWarningKey.T();
                storageRaidWarning = Notification.BuildFor(obj, null, warning, 0,
                        NotificationType.Warning)
                    .Group("missile_overhaul_storage_raid", warning)
                    .Show();
                S.Sys.Log.AddLine(obj, warning);
                S.Sig.ShowFanfare.Send(Texts.LinkToObj(obj, warning));
            } else if (wasActive && !suspectedStorageRaidActive) {
                Notification.Remove(ref storageRaidWarning);
                storageTargets.Clear();
            }
            if (!suspectedStorageRaidActive) return;
            for (int i = 0; i < storageCapsules.Count; i++) {
                var capsule = storageCapsules[i];
                int id = capsule.Entity.Id;
                if (!storageTargets.TryGetValue(id, out var target)) {
                    target = new StorageCapsuleTarget(capsule, storageCapsuleTargetType);
                    storageTargets.Add(id, target);
                }
                targets.Add(target);
            }
        }

        internal void Coordinate(long ticks) {
            try {
                targets.Clear();
                int total = S.Components.CountByInterface<IShipCombatTurretTarget>();
                if (targetBuffer.Length < total) targetBuffer = new IShipCombatTurretTarget[total * 2];
                int found = total > 0 ? S.Components.FindAllByInterface(targetBuffer) : 0;
                for (int i = 0; i < found; i++) {
                    targets.Add(targetBuffer[i]);
                    targetBuffer[i] = null;
                }
                targets.RemoveAll(t => !IsLive(t) || !(t.Entity is Obj));
                AddSuspectedStorageCapsules();
                targets.Sort((a, b) => a.Entity.Id.CompareTo(b.Entity.Id));
                launchers.Clear();
                foreach (var weapon in S.Sys.Get<ShipWeaponSys>().PlayerWeapons) {
                    if (Available(weapon)) launchers.Add(weapon);
                }
                launchers.Sort((a, b) => {
                    int cmp = (b.Phase == ShipWeaponPhase.Ready).CompareTo(a.Phase == ShipWeaponPhase.Ready);
                    return cmp != 0 ? cmp : a.Device.Id.CompareTo(b.Device.Id);
                });
                int count = targets.Count;
                var priorities = new int[launchers.Count, count];
                var distances = new float[launchers.Count, count];
                var existing = new int[launchers.Count];
                var flightLoads = new int[count];
                for (int t = 0; t < count; t++) flights.TryGetValue(targets[t], out flightLoads[t]);
                for (int w = 0; w < launchers.Count; w++) {
                    var weapon = launchers[w];
                    existing[w] = targets.IndexOf(weapon.NonShipTarget);
                    for (int t = 0; t < count; t++) {
                        var target = targets[t];
                        float distance = (target.Position - weapon.Device.Position).sqrMagnitude;
                        distances[w, t] = distance;
                        float range = weapon.Comp.Turret.RangeSqr;
                        if (!RoleAllows(weapon, target) || !weapon.CanEngage(target) || (range > 0 && distance > range)
                                || !Aim(weapon, target, out _, out _)) continue;
                        priorities[w, t] = weapon.EffectiveSettings.GetPriorityFor(target.TargetType);
                    }
                }
                int[] assigned = DefensePolicy.Assign(priorities, distances, existing, flightLoads);
                for (int w = 0; w < launchers.Count; w++) {
                    var weapon = launchers[w];
                    var target = assigned[w] >= 0 ? targets[assigned[w]] : null;
                    weapon.Comp.SetTarget(target);
                    if (target != null && IsLive(target) && CanFire(weapon)) weapon.Comp.TryShoot(ticks);
                }
            } catch (Exception ex) { Disable(ex); }
        }

        internal static bool IsLive(IShipCombatTurretTarget target) {
            return target?.Entity?.IsActive == true && target.IsThreatActive
                && !(target.Entity is Obj obj && obj.Health.IsDestroyed)
                && !(target is MeteorComp meteor && meteor.HadImpactWithTheShip);
        }

        private bool Aim(ShipWeapon weapon, IShipCombatTurretTarget target,
                out Vector2 position, out float speed) {
            Vector2 velocity;
            switch (target) {
                case MeteorComp m: velocity = m.Velocity; break;
                case AsteroidComp a: velocity = a.Velocity; break;
                case HullpiercerComp h: velocity = h.Velocity; break;
                case BreachCapsuleFlyingComp b: velocity = b.Velocity; break;
                case StorageCapsuleTarget s:
                    if (s.TryGetVelocity(out velocity)) break;
                    position = target.Position; speed = 0; return false;
                default: position = target.Position; speed = 0; return false;
            }
            // Hazard velocity is per tick; shot duration and VFX use scaled seconds.
            if (timingFrame != GameTimer.frameCount) {
                timingFrame = GameTimer.frameCount;
                float fxScale = S.Clock.FXTimeScale;
                normalTicksPerSecond = (float)normalUpsField.GetValue(S.Clock);
                scaledTicksPerSecond = fxScale > 0
                    ? (float)expectedTicks.Invoke(S.Clock, null) / fxScale : 0;
            }
            if (scaledTicksPerSecond <= 0 || normalTicksPerSecond <= 0) {
                position = target.Position; speed = 0; return false;
            }
            float scaledTicks = scaledTicksPerSecond;
            velocity *= scaledTicks;
            speed = weapon.WeaponDef.ProjectileSpeed * scaledTicks / normalTicksPerSecond;
            var relative = target.Position - weapon.Device.Position;
            bool canIntercept = DefensePolicy.Intercept(relative.x, relative.y,
                velocity.x, velocity.y, speed, out double duration);
            position = target.Position + velocity * (float)duration;
            return canIntercept;
        }

        internal bool Shoot(ShipWeapon weapon, GameState state, Rng rng) {
            var target = weapon.NonShipTarget;
            if (!Supports(weapon) || weapon.Device.Ship.IsPlayer != true
                    || !(target?.Entity is Obj) || !IsLive(target)
                    || !RoleAllows(weapon, target)) return false;
            if (!Aim(weapon, target, out Vector2 position, out float speed)) return false;
            bool trajectoryAligned = (target is IWithTrajectory trajectory && trajectory.WasAligned)
                || (target is StorageCapsuleTarget storage && storage.WasAligned);
            bool hit = rng.Chance(DefensePolicy.HitChance(weapon.WeaponDef.Accuracy,
                weapon.IsPointDefense ? target.PointDefenseEvadePercent : target.TurretEvadePercent,
                trajectoryAligned));
            var lease = new ShotLease<IShipCombatTurretTarget>(target);
            var shot = new ShipWeaponShot { Weapon = weapon, IsHit = hit,
                TargetPos = hit ? position : weapon.NonShipTargetMissPosition(position, weapon.Device.Position),
                OverrideProjectileSpeed = speed };
            flights.TryGetValue(target, out int active);
            flights[target] = active + 1;
            shot.OnEndAnimate = (s, finished) => {
                if (!lease.Complete()) return;
                if (flights.TryGetValue(lease.Target, out int remaining)) {
                    if (remaining <= 1) flights.Remove(lease.Target);
                    else flights[lease.Target] = remaining - 1;
                }
                if (!hit || !IsLive(lease.Target)) return;
                // A launched shot keeps its captured target after the building retargets.
                var obj = (Obj)lease.Target.Entity;
                float damage = weapon.Damage;
                Explosions.CreateExplosionFX(s, obj.Position, 2, damage, false);
                var storageTarget = lease.Target as StorageCapsuleTarget;
                bool wasInvincible = storageTarget != null && obj.Health.IsInvincible;
                if (storageTarget != null) {
                    // Core owns capsule contents; release them at the current position before lethal removal.
                    if (!storageTarget.Capsule.IsOpen && damage >= obj.Health.Health)
                        storageTarget.Capsule.Open(null);
                    obj.Health.IsInvincible = false;
                }
                try {
                    if (obj.IsActive)
                        obj.Health.TakeDamage(damage, weapon.WeaponDef.DamageType);
                } finally {
                    if (wasInvincible && obj.IsActive && !obj.Health.IsDestroyed)
                        obj.Health.IsInvincible = true;
                }
                if (obj.Health.IsDestroyed && weapon.NonShipTarget == lease.Target) weapon.Comp.SetTarget(null);
            };
            state.Query.ShootShipWeapon.Ask(shot);
            return true;
        }

        internal ShipWeapon SelectPointDefense(ShipCombatState combatState, Vector2 position) {
            ShipWeapon closest = null;
            float closestDistance = float.MaxValue;
            foreach (var weapon in combatState.Weapons) {
                if (weapon.Phase != ShipWeaponPhase.Ready || !weapon.IsPointDefense) continue;
                if (DefensePolicy.Supports(weapon.WeaponDef.Id)
                        && (!DefensePolicy.RoleAllows(weapon.WeaponDef.Id, DefenseTargetKind.IncomingMissile)
                            || !CanFire(weapon))) continue;
                float distance = (weapon.Device.Position - position).sqrMagnitude;
                float range = weapon.Comp?.Turret?.RangeSqr ?? 0;
                if (Supports(weapon) && range > 0 && distance > range) continue;
                if (distance < closestDistance) {
                    closestDistance = distance;
                    closest = weapon;
                }
            }
            return closest;
        }

        internal void Disable(Exception ex) {
            faulted = true;
            NativePass = false;
            harmony?.UnpatchAll(HarmonyId);
            Notification.Remove(ref storageRaidWarning);
            suspectedStorageRaidActive = false;
            storageTargets.Clear();
            D.LogEx(ex, "[Missile Overhaul] Disabled for this session; Core weapon behavior restored.");
        }

        public override void Unload() {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            NativePass = false;
            Notification.Remove(ref storageRaidWarning);
            suspectedStorageRaidActive = false;
            flights.Clear(); targets.Clear(); launchers.Clear();
            storageCapsules.Clear(); storageTargets.Clear();
            storageCapsuleTargetType = null;
            Array.Clear(targetBuffer, 0, targetBuffer.Length);
            if (Instance == this) Instance = null;
        }
    }

    [HarmonyPatch(typeof(ShipDevice), "get_ShouldBeActive")]
    internal static class ArmedReadinessPatch {
        private static void Postfix(ShipDevice __instance, ref bool __result) {
            var mod = MissileOverhaulSys.Instance;
            if (mod == null || __result) return;
            var weapon = mod.FindWeapon(__instance);
            if (!mod.Manages(weapon)) return;
            if (!__instance.Ship.IsPlayer || __instance.Tile?.ENode?.IsPowerable == true) __result = true;
        }
    }

    [HarmonyPatch(typeof(TurretsSys), "TargetShipCombatTurrets")]
    internal static class SharedTargetingPatch {
        private static void Prefix(long ticks) {
            var mod = MissileOverhaulSys.Instance;
            if (mod == null) return;
            mod.Coordinate(ticks);
            mod.NativePass = true;
        }
        private static Exception Finalizer(Exception __exception) {
            if (MissileOverhaulSys.Instance != null) MissileOverhaulSys.Instance.NativePass = false;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ShipCombatAI), nameof(ShipCombatAI.CanShootNow))]
    internal static class NativeDefensePassPatch {
        private static bool Prefix(ShipWeapon w, ref bool __result) {
            var mod = MissileOverhaulSys.Instance;
            if (mod?.NativePass != true || !mod.Manages(w)) return true;
            // Core continues to select and fire all other turret types.
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(ShipWeapon), "ShootNonShipTarget")]
    internal static class LockedMissilePatch {
        private static bool Prefix(ShipWeapon __instance, GameState s, Rng rng) {
            var mod = MissileOverhaulSys.Instance;
            if (mod == null) return true;
            try { return !mod.Shoot(__instance, s, rng); }
            catch (Exception ex) { mod.Disable(ex); return false; }
        }
    }

    [HarmonyPatch(typeof(ShipWeapon), nameof(ShipWeapon.CanEngage))]
    internal static class WeaponRoleEligibilityPatch {
        private static bool Prefix(ShipWeapon __instance, IShipCombatTurretTarget target, ref bool __result) {
            var mod = MissileOverhaulSys.Instance;
            if (mod == null || !mod.Supports(__instance) || target == null) return true;
            if (!mod.RoleAllows(__instance, target)) {
                __result = false;
                return false;
            }
            // Point defense remains responsible for its threats during ship combat.
            if (__instance.IsPointDefense) {
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ShipWeapon), nameof(ShipWeapon.Shoot))]
    internal static class WeaponRoleFirePatch {
        private static bool Prefix(ShipWeapon __instance, ref bool __result) {
            var mod = MissileOverhaulSys.Instance;
            if (mod == null || !mod.Supports(__instance)) return true;
            if (!mod.RoleAllows(__instance, __instance.NonShipTarget)) {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ShipCombatState), nameof(ShipCombatState.GetClosestAvailablePointDefense))]
    internal static class IncomingMissileDefensePatch {
        private static bool Prefix(ShipCombatState __instance, Vector2 position, ref ShipWeapon __result) {
            var mod = MissileOverhaulSys.Instance;
            if (mod == null || mod.IsDeactivated || __instance.Ship == null) return true;
            try {
                // Core locks the interception outcome and immediately consumes this defender's shot.
                __result = mod.SelectPointDefense(__instance, position);
                return false;
            } catch (Exception ex) { mod.Disable(ex); return true; }
        }
    }
}
