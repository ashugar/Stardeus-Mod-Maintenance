using System.Collections.Generic;
using Game;

namespace Game.Systems.IntelligentRecycling {
    /// <summary>
    /// Versioned, read-only compatibility surface for Intelligent Recycling.
    /// Snapshot methods are intended for Stardeus's main simulation thread.
    /// They never create, cancel, retry, or modify tasks or settings.
    /// </summary>
    public static class IntelligentRecyclingApi {
        public static int ContractVersion => 1;

        public static IntelligentRecyclingApiInfo GetApiInfo() {
            return IntelligentRecyclingSys.BuildApiInfo();
        }

        public static IntelligentRecyclingStateSnapshot GetStateSnapshot() {
            IntelligentRecyclingSys system = IntelligentRecyclingSys.ApiInstance;
            if (system == null) {
                return IntelligentRecyclingSys.BuildUnavailableStateSnapshot(
                    "system-not-initialized");
            }
            if (!A.IsMainThread) {
                return IntelligentRecyclingSys.BuildUnavailableStateSnapshot(
                    "main-thread-required");
            }
            return system.BuildStateSnapshot();
        }

        public static bool TryGetObjectSnapshot(uint objectId,
                out IntelligentRecyclingObjectSnapshot snapshot) {
            snapshot = null;
            IntelligentRecyclingSys system = IntelligentRecyclingSys.ApiInstance;
            if (objectId == 0 || system == null || !A.IsMainThread) {
                return false;
            }
            return system.TryBuildObjectSnapshot(objectId, out snapshot);
        }

        public static bool TryGetBeingSnapshot(uint beingId,
                out IntelligentRecyclingSpecialTargetSnapshot snapshot) {
            snapshot = null;
            IntelligentRecyclingSys system = IntelligentRecyclingSys.ApiInstance;
            if (beingId == 0 || system == null || !A.IsMainThread) {
                return false;
            }
            return system.TryBuildBeingSnapshot(beingId, out snapshot);
        }

        public static IReadOnlyList<IntelligentRecyclingCategorySnapshot>
                GetCategories() {
            IntelligentRecyclingSys system = IntelligentRecyclingSys.ApiInstance;
            if (system == null || !A.IsMainThread) {
                return IntelligentRecyclingApiIds.ReadOnlyCopy(
                    (IEnumerable<IntelligentRecyclingCategorySnapshot>) null);
            }
            return system.BuildCategorySnapshots();
        }

        public static IReadOnlyList<IntelligentRecyclingProcessorSnapshot>
                GetProcessors() {
            IntelligentRecyclingSys system = IntelligentRecyclingSys.ApiInstance;
            if (system == null || !A.IsMainThread) {
                return IntelligentRecyclingApiIds.ReadOnlyCopy(
                    (IEnumerable<IntelligentRecyclingProcessorSnapshot>) null);
            }
            return system.BuildProcessorSnapshots();
        }

        public static IReadOnlyList<IntelligentRecyclingTrackedTaskSnapshot>
                GetTrackedTasks() {
            IntelligentRecyclingSys system = IntelligentRecyclingSys.ApiInstance;
            if (system == null || !A.IsMainThread) {
                return IntelligentRecyclingApiIds.ReadOnlyCopy(
                    (IEnumerable<IntelligentRecyclingTrackedTaskSnapshot>) null);
            }
            return system.BuildTrackedTaskSnapshots();
        }

        public static IReadOnlyList<IntelligentRecyclingSpecialTargetSnapshot>
                GetSpecialTargets() {
            IntelligentRecyclingSys system = IntelligentRecyclingSys.ApiInstance;
            if (system == null || !A.IsMainThread) {
                return IntelligentRecyclingApiIds.ReadOnlyCopy(
                    (IEnumerable<IntelligentRecyclingSpecialTargetSnapshot>) null);
            }
            return system.BuildSpecialTargetSnapshots();
        }
    }
}
