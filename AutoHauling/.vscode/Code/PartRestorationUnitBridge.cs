using System;
using System.Reflection;

namespace Game.Systems.AutoHauling {
    /// <summary>
    /// Optional, read-only reflection bridge. Auto Hauling never loads the
    /// Part Restoration Unit assembly and never mutates its tasks or policy.
    /// </summary>
    internal sealed class PartRestorationUnitBridge {
        private const int SupportedContractVersion = 1;
        private const string ApiTypeName =
            "Game.Systems.PartRestoration.PartRestorationApi";
        private const string SnapshotTypeName =
            "Game.Systems.PartRestoration.PartRestorationObjectSnapshot";

        private MethodInfo tryGetObjectSnapshot;
        private PropertyInfo contractVersion;
        private PropertyInfo releasedForExternalAutomation;
        private bool metadataReady;

        internal bool IsInstalled { get; private set; }

        internal bool RefreshAvailability() {
            try {
                if (!metadataReady && !Resolve()) {
                    return false;
                }
                return Convert.ToInt32(
                    contractVersion.GetValue(null, null))
                        == SupportedContractVersion;
            } catch {
                metadataReady = false;
                return false;
            }
        }

        internal bool TryIsReleasedForExternalAutomation(
                uint objectId, out bool released) {
            released = false;
            if (objectId == 0u || !metadataReady) {
                return false;
            }
            try {
                object[] args = { objectId, null };
                bool found = Convert.ToBoolean(
                    tryGetObjectSnapshot.Invoke(null, args));
                if (!found || args[1] == null) {
                    return false;
                }
                released = Convert.ToBoolean(
                    releasedForExternalAutomation.GetValue(args[1], null));
                return true;
            } catch {
                // The published contract grants no permission when a query
                // fails. Invalidate the cached metadata so the next scan can
                // rediscover a repaired or reloaded API.
                metadataReady = false;
                return false;
            }
        }

        private bool Resolve() {
            IsInstalled = false;
            foreach (Assembly assembly in AppDomain.CurrentDomain
                    .GetAssemblies()) {
                string assemblyName;
                try {
                    assemblyName = assembly.GetName().Name;
                } catch {
                    continue;
                }
                if (!string.Equals(assemblyName,
                        "PartRestorationUnit",
                        StringComparison.Ordinal)) {
                    continue;
                }
                IsInstalled = true;
                try {
                    Type api = assembly.GetType(ApiTypeName, false, false);
                    Type snapshot = assembly.GetType(
                        SnapshotTypeName, false, false);
                    if (api == null || snapshot == null) {
                        return false;
                    }
                    contractVersion = api.GetProperty(
                        "ContractVersion",
                        BindingFlags.Public | BindingFlags.Static);
                    tryGetObjectSnapshot = api.GetMethod(
                        "TryGetObjectSnapshot",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(uint), snapshot.MakeByRefType() },
                        null);
                    releasedForExternalAutomation = snapshot.GetProperty(
                        "ReleasedForExternalAutomation",
                        BindingFlags.Public | BindingFlags.Instance);
                    metadataReady = IsReadable(contractVersion, typeof(int))
                        && tryGetObjectSnapshot?.ReturnType == typeof(bool)
                        && tryGetObjectSnapshot.GetParameters().Length == 2
                        && tryGetObjectSnapshot.GetParameters()[1].IsOut
                        && IsReadable(
                            releasedForExternalAutomation, typeof(bool));
                    return metadataReady;
                } catch {
                    metadataReady = false;
                    return false;
                }
            }
            return false;
        }

        private static bool IsReadable(
                PropertyInfo property, Type requiredType) {
            return property != null
                && property.CanRead
                && property.PropertyType == requiredType;
        }
    }
}
