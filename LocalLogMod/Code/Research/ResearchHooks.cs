using Game.Research;
using Game.Systems;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.Research {
    [HarmonyPatch]
    internal static class ResearchHooks {
        [HarmonyPatch(typeof(ResearchProgress), nameof(ResearchProgress.Unlock))]
        [HarmonyPrefix]
        private static void BeforeUnlock(ResearchProgress __instance, out bool __state) {
            __state = __instance.InternalUnlocked;
        }

        [HarmonyPatch(typeof(ResearchProgress), nameof(ResearchProgress.Unlock))]
        [HarmonyPostfix]
        private static void AfterUnlock(ResearchProgress __instance, bool __state) {
            SessionLog.Guard("research.unlock", () => {
                if (!__state && __instance.InternalUnlocked) {
                    SessionLog.Write("Research", "unlocked id=" + __instance.Id);
                }
            });
        }

        [HarmonyPatch(typeof(ResearchProgress), nameof(ResearchProgress.Discover))]
        [HarmonyPrefix]
        private static void BeforeDiscover(ResearchProgress __instance, out bool __state) {
            __state = __instance.IsMarkedDiscovered;
        }

        [HarmonyPatch(typeof(ResearchProgress), nameof(ResearchProgress.Discover))]
        [HarmonyPostfix]
        private static void AfterDiscover(ResearchProgress __instance, bool __state) {
            SessionLog.Guard("research.discover", () => {
                if (!__state && __instance.IsMarkedDiscovered) {
                    SessionLog.Write("Research", "discovered id=" + __instance.Id);
                }
            });
        }

        [HarmonyPatch(typeof(ResearchTree), nameof(ResearchTree.TryUnlock))]
        [HarmonyPostfix]
        private static void TryUnlock(ResearchProgress progress, bool __result) {
            SessionLog.Guard("research.attempt", () => SessionLog.Write("Research",
                "unlock_attempt id=" + progress?.Id + " success=" + __result
                + " lackingDisk=" + progress?.IsLackingDisk));
        }

        [HarmonyPatch(typeof(ResearchSys), nameof(ResearchSys.TryAddBookmark))]
        [HarmonyPostfix]
        private static void Bookmark(ResearchProgress progress, bool __result) {
            SessionLog.Guard("research.bookmark", () => SessionLog.Write("Research",
                "bookmark id=" + progress?.Id + " success=" + __result));
        }

        [HarmonyPatch(typeof(ResearchSys), nameof(ResearchSys.TryRemoveBookmark))]
        [HarmonyPostfix]
        private static void RemoveBookmark(ResearchProgress progress, bool __result) {
            SessionLog.Guard("research.bookmark.remove", () => SessionLog.Write("Research",
                "bookmark_removed id=" + progress?.Id + " success=" + __result));
        }

        [HarmonyPatch(typeof(ResearchProgress), nameof(ResearchProgress.Remove))]
        [HarmonyPostfix]
        private static void Removed(ResearchProgress __instance) {
            SessionLog.Guard("research.remove", () => SessionLog.Write("Research", "progress_removed id=" + __instance.Id));
        }

        [HarmonyPatch(typeof(ResearchTree), nameof(ResearchTree.TryUnlockBonus))]
        [HarmonyPostfix]
        private static void BonusUnlock(ResearchTree __instance, int idH, bool __result) {
            SessionLog.Guard("research.bonus", () => SessionLog.Write("Research",
                "bonus_unlock id=" + (__instance.Progress.TryGetValue(idH, out var progress) ? progress.Id : idH.ToString())
                + " success=" + __result));
        }

        [HarmonyPatch(typeof(ResearchTree), nameof(ResearchTree.Reload))]
        [HarmonyPostfix]
        private static void Reloaded(ResearchTree __instance) {
            SessionLog.Guard("research.reload", () => SessionLog.Write("Research",
                "tree_reloaded progressCount=" + __instance.Progress.Count));
        }
    }
}
