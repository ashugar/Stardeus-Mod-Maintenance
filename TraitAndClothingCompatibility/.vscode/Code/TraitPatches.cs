using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Components;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Policies;
using Game.Systems.Traits;
using HarmonyLib;

namespace TraitAndClothingCompatibility {
    // Notifications only. Core remains responsible for applying/removing traits.
    [HarmonyPatch(typeof(BeingTraits), nameof(BeingTraits.AddTrait))]
    internal static class TraitAddedPatch {
        private static void Postfix() => TaccSystem.Instance?.RequestScan();
    }
    [HarmonyPatch(typeof(BeingTraits), nameof(BeingTraits.RemoveTrait))]
    internal static class TraitRemovedPatch {
        private static void Postfix() => TaccSystem.Instance?.RequestScan();
    }

    [HarmonyPatch]
    internal static class EquipmentSearchScopePatch {
        [ThreadStatic]
        private static int allowForbiddenDepth;

        internal static bool AllowsForbidden => allowForbiddenDepth > 0;

        private static IEnumerable<MethodBase> TargetMethods() {
            yield return AccessTools.Method(typeof(AIActionFindEquipment), nameof(AIActionFindEquipment.Execute));
            yield return AccessTools.Method(typeof(AIActionFindBestEquipment), nameof(AIActionFindBestEquipment.Execute));
        }

        private static void Prefix(AIAgentComp agent, out bool __state) {
            __state = TaccSystem.Instance?.AllowsForbiddenEquipment(agent?.BeingData) == true;
            if (__state) allowForbiddenDepth++;
        }

        private static Exception Finalizer(Exception __exception, bool __state) {
            if (__state && allowForbiddenDepth > 0) allowForbiddenDepth--;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(StorableObjComp), nameof(StorableObjComp.IsHaulingForbidden), MethodType.Getter)]
    internal static class EquipmentSearchForbiddenGetterPatch {
        private static void Postfix(ref bool __result) {
            if (EquipmentSearchScopePatch.AllowsForbidden) __result = false;
        }
    }

    [HarmonyPatch(typeof(InventoryItemSlot), nameof(InventoryItemSlot.BetterItem))]
    [HarmonyBefore("ExpandedTraitsMod.Runtime.Bootstrap")]
    internal static class MoodFirstEquipmentPatch {
        private static bool Prefix(InventoryItemSlot __instance, ObjData a, ObjData b, ref ObjData __result) {
            BeingData wearer = __instance?.Inventory?.Owner;
            if (__instance?.Type?.Worn != true || a == null || b == null
                    || TaccSystem.Instance?.ManagesEquipment(wearer) != true) return true;
            int aMood = LoadoutMood(__instance, a, wearer);
            int bMood = LoadoutMood(__instance, b, wearer);
            if (aMood == bMood) return true;
            __result = bMood > aMood ? b : a;
            return false;
        }

        private static int LoadoutMood(InventoryItemSlot replacing, ObjData candidate, BeingData wearer) {
            var items = new List<ClothingMoodState>();
            foreach (InventoryItemSlot slot in replacing.Inventory.Equipment.Values) {
                if (ReferenceEquals(slot, replacing)) continue;
                Add(items, slot?.ObjData, wearer);
            }
            Add(items, candidate, wearer);
            int tatteredMood = wearer.HasTrait(TraitIdH.Fastidious)
                ? Tunables.WornGearMoodTatteredFastidious : Tunables.WornGearMoodTattered;
            int adjustment = wearer.HasTrait(TraitIdH.Optimist) ? 1
                : wearer.HasTrait(TraitIdH.Pessimist) ? -1 : 0;
            return ClothingMoodPriority.Score(items, ItemOwnership.SecondHandMoodFor(wearer),
                ItemOwnership.DeadMansMoodFor(wearer), tatteredMood, adjustment);
        }

        private static void Add(List<ClothingMoodState> items, ObjData item, BeingData wearer) {
            if (!ItemOwnership.TracksOwnership(item)) return;
            bool dead = ItemOwnership.IsDeadMansGear(item);
            items.Add(new ClothingMoodState(dead,
                !dead && ItemOwnership.IsSecondHandFor(item, wearer), item.IsTattered));
        }
    }

    [HarmonyPatch(typeof(AINeedEquipment), nameof(AINeedEquipment.Tick))]
    internal static class ManagedEquipmentUrgencyPatch {
        private sealed class GoalAdjustment {
            internal long ActivationTick = long.MinValue;
            internal bool HasAvailableCandidate;
            internal bool Adjusted;
        }

        private static readonly Dictionary<uint, GoalAdjustment> Adjustments = new Dictionary<uint, GoalAdjustment>();

        private static void Postfix(AINeedEquipment __instance, GameState s, BeingData data, long ticks) {
            AIAgentComp agent = data?.Being?.AIAgent;
            if (agent == null || !agent.BB.TryNeedGoal(__instance.IdH, out AIGoal goal)) return;

            bool managed = TaccSystem.Instance?.ManagesEquipment(data) == true;
            bool hasEmptyWorn = managed && HasEmptyAutomaticWornSlot(s, data);
            if (!Adjustments.TryGetValue(goal.Id, out GoalAdjustment adjustment)) {
                if (!managed || !hasEmptyWorn) return;
                adjustment = new GoalAdjustment();
                Adjustments[goal.Id] = adjustment;
            }

            if (managed && hasEmptyWorn && data.Inventory.IsNaked
                    && adjustment.ActivationTick != goal.CreatedAt) {
                adjustment.ActivationTick = goal.CreatedAt;
                adjustment.HasAvailableCandidate = HasAvailableWornCandidate(s, agent);
            }

            EquipmentUrgencyDecision decision = EquipmentUrgencyRules.Decide(managed, hasEmptyWorn,
                data.Inventory?.IsNaked == true, adjustment.HasAvailableCandidate);
            if (decision.Adjust) {
                goal.GoalPriority = decision.GoalPriority;
                goal.WithInterrupt(decision.InterruptPriority, 240);
                adjustment.Adjusted = true;
            } else if (adjustment.Adjusted) {
                goal.GoalPriority = (byte)(data?.Inventory?.IsNaked == true ? 5 : 2);
                goal.WithInterrupt(1, 240);
                adjustment.Adjusted = false;
                adjustment.HasAvailableCandidate = false;
                Adjustments.Remove(goal.Id);
            }
        }

        private static bool HasEmptyAutomaticWornSlot(GameState s, BeingData data) {
            foreach (InventoryItemSlot slot in data.Inventory.Equipment.Values) {
                if (slot?.Type?.Worn != true || slot.ObjData != null) continue;
                EquipmentSlotPolicy policy = s.Query.EquipmentPolicyFor.Ask(data, slot.Type);
                if (policy != null && policy.Mode != AutoEquipMode.Manual) return true;
            }
            return false;
        }

        private static bool HasAvailableWornCandidate(GameState s, AIAgentComp agent) {
            bool allowForbidden = TaccSystem.Instance?.AllowsForbiddenEquipment(agent.BeingData) == true;
            var rejected = new HashSet<uint>();
            var result = new ObjData[1];
            foreach (KeyValuePair<int, InventoryItemSlot> entry in agent.BeingData.Inventory.Equipment) {
                InventoryItemSlot slot = entry.Value;
                if (slot?.Type?.Worn != true || slot.ObjData != null) continue;
                EquipmentSlotPolicy policy = s.Query.EquipmentPolicyFor.Ask(agent.BeingData, slot.Type);
                if (policy == null || policy.Mode == AutoEquipMode.Manual) continue;

                while (true) {
                    result[0] = null;
                    int count = s.Query.FindCompatibleEquipment.Ask(agent, entry.Key, result, rejected, null);
                    if (count == 0 || result[0] == null) break;
                    ObjData candidate = result[0];
                    if (allowForbidden || candidate.Obj?.StorableObj?.IsHaulingForbidden != true) return true;
                    if (!rejected.Add(candidate.Id)) break;
                }
            }
            return false;
        }
    }
}
