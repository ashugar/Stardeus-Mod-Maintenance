using System;
using System.Globalization;
using Game.Components;
using Game.Systems.Electricity;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.Power {
    [HarmonyPatch]
    internal static class PowerHooks {
        [HarmonyPatch(typeof(ElectricNodeComp), nameof(ElectricNodeComp.Update))]
        [HarmonyPrefix]
        private static void BeforeUpdate(ElectricNodeComp __instance, out ElectricNode __state) {
            __state = __instance.Node;
        }

        [HarmonyPatch(typeof(ElectricNodeComp), nameof(ElectricNodeComp.Update))]
        [HarmonyPostfix]
        private static void AfterUpdate(ElectricNodeComp __instance, ElectricNode __state) {
            SessionLog.Guard("power.update", () => {
                var n = __instance.Node;
                if (__state.GridId == n.GridId && __state.IsOn == n.IsOn
                        && __state.IsReachable == n.IsReachable
                        && SameLoggedValue(__state.Produced, n.Produced)
                        && SameLoggedValue(__state.Consumed, n.Consumed)
                        && SameLoggedValue(__state.Deficit, n.Deficit)
                        && SameLoggedValue(__state.BatteryCharge, n.BatteryCharge)
                        && __state.NumConnections == n.NumConnections
                        && SameLoggedValue(__state.ConsumerWantedInput, n.ConsumerWantedInput)) { return; }
                SessionLog.Building("Power", __instance.Tile,
                    "node grid=" + __state.GridId + "->" + n.GridId
                    + " on=" + __state.IsOn + "->" + n.IsOn
                    + " reachable=" + __state.IsReachable + "->" + n.IsReachable
                    + " connections=" + __state.NumConnections + "->" + n.NumConnections
                    + " produced=" + F(__state.Produced) + "->" + F(n.Produced)
                    + " consumed=" + F(__state.Consumed) + "->" + F(n.Consumed)
                    + " deficit=" + F(__state.Deficit) + "->" + F(n.Deficit)
                    + " wanted=" + F(__state.ConsumerWantedInput) + "->" + F(n.ConsumerWantedInput)
                    + " battery=" + F(__state.BatteryCharge) + "->" + F(n.BatteryCharge));
            });
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool SameLoggedValue(float previous, float current) {
            if (float.IsNaN(previous) || float.IsNaN(current)
                    || float.IsInfinity(previous) || float.IsInfinity(current)) {
                return previous.Equals(current);
            }
            return Math.Round(previous, 3, MidpointRounding.AwayFromZero)
                == Math.Round(current, 3, MidpointRounding.AwayFromZero);
        }

        [HarmonyPatch(typeof(ElectricNodeComp), nameof(ElectricNodeComp.TryConnectTo))]
        [HarmonyPostfix]
        private static void Connected(ElectricNodeComp __instance, ElectricNodeComp other, bool __result, string failReason) {
            SessionLog.Guard("power.connect", () => SessionLog.Building("Power", __instance.Tile,
                "connect other=" + SessionLog.Entity(other?.Tile) + " success=" + __result + " reason=" + failReason));
        }

        [HarmonyPatch(typeof(ElectricNodeComp), nameof(ElectricNodeComp.TryDisconnectFrom))]
        [HarmonyPostfix]
        private static void Disconnected(ElectricNodeComp __instance, ElectricNodeComp other, bool __result, string failReason) {
            SessionLog.Guard("power.disconnect", () => SessionLog.Building("Power", __instance.Tile,
                "disconnect other=" + SessionLog.Entity(other?.Tile) + " success=" + __result + " reason=" + failReason));
        }

        [HarmonyPatch(typeof(ElectricNodeComp), nameof(ElectricNodeComp.Switch))]
        [HarmonyPostfix]
        private static void Switched(ElectricNodeComp __instance, bool on) {
            SessionLog.Guard("power.switch", () => SessionLog.Building("Power", __instance.Tile, "switch requested=" + on));
        }

        [HarmonyPatch(typeof(ElectricitySys), nameof(ElectricitySys.AddNode))]
        [HarmonyPostfix]
        private static void Added(ElectricNodeComp comp) {
            SessionLog.Guard("power.add", () => SessionLog.Building("Power", comp?.Tile, "node_added"));
        }

        [HarmonyPatch(typeof(ElectricitySys), nameof(ElectricitySys.RemoveNode))]
        [HarmonyPrefix]
        private static void Removed(ElectricNodeComp comp) {
            SessionLog.Guard("power.remove", () => SessionLog.Building("Power", comp?.Tile, "node_removed"));
        }
    }
}
