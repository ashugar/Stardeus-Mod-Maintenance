using System;
using System.Collections.Generic;
using System.Linq;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Items;
using Game.Systems.Notify;
using Game.Utils;
using KL.Grid;
using KL.Utils;

namespace Game.Systems.QuantumStorage
{
    public sealed partial class QuantumObjectStorageNetworkSys : GameSystem, ISaveable
    {
        public const string SysId = "QuantumObjectStorageNetworkSys";

        private const int SaveSchema = 1;
        private readonly Dictionary<string, QuantumObjectStorageNetwork> networks = new Dictionary<string, QuantumObjectStorageNetwork>(StringComparer.Ordinal);
        private readonly Dictionary<ObjStorageComp, QuantumObjectStorageNetwork> bindings = new Dictionary<ObjStorageComp, QuantumObjectStorageNetwork>();
        private readonly Dictionary<int, QuantumObjectStorageNetwork> networksByStorageId = new Dictionary<int, QuantumObjectStorageNetwork>();
        private readonly Dictionary<QuantumObjectStorageNetwork, int> bulkMutationDepths = new Dictionary<QuantumObjectStorageNetwork, int>();
        private readonly HashSet<QuantumObjectStorageNetwork> dirtyBulkMutationNetworks = new HashSet<QuantumObjectStorageNetwork>();
        private readonly HashSet<int> fireIncidentSources = new HashSet<int>();
        private readonly List<ObjStorageComp> feedbackTargets = new List<ObjStorageComp>();
        private ComponentData data;
        private bool applyingFeedback;

        public override string Id => SysId;

        private ComponentData Data => data ?? (data = new ComponentData(0, Id));

        internal bool IsApplyingFeedback => applyingFeedback;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register()
        {
            GameSystems.Register(SysId, () => new QuantumObjectStorageNetworkSys());
        }

        protected override void OnInitialize()
        {
        }

        public override void Unload()
        {
            bindings.Clear();
            networksByStorageId.Clear();
            networks.Clear();
            bulkMutationDepths.Clear();
            dirtyBulkMutationNetworks.Clear();
            publishingCounts.Clear();
            pendingCountPublications.Clear();
            fireIncidentSources.Clear();
            feedbackTargets.Clear();
            applyingFeedback = false;
            data = null;
        }

        public ComponentData Save()
        {
            ComponentData result = Data;
            result.SetInt("Schema", SaveSchema);
            result.SetIntSet("FireIncidentSources", fireIncidentSources.OrderBy(id => id).ToArray());
            return result;
        }

        public void Load(ComponentData loadedData)
        {
            data = loadedData;
            bindings.Clear();
            networksByStorageId.Clear();
            networks.Clear();
            bulkMutationDepths.Clear();
            dirtyBulkMutationNetworks.Clear();
            publishingCounts.Clear();
            pendingCountPublications.Clear();
            fireIncidentSources.Clear();

            int[] fireSources = loadedData?.GetIntSet("FireIncidentSources", null);
            if (fireSources == null)
            {
                return;
            }

            for (int i = 0; i < fireSources.Length; i++)
            {
                fireIncidentSources.Add(fireSources[i]);
            }
        }

        public static QuantumObjectStorageNetworkSys Get(GameState state)
        {
            if (state?.Sys?.Known != null && state.Sys.Known.TryGetValue(SysId, out GameSystem system))
            {
                return system as QuantumObjectStorageNetworkSys;
            }

            return null;
        }

        public static bool TryGetNode(ObjStorageComp storage, out QuantumStorageNodeComp node)
        {
            node = null;
            return storage?.Entity != null && storage.Entity.TryGetComponent(out node);
        }

        public void Attach(ObjStorageComp storage)
        {
            if (storage == null || !storage.Entity.IsActive || !storage.Tile.IsConstructed || !TryGetNode(storage, out QuantumStorageNodeComp node))
            {
                return;
            }

            if (storage.Entity.TryGetComponent(out ObjHaulTargetComp haulTarget))
            {
                if (storage.HaulTarget == null)
                {
                    storage.HaulTarget = haulTarget;
                }
                if (haulTarget.Storage == null)
                {
                    haulTarget.Storage = storage;
                }
            }

            QuantumObjectStorageNetwork network = GetOrCreateNetwork(node.NetworkId);
            if (bindings.TryGetValue(storage, out QuantumObjectStorageNetwork oldNetwork) && oldNetwork != network)
            {
                oldNetwork.Detach(storage);
                networksByStorageId.Remove(storage.EntityId);
                Refresh(oldNetwork);
            }

            bindings[storage] = network;
            networksByStorageId[storage.EntityId] = network;
            network.Attach(storage, node);
            if (storage.Entity.Flammable?.IsOnFire != true)
            {
                fireIncidentSources.Remove(storage.EntityId);
            }

            Refresh(network);
            QuantumNanobotObjectHauling.InvalidateNetwork(network.Id);
        }

        internal bool TryGetNetwork(ObjStorageComp storage, out QuantumObjectStorageNetwork network)
        {
            return bindings.TryGetValue(storage, out network);
        }

        internal bool TryGetNetwork(int storageId, out QuantumObjectStorageNetwork network)
        {
            return networksByStorageId.TryGetValue(storageId, out network);
        }

        internal bool TryGetNetwork(string networkId, out QuantumObjectStorageNetwork network)
        {
            network = null;
            return !string.IsNullOrWhiteSpace(networkId) && networks.TryGetValue(networkId, out network);
        }

        // ObjData.StorageId is the single Core-owned serialization anchor for an object.
        // It identifies the network, not the only endpoint through which that object can
        // be accessed. Physical consumers must resolve an access point through this API.
        internal bool TryGetStoredObjectNetwork(
            ObjData objData,
            string expectedNetworkId,
            out QuantumObjectStorageNetwork network)
        {
            network = null;
            return objData != null && objData.State == ObjState.Stored && objData.StorageId != 0 &&
                networksByStorageId.TryGetValue(objData.StorageId, out network) &&
                (string.IsNullOrEmpty(expectedNetworkId) ||
                    string.Equals(network.Id, expectedNetworkId, StringComparison.Ordinal));
        }

        internal bool IsStoredObjectAccessibleAt(
            ObjData objData,
            string expectedNetworkId,
            ObjStorageComp accessPoint)
        {
            return TryGetStoredObjectNetwork(objData, expectedNetworkId, out QuantumObjectStorageNetwork network) &&
                IsLiveAccessPoint(network, accessPoint);
        }

        internal bool TrySelectStoredObjectAccessPoint(
            ObjData objData,
            string expectedNetworkId,
            ref int cursor,
            int maxChecks,
            Func<ObjStorageComp, bool> predicate,
            Func<ObjStorageComp, float> score,
            out ObjStorageComp accessPoint)
        {
            accessPoint = null;
            return TryGetStoredObjectNetwork(objData, expectedNetworkId, out QuantumObjectStorageNetwork network) &&
                TrySelectAccessPoint(network, ref cursor, maxChecks, predicate, score, out accessPoint);
        }

        internal bool TrySelectNearestStoredObjectAccessPoint(
            ObjData objData,
            string expectedNetworkId,
            int anchorPos,
            Func<ObjStorageComp, bool> predicate,
            out ObjStorageComp accessPoint)
        {
            accessPoint = null;
            if (!EntityUtils.IsWithinBounds(anchorPos) ||
                !TryGetStoredObjectNetwork(objData, expectedNetworkId, out QuantumObjectStorageNetwork network))
            {
                return false;
            }

            int cursor = 0;
            return TrySelectAccessPoint(
                network,
                ref cursor,
                network.NodeCount,
                predicate,
                storage => Pos.SqrDistance(anchorPos, storage.InPort),
                out accessPoint);
        }

        internal bool TrySelectNetworkAccessPoint(
            string networkId,
            ref int cursor,
            int maxChecks,
            Func<ObjStorageComp, bool> predicate,
            Func<ObjStorageComp, float> score,
            out ObjStorageComp accessPoint)
        {
            accessPoint = null;
            return TryGetNetwork(networkId, out QuantumObjectStorageNetwork network) &&
                TrySelectAccessPoint(network, ref cursor, maxChecks, predicate, score, out accessPoint);
        }

        private static bool TrySelectAccessPoint(
            QuantumObjectStorageNetwork network,
            ref int cursor,
            int maxChecks,
            Func<ObjStorageComp, bool> predicate,
            Func<ObjStorageComp, float> score,
            out ObjStorageComp accessPoint)
        {
            accessPoint = null;
            int count = network?.NodeCount ?? 0;
            if (count == 0 || maxChecks < 1)
            {
                cursor = 0;
                return false;
            }

            if (cursor < 0 || cursor >= count)
            {
                cursor = 0;
            }

            int checks = Math.Min(maxChecks, count);
            float bestScore = float.MaxValue;
            for (int i = 0; i < checks; i++)
            {
                ObjStorageComp candidate = network.NodeAt(cursor++);
                if (cursor >= count)
                {
                    cursor = 0;
                }
                if (!IsLiveAccessPoint(network, candidate) || predicate?.Invoke(candidate) == false)
                {
                    continue;
                }

                if (score == null)
                {
                    accessPoint = candidate;
                    return true;
                }

                float candidateScore = score(candidate);
                if (accessPoint == null || candidateScore < bestScore ||
                    (Math.Abs(candidateScore - bestScore) < 0.0001f &&
                        candidate.EntityId < accessPoint.EntityId))
                {
                    accessPoint = candidate;
                    bestScore = candidateScore;
                }
            }
            return accessPoint != null;
        }

        private static bool IsLiveAccessPoint(
            QuantumObjectStorageNetwork network,
            ObjStorageComp accessPoint)
        {
            return network != null && accessPoint != null && network.Contains(accessPoint) &&
                accessPoint.Entity?.IsActive == true && accessPoint.Tile?.IsActive == true &&
                accessPoint.Tile.IsConstructed;
        }

        internal void PrepareRemove(ObjStorageComp storage)
        {
            if (!bindings.TryGetValue(storage, out QuantumObjectStorageNetwork network))
            {
                return;
            }

            ReinstallableComp reinstallable = storage.Tile.Reinstallable;
            bool relocating = reinstallable != null && (reinstallable.IsMidRelocation || reinstallable.IsPending);
            if (relocating || network.IsLast(storage))
            {
                return;
            }

            ObjStorageComp target = network.RehomeTarget(storage);
            if (target != null)
            {
                RehomeOwnedObjects(storage.EntityId, target.EntityId);
            }
        }

        internal void CompleteRemove(ObjStorageComp storage)
        {
            if (!bindings.TryGetValue(storage, out QuantumObjectStorageNetwork network))
            {
                return;
            }

            bindings.Remove(storage);
            networksByStorageId.Remove(storage.EntityId);
            fireIncidentSources.Remove(storage.EntityId);
            network.Detach(storage);
            Refresh(network);
            QuantumNanobotObjectHauling.InvalidateNetwork(network.Id);
        }

        internal bool Relocate(ObjStorageComp source, Entity targetEntity)
        {
            if (!bindings.TryGetValue(source, out QuantumObjectStorageNetwork network) ||
                targetEntity == null || !targetEntity.TryGetComponent(out ObjStorageComp target))
            {
                return false;
            }

            int usedCapacity = CountStored(network);
            RehomeOwnedObjects(source.EntityId, target.EntityId);
            target.MaxCapacity = network.Capacity;
            target.UsedCapacity = usedCapacity;
            // Relocation can move ownership to a not-yet-attached endpoint.
            // Reconcile both sides from Core after the ownership change.
            Refresh(network);
            RefreshFor(target);
            QuantumNanobotObjectHauling.InvalidateNetwork(network.Id);
            return true;
        }

        internal void PrepareCrossEndpointUnstore(ObjData objData, ObjStorageComp requestedStorage)
        {
            if (objData == null || requestedStorage == null ||
                !bindings.TryGetValue(requestedStorage, out QuantumObjectStorageNetwork requestedNetwork) ||
                !TryGetStoredObjectNetwork(objData, null, out QuantumObjectStorageNetwork owningNetwork) ||
                !ReferenceEquals(requestedNetwork, owningNetwork))
            {
                return;
            }

            objData.StorageId = requestedStorage.EntityId;
        }

        internal void RefreshFor(ObjStorageComp storage)
        {
            if (storage != null && bindings.TryGetValue(storage, out QuantumObjectStorageNetwork network))
            {
                Refresh(network);
            }
        }

        internal QuantumObjectStorageNetwork BeginBulkMutation(ObjStorageComp storage)
        {
            if (storage == null || !bindings.TryGetValue(storage, out QuantumObjectStorageNetwork network))
            {
                return null;
            }

            bulkMutationDepths.TryGetValue(network, out int depth);
            if (depth == 0)
            {
                Refresh(network);
                dirtyBulkMutationNetworks.Remove(network);
            }
            bulkMutationDepths[network] = depth + 1;
            return network;
        }

        internal void EndBulkMutation(QuantumObjectStorageNetwork network)
        {
            if (network == null || !bulkMutationDepths.TryGetValue(network, out int depth))
            {
                return;
            }

            if (depth > 1)
            {
                bulkMutationDepths[network] = depth - 1;
                return;
            }

            bulkMutationDepths.Remove(network);
            if (dirtyBulkMutationNetworks.Remove(network))
            {
                if (!network.CountValid) { CountStored(network); }
                PublishCount(network);
            }
        }

        internal void RefreshAfterMutation(QuantumObjectStorageNetwork network)
        {
            if (network == null)
            {
                return;
            }

            // Legacy/external notification carries no exact object identity.
            // Keep it as a recovery boundary, not a guessed incremental delta.
            if (bulkMutationDepths.ContainsKey(network))
            {
                network.CountValid = false;
                network.RefreshFullState();
                dirtyBulkMutationNetworks.Add(network);
                return;
            }
            Refresh(network);
        }

        internal void Refresh(QuantumObjectStorageNetwork network)
        {
            if (network != null)
            {
                CountStored(network);
                PublishCount(network);
            }
        }

        internal int CountOwnedBy(int storageId)
        {
            int count = 0;
            Dictionary<uint, ObjData> all = S.Query.GetAllObjs.Ask();
            if (all == null)
            {
                return 0;
            }

            foreach (ObjData objData in all.Values)
            {
                if (objData.State == ObjState.Stored && objData.StorageId == storageId)
                {
                    count++;
                }
            }

            return count;
        }

        internal void EndFireIncident(int entityId)
        {
            fireIncidentSources.Remove(entityId);
        }

        internal void HandleDamageFeedback(QuantumObjectStorageNetwork network, ObjStorageComp source, float incomingDamage, DamageType type, bool wasOnFire)
        {
            if (network == null || source == null || incomingDamage <= 0f || applyingFeedback)
            {
                return;
            }

            float chance;
            if (type == DamageType.Heat)
            {
                if (!wasOnFire || !fireIncidentSources.Add(source.EntityId))
                {
                    return;
                }

                chance = network.FireFeedbackChance;
            }
            else if (type == DamageType.Explosive)
            {
                chance = network.ExplosionFeedbackChance;
            }
            else
            {
                return;
            }

            feedbackTargets.Clear();
            foreach (ObjStorageComp candidate in network.Nodes)
            {
                if (candidate != null &&
                    !ReferenceEquals(candidate, source) &&
                    candidate.Entity.IsActive &&
                    candidate.Tile.ENode?.IsPowerable == true &&
                    candidate.Tile.Health != null)
                {
                    feedbackTargets.Add(candidate);
                }
            }

            if (feedbackTargets.Count == 0 || chance <= 0f || !S.Rng.Chance(chance))
            {
                return;
            }

            ObjStorageComp target = feedbackTargets[S.Rng.Range(0, feedbackTargets.Count)];
            if (TryConsumeFuse(source))
            {
                ReportFuseInterception(source, type);
                return;
            }

            float feedbackDamage = incomingDamage * network.FeedbackDamageFraction;
            if (feedbackDamage <= 0f)
            {
                return;
            }

            Tile targetTile = target.Tile;
            applyingFeedback = true;
            try
            {
                targetTile.Health.TakeDamage(feedbackDamage, DamageType.Energy);
            }
            finally
            {
                applyingFeedback = false;
            }

            ReportTransferredDamage(targetTile, feedbackDamage, type);
        }

        private int CountStored(QuantumObjectStorageNetwork network)
        {
            network.CountValid = false;
            network.RefreshFullState();
            Dictionary<uint, ObjData> all = S.Query.GetAllObjs.Ask();
            if (all == null)
            {
                network.RefreshFullState();
                return network.UsedCount;
            }

            var records = new Dictionary<uint, ObjData>();
            foreach (ObjData objData in all.Values)
            {
                if (objData.State == ObjState.Stored &&
                    networksByStorageId.TryGetValue(objData.StorageId, out var owner) && ReferenceEquals(owner, network))
                {
                    records.Add(objData.Id, objData);
                }
            }

            network.CountedObjects = records;
            network.CountValid = true;
            return network.UsedCount;
        }

        private void RehomeOwnedObjects(int sourceStorageId, int targetStorageId)
        {
            Dictionary<uint, ObjData> all = S.Query.GetAllObjs.Ask();
            if (all == null)
            {
                return;
            }

            foreach (ObjData objData in all.Values)
            {
                if (objData.State == ObjState.Stored && objData.StorageId == sourceStorageId)
                {
                    objData.StorageId = targetStorageId;
                }
            }
        }

        private static bool TryConsumeFuse(ObjStorageComp source)
        {
            return source.Entity.TryGetComponent(out WithFuseComp hasFuse) && hasFuse.TryBlowFuse();
        }

        private void ReportFuseInterception(ObjStorageComp source, DamageType type)
        {
            string cause = type == DamageType.Explosive ? "explosion" : "fire";
            string text = "Quantum feedback from " + cause + " damage was absorbed; a Fuse was consumed.";
            Notification.BuildFor(source.Tile, "Icons/Color/Warning", text, 480, NotificationType.Info)
                .Group("quantum_feedback", "Quantum Feedback", "Icons/Color/Warning")
                .Show();
            S.Sys.Log.AddLine(source.Tile, Texts.Yellow(text));
        }

        private void ReportTransferredDamage(Tile target, float damage, DamageType type)
        {
            string cause = type == DamageType.Explosive ? "explosion" : "fire";
            string text = "Quantum feedback from " + cause + " damage transferred " + damage.ToString("0.#") + " damage to another endpoint.";
            Notification.BuildFor(target, "Icons/Color/Warning", text, 480, NotificationType.Alert)
                .Group("quantum_feedback", "Quantum Feedback", "Icons/Color/Warning")
                .Show();
            S.Sys.Log.AddLine(target, Texts.Red(text));
        }

        private QuantumObjectStorageNetwork GetOrCreateNetwork(string id)
        {
            if (!networks.TryGetValue(id, out QuantumObjectStorageNetwork network))
            {
                network = new QuantumObjectStorageNetwork(id);
                networks.Add(id, network);
            }

            return network;
        }
    }
}
