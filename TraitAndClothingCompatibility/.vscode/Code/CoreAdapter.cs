using System;
using System.Collections.Generic;
using System.Linq;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Policies;

namespace TraitAndClothingCompatibility {
    internal static class CoreAdapter {
        internal static SlotPolicy Capture(EquipmentSlotPolicy s) => s == null ? null : new SlotPolicy {
            Slot = s.SlotId, Mode = (int)s.Mode, QualityMin = s.QualityMin, QualityMax = s.QualityMax,
            ColorId = s.ColorId, ItemDefId = s.ItemDefId, WeaponKind = (int)s.WeaponKind,
            ForbidWhenDropped = s.ForbidWhenDropped, Incompatible = (int)s.Incompatible,
            ForbidSecondHand = s.ForbidSecondHand, ForbidDeadMans = s.ForbidDeadMans
        };

        internal static PolicyState Capture(EquipmentPolicy p) => p == null ? null : new PolicyState {
            Slots = (p.Slots ?? Array.Empty<EquipmentSlotPolicy>()).Where(s => s != null).Select(Capture).ToList()
        };

        internal static EquipmentSlotPolicy Materialize(SlotPolicy s) => new EquipmentSlotPolicy {
            SlotId = s.Slot, Mode = (AutoEquipMode)s.Mode, QualityMin = s.QualityMin, QualityMax = s.QualityMax,
            ColorId = s.ColorId, ItemDefId = s.ItemDefId, WeaponKind = (WeaponKind)s.WeaponKind,
            ForbidWhenDropped = s.ForbidWhenDropped, Incompatible = (IncompatibleAction)s.Incompatible,
            ForbidSecondHand = s.ForbidSecondHand, ForbidDeadMans = s.ForbidDeadMans
        };

        internal static EquipmentPolicy Materialize(PolicyState p) => p == null ? null : new EquipmentPolicy {
            Slots = (p.Slots ?? new List<SlotPolicy>()).Where(s => s != null).Select(Materialize).ToArray()
        };

        internal static List<SlotPolicy> Baselines(PoliciesSys policies, BeingData being, PolicyState original) {
            EquipmentPolicy own = Materialize(original);
            var result = new List<SlotPolicy>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inventorySlot in being.Inventory.Equipment.Values) {
                var type = inventorySlot.Type;
                if (type?.Worn != true || !seen.Add(type.Id)) continue;
                result.Add(Capture(EquipmentPolicyResolver.Resolve(own,
                    policies.SpeciesEquipment(being.Species), policies.GlobalEquipment, type)));
            }
            return result;
        }
    }
}
