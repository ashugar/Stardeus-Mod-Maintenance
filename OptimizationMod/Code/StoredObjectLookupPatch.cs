using System;
using System.Collections.Generic;
using Game.Systems.Items;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Core sorts the complete global object registry before filtering it to one
    /// storage unit. Filter first and sort only that storage's matching contents.
    /// </summary>
    internal static class StoredObjectLookupPatch {
        internal static bool Prefix(
                ObjsSys __instance,
                int storageId,
                ObjData[] res,
                string searchStr,
                ObjData.SortMode sortMode,
                ref int __result) {
            if (__instance == null || res == null) { return true; }

            try {
                int found = 0;
                var searchQuery = Strings.ToSearchString(searchStr, true);

                if (sortMode == ObjData.SortMode.Unsorted) {
                    foreach (ObjData obj in __instance.All.Values) {
                        if (obj.StorageId != storageId) { continue; }
                        if (!obj.MatchesSearchQuery(searchQuery)) { continue; }
                        if (found < res.Length) {
                            res[found++] = obj;
                        }
                        if (found == res.Length) { break; }
                    }
                } else {
                    var matches = new List<ObjData>();
                    foreach (ObjData obj in __instance.All.Values) {
                        if (obj.StorageId == storageId
                                && obj.MatchesSearchQuery(searchQuery)) {
                            matches.Add(obj);
                        }
                    }

                    foreach (ObjData obj in ObjData.SortedBy(
                            __instance.S, matches, sortMode)) {
                        if (found < res.Length) {
                            res[found++] = obj;
                        }
                        if (found == res.Length) { break; }
                    }
                }

                __result = found;
                return false;
            } catch (Exception ex) {
                D.Err("Optimization Mod stored-object query failed open: {0}", ex);
                return true;
            }
        }
    }
}
