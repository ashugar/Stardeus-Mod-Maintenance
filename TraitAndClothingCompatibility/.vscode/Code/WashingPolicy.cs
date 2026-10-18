using Game.Components;
using Game.Data;
using Game.Systems.Items;
using KL.Grid;

namespace TraitAndClothingCompatibility {
    internal static class WashingPolicy {
        internal static bool IsClothing(ObjData item) {
            return item?.Item?.SlotType?.Worn == true;
        }

        internal static bool IsWashable(ObjData item) {
            return IsClothing(item) && item.BelongedToDead;
        }

        internal static ObjFlags WashedFlags(ObjFlags flags) {
            return WashingRules.WashedFlags(flags);
        }

        internal static void ApplyWash(ObjData item) {
            if (item == null) return;
            item.Flags = WashedFlags(item.Flags);
        }

        internal static bool IsOwned(GameState state, uint objectId) {
            if (state == null || objectId == 0u) return false;
            var components = state.Components.AllByType<TaccWashingMachineComp>();
            if (components == null) return false;
            foreach (IComponent component in components) {
                if (component is TaccWashingMachineComp machine
                        && machine.InputId == objectId) return true;
            }
            return false;
        }

        internal static bool TryFindAvailableMachine(
                GameState state, ObjData item,
                out TaccWashingMachineComp best) {
            best = null;
            if (state == null || item == null) return false;
            var components = state.Components.AllByType<TaccWashingMachineComp>();
            if (components == null) return false;

            int itemPos = item.Obj?.PosIdx ?? 0;
            int bestDistance = int.MaxValue;
            foreach (IComponent component in components) {
                if (!(component is TaccWashingMachineComp machine)
                        || !machine.CanAcceptItem) continue;
                int distance = item.Obj == null
                    ? 0
                    : Pos.SqrDistance(itemPos, machine.Tile.PosIdx);
                if (best == null || distance < bestDistance) {
                    best = machine;
                    bestDistance = distance;
                }
            }
            return best != null;
        }
    }
}
