using System;
using System.Collections.Generic;
using Game.Constants;
using Game.Systems.Items;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Core shares one haulable-object snapshot among all askers in a tick, but
    /// builds it by searching every persistent ObjData. Build the same bounded
    /// per-tick snapshot from ActiveObjs, then retain Core's query-time
    /// revalidation for objects claimed by an earlier asker in the same tick.
    /// </summary>
    internal static class ActiveHaulQueryPatch {
        private static readonly List<ObjData> HaulableObjs = new List<ObjData>();
        private static ObjsSys cachedSystem;
        private static long cachedTick = -1;

        internal static void Reset() {
            cachedSystem = null;
            cachedTick = -1;
            HaulableObjs.Clear();
        }

        internal static bool Prefix(
                ObjsSys __instance,
                ObjData[] res,
                Func<ObjData, bool> accepts,
                ref int __result) {
            if (__instance?.ActiveObjs == null || res == null || accepts == null) {
                return true;
            }

            try {
                int found = 0;
                long ticks = G.Ticks;
                long canHaulAfter = ticks - Consts.TicksPerHour;
                if (!ReferenceEquals(cachedSystem, __instance)
                        || cachedTick != ticks) {
                    cachedSystem = __instance;
                    cachedTick = ticks;
                    HaulableObjs.Clear();
                    List<ObjData> active = __instance.ActiveObjs;
                    for (int i = 0; i < active.Count; i++) {
                        ObjData candidate = active[i];
                        if (IsHaulableUnstored(candidate, canHaulAfter)) {
                            HaulableObjs.Add(candidate);
                        }
                    }
                }

                for (int i = 0; i < HaulableObjs.Count; i++) {
                    ObjData o = HaulableObjs[i];
                    // An earlier asker this tick may already have reserved it or
                    // assigned a hauling goal, matching Core's shared-cache path.
                    if (!IsHaulableUnstored(o, canHaulAfter)) { continue; }
                    if (!accepts(o)) { continue; }

                    if (found < res.Length) {
                        res[found++] = o;
                    }
                    if (found == res.Length) {
                        break;
                    }
                }

                __result = found;
                return false;
            } catch (Exception ex) {
                D.Err("Optimization Mod active-haul query failed open: {0}", ex);
                return true;
            }
        }

        private static bool IsHaulableUnstored(
                ObjData obj, long canHaulAfter) {
            return obj != null
                && obj.State == ObjState.Active
                && obj.Reservation?.IsActive != true
                && obj.Obj?.StorableObj != null
                && !obj.Obj.StorableObj.IsHaulingForbidden
                && obj.Obj.StorableObj.SpawnedAt <= canHaulAfter
                && obj.Obj.Goal == null;
        }
    }
}
