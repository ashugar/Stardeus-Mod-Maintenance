using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Creatures;
using Game.Systems.Inventory;
using Game.Systems.QuantumStorage.Relays;

namespace Game.Systems.QuantumStorage
{
    internal static class RelayMaterialQuery
    {
        private static readonly FieldInfo nonPileStorages = FindNonPileStorages();
        private static bool fastCandidatesAvailable = nonPileStorages != null;
        internal static bool FastCandidatesAvailable => fastCandidatesAvailable;

        private static List<IMatStorage> ReadNonPileStorages(InventorySys inventory)
        {
            if (!fastCandidatesAvailable) { return null; }
            try { return nonPileStorages.GetValue(inventory) as List<IMatStorage>; }
            catch (MemberAccessException) { fastCandidatesAvailable = false; return null; }
        }

        private static FieldInfo FindNonPileStorages()
        {
            var field = typeof(InventorySys).GetField("nonPileStorages", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            return field != null && !field.IsStatic && field.IsInitOnly && field.FieldType == typeof(List<IMatStorage>) ? field : null;
        }

        internal static bool HasEnough(InventorySys inventory, MatType type, int amount, int islandId, Being being)
        {
#if QUANTUM_RELAY_QUERY_BASELINE
            if (RelayQueryBaseline.TryBeginQuery(out var sample))
                return SampleQuery(inventory, type, amount, islandId, being, sample);
#endif
            var source = new Source(inventory, type, islandId, being);
            return MaterialQueryEngine.HasEnough<IMatStorage, Source>(ref source, amount);
        }

#if QUANTUM_RELAY_QUERY_BASELINE
        private static bool SampleQuery(InventorySys inventory, MatType type, int amount, int islandId, Being being,
            RelayQueryBaseline.QuerySample sample)
        {
            bool failed = true;
            try
            {
                var source = new Source(inventory, type, islandId, being);
                RelayQueryFocus.ArmEngine();
                bool result = MaterialQueryEngine.HasEnough<IMatStorage, Source>(ref source, amount);
                failed = false;
                return result;
            }
            finally { RelayQueryBaseline.EndQuery(sample, failed); }
        }
#endif

        private struct Source : IMaterialQuerySource<IMatStorage>
        {
            private readonly InventorySys inventory;
            private readonly MatType type;
            private readonly int islandId;
            private readonly Being being;
            private readonly BeingSafety safety;
            private readonly StorageRelaySys relays;
            private readonly QuantumStorageNetworkSys quantum;
            private readonly bool advisory;
            private List<IMatStorage> stores;
            private List<UnstoredMatComp> piles;
            private StorageRelaySys.MaterialReadScope scope;

            internal Source(InventorySys inventory, MatType type, int islandId, Being being)
            {
                this.inventory = inventory; this.type = type; this.islandId = islandId; this.being = being;
                safety = being?.Data.Safety;
                relays = StorageRelaySys.Get(inventory.S);
                quantum = QuantumStorageNetworkSys.Get(inventory.S);
                advisory = islandId == -1 && being == null && NanobotCandidateQueries.IsAdvisory(inventory);
                stores = null; piles = null; scope = null;
            }

            public bool BeginAttempt()
            {
                // Reflection is cached and bounded to one field read per attempt.
                // An uncertain private-field contract uses the original broad
                // domain with this same Relay-aware policy/deduplication loop.
                stores = ReadNonPileStorages(inventory);
                if (stores == null) { stores = inventory.Storages; piles = null; }
                else { piles = inventory.Piles.Of(type); }
                // Keep an ambient dependency witness for indirect provider reads;
                // ordinary-only answers do not need a whole-registry walk.
                scope = relays == null ? null : advisory && NanobotCandidateQueries.CanUseAdvisory(inventory) ?
                    relays.BeginAdvisoryMaterialRead() : relays.BeginDeferredMaterialRead();
                return scope == null || scope.Valid;
            }

            public int Count => stores.Count + (piles?.Count ?? 0);
            public IMatStorage Candidate(int index)
            {
                return index < stores.Count ? stores[index] : piles[index - stores.Count];
            }

            public bool IsEligible(IMatStorage storage)
            {
                if (storage.StorageType == StorageType.StoreOnly || InventorySys.IsForbiddenFor(storage, being)) { return false; }
                int port = storage.OutPort;
                return (islandId == -1 || inventory.S.Sys.Areas.CanReachGroundedIslandToPos(islandId, port)) &&
                    (safety == null || safety.IgnoreDanger || safety.IsActuallySafeAt(port));
            }

            public object PoolKey(IMatStorage storage)
            {
#if QUANTUM_RELAY_QUERY_BASELINE
                if (RelayQueryFocus.TryEnterPoolProbe()) { return PoolKeyProfiled(storage); }
#endif
                if (storage is MatStorageComp material)
                {
                    if (StorageRelaySys.IsRelay(material)) { return (object)scope?.Pool(material) ?? material; }
                    if (quantum?.TryGetNetwork(material, out var network) == true) { return network; }
                }
                return storage;
            }

#if QUANTUM_RELAY_QUERY_BASELINE
            private object PoolKeyProfiled(IMatStorage storage)
            {
                if (storage is MatStorageComp material)
                {
                    bool relay;
                    long token = RelayQueryFocus.BeginStage(10);
                    try { relay = StorageRelaySys.IsRelay(material); }
                    finally { RelayQueryFocus.EndStage(10, token); }
                    if (relay)
                    {
                        RelayQueryFocus.NotePoolBranch(1);
                        object group = null;
                        token = RelayQueryFocus.BeginStage(11);
                        try
                        {
                            if (scope != null) { RelayQueryFocus.ArmRelayPoolProbe(); group = scope.Pool(material); }
                            else { RelayQueryFocus.NotePoolBranch(3); }
                        }
                        finally { RelayQueryFocus.LeaveRelayPoolProbe(); RelayQueryFocus.EndStage(11, token); }
                        RelayQueryFocus.NotePoolBranch(group != null ? 4 : 5);
                        return group ?? material;
                    }
                    QuantumStorageNetwork network = null;
                    bool found;
                    token = RelayQueryFocus.BeginStage(11);
                    try { found = quantum?.TryGetNetwork(material, out network) == true; }
                    finally { RelayQueryFocus.EndStage(11, token); }
                    if (found) { RelayQueryFocus.NotePoolBranch(2); return network; }
                }
                RelayQueryFocus.NotePoolBranch(0);
                return storage;
            }
#endif

            // Retain native/other-mod provider hooks and provider-specific
            // reservation semantics rather than substituting another calculator.
            public int Available(IMatStorage storage) => storage.Available(type);
            public bool ValidateAttempt() => scope == null || scope.Validate();
            public void EndAttempt() { scope?.Dispose(); scope = null; }
        }
    }
}
