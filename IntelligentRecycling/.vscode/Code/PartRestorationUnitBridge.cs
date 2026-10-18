using System;
using System.Reflection;

namespace Game.Systems.IntelligentRecycling {
    /// <summary>
    /// Optional, read-only ownership bridge. Intelligent Recycling never loads
    /// the Part Restoration Unit assembly and never changes its tasks.
    /// </summary>
    internal sealed class PartRestorationUnitBridge {
        private const string ApiTypeName =
            "Game.Systems.PartRestoration.PartRestorationApi";
        private const string SnapshotTypeName =
            "Game.Systems.PartRestoration.PartRestorationOwnershipSnapshot";

        private MethodInfo tryGetOwnershipSnapshot;
        private PropertyInfo contractVersion;
        private PropertyInfo owned;
        private bool metadataReady;

        internal bool RefreshAvailability() {
            if (!metadataReady && !Resolve()) {
                return false;
            }
            try {
                return Convert.ToInt32(
                    contractVersion.GetValue(null, null)) == 1;
            } catch {
                metadataReady = false;
                return false;
            }
        }

        internal bool IsOwned(uint objectId) {
            if (objectId == 0u || !metadataReady) {
                return false;
            }
            try {
                object[] args = { objectId, null };
                bool found = Convert.ToBoolean(
                    tryGetOwnershipSnapshot.Invoke(null, args));
                return found && args[1] != null
                    && Convert.ToBoolean(owned.GetValue(args[1], null));
            } catch {
                metadataReady = false;
                return false;
            }
        }

        private bool Resolve() {
            foreach (Assembly assembly in AppDomain.CurrentDomain
                    .GetAssemblies()) {
                if (!string.Equals(assembly.GetName().Name,
                        "PartRestorationUnit",
                        StringComparison.Ordinal)) {
                    continue;
                }
                Type api = assembly.GetType(ApiTypeName, false, false);
                Type snapshot = assembly.GetType(
                    SnapshotTypeName, false, false);
                if (api == null || snapshot == null) {
                    return false;
                }
                contractVersion = api.GetProperty(
                    "ContractVersion",
                    BindingFlags.Public | BindingFlags.Static);
                tryGetOwnershipSnapshot = api.GetMethod(
                    "TryGetObjectOwnershipSnapshot",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(uint), snapshot.MakeByRefType() },
                    null);
                owned = snapshot.GetProperty(
                    "OwnedByPartRestorationUnit",
                    BindingFlags.Public | BindingFlags.Instance);
                metadataReady = contractVersion?.PropertyType == typeof(int)
                    && tryGetOwnershipSnapshot?.ReturnType == typeof(bool)
                    && owned?.PropertyType == typeof(bool);
                return metadataReady;
            }
            return false;
        }
    }
}
