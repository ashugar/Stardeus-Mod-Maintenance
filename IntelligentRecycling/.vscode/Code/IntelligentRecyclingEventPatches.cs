using System;
using Game.Components;
using Game.Data;
using Game.Systems.Items;
using HarmonyLib;
using UnityEngine;

namespace Game.Systems.IntelligentRecycling {
    /// <summary>
    /// Requests a deferred scan after central vanilla state changes. The game
    /// system performs the actual scan on a clock tick, keeping paused games idle.
    /// </summary>
    [HarmonyPatch]
    internal static class IntelligentRecyclingEventPatches {
        [HarmonyPatch(typeof(Objs), nameof(Objs.Create),
            new[] { typeof(Vector2), typeof(ObjData), typeof(bool) })]
        [HarmonyPostfix]
        private static void AfterCreateFromData(Obj __result) {
            if (__result != null) {
                IntelligentRecyclingSys.RequestEventScan(__result);
            }
        }

        [HarmonyPatch(typeof(Objs), nameof(Objs.Create),
            new[] { typeof(Vector2), typeof(Def), typeof(bool) })]
        [HarmonyPostfix]
        private static void AfterCreateFromDefinition(Obj __result) {
            if (__result != null) {
                IntelligentRecyclingSys.RequestEventScan(__result);
            }
        }

        [HarmonyPatch(typeof(Entity), nameof(Entity.SetCarrier))]
        [HarmonyPostfix]
        private static void AfterCarrierChanged(Entity __instance, Being c, bool __result) {
            if (__result && c == null && __instance is Obj obj) {
                IntelligentRecyclingSys.RequestEventScan(obj);
            }
        }

        [HarmonyPatch(typeof(DisposerComp), nameof(DisposerComp.PartQualityThreshold),
            MethodType.Setter)]
        [HarmonyPostfix]
        private static void AfterProcessorThresholdChanged(
                DisposerComp __instance) {
            IntelligentRecyclingSys.NotifyProcessorThresholdChanged(__instance);
        }

        [HarmonyPatch(typeof(DisposerComp), nameof(DisposerComp.CopyConfigTo))]
        [HarmonyPostfix]
        private static void AfterProcessorConfigurationCopied(
                ICopyableComp target, bool __result) {
            if (__result && target is DisposerComp disposer) {
                IntelligentRecyclingSys.NotifyProcessorThresholdChanged(disposer);
            }
        }

        [HarmonyPatch(typeof(StorageCapsuleComp), nameof(StorageCapsuleComp.Open))]
        [HarmonyPostfix]
        private static void AfterStorageCapsuleOpened() {
            IntelligentRecyclingSys.RequestEventScan();
        }

        [HarmonyPatch(typeof(QuestPackageComp), nameof(QuestPackageComp.Open))]
        [HarmonyPostfix]
        private static void AfterSafeBoxOpened() {
            IntelligentRecyclingSys.RequestEventScan();
        }

        [HarmonyPatch(typeof(HullpiercerComp), nameof(HullpiercerComp.Open))]
        [HarmonyPostfix]
        private static void AfterHullpiercerOpened() {
            IntelligentRecyclingSys.RequestEventScan();
        }

        [HarmonyPatch(typeof(StasisPodComp), nameof(StasisPodComp.Open))]
        [HarmonyPostfix]
        private static void AfterStasisPodOpened() {
            IntelligentRecyclingSys.RequestEventScan();
        }

        [HarmonyPatch(typeof(BreachCapsuleComp), "StartRaid", new Type[] { })]
        [HarmonyPostfix]
        private static void AfterBreachCapsuleRaidStarted() {
            IntelligentRecyclingSys.RequestEventScan();
        }

        [HarmonyPatch(typeof(ShuttleComp), nameof(ShuttleComp.Hide),
            new[] { typeof(bool) })]
        [HarmonyPostfix]
        private static void AfterShuttleHidden(ShuttleComp __instance,
                bool permanent) {
            if (permanent && __instance?.IsLost == true) {
                IntelligentRecyclingSys.RequestEventScan();
            }
        }
    }
}
