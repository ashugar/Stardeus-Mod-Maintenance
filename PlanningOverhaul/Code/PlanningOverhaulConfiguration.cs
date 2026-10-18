using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using Game.Components;
using Game.Components.Upgrades;
using Game.Constants;
using Game.Data;
using Game.Systems.Crafting;
using Game.Utils;
using KL.Utils;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private enum PostRotationConfigurationPhase
    {
        None,
        Production,
        Transporters,
        Upgrades,
        Planters
    }

    private sealed class ConfigurationPassResult
    {
        internal int Planned;
        internal int Changed;
        internal int Pending;
        internal int MissingMetadata;
        internal string FirstWait;
    }

    private PostRotationConfigurationPhase postRotationConfigurationPhase;
    private bool postRotationConfigurationCompletedPendingStatus;
    private ConfigurationPassResult lastProductionConfigurationResult;
    private ConfigurationPassResult lastTransporterConfigurationResult;
    private ConfigurationPassResult lastUpgradeConfigurationResult;
    private ConfigurationPassResult lastPlanterConfigurationResult;
    private int planterMetadataMissingCount;
    private static readonly FieldInfo PlanterPlantDefIdField = typeof(PlanterComp).GetField(
        "plantDefId", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly HashSet<string> ExternallyManagedProductionDefinitionIds =
        new(StringComparer.Ordinal)
        {
            "Objects/Devices/NanoFabricator",
            "Objects/Devices/NanoFoundry",
            "Objects/Devices/NanoCloningPod"
        };

    private bool IsExternallyManagedProductionQueue(string definitionId)
    {
        return !string.IsNullOrWhiteSpace(definitionId)
            && ExternallyManagedProductionDefinitionIds.Contains(definitionId)
            && IsAutoFactoryAssemblyLoaded();
    }

    private void CaptureBuildingConfiguration(Tile tile, PlanEntry entry)
    {
        if (tile.TryGetComponent(out UpgradesComp upgrades)
                && upgrades.Upgrades != null)
            entry.InstalledUpgradeSlots.AddRange(upgrades.Upgrades);

        if (!IsExternallyManagedProductionQueue(tile.DefinitionId)
                && tile.TryGetComponent(out MultiCrafterComp crafter))
        {
            var production = new ProductionPlan();
            var settings = crafter.Settings;
            if (settings != null)
            {
                production.TaskPriority = settings.TaskPriority;
                production.IdleWarning = settings.IsIdleWarnOn;
                production.IgnoreUnconfigured = settings.IsIgnoreUnconfiguredOn;
            }

            var orders = crafter.Orders;
            if (orders != null)
            {
                for (int i = 0; i < orders.Count; i++)
                {
                    CraftingOrder order = orders[i];
                    if (order?.Demand?.Craftable == null) continue;
                    production.Orders.Add(new ProductionOrderPlan
                    {
                        Order = i,
                        CraftableId = order.Demand.Craftable.Id,
                        DemandType = (int)order.Demand.Type,
                        AmountWanted = order.Demand.AmountWanted,
                        BatchSize = order.Demand.BatchSize,
                        ColorId = order.Demand.ColorId,
                        Paused = order.State == CraftingOrderState.Paused,
                        SuppressDeficitWarning = order.SuppressDeficitWarning
                    });
                }
            }

            entry.Production = production;
        }

        if (tile.TryGetComponent(out TeleporterComp _))
        {
            entry.HasTransporterConfiguration = true;
            if (tile.TryGetComponent(out TileLabelComp label))
                entry.TransporterLabel = label.Label;
        }

        if (tile.TryGetComponent(out PlanterComp planter))
        {
            string plantDefId = GetPlanterPlantDefId(planter);
            if (!planter.IsConfigured || !string.IsNullOrWhiteSpace(plantDefId))
            {
                entry.HasPlanterConfiguration = true;
                entry.PlanterPlantDefId = plantDefId;
                entry.PlanterAutoHarvest = planter.IsAutoHarvest;
            }
        }
    }

    private static int CaptureTransporterLinks(IEnumerable<Tile> capturedTiles,
        IReadOnlyDictionary<int, PlanEntry> entryByTileId)
    {
        int skippedExternal = 0;
        foreach (Tile tile in capturedTiles)
        {
            if (!entryByTileId.TryGetValue(tile.Id, out PlanEntry entry)
                    || !tile.TryGetComponent(out TeleporterComp teleporter)
                    || teleporter.Outbound?.Tile == null) continue;
            Tile target = EntityUtils.ParentObj(teleporter.Outbound.Tile);
            if (target != null && entryByTileId.TryGetValue(target.Id,
                    out PlanEntry targetEntry))
                entry.TransporterOutboundPosition = targetEntry.Position;
            else
                skippedExternal++;
        }
        return skippedExternal;
    }

    private static PlanEntry ClonePlanEntry(PlanEntry source, bool issuedByPlan)
    {
        var result = new PlanEntry
        {
            RoomId = source.RoomId,
            Position = source.Position,
            Rotation = source.Rotation,
            Layer = source.Layer,
            DefId = source.DefId,
            IssuedByPlan = issuedByPlan,
            HasTransporterConfiguration = source.HasTransporterConfiguration,
            TransporterLabel = source.TransporterLabel,
            TransporterOutboundPosition = source.TransporterOutboundPosition,
            Production = CloneProductionPlan(source.Production),
            HasPlanterConfiguration = source.HasPlanterConfiguration,
            PlanterPlantDefId = source.PlanterPlantDefId,
            PlanterAutoHarvest = source.PlanterAutoHarvest
        };
        result.InstalledUpgradeSlots.AddRange(source.InstalledUpgradeSlots);
        return result;
    }

    private static ProductionPlan CloneProductionPlan(ProductionPlan source)
    {
        if (source == null) return null;
        var result = new ProductionPlan
        {
            TaskPriority = source.TaskPriority,
            IdleWarning = source.IdleWarning,
            IgnoreUnconfigured = source.IgnoreUnconfigured
        };
        result.Orders.AddRange(source.Orders.Select(order => new ProductionOrderPlan
        {
            Order = order.Order,
            CraftableId = order.CraftableId,
            DemandType = order.DemandType,
            AmountWanted = order.AmountWanted,
            BatchSize = order.BatchSize,
            ColorId = order.ColorId,
            Paused = order.Paused,
            SuppressDeficitWarning = order.SuppressDeficitWarning
        }));
        return result;
    }

    private bool HasPlannedConfiguration(PlanEntry entry)
    {
        return entry != null && (entry.HasTransporterConfiguration
            || entry.HasPlanterConfiguration
            || (entry.Production != null
                && !IsExternallyManagedProductionQueue(entry.DefId))
            || entry.InstalledUpgradeSlots.Any(id => !string.IsNullOrWhiteSpace(id)));
    }

    private bool RoomHasPendingConfiguration(int roomId)
    {
        return planIndex.EntriesForRoom(roomId).Any(entry =>
            HasPlannedConfiguration(entry)
            && IsEquipmentEntrySatisfied(entry)
            && !IsPlannedConfigurationComplete(entry, out _));
    }

    private bool HasPendingPlannedConfiguration()
    {
        return planIndex.ConfiguredEntries.Any(entry => HasPlannedConfiguration(entry)
            && IsEquipmentEntrySatisfied(entry)
            && !IsPlannedConfigurationComplete(entry, out _));
    }

    private bool IsPlannedConfigurationComplete(PlanEntry entry, out string reason)
    {
        reason = null;
        Tile tile = ExistingAt(entry);
        if (tile?.IsConstructed != true)
        {
            reason = "the building is not constructed";
            return false;
        }

        if (!AreProductionSettingsComplete(tile, entry.Production, out reason))
            return false;
        if (!IsTransporterConfigurationComplete(tile, entry, out reason))
            return false;
        if (!AreUpgradeSettingsComplete(tile, entry, out reason))
            return false;
        if (!IsPlanterConfigurationComplete(tile, entry, out reason))
            return false;
        return true;
    }

    private bool AreProductionSettingsComplete(Tile tile,
        ProductionPlan plan, out string reason)
    {
        reason = null;
        if (plan == null) return true;
        if (IsExternallyManagedProductionQueue(tile?.DefinitionId)) return true;
        if (!tile.TryGetComponent(out MultiCrafterComp crafter))
        {
            reason = "the saved production component is unavailable";
            return false;
        }
        CrafterConfig settings = crafter.Settings;
        if (settings == null)
        {
            reason = "the production controls are not ready";
            return false;
        }
        if (settings.TaskPriority != plan.TaskPriority
                || settings.IsIdleWarnOn != plan.IdleWarning
                || settings.IsIgnoreUnconfiguredOn != plan.IgnoreUnconfigured)
        {
            reason = "production controls have not been restored";
            return false;
        }
        ProductionOrderPlan[] desired = plan.Orders.OrderBy(order => order.Order).ToArray();
        List<CraftingOrder> actualOrders = crafter.Orders;
        if ((actualOrders?.Count ?? 0) != desired.Length)
        {
            reason = "the production order list has not been restored";
            return false;
        }
        for (int i = 0; i < desired.Length; i++)
        {
            CraftingOrder actual = actualOrders[i];
            ProductionOrderPlan expected = desired[i];
            if (actual?.Demand?.Craftable == null
                    || actual.Demand.Craftable.Id != expected.CraftableId
                    || (int)actual.Demand.Type != expected.DemandType
                    || actual.Demand.AmountWanted != expected.AmountWanted
                    || actual.Demand.BatchSize != expected.BatchSize
                    || !SameOptionalText(actual.Demand.ColorId, expected.ColorId)
                    || (actual.State == CraftingOrderState.Paused) != expected.Paused
                    || actual.SuppressDeficitWarning != expected.SuppressDeficitWarning)
            {
                reason = "production order " + (i + 1) + " differs from the blueprint";
                return false;
            }
        }
        return true;
    }

    private static bool AreUpgradeSettingsComplete(Tile tile, PlanEntry entry,
        out string reason)
    {
        reason = null;
        if (!entry.InstalledUpgradeSlots.Any(id => !string.IsNullOrWhiteSpace(id)))
            return true;
        if (!tile.TryGetComponent(out UpgradesComp upgrades))
        {
            reason = "the saved upgrade component is unavailable";
            return false;
        }
        foreach (IGrouping<string, string> planned in entry.InstalledUpgradeSlots
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .GroupBy(id => id, StringComparer.Ordinal))
        {
            int required = planned.Count();
            int installed = CountUpgrade(upgrades.Upgrades, planned.Key);
            if (installed >= required) continue;
            int pending = CountUpgrade(upgrades.PendingUpgrades, planned.Key);
            reason = pending > 0
                ? "upgrade “" + UpgradeName(planned.Key) + "” is being installed ("
                    + installed + " installed, " + pending + " pending, "
                    + required + " planned)"
                : "upgrade “" + UpgradeName(planned.Key) + "” has not been ordered ("
                    + installed + " installed, " + required + " planned)";
            return false;
        }
        return true;
    }

    private bool IsTransporterConfigurationComplete(Tile tile, PlanEntry entry,
        out string reason)
    {
        reason = null;
        if (!entry.HasTransporterConfiguration) return true;
        if (!tile.TryGetComponent(out TeleporterComp teleporter))
        {
            reason = "the saved Transporter component is unavailable";
            return false;
        }
        string actualLabel = tile.TryGetComponent(out TileLabelComp label)
            ? label.Label : null;
        if (!SameOptionalText(actualLabel, entry.TransporterLabel))
        {
            reason = "the Transporter name has not been restored";
            return false;
        }
        if (entry.TransporterOutboundPosition < 0)
        {
            if (teleporter.Outbound == null) return true;
            reason = "the Transporter still has an outbound link not present in the blueprint";
            return false;
        }
        PlanEntry targetEntry = planIndex.TransporterAt(
            entry.TransporterOutboundPosition);
        Tile targetTile = targetEntry == null ? null : ExistingAt(targetEntry);
        if (targetTile?.IsConstructed != true
                || !targetTile.TryGetComponent(out TeleporterComp target))
        {
            reason = "the linked Transporter at "
                + PositionText(entry.TransporterOutboundPosition) + " is not ready";
            return false;
        }
        if (teleporter.Outbound == target) return true;
        reason = "the outbound Transporter link has not been restored";
        return false;
    }

    private static bool IsPlanterConfigurationComplete(Tile tile, PlanEntry entry,
        out string reason)
    {
        reason = null;
        if (!entry.HasPlanterConfiguration) return true;
        if (PlanterPlantDefIdField == null)
        {
            reason = "Core's planter crop setting is unavailable";
            return false;
        }
        if (!tile.TryGetComponent(out PlanterComp planter))
        {
            reason = "the saved planter component is unavailable";
            return false;
        }
        if (!SameOptionalText(GetPlanterPlantDefId(planter), entry.PlanterPlantDefId))
        {
            reason = string.IsNullOrWhiteSpace(entry.PlanterPlantDefId)
                ? "the planter is still configured to grow a crop"
                : "the saved crop has not been selected";
            return false;
        }
        if (planter.IsAutoHarvest != entry.PlanterAutoHarvest)
        {
            reason = "the saved Auto Harvest setting has not been restored";
            return false;
        }
        return true;
    }

    private void BeginPostRotationConfigurationPass()
    {
        postRotationConfigurationCompletedPendingStatus = false;
        lastProductionConfigurationResult = null;
        lastTransporterConfigurationResult = null;
        lastUpgradeConfigurationResult = null;
        lastPlanterConfigurationResult = null;
        postRotationConfigurationPhase = PostRotationConfigurationPhase.Production;
        continuousRoomId = 0;
        ClearContinuousConstructionPreview();
    }

    private void ResetPostRotationConfigurationPass()
    {
        postRotationConfigurationPhase = PostRotationConfigurationPhase.None;
        postRotationConfigurationCompletedPendingStatus = false;
    }

    private bool AdvancePostRotationConfigurationPass(out string outcome)
    {
        ConfigurationPassResult result;
        string passName;
        string nextName;
        switch (postRotationConfigurationPhase)
        {
            case PostRotationConfigurationPhase.Production:
                result = RunProductionConfigurationPass();
                lastProductionConfigurationResult = result;
                passName = "production settings";
                nextName = "Transporter names and links";
                postRotationConfigurationPhase = PostRotationConfigurationPhase.Transporters;
                outcome = "post-rotation-production";
                break;
            case PostRotationConfigurationPhase.Transporters:
                result = RunTransporterConfigurationPass();
                lastTransporterConfigurationResult = result;
                passName = "Transporter names and links";
                nextName = "installed upgrades";
                postRotationConfigurationPhase = PostRotationConfigurationPhase.Upgrades;
                outcome = "post-rotation-transporters";
                break;
            case PostRotationConfigurationPhase.Upgrades:
                result = RunUpgradeConfigurationPass();
                lastUpgradeConfigurationResult = result;
                passName = "installed upgrades";
                nextName = "planter crop and Auto Harvest settings";
                postRotationConfigurationPhase = PostRotationConfigurationPhase.Planters;
                outcome = "post-rotation-upgrades";
                break;
            case PostRotationConfigurationPhase.Planters:
                result = RunPlanterConfigurationPass();
                lastPlanterConfigurationResult = result;
                planterMetadataMissingCount = result.MissingMetadata;
                passName = "planter crop and Auto Harvest settings";
                nextName = null;
                postRotationConfigurationPhase = PostRotationConfigurationPhase.None;
                outcome = "post-rotation-planters";
                break;
            default:
                outcome = "post-rotation-none";
                return true;
        }

        lastStatus = "Post-rotation " + passName + " pass checked "
            + result.Planned + " planned building"
            + (result.Planned == 1 ? string.Empty : "s") + "; changed "
            + result.Changed + "; " + result.Pending + " still waiting.";
        if (result.Changed > 0) RecordActiveWorkRotationChange();
        if (!string.IsNullOrWhiteSpace(result.FirstWait))
            lastStatus += " First wait: " + result.FirstWait;
        if (result.MissingMetadata > 0)
            lastStatus = AppendPlanterMetadataWarning(lastStatus);
        lastStatus += nextName == null
            ? " Returning to the room construction rotation."
            : " Next: " + nextName + ".";
        return nextName == null;
    }

    private string DescribePendingConfigurationCycle()
    {
        var categories = new List<string>();
        var details = new List<string>();
        AddConfigurationWaitSummary(categories, details, "production settings",
            lastProductionConfigurationResult);
        AddConfigurationWaitSummary(categories, details, "Transporter names or links",
            lastTransporterConfigurationResult);
        AddConfigurationWaitSummary(categories, details, "installed upgrades",
            lastUpgradeConfigurationResult);
        AddConfigurationWaitSummary(categories, details, "planter settings",
            lastPlanterConfigurationResult);
        if (categories.Count == 0)
            return "Saved building settings remain incomplete, but the previous passes did not report an individual wait.";

        string status = "Waiting on " + string.Join("; ", categories) + ".";
        if (details.Count > 0)
            status += " First waits: " + string.Join(" ", details);
        return status;
    }

    private static void AddConfigurationWaitSummary(List<string> categories,
        List<string> details, string category, ConfigurationPassResult result)
    {
        if (result?.Pending <= 0) return;
        categories.Add(result.Pending + " building"
            + (result.Pending == 1 ? string.Empty : "s") + " for " + category);
        if (!string.IsNullOrWhiteSpace(result.FirstWait))
            details.Add(result.FirstWait);
    }

    private ConfigurationPassResult RunProductionConfigurationPass()
    {
        var result = new ConfigurationPassResult();
        var seen = new HashSet<int>();
        foreach (PlanEntry entry in planIndex.ProductionEntries.Where(item =>
                         item.Production != null
                         && !IsExternallyManagedProductionQueue(item.DefId))
                     .OrderBy(item => item.RoomId).ThenBy(item => item.Position))
        {
            result.Planned++;
            if (!IsEquipmentEntrySatisfied(entry))
            {
                RecordConfigurationWait(result, entry,
                    "is waiting for its building to be completed");
                continue;
            }
            Tile tile = ExistingAt(entry);
            if (tile == null || !seen.Add(tile.Id)) continue;
            if (AreProductionSettingsComplete(tile, entry.Production, out _)) continue;
            if (TryApplyProductionSettings(tile, entry.Production, out string problem))
                result.Changed++;
            if (!AreProductionSettingsComplete(tile, entry.Production, out string reason))
                RecordConfigurationWait(result, entry, problem ?? reason);
        }
        return result;
    }

    private ConfigurationPassResult RunTransporterConfigurationPass()
    {
        var result = new ConfigurationPassResult();
        var seen = new HashSet<int>();
        foreach (PlanEntry entry in planIndex.TransporterEntries
                     .OrderBy(item => item.RoomId).ThenBy(item => item.Position))
        {
            result.Planned++;
            if (!IsEquipmentEntrySatisfied(entry))
            {
                RecordConfigurationWait(result, entry,
                    "is waiting for its building to be completed");
                continue;
            }
            Tile tile = ExistingAt(entry);
            if (tile == null || !seen.Add(tile.Id)) continue;
            if (IsTransporterConfigurationComplete(tile, entry, out _)) continue;
            if (TryApplyTransporterConfiguration(tile, entry, out string problem))
                result.Changed++;
            if (!IsTransporterConfigurationComplete(tile, entry, out string reason))
                RecordConfigurationWait(result, entry, problem ?? reason);
        }
        return result;
    }

    private ConfigurationPassResult RunUpgradeConfigurationPass()
    {
        var result = new ConfigurationPassResult();
        var seen = new HashSet<int>();
        foreach (PlanEntry entry in planIndex.UpgradeEntries
                     .OrderBy(item => item.RoomId).ThenBy(item => item.Position))
        {
            result.Planned++;
            if (!IsEquipmentEntrySatisfied(entry))
            {
                RecordConfigurationWait(result, entry,
                    "is waiting for its building to be completed");
                continue;
            }
            Tile tile = ExistingAt(entry);
            if (tile == null || !seen.Add(tile.Id)) continue;
            if (AreUpgradeSettingsComplete(tile, entry, out _)) continue;
            if (TryOrderMissingUpgrades(tile, entry, out string problem))
                result.Changed++;
            if (!AreUpgradeSettingsComplete(tile, entry, out string reason))
                RecordConfigurationWait(result, entry, problem ?? reason);
        }
        return result;
    }

    private ConfigurationPassResult RunPlanterConfigurationPass()
    {
        var result = new ConfigurationPassResult();
        var seen = new HashSet<int>();
        foreach (PlanEntry entry in planIndex.PlanterEntries
                     .OrderBy(item => item.RoomId).ThenBy(item => item.Position))
        {
            result.Planned++;
            if (!IsEquipmentEntrySatisfied(entry))
            {
                RecordConfigurationWait(result, entry,
                    "is waiting for its planter to be completed");
                continue;
            }
            Tile tile = ExistingAt(entry);
            if (tile == null || !seen.Add(tile.Id)) continue;
            if (IsPlanterConfigurationComplete(tile, entry, out _)) continue;
            if (TryApplyPlanterConfiguration(tile, entry, out string problem))
                result.Changed++;
            if (!IsPlanterConfigurationComplete(tile, entry, out string reason))
                RecordConfigurationWait(result, entry, problem ?? reason);
        }
        result.MissingMetadata = CountConstructedPlantersWithoutSavedConfiguration();
        return result;
    }

    private int CountConstructedPlantersWithoutSavedConfiguration()
    {
        var seen = new HashSet<int>();
        int count = 0;
        foreach (PlanEntry entry in planIndex.EquipmentEntries.Where(item =>
                     item.Layer == WorldLayer.Objects
                     && !item.HasPlanterConfiguration))
        {
            if (!IsEquipmentEntrySatisfied(entry)) continue;
            Tile tile = ExistingAt(entry);
            if (tile?.IsConstructed != true || !seen.Add(tile.Id)) continue;
            if (tile.TryGetComponent(out PlanterComp _)) count++;
        }
        return count;
    }

    private string AppendPlanterMetadataWarning(string status)
    {
        if (planterMetadataMissingCount <= 0) return status;
        return status + " " + planterMetadataMissingCount + " constructed planter"
            + (planterMetadataMissingCount == 1 ? " has" : "s have")
            + " no saved crop or Auto Harvest settings in this blueprint. "
            + "If this blueprint was captured before PO4, recapture the original "
            + "constructed ship with the current version.";
    }

    private void RecordConfigurationWait(ConfigurationPassResult result,
        PlanEntry entry, string reason)
    {
        result.Pending++;
        result.FirstWait ??= ConfigurationStatus(entry,
            reason ?? "is waiting for its saved configuration");
    }

    private bool TryApplyProductionSettings(Tile tile, ProductionPlan plan,
        out string problem)
    {
        problem = null;
        if (IsExternallyManagedProductionQueue(tile?.DefinitionId)) return true;
        if (!tile.TryGetComponent(out MultiCrafterComp crafter))
        {
            problem = "cannot restore production settings because its production component is unavailable";
            return false;
        }
        CrafterConfig settings = crafter.Settings;
        if (settings == null || crafter.UI == null)
        {
            problem = "is waiting for its production controls to become ready";
            return false;
        }
        ProductionOrderPlan[] desired = plan.Orders.OrderBy(order => order.Order).ToArray();
        var craftables = new List<Craftable>(desired.Length);
        foreach (ProductionOrderPlan order in desired)
        {
            if (!Enum.IsDefined(typeof(CraftingDemandType), order.DemandType))
            {
                problem = "cannot restore recipe “" + order.CraftableId
                    + "” because its saved production mode is invalid";
                return false;
            }
            Craftable craftable = Craftable.Find(Hashes.S(order.CraftableId));
            if (craftable == null)
            {
                problem = "cannot restore missing recipe “" + order.CraftableId + "”";
                return false;
            }
            if (settings.CompatibleTypes?.Contains(craftable.Type) != true)
            {
                problem = "cannot use saved recipe “" + craftable.Id
                    + "” with this building definition";
                return false;
            }
            craftables.Add(craftable);
        }

        List<CraftingOrder> actualOrders = crafter.Orders;
        bool sameRecipes = (actualOrders?.Count ?? 0) == desired.Length;
        for (int i = 0; sameRecipes && i < desired.Length; i++)
            sameRecipes = actualOrders[i]?.Demand?.Craftable?.Id == desired[i].CraftableId;
        if (!sameRecipes)
        {
            if ((actualOrders?.Count ?? 0) > 0) crafter.UI.StopAll();
            for (int i = 0; i < desired.Length; i++)
            {
                var demand = CraftingDemand.CreateFor(craftables[i]);
                demand.Type = (CraftingDemandType)desired[i].DemandType;
                demand.AmountWanted = desired[i].AmountWanted;
                demand.SetBatchSize(Math.Max(1, desired[i].BatchSize));
                demand.ColorId = EmptyToNull(desired[i].ColorId);
                S.Query.CreateCraftingOrder.Ask(crafter, demand);
            }
            actualOrders = crafter.Orders;
            if ((actualOrders?.Count ?? 0) != desired.Length)
            {
                problem = "is waiting for its production order list to become ready";
                return false;
            }
        }

        settings.TaskPriority.Set(plan.TaskPriority);
        crafter.Operatable?.SetPriority(plan.TaskPriority);
        settings.IsIdleWarnOn = plan.IdleWarning;
        settings.IsIgnoreUnconfiguredOn = plan.IgnoreUnconfigured;
        if (plan.IgnoreUnconfigured || desired.Length > 0) crafter.UI.MarkConfigured();
        if (!plan.IdleWarning) crafter.UI.HideIdleNotification();

        for (int i = 0; i < desired.Length; i++)
        {
            CraftingOrder actual = actualOrders[i];
            ProductionOrderPlan expected = desired[i];
            if (actual?.Demand == null)
            {
                problem = "is waiting for production order " + (i + 1) + " to become ready";
                return false;
            }
            if ((int)actual.Demand.Type != expected.DemandType)
                actual.ChangeDemandType((CraftingDemandType)expected.DemandType);
            actual.Demand.AmountWanted = expected.AmountWanted;
            if (actual.Demand.BatchSize != expected.BatchSize)
                actual.UpdateBatchSize(Math.Max(1, expected.BatchSize));
            actual.Demand.ColorId = EmptyToNull(expected.ColorId);
            actual.SuppressDeficitWarning = expected.SuppressDeficitWarning;
            actual.State = expected.Paused
                ? CraftingOrderState.Paused : CraftingOrderState.InQueue;
            actual.Ping();
        }
        crafter.UI.UpdateProgressBar();
        crafter.UpdateExtraInfo();
        return true;
    }

    private bool TryApplyTransporterConfiguration(Tile tile, PlanEntry entry,
        out string problem)
    {
        problem = null;
        if (!tile.TryGetComponent(out TeleporterComp teleporter))
        {
            problem = "cannot restore its Transporter configuration because the component is unavailable";
            return false;
        }
        if (tile.TryGetComponent(out TileLabelComp label)
                && !SameOptionalText(label.Label, entry.TransporterLabel))
        {
            label.SetLabel(EmptyToNull(entry.TransporterLabel));
            S.Sig.LabelChanged.Send(label);
        }
        if (entry.TransporterOutboundPosition < 0)
        {
            if (teleporter.Outbound != null) teleporter.UnsetOutbound();
            return true;
        }
        PlanEntry targetEntry = planIndex.TransporterAt(
            entry.TransporterOutboundPosition);
        Tile targetTile = targetEntry == null ? null : ExistingAt(targetEntry);
        if (targetTile?.IsConstructed != true
                || !targetTile.TryGetComponent(out TeleporterComp target))
        {
            problem = "is waiting for linked Transporter at "
                + PositionText(entry.TransporterOutboundPosition) + " to be constructed";
            return false;
        }
        if (teleporter.Outbound != target) teleporter.SetOutbound(target);
        return true;
    }

    private static bool TryOrderMissingUpgrades(Tile tile, PlanEntry entry,
        out string problem)
    {
        problem = null;
        if (!tile.TryGetComponent(out UpgradesComp upgrades))
        {
            problem = "cannot restore installed upgrades because the component is unavailable";
            return false;
        }
        bool ordered = false;
        foreach (IGrouping<string, string> planned in entry.InstalledUpgradeSlots
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .GroupBy(id => id, StringComparer.Ordinal))
        {
            string expected = planned.Key;
            int needed = planned.Count()
                - CountUpgrade(upgrades.Upgrades, expected)
                - CountUpgrade(upgrades.PendingUpgrades, expected);
            if (needed <= 0) continue;
            Upgrade upgrade = UpgradeRegistry.Get(expected);
            if (upgrade == null)
            {
                problem = "cannot restore missing upgrade definition “" + expected + "”";
                return false;
            }
            if (!upgrade.IsCompatibleWith(tile))
            {
                problem = "cannot restore “" + UpgradeName(expected)
                    + "” because it is no longer compatible with this building";
                return false;
            }
            if (!upgrade.CanInstallNow(tile))
            {
                problem = "cannot yet order “" + UpgradeName(expected)
                    + "” because its Core installation requirements are not met";
                return false;
            }
            for (int i = 0; i < needed; i++)
            {
                if (upgrades.FirstEmptySlot < 0)
                {
                    problem = "cannot restore “" + UpgradeName(expected)
                        + "” because no upgrade slot is available";
                    return false;
                }
                if (!upgrades.OrderUpgrade(upgrade))
                {
                    problem = "Core did not accept the “" + UpgradeName(expected)
                        + "” installation request";
                    return false;
                }
                ordered = true;
            }
        }
        if (!ordered)
        {
            problem = "is waiting for its queued upgrades to be installed";
            return false;
        }
        return true;
    }

    private bool TryApplyPlanterConfiguration(Tile tile, PlanEntry entry,
        out string problem)
    {
        problem = null;
        if (PlanterPlantDefIdField == null)
        {
            problem = "cannot restore planter settings because Core's crop setting is unavailable";
            return false;
        }
        if (!tile.TryGetComponent(out PlanterComp planter))
        {
            problem = "cannot restore planter settings because its planter component is unavailable";
            return false;
        }

        string currentPlant = GetPlanterPlantDefId(planter);
        string desiredPlant = EmptyToNull(entry.PlanterPlantDefId);
        if (desiredPlant == null)
        {
            if (planter.IsConfigured) planter.StopGrowing();
        }
        else if (!string.Equals(currentPlant, desiredPlant, StringComparison.Ordinal))
        {
            Def plantDef = The.Defs.TryGet(desiredPlant);
            if (plantDef == null)
            {
                problem = "cannot restore missing crop definition “" + desiredPlant + "”";
                return false;
            }
            if (!S.Research.IsDefUnlocked(plantDef))
            {
                problem = "is waiting for crop “" + plantDef.NameT + "” to be unlocked";
                return false;
            }
            PlanterComp.EnsureAvailablePlants();
            PlantComp plant = PlanterComp.AvailablePlants.FirstOrDefault(item =>
                item?.Def?.Id == desiredPlant);
            if (plant == null)
            {
                problem = "cannot restore crop “" + plantDef.NameT
                    + "” because Core does not list it as plantable";
                return false;
            }
            if (!planter.CanGrow(plant))
            {
                problem = "cannot grow saved crop “" + plantDef.NameT
                    + "” in this planter size";
                return false;
            }
            if (planter.IsConfigured) planter.StopGrowing();
            planter.StartGrowing(plant);
        }
        planter.IsAutoHarvest = entry.PlanterAutoHarvest;
        return true;
    }

    private static string GetPlanterPlantDefId(PlanterComp planter)
    {
        return PlanterPlantDefIdField?.GetValue(planter) as string;
    }

    private static int CountUpgrade(IEnumerable<string> upgrades, string id)
    {
        return upgrades?.Count(value => string.Equals(value, id,
            StringComparison.Ordinal)) ?? 0;
    }

    private string DescribeConfigurationWait(int roomId)
    {
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == roomId);
        foreach (PlanEntry entry in planIndex.EntriesForRoom(roomId)
                     .Where(HasPlannedConfiguration).OrderBy(item => item.Position))
            if (IsEquipmentEntrySatisfied(entry)
                    && !IsPlannedConfigurationComplete(entry, out string reason))
                return ConfigurationStatus(entry, reason)
                    + " The next post-rotation configuration pass will retry it.";
        return (room?.Name ?? "The active room")
            + " is waiting for its saved building settings to be restored.";
    }

    private string ConfigurationStatus(PlanEntry entry, string detail)
    {
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == entry.RoomId);
        Def def = The.Defs.TryGet(entry.DefId);
        return (room?.Name ?? "The active room") + ": "
            + (def?.NameT ?? entry.DefId) + " at " + PositionText(entry.Position)
            + " " + (detail ?? "is waiting for its saved configuration").TrimEnd('.') + ".";
    }

    private static string UpgradeName(string id)
    {
        return UpgradeRegistry.Get(id)?.Title ?? id;
    }

    private static bool SameOptionalText(string left, string right)
    {
        return string.Equals(EmptyToNull(left), EmptyToNull(right),
            StringComparison.Ordinal);
    }

    private static string EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

