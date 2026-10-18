using System;
using System.Reflection;

namespace Game.Systems.PartRestoration {
    /// <summary>
    /// Optional, read-only bridge. The Part Restoration Unit never loads or
    /// mutates Intelligent Recycling and yields when its quality policy claims
    /// a body part.
    /// </summary>
    internal static class IntelligentRecyclingBridge {
        private const string ApiTypeName =
            "Game.Systems.IntelligentRecycling.IntelligentRecyclingApi";
        private const string SnapshotTypeName =
            "Game.Systems.IntelligentRecycling."
            + "IntelligentRecyclingObjectSnapshot";

        private static bool resolved;
        private static bool compatible;
        private static MethodInfo tryGetObjectSnapshot;
        private static PropertyInfo wouldClaim;

        internal static bool TryWouldClaim(
                uint objectId, out bool intelligentRecyclingWouldClaim) {
            intelligentRecyclingWouldClaim = false;
            if (objectId == 0u || (!resolved && !Resolve()) || !compatible) {
                return false;
            }

            try {
                object[] args = { objectId, null };
                bool found = Convert.ToBoolean(
                    tryGetObjectSnapshot.Invoke(null, args));
                if (!found || args[1] == null) {
                    return false;
                }
                intelligentRecyclingWouldClaim = Convert.ToBoolean(
                    wouldClaim.GetValue(args[1], null));
                return true;
            } catch {
                compatible = false;
                return false;
            }
        }

        private static bool Resolve() {
            resolved = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain
                    .GetAssemblies()) {
                if (!string.Equals(assembly.GetName().Name,
                        "IntelligentRecycling",
                        StringComparison.Ordinal)) {
                    continue;
                }

                Type api = assembly.GetType(ApiTypeName, false, false);
                Type snapshot = assembly.GetType(
                    SnapshotTypeName, false, false);
                PropertyInfo contract = api?.GetProperty(
                    "ContractVersion",
                    BindingFlags.Public | BindingFlags.Static);
                if (api == null || snapshot == null || contract == null
                        || Convert.ToInt32(contract.GetValue(null, null)) != 1) {
                    return false;
                }

                tryGetObjectSnapshot = api.GetMethod(
                    "TryGetObjectSnapshot",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(uint), snapshot.MakeByRefType() },
                    null);
                wouldClaim = snapshot.GetProperty(
                    "IntelligentRecyclingWouldClaimUnderPolicy",
                    BindingFlags.Public | BindingFlags.Instance);
                compatible = tryGetObjectSnapshot != null
                    && tryGetObjectSnapshot.ReturnType == typeof(bool)
                    && wouldClaim != null
                    && wouldClaim.CanRead
                    && wouldClaim.PropertyType == typeof(bool);
                return compatible;
            }
            return false;
        }
    }
}
