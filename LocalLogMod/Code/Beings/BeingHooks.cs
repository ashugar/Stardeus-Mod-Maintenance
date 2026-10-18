using Game.Data;
using Game.Systems.AI;
using Game.Systems.AI.Combat;
using Game.Systems.Creatures;
using Game.Systems.Combat;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.Beings {
    [HarmonyPatch]
    internal static class BeingHooks {
        [HarmonyPatch(typeof(Being), nameof(Being.SetData))]
        [HarmonyPostfix]
        private static void DataSet(Being __instance) {
            SessionLog.Guard("beings.data", () => SessionLog.Write("Beings",
                "being_registered " + SessionLog.Entity(__instance)
                + " mind=" + __instance.Data?.Mind?.Name?.DisplayName
                + " species=" + __instance.Species?.Id));
        }

        [HarmonyPatch(typeof(AIAgentComp), nameof(AIAgentComp.SetAction))]
        [HarmonyPostfix]
        private static void ActionSet(AIAgentComp __instance) {
            SessionLog.Guard("beings.action", () => SessionLog.Write("Beings",
                "action being=" + SessionLog.Entity(__instance.Being)
                + " action=" + __instance.CurrentAction?.Id
                + " " + SessionLog.Goal(__instance.Goal)));
        }

        [HarmonyPatch(typeof(BeingsSys), "OnBeingDeath")]
        [HarmonyPostfix]
        private static void Died(BeingData dead) {
            SessionLog.Guard("beings.death", () => SessionLog.Write("Beings",
                "died mind=" + dead?.Mind?.Name?.DisplayName + " data=" + dead?.Id));
        }

        [HarmonyPatch(typeof(CombatSys), "OnStartCombatBetween")]
        [HarmonyPostfix]
        private static void CombatStarted(CombatUnit attacker, CombatUnit target, bool __result) {
            SessionLog.Guard("beings.combat", () => SessionLog.Write("Beings",
                "combat_started attacker=" + SessionLog.Entity(attacker?.Agent?.Being)
                + " target=" + SessionLog.Entity(target?.Agent?.Being) + " success=" + __result));
        }

        [HarmonyPatch(typeof(CombatSys), "OnStartFriendlyFightBetween")]
        [HarmonyPostfix]
        private static void FriendlyFight(CombatUnit unit1, CombatUnit unit2, bool __result) {
            SessionLog.Guard("beings.fight", () => SessionLog.Write("Beings",
                "friendly_fight first=" + SessionLog.Entity(unit1?.Agent?.Being)
                + " second=" + SessionLog.Entity(unit2?.Agent?.Being) + " success=" + __result));
        }

        [HarmonyPatch(typeof(AttackAction), nameof(AttackAction.DealDamageTo))]
        [HarmonyPostfix]
        private static void Damage(AttackMethod method, IAttackable attacker, CombatUnit target, bool __result) {
            SessionLog.Guard("beings.damage", () => SessionLog.Write("Beings",
                "attack_damage attacker=" + attacker?.EntityName
                + " target=" + SessionLog.Entity(target?.Agent?.Being)
                + " method=" + method?.Id + " type=" + method?.Type
                + " applied=" + __result));
        }
    }
}
