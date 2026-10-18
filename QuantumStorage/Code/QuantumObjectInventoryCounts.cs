using System;
using System.Collections.Generic;
using Game.Components;
using Game.Constants;
using Game.Systems.Items;

namespace Game.Systems.QuantumStorage
{
    public sealed partial class QuantumObjectStorageNetworkSys
    {
        private readonly HashSet<QuantumObjectStorageNetwork> publishingCounts = new HashSet<QuantumObjectStorageNetwork>();
        private readonly HashSet<QuantumObjectStorageNetwork> pendingCountPublications = new HashSet<QuantumObjectStorageNetwork>();

        internal QuantumObjectMutationState BeginObjectMutation(ObjData record, ObjStorageComp destination = null)
        {
            if (record == null) { return null; }
            TryGetStoredObjectNetwork(record, null, out var before);
            QuantumObjectStorageNetwork requested = null;
            if (destination != null) { bindings.TryGetValue(destination, out requested); }
            if (before == null && requested == null) { return null; }
            CheckCountBeforeMutation(before, record);
            if (!ReferenceEquals(before, requested)) { CheckCountBeforeMutation(requested, record); }
            return new QuantumObjectMutationState
            {
                System = this, Network = before, Destination = requested, Record = record,
                PreviousStorageId = record.StorageId, RequestedStorageId = destination?.EntityId ?? 0
            };
        }

        private bool BelongsTo(ObjData record, QuantumObjectStorageNetwork network)
        {
            return record.State == ObjState.Stored &&
                networksByStorageId.TryGetValue(record.StorageId, out var owner) && ReferenceEquals(owner, network);
        }

        private void CheckCountBeforeMutation(QuantumObjectStorageNetwork network, ObjData record)
        {
            if (network == null) { return; }
            bool tracked = network.CountedObjects.TryGetValue(record.Id, out var existing);
            bool mismatch = tracked != BelongsTo(record, network) || (tracked && !ReferenceEquals(existing, record));
            // Native counters can differ temporarily inside a bulk operation or
            // an UpdateStats callback. Outside those scopes they must agree.
            if (!bulkMutationDepths.ContainsKey(network) && !publishingCounts.Contains(network))
            {
                foreach (var node in network.Nodes)
                {
                    if (node.UsedCapacity != network.UsedCount || node.MaxCapacity != network.Capacity ||
                        node.IsFull != (network.UsedCount >= network.Capacity)) { mismatch = true; break; }
                }
            }
            if (!network.CountValid || mismatch) { Refresh(network); }
        }

        internal void CompleteObjectMutation(QuantumObjectMutationState mutation, bool failed)
        {
            var record = mutation.Record;
            TryGetStoredObjectNetwork(record, null, out var after);
            ObserveMembership(mutation.Network, record, failed);
            if (!ReferenceEquals(mutation.Destination, mutation.Network)) { ObserveMembership(mutation.Destination, record, failed); }
            if (!ReferenceEquals(after, mutation.Network) && !ReferenceEquals(after, mutation.Destination)) { ObserveMembership(after, record, failed); }
            PublishCount(mutation.Network);
            if (!ReferenceEquals(mutation.Destination, mutation.Network)) { PublishCount(mutation.Destination); }
            if (!ReferenceEquals(after, mutation.Network) && !ReferenceEquals(after, mutation.Destination)) { PublishCount(after); }
        }

        private void ObserveMembership(QuantumObjectStorageNetwork network, ObjData record, bool failed)
        {
            if (network == null) { return; }
            if (failed || !network.CountValid ||
                (network.CountedObjects.TryGetValue(record.Id, out var prior) && !ReferenceEquals(prior, record)))
            {
                // May have committed despite a false result/exception. Recover
                // from real records, never assume success or undo Core state.
                CountStored(network);
                return;
            }
            if (BelongsTo(record, network)) { network.CountedObjects[record.Id] = record; }
            else { network.CountedObjects.Remove(record.Id); }
        }

        private void PublishCount(QuantumObjectStorageNetwork network)
        {
            if (network == null) { return; }
            // Full-state admission must stay current even while UI updates are
            // deferred. Core remains responsible for performing the mutation.
            network.RefreshFullState();
            if (bulkMutationDepths.ContainsKey(network)) { dirtyBulkMutationNetworks.Add(network); return; }
            if (!publishingCounts.Add(network)) { pendingCountPublications.Add(network); return; }
            try
            {
                do
                {
                    pendingCountPublications.Remove(network);
                    if (!network.CountValid) { break; }
                    network.RefreshAll(network.UsedCount);
                }
                while (pendingCountPublications.Contains(network));
            }
            catch
            {
                network.CountValid = false;
                network.RefreshFullState();
                throw;
            }
            finally { publishingCounts.Remove(network); pendingCountPublications.Remove(network); }
        }

        internal void ReconcileForEnumeration(ObjStorageComp storage)
        {
            if (storage != null && bindings.TryGetValue(storage, out var network) &&
                !bulkMutationDepths.ContainsKey(network) && !publishingCounts.Contains(network))
            {
                // Infrequent inspection/bulk entry is also a recovery boundary
                // for unannounced writes by Core/other mods. Recount BEFORE Core
                // rents its enumeration buffer, so an undercount cannot truncate
                // Eject All or overwrite the displayed count with a partial list.
                Refresh(network);
            }
        }

        internal void ReconcileCounts()
        {
            foreach (var network in networks.Values) { Refresh(network); }
        }
    }
}
