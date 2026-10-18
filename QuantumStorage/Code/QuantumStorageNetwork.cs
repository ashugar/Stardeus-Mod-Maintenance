using System;
using System.Collections.Generic;
using System.Linq;
using Game.Components;
using Game.Constants;
using Game.Data;
using KL.Utils;

namespace Game.Systems.QuantumStorage
{
    internal sealed class QuantumStorageNetwork
    {
        private readonly HashSet<MatStorageComp> nodes = new HashSet<MatStorageComp>();
        private bool configured;
        private bool loggedConfigurationMismatch;

        public QuantumStorageNetwork(string id)
        {
            Id = id;
            Contents = new Dictionary<MatType, int>();
        }

        public string Id { get; }

        public Dictionary<MatType, int> Contents { get; }

        public MatStorageReservations Reservations { get; private set; }

        public bool HasSystemSnapshot { get; private set; }

        public int BaseCapacity { get; private set; } = 500;

        public int CapacityMultiplier { get; private set; } = 2;

        public int MaxNetworkCapacity { get; private set; } = 2000000000;

        public float FireFeedbackChance { get; private set; } = 0.02f;

        public float ExplosionFeedbackChance { get; private set; } = 0.05f;

        public float FeedbackDamageFraction { get; private set; } = 0.2f;

        public int Capacity { get; private set; }

        public int NodeCount => nodes.Count;

        public IEnumerable<MatStorageComp> Nodes => nodes;

        public int Available(MatType type)
        {
            if (type == null || !Contents.TryGetValue(type, out int stored) || stored < 1)
            {
                return 0;
            }

            int reserved = 0;
            Reservations?.Mats.TryGetValue(type, out reserved);
            return Math.Max(0, stored - reserved);
        }

        internal bool SanitizeGroupOptions(int groupId, List<Mat> options)
        {
            // Keep Core's candidate order and eligibility, but each shared material
            // must appear once and have stock left after network-wide reservations.
            HashSet<MatType> included = new HashSet<MatType>();
            int count = 0;
            for (int i = 0; i < options.Count; i++)
            {
                Mat option = options[i];
                MatType type = option.Type;
                if (type == null || type.GroupH != groupId || option.StackSize < 1)
                {
                    continue;
                }

                int available = Math.Min(option.StackSize, Available(type));
                if (available < 1 || !included.Add(type))
                {
                    continue;
                }

                option.StackSize = available;
                option.MaxStackSize = available;
                options[count++] = option;
            }

            options.RemoveRange(count, options.Count - count);
            return count > 0;
        }

        public void LoadSnapshot(Mat[] mats)
        {
            Contents.Clear();
            if (mats != null)
            {
                for (int i = 0; i < mats.Length; i++)
                {
                    Mat mat = mats[i];
                    if (mat.Type != null && mat.StackSize > 0)
                    {
                        Contents[mat.Type] = mat.StackSize;
                    }
                }
            }

            HasSystemSnapshot = true;
        }

        public Mat[] CreateSnapshot()
        {
            if (Contents.Count == 0)
            {
                return null;
            }

            Mat[] mats = new Mat[Contents.Count];
            int index = 0;
            foreach (KeyValuePair<MatType, int> pair in Contents)
            {
                mats[index++] = new Mat
                {
                    Type = pair.Key,
                    StackSize = pair.Value,
                    MaxStackSize = pair.Value
                };
            }

            return mats;
        }

        public void Attach(MatStorageComp storage, QuantumStorageNodeComp node)
        {
            Configure(node);
            if (!nodes.Add(storage))
            {
                Rebind(storage, CalculateUsedCapacity());
                return;
            }

            Dictionary<MatType, int> localContents = QuantumStorageReflection.GetContents(storage);
            if (!HasSystemSnapshot && Contents.Count == 0 && localContents != null && !ReferenceEquals(localContents, Contents))
            {
                foreach (KeyValuePair<MatType, int> pair in localContents)
                {
                    if (pair.Key != null && pair.Value > 0)
                    {
                        Contents[pair.Key] = pair.Value;
                    }
                }
            }

            if (Reservations == null)
            {
                Reservations = storage.Reservations ?? new MatStorageReservations(storage);
            }

            RefreshAll();
        }

        public void Detach(MatStorageComp storage)
        {
            nodes.Remove(storage);
            RefreshAll();
        }

        public bool Contains(MatStorageComp storage)
        {
            return nodes.Contains(storage);
        }

        public bool IsLast(MatStorageComp storage)
        {
            return nodes.Count == 1 && nodes.Contains(storage);
        }

        public bool IsReporter(MatStorageComp storage)
        {
            if (!nodes.Contains(storage))
            {
                return false;
            }

            int lowestEntityId = int.MaxValue;
            foreach (MatStorageComp node in nodes)
            {
                if (node.EntityId < lowestEntityId)
                {
                    lowestEntityId = node.EntityId;
                }
            }

            return storage.EntityId == lowestEntityId;
        }

        public void RefreshAll()
        {
            Capacity = CalculateCapacity(nodes.Count);
            int usedCapacity = CalculateUsedCapacity();
            foreach (MatStorageComp storage in nodes.ToArray())
            {
                Rebind(storage, usedCapacity);
            }
        }

        public void SynchronizeContents()
        {
            int usedCapacity = CalculateUsedCapacity();
            foreach (MatStorageComp storage in nodes.ToArray())
            {
                if (storage == null)
                {
                    continue;
                }

                QuantumStorageReflection.SetUsedCapacity(storage, usedCapacity);
                QuantumStorageReflection.RefreshDisplay(storage);
            }
        }

        private void Rebind(MatStorageComp storage, int usedCapacity)
        {
            if (storage == null)
            {
                return;
            }

            if (Reservations == null)
            {
                Reservations = storage.Reservations ?? new MatStorageReservations(storage);
            }

            QuantumStorageReflection.Bind(storage, Contents, Reservations);
            QuantumStorageReflection.SetUsedCapacity(storage, usedCapacity);
            int componentBaseCapacity = QuantumStorageReflection.GetBaseCapacity(storage);
            storage.ExtraCapacity = Capacity - componentBaseCapacity;
            QuantumStorageReflection.RefreshDisplay(storage);
        }

        private int CalculateUsedCapacity()
        {
            long usedCapacity = 0L;
            foreach (KeyValuePair<MatType, int> pair in Contents)
            {
                if (pair.Value > 0)
                {
                    usedCapacity += pair.Value;
                    if (usedCapacity >= int.MaxValue)
                    {
                        return int.MaxValue;
                    }
                }
            }

            return (int)usedCapacity;
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
                D.Warn("Quantum Storage network {0} has mismatched node settings; the first constructed node's profile remains authoritative.", Id);
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
