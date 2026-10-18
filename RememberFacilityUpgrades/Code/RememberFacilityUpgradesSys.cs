using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Components.Upgrades;
using Game.Data;
using Game.Systems;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace RememberFacilityUpgrades
{

public sealed class RememberFacilityUpgradesSys : GameSystem, ISaveable
{
    public const string SysId = "RememberFacilityUpgradesSys";

    private const string HarmonyId = "RememberFacilityUpgrades.Runtime";
    private const int SaveSchemaVersion = 1;

    private static Harmony harmony;

    private readonly Dictionary<string, UpgradeRecoveryRecord> records =
        new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> recordKeyByEntityId = new();
    private readonly HashSet<int> relocatingSourceEntityIds = new();
    private readonly List<UpgradesComp> scanBuffer = new();

    private GameState loadedState;
    private bool isReconciling;
    private bool isRestoring;

    public static RememberFacilityUpgradesSys Instance { get; private set; }

    public override string Id => SysId;
    public override bool SkipInSandbox => false;

    [SelfInit]
    private static void Register()
    {
        GameSystems.Register(SysId,
            () => new RememberFacilityUpgradesSys());
    }

    protected override void OnInitialize()
    {
        Instance = this;
        if (harmony == null)
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(RememberFacilityUpgradesSys).Assembly);
        }

        S.Sig.AfterLoadState.AddListener(OnLoadState);
    }

    private void OnLoadState(GameState state)
    {
        DetachStateListeners();
        loadedState = state;
        state.Sig.TileUpgradeChanged.AddListener(OnTileUpgradeChanged);
        state.Sig.ConstructionCompleted.AddListener(OnConstructionCompleted);
        state.Sig.EntityRemoved.AddListener(OnEntityRemoved);
        state.Sig.TileRelocated.AddListener(OnTileRelocated);
        state.Clock.OnNextUpdate(ReconcileAfterLoad);
    }

    private void DetachStateListeners()
    {
        if (loadedState == null) return;
        loadedState.Sig.TileUpgradeChanged.RemoveListener(
            OnTileUpgradeChanged);
        loadedState.Sig.ConstructionCompleted.RemoveListener(
            OnConstructionCompleted);
        loadedState.Sig.EntityRemoved.RemoveListener(OnEntityRemoved);
        loadedState.Sig.TileRelocated.RemoveListener(OnTileRelocated);
        loadedState = null;
    }

    private void ReconcileAfterLoad()
    {
        if (loadedState == null || isReconciling) return;
        isReconciling = true;
        try
        {
            recordKeyByEntityId.Clear();
            relocatingSourceEntityIds.Clear();
            scanBuffer.Clear();
            loadedState.Components.FindAll(scanBuffer);

            var liveKeys = new HashSet<string>(StringComparer.Ordinal);
            var restorations = new List<Tuple<UpgradesComp,
                UpgradeRecoveryRecord>>();
            int constructedFacilities = 0;

            foreach (UpgradesComp upgrades in scanBuffer)
            {
                if (!IsConstructedFacility(upgrades)) continue;
                constructedFacilities++;
                string key = KeyFor(upgrades.Tile);
                liveKeys.Add(key);

                if (records.TryGetValue(key, out var saved)
                    && saved.AwaitingRebuild)
                {
                    AttachEntity(saved, upgrades.Entity.Id);
                    restorations.Add(Tuple.Create(upgrades, saved));
                    continue;
                }

                CaptureLiveConfiguration(upgrades);
            }

            foreach (string staleKey in records
                         .Where(pair => !pair.Value.AwaitingRebuild
                             && !liveKeys.Contains(pair.Key))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                RemoveRecord(staleKey);
            }

            foreach (Tuple<UpgradesComp, UpgradeRecoveryRecord> item
                     in restorations)
            {
                TryOrderRememberedUpgrades(item.Item1, item.Item2,
                    "save-load reconciliation");
            }

            D.Log("Remember Facility Upgrades: scanned {0} constructed "
                + "facilities once after load; tracking {1} upgraded "
                + "facilities with {2} awaiting rebuild.",
                constructedFacilities, records.Count,
                records.Values.Count(record => record.AwaitingRebuild));
        }
        catch (Exception exception)
        {
            D.LogEx(exception,
                "Remember Facility Upgrades load reconciliation failed");
        }
        finally
        {
            isReconciling = false;
        }
    }

    internal void CaptureDamageDestruction(UpgradesComp upgrades)
    {
        Tile tile = upgrades?.Tile;
        if (tile?.Health?.IsDestroyed != true
            || !CanAutoRebuild(tile))
        {
            return;
        }

        CaptureForAutoRebuild(upgrades, "damage destruction");
    }

    internal UpgradeDeconstructionCapture
        CaptureBeforeBreakdownDeconstruction(Tile tile)
    {
        if (loadedState == null || !GameState.IsCurrent(loadedState)
            || tile?.IsActive != true || !tile.IsConstructed
            || tile.Constructable?.IsBrokenDown != false
            || tile.Upgrades == null || !CanAutoRebuild(tile))
        {
            return null;
        }

        string[] installed = SnapshotInstalled(tile.Upgrades);
        if (!HasAnyUpgrade(installed)) return null;

        return new UpgradeDeconstructionCapture
        {
            EntityId = tile.Id,
            Position = tile.PosIdx,
            Layer = tile.Definition.LayerId,
            DefinitionId = tile.DefinitionId,
            InstalledBefore = installed
        };
    }

    internal void CaptureAfterBreakdownDeconstruction(
        UpgradeDeconstructionCapture capture)
    {
        if (capture == null || loadedState == null
            || !GameState.IsCurrent(loadedState))
        {
            return;
        }

        Tile tile = loadedState.FindEntity<Tile>(capture.EntityId);
        if (tile?.Constructable?.IsBrokenDown != true
            || tile.PosIdx != capture.Position
            || tile.Definition.LayerId != capture.Layer
            || tile.DefinitionId != capture.DefinitionId)
        {
            return;
        }

        StoreAutoRebuildRecord(capture.Position, capture.Layer,
            capture.EntityId, capture.DefinitionId,
            capture.InstalledBefore,
            "mental-breakdown deconstruction");

        D.Warn("Remember Facility Upgrades: captured {0} installed "
            + "upgrade(s) before mental-breakdown sabotage of {1} "
            + "at position {2}.",
            capture.InstalledBefore.Count(id =>
                !string.IsNullOrWhiteSpace(id)),
            capture.DefinitionId, capture.Position);
    }

    internal void CaptureAutoRebuildCommand(Tile tile, bool autoRebuild)
    {
        if (!autoRebuild || loadedState == null || tile == null) return;
        if (tile.Transform?.IsChild == true) tile = tile.Transform.Parent;
        if (tile?.IsActive != true || tile.Upgrades == null
            || A.InstantBuild
            || !EntityUtils.IsWithinMargin(tile.PosIdx)
            || !loadedState.Research.IsDefUnlocked(tile.Definition))
        {
            return;
        }

        CaptureForAutoRebuild(tile.Upgrades, "auto-rebuild removal");
    }

    internal UpgradeTheftCapture CaptureBeforeUpgradeTheft(Tile tile)
    {
        if (loadedState == null || !GameState.IsCurrent(loadedState)
            || tile?.IsActive != true || !tile.IsConstructed
            || tile.Upgrades == null)
        {
            return null;
        }

        string[] installed = SnapshotInstalled(tile.Upgrades);
        if (!HasAnyUpgrade(installed)) return null;

        string[] desired = (string[])installed.Clone();
        bool awaitingRebuild = false;
        if (records.TryGetValue(KeyFor(tile), out var record))
        {
            desired = MergeRememberedConfiguration(
                record.UpgradeIds, installed);
            awaitingRebuild = record.AwaitingRebuild;
        }

        return new UpgradeTheftCapture
        {
            EntityId = tile.Id,
            InstalledBefore = installed,
            DesiredBefore = desired,
            AwaitingRebuildBefore = awaitingRebuild
        };
    }

    internal void RecoverAfterUpgradeTheft(Tile tile,
        UpgradeTheftCapture capture)
    {
        if (capture == null || loadedState == null
            || !GameState.IsCurrent(loadedState)
            || tile?.IsActive != true || !tile.IsConstructed
            || tile.Id != capture.EntityId || tile.Upgrades == null)
        {
            return;
        }

        UpgradesComp upgrades = tile.Upgrades;
        string[] installedAfter = SnapshotInstalled(upgrades);
        int slot = FindSingleRemovedSlot(capture.InstalledBefore,
            installedAfter);
        if (slot == -1) return;
        if (slot < -1)
        {
            D.ErrSoft("Remember Facility Upgrades: Core's upgrade theft "
                + "changed more than one occupied slot on {0}; no "
                + "automatic recovery was queued.", tile);
            return;
        }

        string upgradeId = capture.InstalledBefore[slot];
        RestoreTheftTracking(tile, capture, slot, upgradeId);

        string pending = upgrades.PendingUpgrades.SafeGet(slot);
        if (pending == upgradeId)
        {
            D.Log("Remember Facility Upgrades: the stolen upgrade {0} "
                + "is already awaiting installation in slot {1} on {2}.",
                upgradeId, slot, tile);
            return;
        }
        if (pending != null || upgrades.Upgrades.SafeGet(slot) != null)
        {
            D.ErrSoft("Remember Facility Upgrades: left stolen upgrade "
                + "{0} unresolved because slot {1} on {2} already has "
                + "a different installed or pending assignment.",
                upgradeId, slot, tile);
            return;
        }

        Upgrade upgrade = UpgradeRegistry.Get(upgradeId);
        if (upgrade == null || !upgrade.IsCompatibleWith(upgrades.Entity)
            || !upgrade.CanInstallNow(upgrades.Entity))
        {
            D.ErrSoft("Remember Facility Upgrades: cannot requeue stolen "
                + "upgrade {0} for slot {1} on {2}; it is missing or no "
                + "longer compatible.", upgradeId, slot, tile);
            return;
        }

        if (upgrades.OrderUpgrade(slot, upgrade))
        {
            D.Log("Remember Facility Upgrades: Core stole {0} from slot "
                + "{1} on {2}; queued the ordinary Install Upgrade job "
                + "for the same physical module.", upgradeId, slot, tile);
        }
        else
        {
            D.ErrSoft("Remember Facility Upgrades: Core rejected the "
                + "Install Upgrade order for stolen upgrade {0} in slot "
                + "{1} on {2}.", upgradeId, slot, tile);
        }
    }

    internal static int FindSingleRemovedSlot(string[] before,
        string[] after)
    {
        if (before == null) return -1;
        int found = -1;
        for (int slot = 0; slot < before.Length; slot++)
        {
            if (string.IsNullOrWhiteSpace(before[slot])
                || after.SafeGet(slot) != null)
            {
                continue;
            }
            if (found >= 0) return -2;
            found = slot;
        }
        return found;
    }

    private void RestoreTheftTracking(Tile tile,
        UpgradeTheftCapture capture, int slot, string upgradeId)
    {
        string key = KeyFor(tile);
        if (!records.TryGetValue(key, out var record))
        {
            record = new UpgradeRecoveryRecord();
            records[key] = record;
        }

        string[] desired = MergeRememberedConfiguration(
            capture.DesiredBefore, capture.InstalledBefore);
        if (desired.Length <= slot)
        {
            Array.Resize(ref desired, slot + 1);
        }
        desired[slot] = upgradeId;
        record.Position = tile.PosIdx;
        record.Layer = tile.Definition.LayerId;
        record.DefinitionId = tile.DefinitionId;
        record.UpgradeIds = desired;
        record.AwaitingRebuild = capture.AwaitingRebuildBefore;
        AttachEntity(record, tile.Id);
    }

    private static string[] MergeRememberedConfiguration(
        string[] remembered, string[] installed)
    {
        int count = Math.Max(remembered?.Length ?? 0,
            installed?.Length ?? 0);
        var merged = new string[count];
        if (remembered != null)
        {
            Array.Copy(remembered, merged, remembered.Length);
        }
        if (installed != null)
        {
            for (int slot = 0; slot < installed.Length; slot++)
            {
                if (merged[slot] == null)
                {
                    merged[slot] = installed[slot];
                }
            }
        }
        return merged;
    }

    private bool CanAutoRebuild(Tile tile)
    {
        return loadedState != null
            && GameState.IsCurrent(loadedState)
            && Ready.Gen
            && loadedState.Clock.Ticks >= 360
            && !A.InstantBuild
            && EntityUtils.IsWithinMargin(tile.PosIdx,
                ignoreMargin: false, ignoreCornerAreas: false)
            && loadedState.Research.IsDefUnlocked(tile.Definition);
    }

    private void CaptureForAutoRebuild(UpgradesComp upgrades,
        string source)
    {
        if (loadedState == null || upgrades?.Entity?.IsActive != true
            || upgrades.Tile == null)
        {
            return;
        }

        string[] installed = SnapshotInstalled(upgrades);
        if (!HasAnyUpgrade(installed)) return;

        Tile tile = upgrades.Tile;
        StoreAutoRebuildRecord(tile.PosIdx, tile.Definition.LayerId,
            tile.Id, tile.DefinitionId, installed, source);
    }

    private void StoreAutoRebuildRecord(int position, int layer,
        int entityId, string definitionId, string[] installed,
        string source)
    {
        if (!HasAnyUpgrade(installed)) return;

        string key = MakeKey(layer, position, definitionId);
        if (!records.TryGetValue(key, out var record))
        {
            record = new UpgradeRecoveryRecord();
            records[key] = record;
        }

        record.Position = position;
        record.Layer = layer;
        record.DefinitionId = definitionId;
        record.UpgradeIds = (string[])installed.Clone();
        record.AwaitingRebuild = true;
        AttachEntity(record, entityId);

        D.Log("Remember Facility Upgrades: remembered {0} installed "
            + "upgrade(s) for {1} at position {2} before {3}.",
            installed.Count(id => !string.IsNullOrWhiteSpace(id)),
            definitionId, position, source);
    }

    private void OnConstructionCompleted(Tile tile)
    {
        if (tile?.IsActive != true || tile.Upgrades == null) return;
        string key = KeyFor(tile);
        if (!records.TryGetValue(key, out var record)
            || !record.AwaitingRebuild)
        {
            CaptureLiveConfiguration(tile.Upgrades);
            return;
        }

        AttachEntity(record, tile.Id);
        loadedState.Clock.OnNextUpdate(() =>
        {
            if (tile?.IsActive != true || !tile.IsConstructed
                || tile.Upgrades == null)
            {
                return;
            }
            TryOrderRememberedUpgrades(tile.Upgrades, record,
                "construction completion");
        });
    }

    private void TryOrderRememberedUpgrades(UpgradesComp upgrades,
        UpgradeRecoveryRecord record, string trigger)
    {
        if (loadedState == null || isRestoring
            || !IsConstructedFacility(upgrades) || record == null
            || !record.AwaitingRebuild)
        {
            return;
        }

        int ordered = 0;
        int alreadyAssigned = 0;
        int unresolved = 0;
        isRestoring = true;
        try
        {
            for (int slot = 0; slot < record.UpgradeIds.Length; slot++)
            {
                string upgradeId = record.UpgradeIds[slot];
                if (string.IsNullOrWhiteSpace(upgradeId)) continue;

                if (slot >= upgrades.NumSlots)
                {
                    unresolved++;
                    D.ErrSoft("Remember Facility Upgrades: cannot restore "
                        + "{0} to slot {1} on {2}; the rebuilt facility "
                        + "has only {3} slots.", upgradeId, slot,
                        upgrades.Tile, upgrades.NumSlots);
                    continue;
                }

                string installed = upgrades.Upgrades.SafeGet(slot);
                string pending = upgrades.PendingUpgrades.SafeGet(slot);
                if (installed == upgradeId || pending == upgradeId)
                {
                    alreadyAssigned++;
                    continue;
                }
                if (installed != null || pending != null)
                {
                    unresolved++;
                    D.ErrSoft("Remember Facility Upgrades: left slot {0} "
                        + "unchanged on {1} because it already contains or "
                        + "is awaiting a different upgrade.", slot,
                        upgrades.Tile);
                    continue;
                }

                Upgrade upgrade = UpgradeRegistry.Get(upgradeId);
                if (upgrade == null)
                {
                    unresolved++;
                    D.ErrSoft("Remember Facility Upgrades: saved upgrade "
                        + "{0} is no longer registered.", upgradeId);
                    continue;
                }
                if (!upgrade.IsCompatibleWith(upgrades.Entity)
                    || !upgrade.CanInstallNow(upgrades.Entity))
                {
                    unresolved++;
                    D.ErrSoft("Remember Facility Upgrades: saved upgrade "
                        + "{0} is not currently compatible with {1}.",
                        upgradeId, upgrades.Tile);
                    continue;
                }

                if (upgrades.OrderUpgrade(slot, upgrade))
                {
                    ordered++;
                }
                else
                {
                    unresolved++;
                    D.ErrSoft("Remember Facility Upgrades: Core rejected "
                        + "the Install Upgrade order for {0} in slot {1} "
                        + "on {2}.", upgradeId, slot, upgrades.Tile);
                }
            }
        }
        catch (Exception exception)
        {
            unresolved++;
            D.LogEx(exception,
                "Remember Facility Upgrades restore ordering failed");
        }
        finally
        {
            isRestoring = false;
        }

        if (unresolved == 0)
        {
            record.AwaitingRebuild = false;
            AttachEntity(record, upgrades.Entity.Id);
            D.Log("Remember Facility Upgrades: restored assignment for "
                + "{0} at position {1} after {2}: {3} Install Upgrade "
                + "order(s), {4} already assigned.",
                record.DefinitionId, record.Position, trigger, ordered,
                alreadyAssigned);
        }
        else
        {
            D.Warn("Remember Facility Upgrades: {0} saved upgrade slot(s) "
                + "remain unresolved for {1} at position {2} after {3}.",
                unresolved, record.DefinitionId, record.Position, trigger);
        }
    }

    private void OnTileUpgradeChanged(Upgrade upgrade, Tile tile,
        bool installed)
    {
        if (isRestoring || tile?.IsActive != true
            || tile.Upgrades == null || !tile.IsConstructed)
        {
            return;
        }
        if (ShouldIgnoreDestructionEjection(installed,
                tile.Health?.IsDestroyed == true))
        {
            return;
        }
        CaptureLiveConfiguration(tile.Upgrades);
    }

    internal static bool ShouldIgnoreDestructionEjection(bool installed,
        bool isDestroyed)
    {
        return !installed && isDestroyed;
    }

    private void CaptureLiveConfiguration(UpgradesComp upgrades)
    {
        if (!IsConstructedFacility(upgrades)) return;
        string key = KeyFor(upgrades.Tile);
        string[] installed = SnapshotInstalled(upgrades);
        if (!HasAnyUpgrade(installed))
        {
            if (records.TryGetValue(key, out var emptyRecord)
                && !emptyRecord.AwaitingRebuild)
            {
                RemoveRecord(key);
            }
            return;
        }

        if (!records.TryGetValue(key, out var record))
        {
            record = new UpgradeRecoveryRecord();
            records[key] = record;
        }
        record.Position = upgrades.Tile.PosIdx;
        record.Layer = upgrades.Tile.Definition.LayerId;
        record.DefinitionId = upgrades.Tile.DefinitionId;
        record.UpgradeIds = installed;
        record.AwaitingRebuild = false;
        AttachEntity(record, upgrades.Entity.Id);
    }

    private void OnTileRelocated(int oldEntityId, int newEntityId)
    {
        if (loadedState == null
            || !recordKeyByEntityId.TryGetValue(oldEntityId,
                out string oldKey)
            || !records.TryGetValue(oldKey, out var record))
        {
            return;
        }

        Tile newTile = loadedState.FindEntity<Tile>(newEntityId);
        if (newTile == null) return;
        relocatingSourceEntityIds.Add(oldEntityId);
        recordKeyByEntityId.Remove(oldEntityId);
        records.Remove(oldKey);
        record.Position = newTile.PosIdx;
        record.Layer = newTile.Definition.LayerId;
        record.DefinitionId = newTile.DefinitionId;
        record.EntityId = newEntityId;
        record.AwaitingRebuild = false;
        string newKey = record.Key;
        records[newKey] = record;
        recordKeyByEntityId[newEntityId] = newKey;
    }

    private void OnEntityRemoved(Entity entity)
    {
        if (entity == null) return;
        int entityId = entity.Id;
        if (relocatingSourceEntityIds.Remove(entityId)) return;
        if (!recordKeyByEntityId.TryGetValue(entityId, out string key)
            || !records.TryGetValue(key, out var record))
        {
            return;
        }

        recordKeyByEntityId.Remove(entityId);
        if (record.AwaitingRebuild)
        {
            record.EntityId = 0;
        }
        else
        {
            records.Remove(key);
        }
    }

    private void AttachEntity(UpgradeRecoveryRecord record, int entityId)
    {
        if (record.EntityId > 0
            && record.EntityId != entityId)
        {
            recordKeyByEntityId.Remove(record.EntityId);
        }
        record.EntityId = entityId;
        if (entityId > 0) recordKeyByEntityId[entityId] = record.Key;
    }

    private void RemoveRecord(string key)
    {
        if (!records.TryGetValue(key, out var record)) return;
        if (record.EntityId > 0)
            recordKeyByEntityId.Remove(record.EntityId);
        records.Remove(key);
    }

    private static string[] SnapshotInstalled(UpgradesComp upgrades)
    {
        int count = Math.Max(upgrades?.NumSlots ?? 0,
            upgrades?.Upgrades?.Length ?? 0);
        if (count == 0) return Array.Empty<string>();
        var result = new string[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = upgrades.Upgrades.SafeGet(i);
        }
        return result;
    }

    private static bool HasAnyUpgrade(string[] upgrades)
    {
        return upgrades != null
            && upgrades.Any(id => !string.IsNullOrWhiteSpace(id));
    }

    private static bool IsConstructedFacility(UpgradesComp upgrades)
    {
        return upgrades?.Entity?.IsActive == true
            && upgrades.Tile?.IsConstructed == true;
    }

    private static string KeyFor(Tile tile)
    {
        return MakeKey(tile.Definition.LayerId, tile.PosIdx,
            tile.DefinitionId);
    }

    internal static string MakeKey(int layer, int position,
        string definitionId)
    {
        return layer + ":" + position + ":" + definitionId;
    }

    public ComponentData Save()
    {
        var data = ComponentData.Create(SysId);
        data.SetInt("SchemaVersion", SaveSchemaVersion);
        data.SetStringSet("Records", records.Values
            .OrderBy(record => record.Layer)
            .ThenBy(record => record.Position)
            .ThenBy(record => record.DefinitionId,
                StringComparer.Ordinal)
            .Select(record => record.Serialize())
            .ToArray());
        return data;
    }

    public void Load(ComponentData data)
    {
        records.Clear();
        recordKeyByEntityId.Clear();
        relocatingSourceEntityIds.Clear();
        if (data == null) return;

        foreach (string serialized in data.GetStringSet(
                     "Records", Array.Empty<string>()))
        {
            if (!UpgradeRecoveryRecord.TryDeserialize(serialized,
                    out var record))
            {
                D.ErrSoft("Remember Facility Upgrades: ignored an "
                    + "unreadable saved recovery record.");
                continue;
            }
            records[record.Key] = record;
            if (record.EntityId > 0)
                recordKeyByEntityId[record.EntityId] = record.Key;
        }
    }

    public override void Unload()
    {
        S.Sig.AfterLoadState.RemoveListener(OnLoadState);
        DetachStateListeners();
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        scanBuffer.Clear();
        records.Clear();
        recordKeyByEntityId.Clear();
        relocatingSourceEntityIds.Clear();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
}
