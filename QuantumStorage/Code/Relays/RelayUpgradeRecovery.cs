using System;
using System.Collections.Generic;
using System.Linq;
using Game.Components;
using Game.Data;
using Game.Systems.Items;
using KL.Utils;

namespace Game.Systems.QuantumStorage.Relays
{
    public sealed partial class StorageRelaySys
    {
        // Only the short, queued Core replacement handover needs a journal.
        // Normal inventory ALWAYS lives in MatStorageComp.RawMats on each Relay.
        private sealed class UpgradeTransfer
        {
            internal int SourceId, Pos, Layer, Attempts;
            internal string Target;
            internal List<Mat> Contents;
            internal List<uint> Objects;
            internal int Output;
        }
        private readonly Dictionary<int, UpgradeTransfer> upgrades = new Dictionary<int, UpgradeTransfer>();
        private ComponentData upgradeData;

        public ComponentData Save()
        {
            var result = upgradeData ??= new ComponentData(0, Id);
            result.SetInt("Schema", 2);
            result.SetIntSet("PendingUpgrades", upgrades.Keys.OrderBy(i => i).ToArray());
            foreach (var p in upgrades)
            {
                string key = "Upgrade." + p.Key + ".";
                result.SetInt(key + "Pos", p.Value.Pos);
                result.SetInt(key + "Layer", p.Value.Layer);
                result.SetString(key + "Target", p.Value.Target);
                result.SetMats(key + "Contents", p.Value.Contents?.ToArray());
                result.SetIntSet(key + "Objects", p.Value.Objects?.Select(id => unchecked((int)id)).ToArray());
                result.SetInt(key + "Output", p.Value.Output);
            }
            return result;
        }

        public void Load(ComponentData data)
        {
            upgradeData = data; upgrades.Clear();
            var ids = data?.GetIntSet("PendingUpgrades", null);
            if (ids == null) { return; }
            foreach (int id in ids)
            {
                string key = "Upgrade." + id + ".";
                var mats = data.GetMats(key + "Contents", null);
                var objects = data.GetIntSet(key + "Objects", null);
                if ((mats == null || mats.Length == 0) && (objects == null || objects.Length == 0)) { continue; }
                upgrades[id] = new UpgradeTransfer { SourceId = id, Pos = data.GetInt(key + "Pos", 0),
                    Layer = data.GetInt(key + "Layer", 2), Target = data.GetString(key + "Target", null),
                    Contents = mats?.Where(m => m.Type != null && m.StackSize > 0).ToList(),
                    Objects = objects?.Select(i => unchecked((uint)i)).ToList(), Output = data.GetInt(key + "Output", 0) };
            }
        }

        internal bool CaptureUpgrade(MatStorageComp storage)
        {
            if (!storage.Entity.TryGetComponent<PendingReplacementComp>(out var replacement) ||
                !replacement.WasSuccessfullyReplaced || replacement.TargetDef == null) { return false; }
            var targetNode = replacement.TargetDef.ComponentConfigFor(Hashes.S("QuantumStorageNode"), false);
            var source = storage.Entity.GetComponent<StorageRelayNodeComp>();
            if (targetNode == null || targetNode.GetString(Hashes.S("NetworkId"), null) != source.NetworkId) { return false; }
            if (upgrades.ContainsKey(storage.EntityId)) { return true; }
            var local = LocalContents(storage);
            var record = new UpgradeTransfer { SourceId = storage.EntityId, Pos = storage.Tile.PosIdx,
                Layer = storage.Tile.Definition.LayerId, Target = replacement.TargetDef.Id,
                Contents = local == null ? new List<Mat>() : local.Where(p => p.Value > 0).Select(p => new Mat { Type = p.Key, StackSize = p.Value, MaxStackSize = p.Value }).ToList() };
            // Journal first, then let Core clear only this unit and cancel its
            // reservations. This method is synchronous on Core's game thread.
            upgrades.Add(storage.EntityId, record);
            using (RelayCore.Local()) { storage.TakeAll(); }
            Changed(storage);
            return true;
        }

        internal void CompleteUpgrade(Tile oldTile, Tile target)
        {
            if (oldTile != null && upgrades.TryGetValue(oldTile.Id, out var transfer)) { DeliverUpgrade(transfer, target); }
        }

        internal void CaptureObjectUpgrade(ObjStorageComp storage)
        {
            if (!storage.Entity.TryGetComponent<PendingReplacementComp>(out var replacement) ||
                !replacement.WasSuccessfullyReplaced || replacement.TargetDef == null) { return; }
            var targetNode = replacement.TargetDef.ComponentConfigFor(Hashes.S("QuantumStorageNode"), false);
            var source = storage.Entity.GetComponent<StorageRelayNodeComp>();
            if (targetNode == null || targetNode.GetString(Hashes.S("NetworkId"), null) != source.NetworkId || upgrades.ContainsKey(storage.EntityId)) { return; }
            // Native OnRemove ejects these exact records before the queued new
            // building appears. Journal IDs only, NEVER a copy of ObjData/minds.
            // Thus a failed replacement leaves real recoverable objects on floor.
            var records = S.Query.GetAllObjs.Ask().Values.Where(o => o.State == ObjState.Stored && o.StorageId == storage.EntityId).Select(o => o.Id).ToList();
            if (records.Count == 0) { return; }
            upgrades[storage.EntityId] = new UpgradeTransfer { SourceId = storage.EntityId, Pos = storage.Tile.PosIdx,
                Layer = storage.Tile.Definition.LayerId, Target = replacement.TargetDef.Id, Objects = records, Output = storage.InPort };
        }

        private void RecoverUpgrades()
        {
            if (!ready || upgrades.Count == 0) { return; }
            // Bound recovery to one queued handover per tick.
            var transfer = upgrades.Values.First();
            var tile = EntityUtils.GetTileInLayer(transfer.Pos, transfer.Layer);
            if (tile?.DefinitionId == transfer.Target && tile.IsConstructed) { DeliverUpgrade(transfer, tile); return; }
            if (++transfer.Attempts < 3) { return; }
            // Object contents already exist as native loose records. They need
            // no spawning/recovery copy if placement failed or was cancelled.
            if (transfer.Objects != null) { upgrades.Remove(transfer.SourceId); return; }
            // A failed/cancelled placement must not strand the saved journal.
            // Emit the remaining materials at the original position, never delete.
            for (int i = transfer.Contents.Count - 1; i >= 0; i--)
            {
                var mat = transfer.Contents[i];
                if (EntityUtils.SpawnRawMaterial(mat, transfer.Pos, 0.5f, mergeIntoBlock: false) == null) { return; }
                transfer.Contents.RemoveAt(i);
            }
            upgrades.Remove(transfer.SourceId);
        }

        private void DeliverUpgrade(UpgradeTransfer transfer, Tile tile)
        {
            if (transfer.Objects != null) { DeliverObjectUpgrade(transfer, tile); return; }
            if (tile?.DefinitionId != transfer.Target || !tile.IsConstructed ||
                !tile.TryGetComponent<MatStorageComp>(out var dest) || !QuantumStorageNetworkSys.TryGetNode(dest, out _)) { return; }
            var quantum = QuantumStorageNetworkSys.Get(S);
            quantum?.Attach(dest);
            if (quantum == null || !quantum.TryGetNetwork(dest, out _)) { return; }
            for (int i = transfer.Contents.Count - 1; i >= 0; i--)
            {
                var mat = transfer.Contents[i];
                int rest = dest.Store(mat.Type, null, mat.StackSize, this);
                if (rest > 0)
                {
                    mat.StackSize = rest; transfer.Contents[i] = mat;
                    if (EntityUtils.SpawnRawMaterial(mat, tile.Transform.AnyPortOutFirst, 0.5f, mergeIntoBlock: false) == null) { return; }
                }
                transfer.Contents.RemoveAt(i);
            }
            upgrades.Remove(transfer.SourceId);
        }

        private void DeliverObjectUpgrade(UpgradeTransfer transfer, Tile tile)
        {
            if (tile?.DefinitionId != transfer.Target || !tile.IsConstructed || !tile.TryGetComponent<ObjStorageComp>(out var dest)) { return; }
            var quantum = QuantumObjectStorageNetworkSys.Get(S);
            quantum?.Attach(dest);
            if (quantum == null || !quantum.TryGetNetwork(dest, out _)) { return; }
            var all = S.Query.GetAllObjs.Ask();
            foreach (uint id in transfer.Objects)
            {
                if (!all.TryGetValue(id, out var obj) || obj.State != ObjState.Active || obj.Obj == null ||
                    KL.Grid.Pos.SqrDistance(obj.Obj.PosIdx, transfer.Output) > 4) { continue; }
                // Use Core's store operation with the existing identity. If it
                // refuses (e.g. full), the item stays on the floor; never delete.
                S.Query.StoreObj.Ask(obj, dest);
            }
            upgrades.Remove(transfer.SourceId);
            ObjectChanged();
        }
    }
}
