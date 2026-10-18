using Game;
using Game.Components;
using Game.Data;
using Game.Systems.Ships;
using Game.Systems.Turrets;
using UnityEngine;

namespace MissileOverhaul {
    internal sealed class StorageCapsuleTarget : IShipCombatTurretTarget {
        public readonly StorageCapsuleComp Capsule;
        public ShipWeaponTargetTypeDef TargetType { get; }

        public StorageCapsuleTarget(StorageCapsuleComp capsule,
                ShipWeaponTargetTypeDef targetType) {
            Capsule = capsule;
            TargetType = targetType;
        }

        public bool IsThreatActive => Capsule?.Entity?.IsActive == true
            && Capsule.IsMidFlight && Capsule.CanChangeTrajectory;
        public Entity Entity => Capsule.Entity;
        public Vector2 Position => Capsule.Entity.Position;
        public float TurretEvadePercent => 0.1f;
        public float PointDefenseEvadePercent => 0.05f;
        public bool WasAligned => Capsule.WasAligned;

        public bool TryGetVelocity(out Vector2 velocity) {
            return Capsule.TryGetHitPoint(out _, out velocity);
        }

        public Vector2 PredictPosition(float afterSeconds) {
            return TryGetVelocity(out Vector2 velocity)
                ? Position + velocity * afterSeconds : Position;
        }
    }
}
