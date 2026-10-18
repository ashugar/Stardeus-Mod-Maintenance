using System;
using System.Reflection;

namespace Game.Systems.AutoHauling {
    internal enum IntelligentRecyclingCompatibilityStatus {
        NotInstalled,
        Initializing,
        Ready,
        UnsupportedContract,
        PerObjectDispositionUnavailable
    }

    /// <summary>
    /// Optional, read-only reflection bridge to Intelligent Recycling.
    /// Auto Hauling never loads the assembly and never reproduces its policy.
    /// </summary>
    internal sealed class IntelligentRecyclingBridge {
        internal const int SupportedContractVersion = 1;
        internal const long PerObjectDispositionsCapability = 128L;

        private const string ApiTypeName =
            "Game.Systems.IntelligentRecycling.IntelligentRecyclingApi";
        private const string SnapshotTypeName =
            "Game.Systems.IntelligentRecycling."
            + "IntelligentRecyclingObjectSnapshot";

        private PropertyInfo contractVersionProperty;
        private MethodInfo getApiInfoMethod;
        private MethodInfo tryGetObjectSnapshotMethod;
        private PropertyInfo infoContractVersionProperty;
        private PropertyInfo capabilitiesProperty;
        private PropertyInfo systemInitializedProperty;
        private PropertyInfo gameStateLoadedProperty;
        private PropertyInfo releasedForAutoHaulingProperty;
        private PropertyInfo canCurrentlyBeHauledProperty;
        private bool metadataReady;

        internal IntelligentRecyclingCompatibilityStatus Status {
            get;
            private set;
        } = IntelligentRecyclingCompatibilityStatus.NotInstalled;

        internal string StatusText {
            get {
                switch (Status) {
                    case IntelligentRecyclingCompatibilityStatus.Ready:
                        return "Ready - contract 1";
                    case IntelligentRecyclingCompatibilityStatus.Initializing:
                        return "Initializing";
                    case IntelligentRecyclingCompatibilityStatus
                            .UnsupportedContract:
                        return "Unsupported contract";
                    case IntelligentRecyclingCompatibilityStatus
                            .PerObjectDispositionUnavailable:
                        return "Per-object disposition unavailable";
                    default:
                        return "Not installed";
                }
            }
        }

        internal bool RefreshAvailability() {
            try {
                // Discovery is intentionally inside the guard. A malformed or
                // partially reloaded optional assembly must disable only the
                // quality lane, never escape into Auto Hauling's tick.
                if (!metadataReady && !TryResolveMetadata()) {
                    return false;
                }
                int contractVersion = Convert.ToInt32(
                    contractVersionProperty.GetValue(null, null));
                if (contractVersion != SupportedContractVersion) {
                    Status = IntelligentRecyclingCompatibilityStatus
                        .UnsupportedContract;
                    return false;
                }

                object info = getApiInfoMethod.Invoke(null, null);
                if (info == null) {
                    Status = IntelligentRecyclingCompatibilityStatus
                        .PerObjectDispositionUnavailable;
                    return false;
                }

                int infoContractVersion = Convert.ToInt32(
                    infoContractVersionProperty.GetValue(info, null));
                if (infoContractVersion != SupportedContractVersion) {
                    Status = IntelligentRecyclingCompatibilityStatus
                        .UnsupportedContract;
                    return false;
                }

                long capabilities = Convert.ToInt64(
                    capabilitiesProperty.GetValue(info, null));
                if ((capabilities & PerObjectDispositionsCapability)
                        != PerObjectDispositionsCapability) {
                    Status = IntelligentRecyclingCompatibilityStatus
                        .PerObjectDispositionUnavailable;
                    return false;
                }

                bool systemInitialized = Convert.ToBoolean(
                    systemInitializedProperty.GetValue(info, null));
                bool gameStateLoaded = Convert.ToBoolean(
                    gameStateLoadedProperty.GetValue(info, null));
                if (!systemInitialized || !gameStateLoaded) {
                    Status = IntelligentRecyclingCompatibilityStatus
                        .Initializing;
                    return false;
                }

                Status = IntelligentRecyclingCompatibilityStatus.Ready;
                return true;
            } catch {
                metadataReady = false;
                Status = IntelligentRecyclingCompatibilityStatus
                    .PerObjectDispositionUnavailable;
                return false;
            }
        }

        internal bool TryAllowsAutoHauling(uint objectId) {
            if (Status != IntelligentRecyclingCompatibilityStatus.Ready
                    || objectId == 0u) {
                return false;
            }

            try {
                object[] arguments = { objectId, null };
                bool found = Convert.ToBoolean(
                    tryGetObjectSnapshotMethod.Invoke(null, arguments));
                object snapshot = arguments[1];
                if (!found || snapshot == null) {
                    return false;
                }

                bool released = Convert.ToBoolean(
                    releasedForAutoHaulingProperty.GetValue(snapshot, null));
                bool currentlyHaulable = Convert.ToBoolean(
                    canCurrentlyBeHauledProperty.GetValue(snapshot, null));
                return released && currentlyHaulable;
            } catch {
                // A failed per-object call is deliberately silent and
                // fail-closed. The next scan can re-check API availability.
                metadataReady = false;
                Status = IntelligentRecyclingCompatibilityStatus
                    .PerObjectDispositionUnavailable;
                return false;
            }
        }

        private bool TryResolveMetadata() {
            Assembly apiAssembly = null;
            Type apiType = null;
            bool intelligentRecyclingAssemblyFound = false;
            foreach (Assembly assembly in AppDomain.CurrentDomain
                    .GetAssemblies()) {
                if (!string.Equals(assembly.GetName().Name,
                        "IntelligentRecycling",
                        StringComparison.Ordinal)) {
                    continue;
                }
                intelligentRecyclingAssemblyFound = true;
                Type candidate;
                try {
                    candidate = assembly.GetType(
                        ApiTypeName, false, false);
                } catch {
                    continue;
                }
                if (candidate == null) {
                    continue;
                }
                apiAssembly = assembly;
                apiType = candidate;
                break;
            }

            if (apiType == null || apiAssembly == null) {
                Status = intelligentRecyclingAssemblyFound
                    ? IntelligentRecyclingCompatibilityStatus
                        .PerObjectDispositionUnavailable
                    : IntelligentRecyclingCompatibilityStatus.NotInstalled;
                return false;
            }

            try {
                Type snapshotType = apiAssembly.GetType(
                    SnapshotTypeName, false, false);
                contractVersionProperty = apiType.GetProperty(
                    "ContractVersion", BindingFlags.Public
                        | BindingFlags.Static);
                getApiInfoMethod = apiType.GetMethod(
                    "GetApiInfo", BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (snapshotType == null || contractVersionProperty == null
                        || !IsReadable(
                            contractVersionProperty, typeof(int))
                        || getApiInfoMethod == null
                        || getApiInfoMethod.ReturnType == typeof(void)) {
                    return MarkDispositionUnavailable();
                }

                tryGetObjectSnapshotMethod = apiType.GetMethod(
                    "TryGetObjectSnapshot",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(uint), snapshotType.MakeByRefType() },
                    null);
                if (tryGetObjectSnapshotMethod == null
                        || tryGetObjectSnapshotMethod.ReturnType
                            != typeof(bool)
                        || !tryGetObjectSnapshotMethod.GetParameters()[1]
                            .IsOut) {
                    return MarkDispositionUnavailable();
                }

                Type infoType = getApiInfoMethod.ReturnType;
                infoContractVersionProperty = infoType.GetProperty(
                    "ContractVersion", BindingFlags.Public
                        | BindingFlags.Instance);
                capabilitiesProperty = infoType.GetProperty(
                    "Capabilities", BindingFlags.Public
                        | BindingFlags.Instance);
                systemInitializedProperty = infoType.GetProperty(
                    "SystemInitialized", BindingFlags.Public
                        | BindingFlags.Instance);
                gameStateLoadedProperty = infoType.GetProperty(
                    "GameStateLoaded", BindingFlags.Public
                        | BindingFlags.Instance);
                releasedForAutoHaulingProperty = snapshotType.GetProperty(
                    "ReleasedForAutoHauling", BindingFlags.Public
                        | BindingFlags.Instance);
                canCurrentlyBeHauledProperty = snapshotType.GetProperty(
                    "CanCurrentlyBeHauled", BindingFlags.Public
                        | BindingFlags.Instance);

                if (!IsReadable(infoContractVersionProperty, typeof(int))
                        || !IsReadable(capabilitiesProperty)
                        || !IsReadable(systemInitializedProperty,
                            typeof(bool))
                        || !IsReadable(gameStateLoadedProperty, typeof(bool))
                        || !IsReadable(releasedForAutoHaulingProperty,
                            typeof(bool))
                        || !IsReadable(canCurrentlyBeHauledProperty,
                            typeof(bool))) {
                    return MarkDispositionUnavailable();
                }

                metadataReady = true;
                return true;
            } catch {
                return MarkDispositionUnavailable();
            }
        }

        private bool MarkDispositionUnavailable() {
            metadataReady = false;
            Status = IntelligentRecyclingCompatibilityStatus
                .PerObjectDispositionUnavailable;
            return false;
        }

        private static bool IsReadable(PropertyInfo property) {
            return property != null && property.CanRead;
        }

        private static bool IsReadable(
                PropertyInfo property, Type requiredType) {
            return IsReadable(property) && property.PropertyType == requiredType;
        }
    }
}
