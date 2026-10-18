using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Story.Events;
using Game.Systems.Creatures;
using Game.Systems.Inventory;
using Game.Systems.Notify;
using Game.Systems.Trade;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.QuantumStorage
{
    public sealed class QuantumStorageNetworkSys : GameSystem, ISaveable
    {
        public const string SysId = "QuantumStorageNetworkSys";
        public const string HarmonyId = "jleas.quantumstorage";

        private const int SaveSchema = 2;
        private readonly Dictionary<string, QuantumStorageNetwork> networks = new Dictionary<string, QuantumStorageNetwork>(StringComparer.Ordinal);
        private readonly Dictionary<MatStorageComp, QuantumStorageNetwork> bindings = new Dictionary<MatStorageComp, QuantumStorageNetwork>();
        private readonly HashSet<int> fireIncidentSources = new HashSet<int>();
        private readonly List<Mat> groupMatsCache = new List<Mat>();
        private readonly List<MatStorageComp> feedbackTargets = new List<MatStorageComp>();
        private ComponentData data;
        private Harmony harmony;
        private bool applyingFeedback;

        public override string Id => SysId;

        private ComponentData Data => data ?? (data = new ComponentData(0, Id));

        public bool HasAttachedNetworks => bindings.Count != 0;

        internal bool IsApplyingFeedback => applyingFeedback;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register()
        {
            GameSystems.Register(SysId, () => new QuantumStorageNetworkSys());
        }

        protected override void OnInitialize()
        {
            try
            {
                QuantumStorageReflection.Validate();
                QuantumNanobotObjectHauling.ReportBindings();
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                QuantumNanobotObjectHauling.InitializeOptionalIntegrations();
                The.SysSig.AfterLoadSaveState.AddListener(OnAfterLoadSaveState);
                D.Log("Quantum Storage {0} initialized with {1} Harmony patches.",
                    Assembly.GetExecutingAssembly().GetName().Version, harmony.GetPatchedMethods().Count());
            }
            catch (Exception ex)
            {
                harmony?.UnpatchAll(HarmonyId);
                harmony = null;
                D.LogEx(ex, "Quantum Storage initialization failed; shared storage remains inactive for this session.");
            }
        }

        public override void Unload()
        {
            The.SysSig.AfterLoadSaveState.RemoveListener(OnAfterLoadSaveState);
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            QuantumNanobotObjectHauling.Reset();
            bindings.Clear();
            networks.Clear();
            fireIncidentSources.Clear();
            feedbackTargets.Clear();
            applyingFeedback = false;
            data = null;
        }

        private static void OnAfterLoadSaveState(GameState state)
        {
            QuantumNanobotObjectHauling.AfterFullLoad(state);
        }

        public ComponentData Save()
        {
            ComponentData result = Data;
            result.SetInt("Schema", SaveSchema);
            result.SetIntSet("FireIncidentSources", fireIncidentSources.OrderBy(id => id).ToArray());
            string[] ids = networks.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            result.SetStringSet("Networks", ids);
            for (int i = 0; i < ids.Length; i++)
            {
                QuantumStorageNetwork network = networks[ids[i]];
                result.SetMats(SnapshotKey(ids[i]), network.CreateSnapshot());
            }

            return result;
        }

        public void Load(ComponentData loadedData)
        {
            data = loadedData;
            networks.Clear();
            bindings.Clear();
            fireIncidentSources.Clear();

            int[] fireSources = loadedData?.GetIntSet("FireIncidentSources", null);
            if (fireSources != null)
            {
                for (int i = 0; i < fireSources.Length; i++)
                {
                    fireIncidentSources.Add(fireSources[i]);
                }
            }

            string[] ids = loadedData?.GetStringSet("Networks", null);
            if (ids == null)
            {
                return;
            }

            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                QuantumStorageNetwork network = GetOrCreateNetwork(id);
                network.LoadSnapshot(loadedData.GetMats(SnapshotKey(id), null));
            }
        }

        public static QuantumStorageNetworkSys Get(GameState state)
        {
            if (state?.Sys?.Known != null && state.Sys.Known.TryGetValue(SysId, out GameSystem system))
            {
                return system as QuantumStorageNetworkSys;
            }

            return null;
        }

        public static bool TryGetNode(MatStorageComp storage, out QuantumStorageNodeComp node)
        {
            node = null;
            return storage?.Entity != null && storage.Entity.TryGetComponent(out node);
        }

        public void Attach(MatStorageComp storage)
        {
            if (storage == null || !storage.Entity.IsActive || !storage.Tile.IsConstructed || !TryGetNode(storage, out QuantumStorageNodeComp node))
            {
                return;
            }

            QuantumStorageNetwork network = GetOrCreateNetwork(node.NetworkId);
            if (bindings.TryGetValue(storage, out QuantumStorageNetwork oldNetwork) && oldNetwork != network)
            {
                oldNetwork.Detach(storage);
            }

            bindings[storage] = network;
            network.Attach(storage, node);
            if (storage.Entity.Flammable?.IsOnFire != true)
            {
                fireIncidentSources.Remove(storage.EntityId);
            }
            foreach (MatType type in network.Contents.Keys)
            {
                S.Sys.Inventory.RegisterKnownMaterial(type);
            }
        }

        public bool HandleRemove(MatStorageComp storage)
        {
            if (!bindings.TryGetValue(storage, out QuantumStorageNetwork network))
            {
                return false;
            }

            ReinstallableComp reinstallable = storage.Tile.Reinstallable;
            bool relocating = reinstallable != null && (reinstallable.IsMidRelocation || reinstallable.IsPending);
            if (network.IsLast(storage) && !relocating)
            {
                storage.DumpContents();
            }

            storage.RemoveFromInventory();
            bindings.Remove(storage);
            fireIncidentSources.Remove(storage.EntityId);
            network.Detach(storage);
            return true;
        }

        internal bool TryGetNetwork(MatStorageComp storage, out QuantumStorageNetwork network)
        {
            return bindings.TryGetValue(storage, out network);
        }

        internal void HandleStorageContentsChanged(MatStorageComp storage)
        {
            if (storage != null && bindings.TryGetValue(storage, out QuantumStorageNetwork network))
            {
                network.SynchronizeContents();
            }
        }

        internal void EndFireIncident(int entityId)
        {
            fireIncidentSources.Remove(entityId);
        }

        internal void HandleDamageFeedback(QuantumStorageNetwork network, MatStorageComp source, float incomingDamage, DamageType type, bool wasOnFire)
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
            foreach (MatStorageComp candidate in network.Nodes)
            {
                if (candidate != null &&
                    !ReferenceEquals(candidate, source) &&
                    candidate.Entity.IsActive &&
                    candidate.IsUsable &&
                    candidate.Tile.Health != null)
                {
                    feedbackTargets.Add(candidate);
                }
            }

            if (feedbackTargets.Count == 0 || chance <= 0f || !S.Rng.Chance(chance))
            {
                return;
            }

            MatStorageComp target = feedbackTargets[S.Rng.Range(0, feedbackTargets.Count)];
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

        private static bool TryConsumeFuse(MatStorageComp source)
        {
            Tile tile = source.Tile;
            UpgradesComp upgrades = tile?.Upgrades;
            if (upgrades?.Upgrades == null || upgrades.UsedSlots <= 0 || !source.Entity.TryGetComponent(out WithFuseComp fuse))
            {
                return false;
            }

            bool hasFuse = false;
            for (int i = 0; i < upgrades.Upgrades.Length; i++)
            {
                if (upgrades.Upgrades[i] == "Fuse")
                {
                    hasFuse = true;
                    break;
                }
            }

            return hasFuse && fuse.TryBlowFuse();
        }

        private void ReportFuseInterception(MatStorageComp source, DamageType type)
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

        public bool IsReporter(MatStorageComp storage)
        {
            return bindings.TryGetValue(storage, out QuantumStorageNetwork network) && network.IsReporter(storage);
        }

        public bool IsLastNode(MatStorageComp storage)
        {
            return bindings.TryGetValue(storage, out QuantumStorageNetwork network) && network.IsLast(storage);
        }

        public bool HasEnough(InventorySys inventory, MatType type, int amount, int islandId, Being being)
        {
            long available = 0;
            bool ignoreDanger = true;
            BeingSafety safety = null;
            if (being != null)
            {
                safety = being.Data.Safety;
                ignoreDanger = safety.IgnoreDanger;
            }

            HashSet<QuantumStorageNetwork> counted = new HashSet<QuantumStorageNetwork>();
            List<IMatStorage> storages = inventory.Storages;
            for (int i = 0; i < storages.Count; i++)
            {
                IMatStorage storage = storages[i];
                if (storage.StorageType == StorageType.StoreOnly)
                {
                    continue;
                }

                int outPort = storage.OutPort;
                if ((islandId != -1 && !S.Sys.Areas.CanReachGroundedIslandToPos(islandId, outPort)) || (!ignoreDanger && !safety.IsActuallySafeAt(outPort)))
                {
                    continue;
                }

                if (storage is MatStorageComp matStorage && bindings.TryGetValue(matStorage, out QuantumStorageNetwork network) && !counted.Add(network))
                {
                    continue;
                }

                available += storage.Available(type);
                if (available >= amount)
                {
                    return true;
                }
            }

            return false;
        }

        public int CountByGroup(InventorySys inventory, int group)
        {
            List<IMatStorage> storages = inventory.StoragesByGroup(group);
            if (storages == null)
            {
                return 0;
            }

            int count = 0;
            HashSet<QuantumStorageNetwork> counted = new HashSet<QuantumStorageNetwork>();
            for (int i = 0; i < storages.Count; i++)
            {
                IMatStorage storage = storages[i];
                if (storage is MatStorageComp matStorage && bindings.TryGetValue(matStorage, out QuantumStorageNetwork network) && !counted.Add(network))
                {
                    continue;
                }

                if (storage.AvailableForGroup(group, groupMatsCache))
                {
                    count += groupMatsCache.Count;
                }
            }

            return count;
        }

        public void CorrectTradeMaterialCounts(
            InventorySys inventory,
            List<TradeableItem> items)
        {
            Dictionary<MatType, int> counts = new Dictionary<MatType, int>();
            HashSet<QuantumStorageNetwork> counted = new HashSet<QuantumStorageNetwork>();
            List<IMatStorage> storages = inventory.Storages;
            for (int i = 0; i < storages.Count; i++)
            {
                IMatStorage storage = storages[i];
                if (storage is MatStorageComp matStorage &&
                    bindings.TryGetValue(matStorage, out QuantumStorageNetwork network) &&
                    !counted.Add(network))
                {
                    continue;
                }

                if (storage is UnstoredMatComp pile)
                {
                    MatType type = pile.Type;
                    if (type != null)
                    {
                        InventorySys.AppendMatCount(counts, type, pile.Available(type));
                    }
                    continue;
                }

                Dictionary<MatType, int> contents = storage.Contents;
                if (contents == null)
                {
                    continue;
                }

                foreach (KeyValuePair<MatType, int> item in contents)
                {
                    if (item.Key != null)
                    {
                        InventorySys.AppendMatCount(counts, item.Key, item.Value);
                    }
                }
            }

            for (int i = items.Count - 1; i >= 0; i--)
            {
                TradeableItem item = items[i];
                MatType type = item.MatType;
                if (item.Owner != TradeableItem.ItemOwner.Us || type == null)
                {
                    continue;
                }

                int count = counts.TryGetValue(type, out int available) ? available : 0;
                if (count < 1)
                {
                    items.RemoveAt(i);
                }
                else
                {
                    item.Quantity = count;
                }
            }
        }

        public IEnumerator RemoveWealthDeduplicated()
        {
            HashSet<QuantumStorageNetwork> counted = new HashSet<QuantumStorageNetwork>();
            HashSet<object> countedRelays = new HashSet<object>();
            List<IMatStorage> storages = S.Sys.Inventory.Storages;
            for (int i = storages.Count - 1; i >= 0; i--)
            {
                IMatStorage storage = i < storages.Count ? storages[i] : null;
                if (storage == null)
                {
                    continue;
                }

                try
                {
                    if (!storage.Entity.IsActive || !storage.IsPublic || storage.IsEmpty)
                    {
                        continue;
                    }

                    if (storage is MatStorageComp matStorage && bindings.TryGetValue(matStorage, out QuantumStorageNetwork network) && !counted.Add(network))
                    {
                        continue;
                    }

                    if (storage is MatStorageComp relayStorage && Relays.StorageRelaySys.IsRelay(relayStorage) &&
                        !countedRelays.Add(Relays.RelayCore.Key(S, storage)))
                    {
                        continue;
                    }

                    if (storage is UnstoredMatComp unstored)
                    {
                        if (storage.Entity.Carrier != null)
                        {
                            continue;
                        }

                        int available = unstored.Available(unstored.Type);
                        if (available != 0)
                        {
                            int amount = Mathf.RoundToInt(available * S.Rng.Range(0.4f, 0.6f));
                            if (amount != 0)
                            {
                                unstored.Take(unstored.Type, null, amount, null);
                            }
                        }
                    }
                    else if (storage.Contents != null)
                    {
                        KeyValuePair<MatType, int>[] contents = storage.Contents.ToArray();
                        for (int j = 0; j < contents.Length; j++)
                        {
                            KeyValuePair<MatType, int> pair = contents[j];
                            if (pair.Value == 0)
                            {
                                continue;
                            }

                            int amount = Mathf.RoundToInt(pair.Value * S.Rng.Range(0.4f, 0.6f));
                            if (amount != 0)
                            {
                                storage.Take(pair.Key, null, amount, null);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    D.LogEx(ex, "Failure while removing wealth from storage: {0}", storage);
                }

                if (!GameTimer.HasAvailableFrameBudget())
                {
                    yield return null;
                    if (!GameState.IsCurrent(S))
                    {
                        yield break;
                    }
                }
            }

            S.Wallet.TryPay(S.Wallet.Amount * S.Rng.Range(0.4f, 0.6f), null);
        }

        private QuantumStorageNetwork GetOrCreateNetwork(string id)
        {
            if (!networks.TryGetValue(id, out QuantumStorageNetwork network))
            {
                network = new QuantumStorageNetwork(id);
                networks.Add(id, network);
            }

            return network;
        }

        private static string SnapshotKey(string id)
        {
            return "Network." + id + ".Contents";
        }
    }
}
