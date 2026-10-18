using System;
using System.Collections.Generic;
using System.Linq;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.AI;
using Game.Utils;
using HarmonyLib;
using KL.Clock;
using KL.Grid;
using KL.Utils;

namespace ReconnectTeleporterConnections;

public sealed class ReconnectTeleporterConnectionsSys : GameSystem, ISaveable
{
    public const string SysId = "ReconnectTeleporterConnectionsSys";
    public const string ConsumerId = "ReconnectTeleporterConnections";
    public const string RequiredUtilitiesVersion = "1.4.0";

    private const string HarmonyId = "ReconnectTeleporterConnections.Runtime";
    private const int SaveSchemaVersion = 2;
    private const int MaximumHistoryEntries = 80;
    private const double ReconcileIntervalSeconds = 5d;

    private static Harmony harmony;

    private readonly Dictionary<int, TrackedTeleporter> records = new();
    private readonly Dictionary<int, int> recordIdByEntityId = new();
    private readonly HashSet<int> relocatingSourceEntityIds = new();
    private readonly HashSet<int> playerRemovalEntityIds = new();
    private readonly List<RecoveryHistoryEntry> history = new();
    private readonly List<TeleporterComp> teleporterBuffer = new();

    private GameState loadedState;
    private TeleporterConnectionPanel panel;
    private int nextRecordId = 1;
    private int nextDefaultLabel = 1;
    private long nextHistorySequence = 1;
    private int selectedGraphEntityId;
    private double nextReconcileAt;
    private bool isReconciling;
    private bool isRestoring;
    private bool isApplyingLabel;

    public static ReconnectTeleporterConnectionsSys Instance { get; private set; }

    public override string Id => SysId;
    public override bool SkipInSandbox => false;

    internal int SelectedGraphEntityId => selectedGraphEntityId;
    internal bool HasConnectionPanel => panel != null;
    internal int PendingRecoveryCount => records.Values.Count(record => record.IsMissing);
    internal int LiveConnectionCount => records.Values.Count(record =>
        !record.IsMissing && record.DesiredOutboundId != 0);

    [SelfInit]
    private static void Register()
    {
        GameSystems.Register(SysId,
            () => new ReconnectTeleporterConnectionsSys());
    }

    protected override void OnInitialize()
    {
        Instance = this;
        if (harmony == null)
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(ReconnectTeleporterConnectionsSys).Assembly);
        }

        if (AutoUtilitiesApi.TryRegisterConsumer(
                ConsumerId,
                "Reconnect Teleporter Connections",
                typeof(ReconnectTeleporterConnectionsSys).Assembly
                    .GetName().Version.ToString(3),
                RequiredUtilitiesVersion))
        {
            panel = new TeleporterConnectionPanel(this);
        }

        S.Sig.AfterLoadState.AddListener(OnLoadState);
    }

    private void OnLoadState(GameState state)
    {
        DetachStateListeners();
        panel?.Close();
        loadedState = state;
        nextReconcileAt = GameTimer.realtimeSinceStartupAsDouble + 1d;
        state.Sig.TileRelocated.AddListener(OnTileRelocated);
        state.Sig.EntityRemoved.AddListener(OnEntityRemoved);
        state.Sig.ConstructionCompleted.AddListener(OnConstructionCompleted);
        state.Sig.LabelChanged.AddListener(OnLabelChanged);
        state.Clock.OnTick.AddListener(OnTick);
        state.Clock.OnNextUpdate(ReconcileAll);
    }

    private void DetachStateListeners()
    {
        if (loadedState == null) return;
        loadedState.Sig.TileRelocated.RemoveListener(OnTileRelocated);
        loadedState.Sig.EntityRemoved.RemoveListener(OnEntityRemoved);
        loadedState.Sig.ConstructionCompleted.RemoveListener(
            OnConstructionCompleted);
        loadedState.Sig.LabelChanged.RemoveListener(OnLabelChanged);
        loadedState.Clock.OnTick.RemoveListener(OnTick);
        loadedState = null;
    }

    private void OnTick(long ticks)
    {
        double now = GameTimer.realtimeSinceStartupAsDouble;
        if (now < nextReconcileAt) return;
        nextReconcileAt = now + ReconcileIntervalSeconds;
        ReconcileAll();
    }

    internal void ReconcileAll()
    {
        if (loadedState == null || isReconciling) return;
        isReconciling = true;
        bool changed = false;
        try
        {
            teleporterBuffer.Clear();
            loadedState.Components.FindAll(teleporterBuffer);
            var liveByEntityId = new Dictionary<int, TeleporterComp>();
            foreach (TeleporterComp teleporter in teleporterBuffer)
            {
                if (!IsConstructedTeleporter(teleporter)) continue;
                liveByEntityId[teleporter.Entity.Id] = teleporter;
            }

            foreach (TrackedTeleporter record in records.Values)
            {
                if (record.EntityId == 0) continue;
                if (liveByEntityId.ContainsKey(record.EntityId)) continue;
                recordIdByEntityId.Remove(record.EntityId);
                record.EntityId = 0;
                changed = true;
            }

            foreach (TeleporterComp teleporter in liveByEntityId.Values)
            {
                TrackedTeleporter record = FindByEntityId(
                    teleporter.Entity.Id);
                if (record == null)
                {
                    record = FindUniqueMissingByPosition(
                        teleporter.Tile.PosIdx);
                    if (record != null)
                    {
                        AttachReplacement(record, teleporter,
                            "matched the tracked position");
                        changed = true;
                    }
                    else
                    {
                        string liveLabel = GetLabel(teleporter);
                        record = FindUniqueMissingByLabel(liveLabel);
                        if (record != null)
                        {
                            AttachReplacement(record, teleporter,
                                "matched the tracked label");
                            changed = true;
                        }
                        else
                        {
                            record = CreateRecord(teleporter);
                            changed = true;
                        }
                    }
                }

                changed |= RefreshRecordIdentity(record, teleporter);
                changed |= EnsureLabel(teleporter, record);
            }

            foreach (TeleporterComp teleporter in liveByEntityId.Values)
            {
                TrackedTeleporter source = FindByEntityId(
                    teleporter.Entity.Id);
                if (source == null) continue;
                int rawTargetEntityId = teleporter.outboundId;
                if (rawTargetEntityId <= 0) continue;
                TrackedTeleporter target = FindByEntityId(rawTargetEntityId);
                if (target != null
                    && source.DesiredOutboundId != target.Id)
                {
                    source.DesiredOutboundId = target.Id;
                    changed = true;
                }
            }

            int restored = RestoreApplicableConnections();
            changed |= restored > 0;
        }
        catch (Exception exception)
        {
            D.LogEx(exception,
                "Reconnect Teleporter Connections reconciliation failed");
        }
        finally
        {
            isReconciling = false;
            if (changed) RefreshUi();
        }
    }

    private void OnConstructionCompleted(Tile tile)
    {
        if (tile?.IsActive != true
            || !tile.TryGetComponent<TeleporterComp>(out var teleporter))
        {
            return;
        }

        HandleBuiltTeleporter(teleporter);
    }

    private void HandleBuiltTeleporter(TeleporterComp teleporter)
    {
        if (!IsConstructedTeleporter(teleporter)) return;

        TrackedTeleporter record = FindByEntityId(teleporter.Entity.Id);
        if (record == null)
        {
            record = FindUniqueMissingByPosition(teleporter.Tile.PosIdx);
            if (record != null)
            {
                AttachReplacement(record, teleporter,
                    "was rebuilt at its tracked position");
            }
            else
            {
                string currentLabel = GetLabel(teleporter);
                record = FindUniqueMissingByLabel(currentLabel);
                if (record != null)
                {
                    AttachReplacement(record, teleporter,
                        "was rebuilt with its tracked label");
                }
                else
                {
                    record = CreateRecord(teleporter);
                }
            }
        }

        RefreshRecordIdentity(record, teleporter);
        EnsureLabel(teleporter, record);
        CaptureLiveOutbound(record, teleporter);
        RestoreApplicableConnections();
        RefreshUi();
    }

    private void OnTileRelocated(int oldEntityId, int newEntityId)
    {
        Tile oldTile = loadedState?.FindEntity<Tile>(oldEntityId);
        Tile newTile = loadedState?.FindEntity<Tile>(newEntityId);
        if (oldTile == null || newTile == null
            || !oldTile.TryGetComponent<TeleporterComp>(out var oldTeleporter)
            || !newTile.TryGetComponent<TeleporterComp>(out var newTeleporter))
        {
            return;
        }

        TrackedTeleporter record = FindByEntityId(oldEntityId)
            ?? CreateRecord(oldTeleporter, oldEntityId);
        int oldPosition = record.Position;
        CaptureLiveOutbound(record, oldTeleporter);

        relocatingSourceEntityIds.Add(oldEntityId);
        recordIdByEntityId.Remove(oldEntityId);
        record.EntityId = newEntityId;
        record.Position = newTile.PosIdx;
        recordIdByEntityId[newEntityId] = record.Id;

        int restored = 0;
        isRestoring = true;
        try
        {
            TeleporterComp oldTarget = oldTeleporter.Outbound;
            if (oldTarget != null)
            {
                newTeleporter.SetOutbound(oldTarget);
                restored++;
            }

            foreach (TrackedTeleporter sourceRecord in records.Values)
            {
                if (sourceRecord.Id == record.Id
                    || sourceRecord.IsMissing
                    || sourceRecord.DesiredOutboundId != record.Id)
                {
                    continue;
                }

                TeleporterComp source = Resolve(sourceRecord);
                if (source == null) continue;
                TeleporterComp current = source.Outbound;
                if (current == null || current.Entity.Id == oldEntityId)
                {
                    source.SetOutbound(newTeleporter);
                    restored++;
                }
            }
        }
        finally
        {
            isRestoring = false;
        }

        AddHistory("Relocated " + Describe(record, oldPosition)
            + " to " + Describe(record) + ". Preserved "
            + DescribeConnectionDependencies(record) + "; transferred "
            + restored + " directed connection"
            + (restored == 1 ? "." : "s."), newTile, true,
            "Relocation", record.Id);

        loadedState.Clock.OnNextUpdate(() =>
        {
            if (newTeleporter.Entity?.IsActive != true) return;
            RefreshRecordIdentity(record, newTeleporter);
            EnsureLabel(newTeleporter, record);
            RestoreApplicableConnections();
            RefreshUi();
        });
    }

    private void OnEntityRemoved(Entity entity)
    {
        if (entity == null
            || !entity.TryGetComponent<TeleporterComp>(out var teleporter))
        {
            return;
        }

        int oldEntityId = entity.Id;
        if (relocatingSourceEntityIds.Remove(oldEntityId)) return;

        TrackedTeleporter record = FindByEntityId(oldEntityId)
            ?? CreateRecord(teleporter, oldEntityId);
        CaptureLiveOutbound(record, teleporter);
        record.Position = entity.PosIdx;
        string lastLabel = GetLabel(teleporter);
        if (!string.IsNullOrWhiteSpace(lastLabel)) record.Label = lastLabel;

        if (playerRemovalEntityIds.Remove(oldEntityId))
        {
            string description = Describe(record);
            string dependencies = DescribeConnectionDependencies(record);
            ForgetRecord(record.Id);
            AddHistory(description + " was player-deconstructed. "
                + "Stopped tracking it. "
                + ClearedDependenciesSentence(dependencies),
                category: "Removal", primaryRecordId: record.Id);
        }
        else
        {
            recordIdByEntityId.Remove(oldEntityId);
            record.EntityId = 0;
            AddHistory(Describe(record)
                + " was destroyed. Waiting to restore "
                + DescribeConnectionDependencies(record) + ". "
                + DescribeReplacementMatch(record) + ".",
                category: "Recovery", primaryRecordId: record.Id);
        }

        RefreshUi();
    }

    private void OnLabelChanged(TileLabelComp labelComponent)
    {
        if (isApplyingLabel || labelComponent?.Tile?.IsConstructed != true
            || !labelComponent.Tile.TryGetComponent<TeleporterComp>(
                out var teleporter))
        {
            return;
        }

        TrackedTeleporter record = FindByEntityId(teleporter.Entity.Id)
            ?? CreateRecord(teleporter);
        string newLabel = NormalizeLabel(labelComponent.Label);
        if (string.IsNullOrEmpty(newLabel))
        {
            loadedState.Clock.OnNextUpdate(() =>
            {
                if (teleporter.Entity?.IsActive != true) return;
                EnsureLabel(teleporter, record);
                RefreshUi();
            });
            return;
        }

        if (record.HasGeneratedLabel
            && !string.Equals(record.Label, newLabel,
                StringComparison.Ordinal))
        {
            TrackedTeleporter missing = FindUniqueMissingByLabel(newLabel,
                record.Id);
            if (missing != null && CanMergeFreshRecord(record))
            {
                MergeReplacement(record, missing, teleporter);
                RestoreApplicableConnections();
                RefreshUi();
                return;
            }
        }

        if (!string.Equals(record.Label, newLabel, StringComparison.Ordinal))
        {
            string oldLabel = record.Label;
            record.Label = newLabel;
            record.HasGeneratedLabel = false;
            record.Position = teleporter.Tile.PosIdx;
            AddHistory("Renamed "
                + (string.IsNullOrWhiteSpace(oldLabel)
                    ? Pos.String(record.Position)
                    : oldLabel + " " + Pos.String(record.Position))
                + " to " + newLabel + ".",
                category: "Label", primaryRecordId: record.Id);
            RefreshUi();
        }
    }

    internal void OnOutboundChanged(TeleporterComp source,
        TeleporterComp target)
    {
        if (loadedState == null || source?.Entity == null) return;
        int sourceEntityId = source.Entity.Id;
        if (sourceEntityId <= 0) return;

        TrackedTeleporter sourceRecord = FindByEntityId(sourceEntityId)
            ?? CreateRecord(source);
        int oldDesiredId = sourceRecord.DesiredOutboundId;
        TrackedTeleporter previousTarget = oldDesiredId == 0
            ? null
            : records.TryGetValue(oldDesiredId, out var previous)
                ? previous
                : null;
        TrackedTeleporter targetRecord = target == null
            ? null
            : FindByEntityId(target.Entity.Id) ?? CreateRecord(target);
        sourceRecord.DesiredOutboundId = targetRecord?.Id ?? 0;

        if (!isRestoring && oldDesiredId != sourceRecord.DesiredOutboundId)
        {
            if (targetRecord == null)
            {
                AddHistory("Unlinked " + Describe(sourceRecord)
                    + (previousTarget == null
                        ? "."
                        : " from " + Describe(previousTarget) + "."),
                    category: "Connection",
                    primaryRecordId: sourceRecord.Id,
                    relatedRecordId: previousTarget?.Id ?? 0);
            }
            else
            {
                string action = previousTarget == null
                    ? "Linked " + Describe(sourceRecord) + " to "
                        + Describe(targetRecord) + "."
                    : "Relinked " + Describe(sourceRecord) + " from "
                        + Describe(previousTarget) + " to "
                        + Describe(targetRecord) + ".";
                AddHistory(action, category: "Connection",
                    primaryRecordId: sourceRecord.Id,
                    relatedRecordId: targetRecord.Id);
            }
        }

        RefreshUi();
    }

    internal void MarkPlayerRemoval(ConstructableComp constructable,
        bool autoRebuild)
    {
        if (constructable?.Tile?.IsConstructed != true
            || constructable.Tile.Teleporter == null)
        {
            return;
        }

        AIGoal goal = constructable.Entity.Goal;
        bool playerDemolition = !autoRebuild
            && goal?.IsByPlayer == true
            && goal.JobIdH == JobTypeIdH.Demolition;
        bool sandboxRemoval = A.InstantBuild && !autoRebuild
            && constructable.Tile.Health.IsDestroyed == false;
        if (playerDemolition || sandboxRemoval)
        {
            playerRemovalEntityIds.Add(constructable.Entity.Id);
        }
    }

    internal void MarkInstantPlayerRemoval(Tile tile, bool autoRebuild)
    {
        if (!A.InstantBuild || autoRebuild || tile?.IsConstructed != true
            || tile.Health.IsDestroyed
            || relocatingSourceEntityIds.Contains(tile.Id)
            || tile.Teleporter == null)
        {
            return;
        }
        playerRemovalEntityIds.Add(tile.Id);
    }

    private bool EnsureLabel(TeleporterComp teleporter,
        TrackedTeleporter record)
    {
        if (teleporter?.Tile == null || record == null) return false;
        if (!teleporter.Tile.TryGetComponent<TileLabelComp>(out var labelComp))
            return false;

        string current = NormalizeLabel(labelComp.Label);
        if (!string.IsNullOrEmpty(current))
        {
            if (string.Equals(record.Label, current, StringComparison.Ordinal))
                return false;
            record.Label = current;
            record.HasGeneratedLabel = false;
            return true;
        }

        string wanted = NormalizeLabel(record.Label);
        if (string.IsNullOrEmpty(wanted))
        {
            wanted = NextUniqueDefaultLabel();
            record.Label = wanted;
            record.HasGeneratedLabel = true;
        }

        isApplyingLabel = true;
        try
        {
            labelComp.SetLabel(wanted);
            loadedState.Sig.LabelChanged.Send(labelComp);
        }
        finally
        {
            isApplyingLabel = false;
        }

        AddHistory("Applied label " + wanted + " to "
            + Pos.String(teleporter.Tile.PosIdx) + ".",
            category: "Label", primaryRecordId: record.Id);
        return true;
    }

    private string NextUniqueDefaultLabel()
    {
        var used = new HashSet<string>(records.Values
            .Select(record => NormalizeLabel(record.Label))
            .Where(label => !string.IsNullOrEmpty(label)),
            StringComparer.OrdinalIgnoreCase);

        string candidate;
        do
        {
            candidate = "Teleporter " + nextDefaultLabel++;
        }
        while (used.Contains(candidate));
        return candidate;
    }

    private int RestoreApplicableConnections()
    {
        if (loadedState == null) return 0;
        int restored = 0;
        isRestoring = true;
        try
        {
            foreach (TrackedTeleporter sourceRecord in records.Values
                         .OrderBy(record => record.Id))
            {
                if (sourceRecord.IsMissing
                    || sourceRecord.DesiredOutboundId == 0
                    || !records.TryGetValue(sourceRecord.DesiredOutboundId,
                        out TrackedTeleporter targetRecord)
                    || targetRecord.IsMissing
                    || sourceRecord.Id == targetRecord.Id)
                {
                    continue;
                }

                TeleporterComp source = Resolve(sourceRecord);
                TeleporterComp target = Resolve(targetRecord);
                if (source == null || target == null) continue;

                TeleporterComp current = source.Outbound;
                if (current != null) continue;
                source.SetOutbound(target);
                restored++;
                AddHistory("Restored " + Describe(sourceRecord) + " -> "
                    + Describe(targetRecord) + ".", source.Tile, true,
                    "Recovery", sourceRecord.Id, targetRecord.Id);
            }
        }
        finally
        {
            isRestoring = false;
        }
        return restored;
    }

    private void AttachReplacement(TrackedTeleporter record,
        TeleporterComp teleporter, string reason)
    {
        record.EntityId = teleporter.Entity.Id;
        record.Position = teleporter.Tile.PosIdx;
        recordIdByEntityId[record.EntityId] = record.Id;

        string currentLabel = GetLabel(teleporter);
        if (string.IsNullOrWhiteSpace(currentLabel))
        {
            EnsureLabel(teleporter, record);
        }
        else if (!string.Equals(currentLabel, record.Label,
                     StringComparison.Ordinal))
        {
            record.Label = currentLabel;
            record.HasGeneratedLabel = false;
        }

        AddHistory("Recovered " + Describe(record) + ": " + reason + ".",
            teleporter.Tile, true, "Recovery", record.Id);
    }

    private void MergeReplacement(TrackedTeleporter fresh,
        TrackedTeleporter missing, TeleporterComp teleporter)
    {
        records.Remove(fresh.Id);
        recordIdByEntityId[teleporter.Entity.Id] = missing.Id;
        missing.EntityId = teleporter.Entity.Id;
        missing.Position = teleporter.Tile.PosIdx;
        missing.Label = GetLabel(teleporter);
        missing.HasGeneratedLabel = false;
        AddHistory("Recovered " + Describe(missing)
            + " after its label was applied to a new Teleporter.",
            teleporter.Tile, true, "Recovery", missing.Id);
    }

    private bool CanMergeFreshRecord(TrackedTeleporter record)
    {
        if (record == null || record.IsMissing
            || record.DesiredOutboundId != 0)
        {
            return false;
        }
        return records.Values.All(other =>
            other.Id == record.Id || other.DesiredOutboundId != record.Id);
    }

    private void ForgetRecord(int recordId)
    {
        if (!records.TryGetValue(recordId, out TrackedTeleporter record))
            return;
        if (record.EntityId != 0) recordIdByEntityId.Remove(record.EntityId);
        records.Remove(recordId);
        foreach (TrackedTeleporter other in records.Values)
        {
            if (other.DesiredOutboundId == recordId)
                other.DesiredOutboundId = 0;
        }
    }

    internal void ForgetMissingRecord(int recordId)
    {
        if (!records.TryGetValue(recordId, out TrackedTeleporter record)
            || !record.IsMissing)
        {
            return;
        }
        string description = Describe(record);
        string dependencies = DescribeConnectionDependencies(record);
        ForgetRecord(recordId);
        AddHistory("Forgot pending recovery record " + description + ". "
            + ClearedDependenciesSentence(dependencies),
            category: "Recovery", primaryRecordId: recordId);
        RefreshUi();
    }

    private TrackedTeleporter CreateRecord(TeleporterComp teleporter,
        int explicitEntityId = 0)
    {
        int entityId = explicitEntityId > 0
            ? explicitEntityId
            : teleporter?.Entity?.Id ?? 0;
        if (entityId > 0)
        {
            TrackedTeleporter existing = FindByEntityId(entityId);
            if (existing != null) return existing;
        }

        var record = new TrackedTeleporter
        {
            Id = nextRecordId++,
            EntityId = entityId,
            Position = teleporter?.Tile?.PosIdx ?? 0,
            Label = GetLabel(teleporter),
            DesiredOutboundId = 0,
            HasGeneratedLabel = false
        };
        records.Add(record.Id, record);
        if (entityId > 0) recordIdByEntityId[entityId] = record.Id;
        return record;
    }

    private bool RefreshRecordIdentity(TrackedTeleporter record,
        TeleporterComp teleporter)
    {
        bool changed = false;
        if (record.EntityId != teleporter.Entity.Id)
        {
            if (record.EntityId > 0) recordIdByEntityId.Remove(record.EntityId);
            record.EntityId = teleporter.Entity.Id;
            recordIdByEntityId[record.EntityId] = record.Id;
            changed = true;
        }
        if (record.Position != teleporter.Tile.PosIdx)
        {
            record.Position = teleporter.Tile.PosIdx;
            changed = true;
        }
        string label = GetLabel(teleporter);
        if (!string.IsNullOrWhiteSpace(label)
            && !string.Equals(record.Label, label, StringComparison.Ordinal))
        {
            record.Label = label;
            record.HasGeneratedLabel = false;
            changed = true;
        }
        return changed;
    }

    private void CaptureLiveOutbound(TrackedTeleporter record,
        TeleporterComp teleporter)
    {
        int targetEntityId = teleporter?.outboundId ?? 0;
        if (targetEntityId <= 0) return;
        TrackedTeleporter target = FindByEntityId(targetEntityId);
        if (target != null) record.DesiredOutboundId = target.Id;
    }

    private TrackedTeleporter FindByEntityId(int entityId)
    {
        if (entityId <= 0
            || !recordIdByEntityId.TryGetValue(entityId, out int recordId))
        {
            return null;
        }
        return records.TryGetValue(recordId, out TrackedTeleporter record)
            ? record
            : null;
    }

    private TrackedTeleporter FindUniqueMissingByPosition(int position)
    {
        TrackedTeleporter match = null;
        foreach (TrackedTeleporter record in records.Values)
        {
            if (!record.IsMissing || record.Position != position) continue;
            if (match != null) return null;
            match = record;
        }
        return match;
    }

    private TrackedTeleporter FindUniqueMissingByLabel(string label,
        int excludedRecordId = 0)
    {
        label = NormalizeLabel(label);
        if (string.IsNullOrEmpty(label)) return null;
        TrackedTeleporter match = null;
        foreach (TrackedTeleporter record in records.Values)
        {
            if (record.Id == excludedRecordId || !record.IsMissing
                || !string.Equals(NormalizeLabel(record.Label), label,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (match != null) return null;
            match = record;
        }
        return match;
    }

    private TeleporterComp Resolve(TrackedTeleporter record)
    {
        if (loadedState == null || record?.EntityId <= 0) return null;
        TeleporterComp teleporter = loadedState.Components
            .Find<TeleporterComp>(record.EntityId);
        return IsConstructedTeleporter(teleporter) ? teleporter : null;
    }

    private static bool IsConstructedTeleporter(TeleporterComp teleporter)
    {
        return teleporter?.Entity?.IsActive == true
            && teleporter.Tile?.IsConstructed == true;
    }

    private static string NormalizeLabel(string label)
    {
        return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
    }

    internal static string GetLabel(TeleporterComp teleporter)
    {
        if (teleporter?.Tile != null
            && teleporter.Tile.TryGetComponent<TileLabelComp>(out var label))
        {
            return NormalizeLabel(label.Label);
        }
        return null;
    }

    internal static string DisplayIdentity(TeleporterComp teleporter)
    {
        if (teleporter?.Tile == null) return "Missing Teleporter";
        string position = Pos.String(teleporter.Tile.PosIdx);
        string label = GetLabel(teleporter);
        return string.IsNullOrWhiteSpace(label)
            ? position
            : label + " " + position;
    }

    internal string Describe(TrackedTeleporter record)
    {
        return Describe(record, record?.Position ?? 0);
    }

    private static string Describe(TrackedTeleporter record, int position)
    {
        if (record == null) return "Missing Teleporter";
        string pos = Pos.String(position);
        return string.IsNullOrWhiteSpace(record.Label)
            ? pos
            : record.Label + " " + pos;
    }

    private void AddHistory(string message, Tile logTile = null,
        bool writeGameLog = false, string category = "Event",
        int primaryRecordId = 0, int relatedRecordId = 0)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        history.Add(new RecoveryHistoryEntry
        {
            Sequence = nextHistorySequence++,
            Tick = loadedState?.Clock?.Ticks ?? 0,
            PrimaryRecordId = primaryRecordId,
            RelatedRecordId = relatedRecordId,
            Category = category,
            Message = message
        });
        while (history.Count > MaximumHistoryEntries) history.RemoveAt(0);
        D.Log("Reconnect Teleporter Connections: {0}", message);
        if (writeGameLog && logTile?.IsActive == true)
        {
            loadedState?.Sys?.Log?.AddLine(logTile,
                Texts.Blue("Teleporter Connections: " + message),
                canRepeat: true);
        }
    }

    internal IReadOnlyList<TeleporterComp> ActiveTeleporters()
    {
        var result = new List<TeleporterComp>();
        if (loadedState == null) return result;
        loadedState.Components.FindAll(result);
        result.RemoveAll(teleporter => !IsConstructedTeleporter(teleporter));
        result.Sort((left, right) => string.Compare(
            DisplayIdentity(left), DisplayIdentity(right),
            StringComparison.OrdinalIgnoreCase));
        return result;
    }

    internal IReadOnlyList<TrackedTeleporter> TrackedTeleporters()
    {
        return records.Values
            .OrderBy(record => record.IsMissing ? 0 : 1)
            .ThenBy(record => record.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Position)
            .ToArray();
    }

    internal IReadOnlyList<RecoveryHistoryEntry> RecoveryHistory()
    {
        return history.OrderByDescending(entry => entry.Tick)
            .ThenByDescending(entry => entry.Sequence)
            .ToArray();
    }

    internal int IncomingConnectionCount(int recordId)
    {
        return records.Values.Count(record =>
            record.DesiredOutboundId == recordId);
    }

    internal IReadOnlyList<TrackedTeleporter> IncomingConnectionSources(
        int recordId)
    {
        return records.Values
            .Where(record => record.DesiredOutboundId == recordId)
            .OrderBy(record => record.IsMissing ? 1 : 0)
            .ThenBy(record => record.Label,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Position)
            .ToArray();
    }

    internal IReadOnlyList<RecoveryHistoryEntry> RecoveryHistoryFor(
        int recordId)
    {
        string identity = records.TryGetValue(recordId,
            out TrackedTeleporter record)
            ? Describe(record)
            : string.Empty;
        return history
            .Where(entry => string.Equals(entry.Category, "Recovery",
                    StringComparison.Ordinal)
                && (entry.PrimaryRecordId == recordId
                    || entry.RelatedRecordId == recordId
                    || (entry.PrimaryRecordId == 0
                        && entry.RelatedRecordId == 0
                        && !string.IsNullOrEmpty(identity)
                        && (entry.Message ?? string.Empty).IndexOf(identity,
                            StringComparison.OrdinalIgnoreCase) >= 0)))
            .OrderByDescending(entry => entry.Tick)
            .ThenByDescending(entry => entry.Sequence)
            .ToArray();
    }

    internal IReadOnlyList<RecoveryHistoryEntry> RecoveryEvents()
    {
        return history
            .Where(entry => string.Equals(entry.Category, "Recovery",
                StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Tick)
            .ThenByDescending(entry => entry.Sequence)
            .ToArray();
    }

    internal string DesiredDestinationDescription(TrackedTeleporter record)
    {
        if (record == null || record.DesiredOutboundId == 0)
            return "No outbound destination";
        return records.TryGetValue(record.DesiredOutboundId,
            out TrackedTeleporter target)
            ? Describe(target)
            : "Destination no longer tracked";
    }

    internal string DescribeConnectionDependencies(
        TrackedTeleporter record)
    {
        if (record == null) return "no recorded connections";
        var dependencies = new List<string>();
        if (record.DesiredOutboundId != 0)
        {
            dependencies.Add("outbound to "
                + DesiredDestinationDescription(record)
                    .Replace("No outbound destination", "none"));
        }

        TrackedTeleporter[] incoming = records.Values
            .Where(other => other.DesiredOutboundId == record.Id)
            .OrderBy(other => other.Label,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(other => other.Position)
            .ToArray();
        if (incoming.Length > 0)
        {
            dependencies.Add("incoming from " + string.Join(", ",
                incoming.Select(source => Describe(source))));
        }

        return dependencies.Count == 0
            ? "no recorded connections"
            : string.Join("; ", dependencies);
    }

    private static string ClearedDependenciesSentence(string dependencies)
    {
        return string.Equals(dependencies, "no recorded connections",
                StringComparison.Ordinal)
            ? "There were no remembered connections to clear."
            : "Cleared " + dependencies + ".";
    }

    internal string DescribeReplacementMatch(TrackedTeleporter record)
    {
        if (record == null) return "No replacement match is available";
        string position = Pos.String(record.Position);
        return string.IsNullOrWhiteSpace(record.Label)
            ? "Build a replacement at " + position + " to recover it"
            : "Build a replacement at " + position
                + " or apply the unique label \"" + record.Label + "\"";
    }

    internal void OpenConnectionMap(TeleporterComp selected = null)
    {
        if (selected?.Entity != null)
            selectedGraphEntityId = selected.Entity.Id;
        panel?.ShowMap();
    }

    internal void SelectGraphTeleporter(TeleporterComp teleporter)
    {
        if (teleporter?.Tile == null) return;
        selectedGraphEntityId = teleporter.Entity.Id;
        EntityUtils.SelectAndFocus(teleporter.Tile);
        panel?.Refresh();
    }

    internal void SelectGraphTeleporter(TrackedTeleporter record)
    {
        TeleporterComp teleporter = Resolve(record);
        if (teleporter != null) SelectGraphTeleporter(teleporter);
    }

    internal void RefreshUi()
    {
        panel?.Refresh();
    }

    public ComponentData Save()
    {
        var data = ComponentData.Create(SysId);
        data.SetInt("SchemaVersion", SaveSchemaVersion);
        data.SetInt("NextRecordId", nextRecordId);
        data.SetInt("NextDefaultLabel", nextDefaultLabel);
        data.SetInt("SelectedGraphEntityId", selectedGraphEntityId);
        data.SetLong("NextHistorySequence", nextHistorySequence);
        data.SetStringSet("Teleporters",
            records.Values.Select(record => record.Serialize()).ToArray());
        data.SetStringSet("History",
            history.Select(entry => entry.Serialize()).ToArray());
        return data;
    }

    public void Load(ComponentData data)
    {
        records.Clear();
        recordIdByEntityId.Clear();
        history.Clear();
        nextRecordId = 1;
        nextDefaultLabel = 1;
        nextHistorySequence = 1;
        selectedGraphEntityId = 0;
        if (data == null) return;

        nextRecordId = Math.Max(1, data.GetInt("NextRecordId", 1));
        nextDefaultLabel = Math.Max(1,
            data.GetInt("NextDefaultLabel", 1));
        selectedGraphEntityId = data.GetInt("SelectedGraphEntityId", 0);
        nextHistorySequence = Math.Max(1L,
            data.GetLong("NextHistorySequence", 1L));

        foreach (string serialized in data.GetStringSet(
                     "Teleporters", Array.Empty<string>()))
        {
            if (!TrackedTeleporter.TryDeserialize(serialized, out var record))
                continue;
            records[record.Id] = record;
            if (record.EntityId > 0)
                recordIdByEntityId[record.EntityId] = record.Id;
            nextRecordId = Math.Max(nextRecordId, record.Id + 1);
        }

        foreach (string serialized in data.GetStringSet(
                     "History", Array.Empty<string>()))
        {
            if (RecoveryHistoryEntry.TryDeserialize(serialized, out var entry))
            {
                if (entry.Sequence <= 0)
                    entry.Sequence = nextHistorySequence++;
                history.Add(entry);
                nextHistorySequence = Math.Max(nextHistorySequence,
                    entry.Sequence + 1);
            }
        }
        history.Sort((left, right) =>
        {
            int tickOrder = left.Tick.CompareTo(right.Tick);
            return tickOrder != 0
                ? tickOrder
                : left.Sequence.CompareTo(right.Sequence);
        });
        while (history.Count > MaximumHistoryEntries) history.RemoveAt(0);
    }

    public override void Unload()
    {
        S.Sig.AfterLoadState.RemoveListener(OnLoadState);
        DetachStateListeners();
        panel?.Dispose();
        panel = null;
        teleporterBuffer.Clear();
        records.Clear();
        recordIdByEntityId.Clear();
        relocatingSourceEntityIds.Clear();
        playerRemovalEntityIds.Clear();
        history.Clear();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
