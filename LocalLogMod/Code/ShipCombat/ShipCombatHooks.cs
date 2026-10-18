using Game.Data;
using Game.Systems.Ships;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.ShipCombat {
    [HarmonyPatch]
    internal static class ShipCombatHooks {
        [HarmonyPatch(typeof(ShipCombatSys), "StartCombatWith")]
        [HarmonyPostfix]
        private static void Started(NPCShip ship, bool __result) {
            SessionLog.Guard("ship.start", () => SessionLog.Write("ShipCombat",
                "player_combat_started enemy=" + SessionLog.Ship(ship) + " success=" + __result));
        }

        [HarmonyPatch(typeof(ShipCombatSys), nameof(ShipCombatSys.EndCombatWith))]
        [HarmonyPostfix]
        private static void Ended(NPCShip ship, ShipStateFlags newState, bool __result) {
            SessionLog.Guard("ship.end", () => SessionLog.Write("ShipCombat",
                "player_combat_ended ship=" + SessionLog.Ship(ship)
                + " nextState=" + newState + " success=" + __result));
        }

        [HarmonyPatch(typeof(ShipWeaponShot), nameof(ShipWeaponShot.Execute))]
        [HarmonyPostfix]
        private static void Shot(ShipWeaponShot __instance) {
            SessionLog.Guard("ship.shot", () => SessionLog.Write("ShipCombat",
                "shot source=" + SessionLog.Ship(__instance.SourceShip)
                + " weapon=" + __instance.Weapon?.Def?.Id
                + " targetShip=" + SessionLog.Ship(__instance.TargetShip)
                + " targetPos=" + __instance.TargetPos
                + " hit=" + __instance.IsHit + " damageType=" + __instance.DamageType));
        }

        [HarmonyPatch(typeof(ShipWeaponShot), nameof(ShipWeaponShot.HitPlayerShip))]
        [HarmonyPostfix]
        private static void PlayerHit(ShipWeaponShot __instance, Entity entity) {
            SessionLog.Guard("ship.impact", () => SessionLog.Write("ShipCombat",
                "player_ship_impact source=" + SessionLog.Ship(__instance.SourceShip)
                + " weapon=" + __instance.Weapon?.Def?.Id
                + " target=" + SessionLog.Entity(entity)));
        }

        [HarmonyPatch(typeof(ShipWeapon), nameof(ShipWeapon.SetPhase))]
        [HarmonyPostfix]
        private static void Phase(ShipWeapon __instance, ShipWeaponPhase phase, bool __result) {
            SessionLog.Guard("ship.phase", () => {
                if (__result) { SessionLog.Write("ShipCombat", "weapon_phase ship=" + SessionLog.Ship(__instance.Device?.Ship)
                    + " weapon=" + __instance.Def?.Id + " phase=" + phase); }
            });
        }
    }
}
