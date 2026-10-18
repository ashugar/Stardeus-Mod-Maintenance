using Game.Data;
using Game.Systems.Items;

namespace Game.Systems.PartRestoration {
    /// <summary>
    /// Versioned, read-only compatibility surface. Snapshot calls must be made
    /// on Stardeus's main simulation thread.
    /// </summary>
    public static class PartRestorationApi {
        public static int ContractVersion => 1;

        public static PartRestorationApiInfo GetApiInfo() {
            return new PartRestorationApiInfo(
                ContractVersion,
                "1.1.2",
                A.IsMainThread,
                A.S != null);
        }

        public static bool TryGetObjectOwnershipSnapshot(
                uint objectId,
                out PartRestorationOwnershipSnapshot snapshot) {
            snapshot = null;
            if (!A.IsMainThread || A.S == null || objectId == 0u) {
                return false;
            }
            ObjData data = A.S.Query.FindObjData.Ask(objectId);
            if (data == null) {
                return false;
            }
            snapshot = new PartRestorationOwnershipSnapshot(
                objectId,
                data.BodyPart != null,
                PartRestorationPolicy.IsOwned(A.S, objectId));
            return true;
        }

        public static bool TryGetObjectSnapshot(
                uint objectId,
                out PartRestorationObjectSnapshot snapshot) {
            snapshot = null;
            if (!A.IsMainThread || A.S == null || objectId == 0u) {
                return false;
            }
            ObjData data = A.S.Query.FindObjData.Ask(objectId);
            if (data == null) {
                return false;
            }

            bool bodyPart = data.BodyPart != null;
            bool repairable = bodyPart
                && PartRestorationPolicy.HasRepairableDamage(data);
            bool owned = bodyPart
                && PartRestorationPolicy.IsOwned(A.S, objectId);
            bool recyclingOwns = repairable
                && PartRestorationPolicy
                    .WouldYieldToIntelligentRecycling(data);
            bool hasUnit = repairable && !recyclingOwns
                && PartRestorationPolicy.TryFindAvailableUnit(
                    A.S, data, out _);
            bool wouldClaim = repairable && !recyclingOwns
                && (owned || hasUnit)
                && data.Obj?.Goal == null;
            bool released = !owned && !wouldClaim;
            bool haulable = released
                && data.State == ObjState.Active
                && data.Obj != null
                && data.Obj.Goal == null
                && data.Reservation == null
                && data.IsAvailable;

            snapshot = new PartRestorationObjectSnapshot(
                objectId,
                bodyPart,
                repairable,
                owned,
                hasUnit,
                recyclingOwns,
                wouldClaim,
                released,
                haulable);
            return true;
        }
    }

    public sealed class PartRestorationApiInfo {
        internal PartRestorationApiInfo(
                int contractVersion, string modVersion,
                bool mainThread, bool gameStateLoaded) {
            ContractVersion = contractVersion;
            ModVersion = modVersion;
            MainThread = mainThread;
            GameStateLoaded = gameStateLoaded;
        }

        public int ContractVersion { get; }
        public string ModVersion { get; }
        public bool MainThread { get; }
        public bool GameStateLoaded { get; }
    }

    public sealed class PartRestorationOwnershipSnapshot {
        internal PartRestorationOwnershipSnapshot(
                uint objectId, bool bodyPart,
                bool ownedByPartRestorationUnit) {
            ObjectId = objectId;
            BodyPart = bodyPart;
            OwnedByPartRestorationUnit = ownedByPartRestorationUnit;
        }

        public uint ObjectId { get; }
        public bool BodyPart { get; }
        public bool OwnedByPartRestorationUnit { get; }
    }

    public sealed class PartRestorationObjectSnapshot {
        internal PartRestorationObjectSnapshot(
                uint objectId, bool bodyPart, bool repairableCondition,
                bool ownedByPartRestorationUnit, bool availableUnit,
                bool yieldedToIntelligentRecycling,
                bool wouldClaimUnderPolicy,
                bool releasedForExternalAutomation,
                bool canCurrentlyBeHauled) {
            ObjectId = objectId;
            BodyPart = bodyPart;
            RepairableCondition = repairableCondition;
            OwnedByPartRestorationUnit = ownedByPartRestorationUnit;
            AvailableUnit = availableUnit;
            YieldedToIntelligentRecycling =
                yieldedToIntelligentRecycling;
            WouldClaimUnderPolicy = wouldClaimUnderPolicy;
            ReleasedForExternalAutomation =
                releasedForExternalAutomation;
            CanCurrentlyBeHauled = canCurrentlyBeHauled;
        }

        public uint ObjectId { get; }
        public bool BodyPart { get; }
        public bool RepairableCondition { get; }
        public bool OwnedByPartRestorationUnit { get; }
        public bool AvailableUnit { get; }
        public bool YieldedToIntelligentRecycling { get; }
        public bool WouldClaimUnderPolicy { get; }
        public bool ReleasedForExternalAutomation { get; }
        public bool CanCurrentlyBeHauled { get; }
    }
}
