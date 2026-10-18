using System;
using System.Collections.Generic;
using System.Linq;
using Game.Components;
using Game.Systems.Items;
using KL.Utils;

namespace Game.Systems.QuantumStorage
{
    internal sealed class QuantumObjectStorageNetwork
    {
        private readonly HashSet<ObjStorageComp> nodes = new HashSet<ObjStorageComp>();
        private readonly List<ObjStorageComp> orderedNodes = new List<ObjStorageComp>();
        private bool configured;
        private bool loggedConfigurationMismatch;

        // Runtime-derived membership only, never serialized or used as inventory.
        // Original Core records let duplicate/nested callbacks be idempotent.
        internal Dictionary<uint, ObjData> CountedObjects = new Dictionary<uint, ObjData>();
        internal bool CountValid;
        internal int UsedCount => CountedObjects.Count;

        internal void RefreshFullState()
        {
            foreach (ObjStorageComp storage in nodes)
            {
                storage.MaxCapacity = Capacity;
                storage.IsFull = !CountValid || UsedCount >= Capacity;
            }
        }

        public QuantumObjectStorageNetwork(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public int BaseCapacity { get; private set; } = 25;

        public int CapacityMultiplier { get; private set; } = 2;

        public int MaxNetworkCapacity { get; private set; } = 2000000000;

        public float FireFeedbackChance { get; private set; } = 0.02f;

        public float ExplosionFeedbackChance { get; private set; } = 0.05f;

        public float FeedbackDamageFraction { get; private set; } = 0.2f;

        public int Capacity { get; private set; }

        public int NodeCount => nodes.Count;

        public IEnumerable<ObjStorageComp> Nodes => nodes;

        public void Attach(ObjStorageComp storage, QuantumStorageNodeComp node)
        {
            Configure(node);
            if (nodes.Add(storage))
            {
                orderedNodes.Add(storage);
            }
            Capacity = CalculateCapacity(nodes.Count);
        }

        public void Detach(ObjStorageComp storage)
        {
            if (nodes.Remove(storage))
            {
                orderedNodes.Remove(storage);
            }
            Capacity = CalculateCapacity(nodes.Count);
        }

        public bool Contains(ObjStorageComp storage)
        {
            return nodes.Contains(storage);
        }

        public bool ContainsStorageId(int storageId)
        {
            if (storageId == 0)
            {
                return false;
            }

            foreach (ObjStorageComp node in nodes)
            {
                if (node != null && node.EntityId == storageId)
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsLast(ObjStorageComp storage)
        {
            return nodes.Count == 1 && nodes.Contains(storage);
        }

        public ObjStorageComp NodeAt(int index)
        {
            return index >= 0 && index < orderedNodes.Count ? orderedNodes[index] : null;
        }

        public ObjStorageComp RehomeTarget(ObjStorageComp excluding)
        {
            ObjStorageComp target = null;
            foreach (ObjStorageComp node in nodes)
            {
                if (node == null || ReferenceEquals(node, excluding) || !node.Entity.IsActive)
                {
                    continue;
                }

                if (target == null || node.EntityId < target.EntityId)
                {
                    target = node;
                }
            }

            return target;
        }

        public void RefreshAll(int usedCapacity)
        {
            Capacity = CalculateCapacity(nodes.Count);
            foreach (ObjStorageComp storage in nodes.ToArray())
            {
                if (storage == null)
                {
                    continue;
                }

                storage.MaxCapacity = Capacity;
                storage.UsedCapacity = usedCapacity;
            }
        }

        private void Configure(QuantumStorageNodeComp node)
        {
            if (!configured)
            {
                configured = true;
                BaseCapacity = node.BaseCapacity;
                CapacityMultiplier = node.CapacityMultiplier;
                MaxNetworkCapacity = node.MaxNetworkCapacity;
                FireFeedbackChance = node.FireFeedbackChance;
                ExplosionFeedbackChance = node.ExplosionFeedbackChance;
                FeedbackDamageFraction = node.FeedbackDamageFraction;
                return;
            }

            if (!loggedConfigurationMismatch &&
                (BaseCapacity != node.BaseCapacity ||
                 CapacityMultiplier != node.CapacityMultiplier ||
                 MaxNetworkCapacity != node.MaxNetworkCapacity ||
                 FireFeedbackChance != node.FireFeedbackChance ||
                 ExplosionFeedbackChance != node.ExplosionFeedbackChance ||
                 FeedbackDamageFraction != node.FeedbackDamageFraction))
            {
                loggedConfigurationMismatch = true;
                D.Warn("Quantum object-storage network {0} has mismatched node settings; the first constructed node's profile remains authoritative.", Id);
            }
        }

        private int CalculateCapacity(int count)
        {
            if (count < 1)
            {
                return 0;
            }

            long capacity = BaseCapacity;
            for (int i = 1; i < count; i++)
            {
                if (CapacityMultiplier <= 1)
                {
                    break;
                }

                if (capacity > MaxNetworkCapacity / (long)CapacityMultiplier)
                {
                    return MaxNetworkCapacity;
                }

                capacity *= CapacityMultiplier;
            }

            return (int)Math.Min(capacity, MaxNetworkCapacity);
        }
    }
}
