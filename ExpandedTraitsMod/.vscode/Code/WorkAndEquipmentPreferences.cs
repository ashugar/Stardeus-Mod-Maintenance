using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Policies;
using HarmonyLib;

namespace ExpandedTraitsMod {
    [HarmonyPatch]
    internal static class MinimalistNoHatPatch {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(ObjsSys), "BestAvailableEquipment", new[] {
                typeof(InventoryItemSlot), typeof(HashSet<uint>), typeof(HashSet<int>)
            });

        private static bool Prefix(InventoryItemSlot slot, ref ObjData __result) {
            BeingData wearer = slot?.Inventory?.Owner;
            if (slot?.Type?.IdH != InventorySlotIdH.Hat
                    || wearer?.Mind?.Traits?.HasTrait(ExpandedTraitIds.MinimalistH) != true) {
                return true;
            }

            // Core's shared default policy is not a player choice. The UI edits an owned
            // policy instance, so any non-default instance remains fully authoritative.
            EquipmentSlotPolicy policy = A.S.Query.EquipmentPolicyFor.Ask(wearer, slot.Type);
            if (policy == null || !ReferenceEquals(policy,
                    EquipmentPolicyDefaults.DefaultFor(slot.Type))) return true;
            __result = null;
            ValidationDiagnostics.Once($"minimalist-no-hat:{wearer.Id}",
                "Minimalist default cosmetic-hat preference applied: mind={0}; result=empty; playerPolicyOverride=false.",
                wearer.Mind);
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryItemSlot), nameof(InventoryItemSlot.BetterItem),
        new[] { typeof(ObjData), typeof(ObjData) })]
    internal static class MinimalistEquipmentPatch {
        private const string Knife = "Obj/Equipment/Weapons/Knife01";
        private const string Suit = "Obj/Equipment/Clothing/SpaceSuit01";
        private const string Helmet = "Obj/Equipment/Hats/SpaceHelmet01";

        private static bool Prefix(InventoryItemSlot __instance, ObjData a, ObjData b,
                ref ObjData __result) {
            BeingData wearer = __instance?.Inventory?.Owner;
            if (wearer?.Mind?.Traits?.HasTrait(ExpandedTraitIds.MinimalistH) != true
                    || a == null || b == null) return true;
            int rankA = Rank(a);
            int rankB = Rank(b);
            if (rankA == rankB) return true;
            __result = rankB > rankA ? b : a;
            ValidationDiagnostics.Once(
                $"minimalist-equipment:{wearer.Id}:{__instance.Type?.IdH}:{__result.Def?.Id}",
                "Minimalist equipment ranking applied: mind={0}; slotHash={1}; candidateA={2} (rank {3}); candidateB={4} (rank {5}); selected={6}.",
                wearer.Mind, __instance.Type?.IdH ?? 0, a.Def?.Id, rankA,
                b.Def?.Id, rankB, __result.Def?.Id);
            return false;
        }

        private static int Rank(ObjData item) {
            string id = item.Def?.Id;
            if (id == Knife || id == Suit || id == Helmet) return 4;
            if (id?.Contains("/Weapons/") == true) return 1;
            if (id?.Contains("/Hats/") == true) return 0;
            if (id?.Contains("/Clothing/") == true) {
                InventoryItemDef def = item.Item;
                bool functional = (def?.AddsAbilities?.Length ?? 0) > 0
                    || (def?.StatModifiers?.Length ?? 0) > 0;
                return functional ? 3 : 2;
            }
            return 1;
        }
    }

    [HarmonyPatch]
    internal static class TraitWorkCostPatch {
        private static IEnumerable<MethodBase> TargetMethods() {
            Type[] signature = { typeof(AIAgentComp), typeof(AIGoal), typeof(long) };
            IEnumerable<MethodBase> overrides = typeof(AIAction).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(AIAction).IsAssignableFrom(t))
                .Select(t => t.GetMethod(nameof(AIAction.CostFor), BindingFlags.Instance
                    | BindingFlags.Public, null, signature, null))
                .Where(m => m != null && m.DeclaringType == m.ReflectedType)
                .Distinct();
            MethodBase baseMethod = typeof(AIAction).GetMethod(nameof(AIAction.CostFor),
                BindingFlags.Instance | BindingFlags.Public, null, signature, null);
            return baseMethod == null ? overrides : overrides.Append(baseMethod);
        }

        private static void Postfix(MethodBase __originalMethod, AIAgentComp agent,
                AIGoal goal, ref float __result) {
            if (__result <= 0f || agent?.BeingData?.Mind?.Traits == null || goal == null
                    || goal.IsForceAssigned || goal.InterruptPriority >= 3) return;
            var traits = agent.BeingData.Mind.Traits;
            Entity target = goal.ReachEntity;
            if (traits.HasTrait(ExpandedTraitIds.IntrovertH)
                    && __originalMethod?.DeclaringType == typeof(AIActionPetAnimal)) {
                __result *= 0.5f;
            }
            if (traits.HasTrait(ExpandedTraitIds.CuriousH)) {
                bool research = goal.JobIdH == JobTypeIdH.Research;
                if (!research && goal.JobIdH == JobTypeIdH.Hauling
                        && IsResearchTarget(target, goal.Target?.Entity,
                            agent.BB.TargetEntity)) {
                    research = true;
                }
                if (research) __result *= 0.7f;
            }
            if (traits.HasTrait(ExpandedTraitIds.ParanoidH)
                    && (goal.JobIdH == JobTypeIdH.Repairs
                        || goal.JobIdH == JobTypeIdH.Construction)
                    && IsSecurity(target?.Definition)) {
                __result *= 0.25f;
            }
            bool hazardous = goal.JobIdH == JobTypeIdH.Firefighting
                || goal.JobIdH == JobTypeIdH.Combat
                || goal.JobIdH == JobTypeIdH.Rescue
                || (target != null && !agent.BeingData.Safety.IsActuallySafeAt(target.PosIdx));
            if (!hazardous) return;
            if (traits.HasTrait(ExpandedTraitIds.DaredevilH)) __result *= 0.75f;
            else if (traits.HasTrait(ExpandedTraitIds.CautiousH)) __result *= 1.35f;
        }

        private static bool IsResearchTarget(params Entity[] targets) {
            foreach (Entity entity in targets) {
                if (entity?.TryGetComponent<OperatableDeviceComp>(out var op) == true
                        && op.JobType?.IdHash == JobTypeIdH.Research) return true;
            }
            return false;
        }

        private static bool IsSecurity(Def def) {
            if (def == null) return false;
            if (string.Equals(def.Spawnable?.Category, "Security", StringComparison.OrdinalIgnoreCase)) return true;
            string id = def.Id ?? string.Empty;
            return id.IndexOf("Turret", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("Siren", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("Firewall", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
