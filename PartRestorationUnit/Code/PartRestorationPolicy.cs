using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Items;
using KL.Grid;
using UnityEngine;

namespace Game.Systems.PartRestoration {
    internal static class PartRestorationPolicy {
        internal static bool HasRepairableDamage(ObjData data) {
            BodyPartState state = data?.BodyPartState;
            if (data?.BodyPart == null || state?.Wounds == null) {
                return false;
            }
            for (int i = state.Wounds.Count - 1; i >= 0; i--) {
                if (state.Wounds[i].CanHeal(null, true)) {
                    return true;
                }
            }
            return false;
        }

        internal static float RepairCeiling(BodyPartState state) {
            if (state != null
                    && (state.Flags & BodyPartStateFlags.Crippled)
                        == BodyPartStateFlags.Crippled) {
                return Tunables.CrippledPartMaxCondition;
            }
            return 1f;
        }

        internal static float RepairProgress(BodyPartState state) {
            float ceiling = RepairCeiling(state);
            return ceiling > 0.0001f
                ? Mathf.Clamp01(state.Condition / ceiling)
                : 1f;
        }

        internal static bool IsOwned(
                GameState state, uint objectId) {
            if (state == null || objectId == 0u) {
                return false;
            }
            var components = state.Components
                .AllByType<PartRestorationUnitComp>();
            if (components == null) {
                return false;
            }
            foreach (IComponent component in components) {
                if (component is PartRestorationUnitComp unit
                        && unit.InputId == objectId) {
                    return true;
                }
            }
            return false;
        }

        internal static bool TryFindAvailableUnit(
                GameState state, ObjData part,
                out PartRestorationUnitComp best) {
            best = null;
            if (state == null || part == null) {
                return false;
            }
            var components = state.Components
                .AllByType<PartRestorationUnitComp>();
            if (components == null) {
                return false;
            }

            int partPos = part.Obj?.PosIdx ?? 0;
            int bestDistance = int.MaxValue;
            foreach (IComponent component in components) {
                if (!(component is PartRestorationUnitComp unit)
                        || !unit.CanAcceptPart) {
                    continue;
                }
                int distance = part.Obj == null
                    ? 0
                    : Pos.SqrDistance(partPos, unit.Tile.PosIdx);
                if (best == null || distance < bestDistance) {
                    best = unit;
                    bestDistance = distance;
                }
            }
            return best != null;
        }

        internal static bool WouldYieldToIntelligentRecycling(
                ObjData part) {
            return IntelligentRecyclingBridge.TryWouldClaim(
                    part?.Id ?? 0u, out bool wouldClaim)
                && wouldClaim;
        }
    }
}
