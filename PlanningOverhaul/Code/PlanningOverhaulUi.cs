using System;
using System.Collections.Generic;
using System.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using KL.Grid;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlanningOverhaul;

internal sealed class PlanningOverhaulUi : IDisposable
{
    private const int MaximumUpgradeRows = 48;
    private static readonly PanelTabDefinition[] Tabs =
    {
        new PanelTabDefinition { Id = "Build", Label = "Build" },
        new PanelTabDefinition { Id = "Rooms", Label = "Rooms" },
        new PanelTabDefinition { Id = "Upgrade", Label = "Upgrade" },
        new PanelTabDefinition { Id = "Staging", Label = "Staging" },
        new PanelTabDefinition { Id = "Pulse", Label = "Pulse Wave" },
        new PanelTabDefinition { Id = "Blueprints", Label = "Blueprints" }
    };

    private readonly PlanningOverhaulSys system;
    private readonly FrameworkPanel panel;
    private readonly ITopBarWidgetHandle widget;
    private readonly Dictionary<string, bool> sectionExpanded =
        new Dictionary<string, bool>(StringComparer.Ordinal);
    private readonly Dictionary<string, float> catalogScrollPositions =
        new Dictionary<string, float>(StringComparer.Ordinal);
    private string activeTab = "Build";
    private string catalogSearch = string.Empty;
    private string upgradeSearch = string.Empty;
    private string newRoomName = string.Empty;
    private string importCode = string.Empty;
    private float roomSequenceScrollPosition = 1f;
    private float savedBlueprintScrollPosition = 1f;
    private int pendingDeleteRoomId;
    private bool pendingDeleteAllRooms;
    private bool pendingCaptureConstructedShip;
    private bool pendingClearBlueprint;
    private bool pulseHadMaterials;
    private TextMeshProUGUI statusBody;
    private Image statusBackground;
    private WText stagingOverviewHeader;
    private readonly Dictionary<StagingAreaType, WText> stagingSectionHeaders =
        new Dictionary<StagingAreaType, WText>();
    private readonly Dictionary<StagingAreaType, WIconText2> stagingInfoRows =
        new Dictionary<StagingAreaType, WIconText2>();
    private WText pulseRoomHeader;
    private WIconText2 pulseStructuralRow;
    private WIconText2 pulseSiteClearingRow;
    private WText pulseMaterialsHeader;
    private WIconText2 pulseBatchRow;
    private WIconText2 pulseNoMaterialsRow;
    private readonly Dictionary<MatType, WIconText2> pulseMaterialRows =
        new Dictionary<MatType, WIconText2>();
    private WrappedInfoRow pulsePausedInspectionRow;
    private WrappedInfoRow pulseResearchLockedRow;
    private WrappedInfoRow pulseCannotPlaceRow;

    internal PlanningOverhaulUi(PlanningOverhaulSys system)
    {
        this.system = system;
        panel = new FrameworkPanel(new FrameworkPanelOptions
        {
            Id = "PlanningOverhaul.Controls",
            Title = "Construction Plan",
            MinSize = new Vector2(660f, 480f),
            MaxSize = new Vector2(1100f, 940f),
            RememberGeometry = true
        }, BuildPanel);

        widget = AutoUtilitiesApi.RegisterTopBarWidget(
            new TopBarWidgetRegistration
            {
                Id = "PlanningOverhaul.Status",
                Icon = () => IconId.WPlanning,
                Tint = () => system.Continuous ? Color.white : Color.gray,
                Tooltip = BuildTooltip,
                BadgeText = () => system.Entries.Count == 0
                    ? string.Empty : system.Entries.Count.ToString(),
                BadgeTextColor = () => Color.white,
                OnClick = system.ToggleUi,
                Width = 36f,
                Order = 24
            });
    }

    internal bool IsOpen => panel.IsOpen;
    internal void Toggle() => panel.Toggle();
    internal void Close() => panel.Close();

    internal void Refresh()
    {
        widget?.Refresh();
        panel?.Refresh();
    }

    internal void RefreshAutomation()
    {
        widget?.Refresh();
        if (!panel.IsOpen) return;
        RefreshStatus();
        if (activeTab == "Staging") RefreshStagingLiveState();
        else if (activeTab == "Pulse") RefreshPulseLiveState();
    }

    internal void RefreshStatus()
    {
        if (!panel.IsOpen || statusBody == null) return;
        string detail = system.LastStatus;
        if (string.Equals(statusBody.text, detail, StringComparison.Ordinal)) return;
        statusBody.SetText(detail);
        UIComponentFactory.SetTip(statusBackground, detail);
    }

    private void ClearLiveRows()
    {
        statusBody = null;
        statusBackground = null;
        stagingOverviewHeader = null;
        stagingSectionHeaders.Clear();
        stagingInfoRows.Clear();
        pulseRoomHeader = null;
        pulseStructuralRow = null;
        pulseSiteClearingRow = null;
        pulseMaterialsHeader = null;
        pulseBatchRow = null;
        pulseNoMaterialsRow = null;
        pulseMaterialRows.Clear();
        pulsePausedInspectionRow = null;
        pulseResearchLockedRow = null;
        pulseCannotPlaceRow = null;
    }

    private void RefreshStagingLiveState()
    {
        int designated = system.StagingAreaCount(StagingAreaType.Indoor)
            + system.StagingAreaCount(StagingAreaType.Outdoor)
            + system.StagingAreaCount(StagingAreaType.OutdoorEdge);
        stagingOverviewHeader?.SetText(
            "Staging areas · " + designated + " designated");
        foreach (StagingAreaType type in Enum.GetValues(typeof(StagingAreaType)))
        {
            int total = system.StagingAreaCount(type);
            int ready = system.ReadyStagingCellCount(type);
            string name = PlanningOverhaulSys.StagingName(type);
            string summary = ready + " ready · " + total + " designated";
            if (stagingSectionHeaders.TryGetValue(type, out WText header))
                header?.SetText(name + " staging · " + summary);
            if (stagingInfoRows.TryGetValue(type, out WIconText2 row))
                row?.SetText2(summary);
        }
    }

    private void RefreshPulseLiveState()
    {
        PulseUiState state = CreatePulseUiState();
        pulsePausedInspectionRow?.Set("Paused room inspection",
            state.PausedInspectionStatus,
            null, state.PausedInspection);
        pulseRoomHeader?.SetText(state.RoomTitle);
        pulseStructuralRow?.SetText2(StructuralSummary(state.Preview));

        bool researchLocked = state.Preview.Locked > 0;
        string lockedSummary = researchLocked
            ? system.SummarizePlanEntries(state.Preview.LockedEntries)
            : string.Empty;
        pulseResearchLockedRow?.Set("Research locked", lockedSummary,
            lockedSummary, researchLocked);

        bool cannotPlace = state.Preview.Blocked > 0;
        string blockedSummary = cannotPlace
            ? system.SummarizePlanEntries(state.Preview.BlockedEntries)
            : string.Empty;
        pulseCannotPlaceRow?.Set("Cannot place", blockedSummary,
            cannotPlace
                ? system.PulseBlockReason(state.Preview, system.Continuous)
                : null,
            cannotPlace);

        int clearing = state.Preview.RelocationsReady
            + state.Preview.RelocationsInProgress
            + state.Preview.RelocationsWaiting;
        SetActive(pulseSiteClearingRow, clearing > 0);
        if (clearing > 0)
        {
            pulseSiteClearingRow?.Load(IconId.WMove, "Site clearing",
                state.Preview.RelocationsReady + " ready · "
                    + state.Preview.RelocationsInProgress + " moving · "
                    + state.Preview.RelocationsWaiting + " require clearing");
        }

        pulseMaterialsHeader?.SetText(
            "Material gate · " + MaterialSummary(state.ShownMaterials));
        bool hasBatch = state.Construction != null
            && state.Construction.Entries.Count > 0;
        SetActive(pulseBatchRow, hasBatch);
        if (hasBatch)
        {
            string phase = string.IsNullOrWhiteSpace(state.Construction.PhaseName)
                ? "equipment" : state.Construction.PhaseName;
            phase = char.ToUpperInvariant(phase[0]) + phase.Substring(1);
            pulseBatchRow?.Load("Icons/Color/Info",
                state.PausedInspection
                    ? "Selected room material wait"
                    : "Next new construction batch",
                state.Construction.RoomName + " · " + phase + " · "
                    + state.Construction.Entries.Count + " buildings");
        }

        SetActive(pulseNoMaterialsRow, state.ShownMaterials.Count == 0);
        foreach (KeyValuePair<MatType, WIconText2> pair in pulseMaterialRows)
        {
            MaterialRequirement item = state.ShownMaterials
                .FirstOrDefault(candidate => candidate.Type == pair.Key);
            SetActive(pair.Value, item != null);
            if (item == null) continue;
            pair.Value.Load(item.Missing == 0
                    ? "Icons/Color/Check" : "Icons/Color/Warning",
                item.Type.NameT, MaterialValue(item));
            UIComponentFactory.SetTip(pair.Value, MaterialTooltip(item));
        }
    }

    private static void SetActive(Component component, bool active)
    {
        if (component != null && component.gameObject.activeSelf != active)
            component.gameObject.SetActive(active);
    }

    private static void SetActive(GameObject root, bool active)
    {
        if (root != null && root.activeSelf != active) root.SetActive(active);
    }

    private string BuildTooltip()
    {
        string mode = system.Continuous ? "Continuous ON" : "Pulse mode";
        return "Construction Plan: " + mode
            + "\nRooms: " + system.Rooms.Count
            + "\nPlanned buildings: " + system.Entries.Count
            + "\nActive room: " + (system.CurrentRoom?.Name ?? "None")
            + "\nClick for planning controls.";
    }

    private void BuildPanel(PanelContentContext context)
    {
        Stopwatch perf = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        string builtTab = activeTab;
        try
        {
            ClearLiveRows();
            context.AddTabs(Tabs, activeTab, value =>
            {
                activeTab = value;
                pendingDeleteRoomId = 0;
                pendingDeleteAllRooms = false;
                pendingCaptureConstructedShip = false;
                pendingClearBlueprint = false;
            });
            AddWrappedInfo(context, "Status", system.LastStatus);
            if (activeTab == "Rooms") BuildRooms(context);
            else if (activeTab == "Upgrade") BuildUpgrade(context);
            else if (activeTab == "Staging") BuildStaging(context);
            else if (activeTab == "Pulse") BuildPulse(context);
            else if (activeTab == "Blueprints") BuildBlueprints(context);
            else BuildCatalog(context);
        }
        finally
        {
            system.ReportUiBuild(builtTab, perf.ElapsedMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
    }

    private void BuildCatalog(PanelContentContext context)
    {
        PlanRoom room = system.CurrentRoom;
        var recent = system.RecentEntries(12);
        string roomName = room?.Name ?? "No room selected";
        string roomSummary = room == null ? "Unavailable"
            : system.EntryCount(room.Id) + " entries";
        if (AddSectionHeader(context, "Build.ActiveRoom",
                "Active construction room · " + roomName, true))
        {
            context.AddInfo(IconId.WPlanning, roomName, roomSummary,
                "Every building placed from this tab is assigned to the active room.");
            context.AddInfo("Icons/Color/Info", "Planning mode",
                system.CaptureActive
                    ? "Drawing " + (system.CaptureDefId ?? "building")
                    : "Choose a building below",
                "Placement uses the normal Stardeus ghost and rotation controls. The click is saved as a plan entry and does not create a construction order.");
            context.AddInfo("Icons/Color/Info", "Current preview",
                "Layered ghosts + structural waves",
                "Floors remain planned underneath walls, doors, and equipment. The last building drawn over the same layer replaces the earlier ghost.");
        }

        if (AddSectionHeader(context, "Build.Recent",
                "Recent entries · " + recent.Count + " in "
                    + (room?.Name ?? "this room"), true))
        {
            if (recent.Count == 0)
            {
                context.AddInfo("Icons/Color/Info", "No planned buildings yet", "Ready");
            }
            else
            {
                foreach (PlanEntry entry in recent)
                {
                    var recentRow = context.AddInfo(IconId.CColorPreview,
                        system.EntryName(entry),
                        LayerName(entry.Layer) + " · " + Pos.String(entry.Position),
                        "Rotation: " + ((Facing.Type)entry.Rotation)
                            + (system.EntryIsUnlocked(entry)
                                ? string.Empty : "\nResearch locked"));
                    recentRow.SetTint(system.EntryDisplayColor(entry));
                }
                context.AddButton(IconId.WArrowLeft, "Undo last planned building",
                    string.Empty, system.UndoLastEntry,
                    "Removes the newest unbuilt plan entry in the active room.");
            }
        }

        if (AddSectionHeader(context, "Build.Colors",
                "Planning colors · " + system.ColorKeyName, true))
        {
            context.AddToggle(IconId.CColorPreview, "Color key",
                system.ColorKeyName, system.ColorByBuildingType,
                system.SetColorByBuildingType,
                "Off assigns one Paint-palette color to each room. On assigns one color to each building type. This changes only the Planning overlay and does not paint constructed buildings.");
            context.AddButton(IconId.WPlanningClear, "Plan Eraser",
                "Remove construction ghosts", system.BeginPlanEraser,
                "Uses the vanilla Planning eraser. It clears ordinary Planning cells and removes only the highest Construction Plan layer present anywhere in one click or drag selection. If a wall or equipment ghost is highlighted, floors throughout the same selection are preserved. Staging is preserved.");
        }

        IReadOnlyList<BuildCatalogCategory> categories =
            system.GetCatalogCategories(catalogSearch);
        int total = categories.Sum(category => category.Buildings.Count);
        if (AddSectionHeader(context, "Build.Catalog",
                "Building catalog · " + total + " matches", true))
        {
            AddIsolatedInput(context, "Icons/Color/Magnifier", "Search any building",
                catalogSearch, value =>
                {
                    catalogSearch = value ?? string.Empty;
                    panel.Refresh();
                }, tooltip: "Searches unlocked, research-locked, Core, and modded construction definitions.");

            if (categories.Count == 0)
            {
                context.AddInfo(IconId.CInfo, "No matching buildings",
                    string.IsNullOrWhiteSpace(catalogSearch)
                        ? "No construction definitions available"
                        : "No matches for this search");
            }
            else
            {
                bool searching = !string.IsNullOrWhiteSpace(catalogSearch);
                foreach (BuildCatalogCategory category in categories)
                {
                    string categoryId = category.Id;
                    if (!AddSectionHeader(context,
                            "Build.CatalogCategory." + categoryId,
                            category.Name + " · " + category.Buildings.Count,
                            false, searching))
                        continue;

                    if (!catalogScrollPositions.TryGetValue(categoryId,
                            out float scrollPosition))
                        scrollPosition = 1f;
                    VerticalScrollViewResult scroll = context.AddVerticalScroll(
                        "PlanningOverhaul_Catalog_" + categoryId,
                        300f, 600f, 1f, scrollPosition,
                        value => catalogScrollPositions[categoryId] = value);
                    LayoutElement size = scroll.Root.GetComponent<LayoutElement>();
                    float height = Mathf.Clamp(
                        category.Buildings.Count * 47f, 47f, 235f);
                    size.minHeight = size.preferredHeight = height;
                    size.flexibleHeight = 0f;
                    int row = 0;
                    foreach (Def def in category.Buildings)
                        AddCatalogRow(context, scroll.Content, row++, def);
                }
            }
        }
    }

    private void AddCatalogRow(PanelContentContext context,
        RectTransform parent, int siblingIndex, Def def)
    {
        Def selected = def;
        bool unlocked = system.EntryIsUnlocked(
            new PlanEntry { DefId = def.Id });
        string preview = string.IsNullOrWhiteSpace(def.Preview)
            ? IconId.WHammer : def.Preview;
        WButtonIconText2 row = UISpawn.Widget<WButtonIconText2>(context.Lifetime,
            UIPrefabId.WContent4Button, parent, siblingIndex);
        row.Load(preview, def.NameT,
            unlocked ? "Plan this building"
                : "Plan now · build after research",
            () => system.BeginPlacement(selected));
        UIComponentFactory.SetTip(row,
            "Definition: " + def.Id + "\nLayer: " + LayerName(def.LayerId)
                + (unlocked ? "\nAvailable with current research."
                    : "\nResearch-locked buildings remain valid plan entries."));
    }

    private void BuildUpgrade(PanelContentContext context)
    {
        Tile[] selected = system.GetUpgradeSelection();
        context.AddHeader("Selected structures · " + selected.Length
            + " selected");
        if (selected.Length == 0)
        {
            context.AddInfo(IconId.CSelectSimilar, "Select walls or floors",
                "No structures selected",
                "Select one constructed wall or floor, then use Core's Select Similar action to collect matching structures currently visible on screen.");
            return;
        }
        if (!system.IsSupportedUpgradeSelection(selected))
        {
            context.AddInfo(IconId.CWarning, "Unsupported selection",
                selected.Length + " selected",
                "Upgrade accepts one layer at a time: all selected structures must be constructed walls and doors, or all must be constructed floors.");
            return;
        }

        int layer = selected[0].Definition.LayerId;
        int sourceTypes = selected.Select(tile => tile.DefinitionId).Distinct().Count();
        string kind = layer == WorldLayer.Floor ? "floor" : "wall / door";
        string sourceName = sourceTypes == 1
            ? selected[0].Definition.NameT
            : sourceTypes + " mixed " + kind + " types";
        context.AddInfo(layer == WorldLayer.Floor ? IconId.WFloors : IconId.WWalls,
            sourceName, selected.Length + " selected",
            "The selection is read directly from Core. Select Similar can populate it with matching structures visible on screen.");
        context.AddInfo(IconId.CUpgrade, "Replacement method",
            "Core construction tasks",
            "Each compatible location receives Core's normal replacement order. Standard costs, work, cancellation, and configuration-copy behavior apply.");

        var candidates = system.GetUpgradeCandidates(selected, upgradeSearch,
            MaximumUpgradeRows);
        string replacementKind = layer == WorldLayer.Floor
            ? "Compatible floor replacements"
            : "Compatible wall and door replacements";
        string shown = candidates.Count >= MaximumUpgradeRows
            ? MaximumUpgradeRows + " shown" : candidates.Count.ToString();
        context.AddHeader(replacementKind + " · " + shown);
        AddIsolatedInput(context, "Icons/Color/Magnifier",
            "Search compatible replacements", upgradeSearch, value =>
            {
                upgradeSearch = value ?? string.Empty;
                panel.Refresh();
            }, tooltip: "Filters compatible unlocked replacements by name. The existing tile icons distinguish definitions that share a name.");
        if (candidates.Count == 0)
        {
            context.AddInfo(IconId.CInfo, "No compatible replacements",
                string.IsNullOrWhiteSpace(upgradeSearch)
                    ? "Current selection cannot be replaced"
                    : "No matches for this search",
                "Finish or cancel existing tasks on the selected structures, clear or refine the search, and verify that the desired replacement is currently unlocked.");
        }
        else
        {
            foreach (Def def in candidates)
            {
                Def replacement = def;
                int compatible = system.CompatibleUpgradeCount(replacement, selected);
                string preview = string.IsNullOrWhiteSpace(replacement.Preview)
                    ? IconId.WHammer : replacement.Preview;
                string action = compatible == selected.Length
                    ? "Replace all " + selected.Length
                    : "Replace " + compatible + " of " + selected.Length;
                context.AddButton(preview, replacement.NameT, action,
                    () => system.ReplaceUpgradeSelection(replacement),
                    "Definition: " + replacement.Id
                        + "\nQueues native replacement tasks for every currently compatible selected "
                        + kind + ".");
            }
        }
    }

    private void BuildStaging(PanelContentContext context)
    {
        int designated = system.StagingAreaCount(StagingAreaType.Indoor)
            + system.StagingAreaCount(StagingAreaType.Outdoor)
            + system.StagingAreaCount(StagingAreaType.OutdoorEdge);
        if (AddSectionHeader(context, "Staging.Overview",
                "Staging areas · " + designated + " designated", true,
                created: header => stagingOverviewHeader = header))
        {
            context.AddInfo("Icons/Color/Info", "Purpose",
                "Clear occupied construction sites",
                "A Pulse Wave first sends blocking buildings to a compatible real location. Indoor staging requires finished floor and one clear designated tile around every staged building. Construction and equipment moves do not require enclosure or breathable oxygen unless Core explicitly requires indoors or outdoors. Outdoor staging accepts exterior open space or completed exterior floor, with Core choosing the support required by each building. Outdoor Edge staging is open space beside a finished floor.");
            context.AddInfo("Icons/Color/Info", "Movement priority", "Priority 5",
                "Relocation jobs and their destination jobs use priority 5. A machine that is working finishes its current task before relocation becomes available.");
            context.AddButton(IconId.WPlanningClear, "Staging Eraser",
                "Remove staging only", system.BeginStagingEraser,
                "Click or drag over Indoor, Outdoor, or Outdoor Edge staging cells. Construction-plan ghosts and vanilla Planning marks are preserved.");
        }

        AddStagingControls(context, StagingAreaType.Indoor,
            "For beds, workstations, storage, and other floor-based buildings. Each staged footprint reserves a one-tile perimeter so doors and walking routes stay clear.");
        AddStagingControls(context, StagingAreaType.Outdoor,
            "For buildings that require outdoors or whose normal placement rule requires open space. Cells may be exterior open space or completed exterior floor; Core uses only the support compatible with the building.");
        AddStagingControls(context, StagingAreaType.OutdoorEdge,
            "For exterior buildings that must attach to the edge of a constructed floor.");

    }

    private void AddStagingControls(PanelContentContext context, StagingAreaType type,
        string description)
    {
        int total = system.StagingAreaCount(type);
        int ready = system.ReadyStagingCellCount(type);
        string name = PlanningOverhaulSys.StagingName(type);
        if (!AddSectionHeader(context, "Staging." + type,
                name + " staging · " + ready + " ready · " + total
                    + " designated", true,
                created: header => stagingSectionHeaders[type] = header))
            return;
        stagingInfoRows[type] = context.AddInfo(IconId.WPlanning,
            name + " cells", ready + " ready · " + total + " designated",
            description);
        context.AddButton(IconId.WPencil, "Draw " + name + " area", string.Empty,
            () => system.BeginStagingArea(type),
            "Click or drag a rectangle using the Planning overlay. Drawing over another staging type changes those cells to " + name + ".");
        if (total > 0)
            context.AddButton(IconId.WPlanningClear, "Clear " + name + " area", string.Empty,
                () => system.ClearStagingArea(type));
    }

    private void BuildRooms(PanelContentContext context)
    {
        if (system.CurrentRoom != null)
        {
            bool forceEditOpen = pendingDeleteRoomId == system.CurrentRoomId;
            if (AddSectionHeader(context, "Rooms.Edit",
                    "Edit active room · " + system.CurrentRoom.Name, true,
                    forceEditOpen))
            {
                AddIsolatedInput(context, IconId.CLabel, "Room name",
                    system.CurrentRoom.Name, system.RenameCurrentRoom);
                context.AddButton(IconId.WArrowLeft,
                    "Undo last planned building", string.Empty,
                    system.UndoLastEntry);
                context.AddButton(IconId.WPlanningClear,
                    "Clear unissued entries in this room", string.Empty,
                    system.ClearCurrentRoom,
                    "Completed or already-dispatched Stardeus construction is preserved.");
                if (pendingDeleteRoomId == system.CurrentRoomId)
                {
                    PlanRoom room = system.CurrentRoom;
                    context.AddInfo("Icons/Color/Warning",
                        "Delete " + room.Name + "?",
                        system.EntryCount(room.Id) + " planning entries",
                        "Deletion removes this room and all of its Construction Plan entries. Completed buildings remain. Active construction or relocation must finish first.");
                    context.AddButton("Icons/Color/Warning",
                        "Confirm room deletion", room.Name, () =>
                        {
                            pendingDeleteRoomId = 0;
                            system.DeleteCurrentRoom();
                        });
                    context.AddButton(IconId.WArrowLeft, "Keep this room",
                        string.Empty, () =>
                        {
                            pendingDeleteRoomId = 0;
                            panel.Refresh();
                        });
                }
                else
                {
                    context.AddButton(IconId.WPlanningClear,
                        "Delete active room",
                        system.EntryCount(system.CurrentRoomId)
                            + " planning entries", () =>
                        {
                            pendingDeleteRoomId = system.CurrentRoomId;
                            panel.Refresh();
                        },
                        "Requires a second confirmation. Existing completed buildings are never removed.");
                }
            }
        }

        if (AddSectionHeader(context, "Rooms.Sequence",
                "Room sequence · " + system.Rooms.Count + " rooms", true))
        {
            AddWrappedInfo(context, "Construction order",
                "Enclosed: shell → power → atmosphere → equipment · Exterior: connected floor → all buildings",
                "Captured and imported blueprints list enclosed rooms from the plan center outward, followed by Exterior. The center-most room becomes the active room. Enclosed rooms retain their shell, power, atmosphere, and equipment stages. Exterior expands connected floors and admits every other building as soon as its completed floor footprint is ready.");
            VerticalScrollViewResult scroll = context.AddVerticalScroll(
                "PlanningOverhaul_RoomSequenceScroll", 300f, 600f, 1f,
                roomSequenceScrollPosition,
                value => roomSequenceScrollPosition = value);
            LayoutElement size = scroll.Root.GetComponent<LayoutElement>();
            float height = Mathf.Clamp(system.Rooms.Count * 47f, 47f, 329f);
            size.minHeight = size.preferredHeight = height;
            size.flexibleHeight = 0f;
            int row = 0;
            foreach (PlanRoom room in system.Rooms.OrderBy(item => item.Order))
                AddRoomSequenceRow(context, scroll.Content, row++, room);
        }

        if (AddSectionHeader(context, "Rooms.Add", "Add a room", false))
        {
            AddIsolatedInput(context, IconId.CLabel, "New room name", newRoomName,
                value => newRoomName = value ?? string.Empty,
                submitted: value =>
                {
                    pendingDeleteRoomId = 0;
                    newRoomName = value ?? string.Empty;
                    system.AddRoom(newRoomName);
                    newRoomName = string.Empty;
                });
            context.AddButton(IconId.WPlus, "Add and select room", string.Empty,
                () =>
                {
                    pendingDeleteRoomId = 0;
                    system.AddRoom(newRoomName);
                    newRoomName = string.Empty;
                });
        }

        bool forceResetOpen = pendingDeleteAllRooms;
        if (AddSectionHeader(context, "Rooms.Reset",
                "Reset construction plan · " + system.Rooms.Count + " rooms · "
                    + system.Entries.Count + " entries", false, forceResetOpen))
        {
            if (pendingDeleteAllRooms)
            {
                AddWrappedInfo(context, "Delete every room and cancel plan work?",
                    "This removes every Construction Plan room and entry, cancels active construction, replacement, relocation, and plan-issued deconstruction tasks, then creates a new empty Room 1. Completed buildings and staging-area designations remain.");
                context.AddButton("Icons/Color/Warning",
                    "Confirm delete all rooms",
                    system.Rooms.Count + " rooms · " + system.Entries.Count
                        + " entries",
                    () =>
                    {
                        pendingDeleteAllRooms = false;
                        pendingDeleteRoomId = 0;
                        system.DeleteAllRoomsAndTasks();
                    });
                context.AddButton(IconId.WArrowLeft, "Keep all rooms",
                    string.Empty,
                    () =>
                    {
                        pendingDeleteAllRooms = false;
                        panel.Refresh();
                    });
            }
            else
            {
                context.AddButton(IconId.WPlanningClear, "Delete all rooms",
                    system.Rooms.Count + " rooms · " + system.Entries.Count
                        + " entries",
                    () =>
                    {
                        pendingDeleteAllRooms = true;
                        pendingDeleteRoomId = 0;
                        panel.Refresh();
                    },
                    "Requires a second confirmation. Active Planning Overhaul work is cancelled; completed buildings and staging areas are preserved.");
            }
        }
    }

    private void AddRoomSequenceRow(PanelContentContext context,
        RectTransform parent, int siblingIndex, PlanRoom room)
    {
        PlanRoom selected = room;
        bool active = room.Id == system.CurrentRoomId;
        WButtonIconText2 row = UISpawn.Widget<WButtonIconText2>(context.Lifetime,
            UIPrefabId.WContent4Button, parent, siblingIndex);
        row.Load(IconId.CColorPreview,
            (room.Order + 1) + ". " + room.Name,
            system.StructuralCount(room.Id) + " structure · "
                + system.EquipmentCount(room.Id) + " equipment"
                + (active ? " · Active" : string.Empty),
            () =>
            {
                pendingDeleteRoomId = 0;
                system.SelectRoom(selected.Id);
            });
        row.SetTint(system.RoomDisplayColor(room.Id));
        UIComponentFactory.SetTip(row,
            "Select this room for building placement and manual Pulse Wave construction.");
    }

    private void BuildPulse(PanelContentContext context)
    {
        if (AddSectionHeader(context, "Pulse.Mode",
                "Construction mode · "
                    + (system.Continuous ? "Continuous" : "Manual Pulse Wave"), true))
        {
            context.AddToggle(system.Continuous
                    ? "Icons/Color/Check" : "Icons/Color/NoEnergy",
                "Continuous construction",
                system.Continuous ? "On" : "Off · manual Pulse Wave",
                system.Continuous, system.SetContinuous,
                "When on, enclosed rooms progress through floors, walls, doors, clearing, power, atmosphere, and remaining equipment. Exterior instead expands only cardinally connected floor and admits clearing, relocation, and every other building as soon as its complete floor footprint is ready. A room yields for a real cross-room prerequisite, materials, a connected power-network wait, or compatible staging. Turning this off prevents new work and pauses unclaimed mod-issued goals.");
        }
        PulseUiState state = CreatePulseUiState();
        bool pausedInspection = state.PausedInspection;
        PulsePreview preview = state.Preview;
        EquipmentConstructionPreview construction = state.Construction;
        pulsePausedInspectionRow = AddWrappedInfo(context,
            "Paused room inspection",
            state.PausedInspectionStatus);
        SetActive(pulsePausedInspectionRow?.Root, pausedInspection);

        if (AddSectionHeader(context, "Pulse.Room", state.RoomTitle, true,
                created: header => pulseRoomHeader = header))
        {
            pulseStructuralRow = context.AddInfo(IconId.WPlanning,
                "Structural entries", StructuralSummary(preview),
                "A Pulse Wave contains only floors, walls, and doors from one room.");
            string lockedSummary = preview.Locked > 0
                ? system.SummarizePlanEntries(preview.LockedEntries)
                : string.Empty;
            pulseResearchLockedRow = AddWrappedInfo(context, "Research locked",
                lockedSummary, lockedSummary);
            SetActive(pulseResearchLockedRow?.Root, preview.Locked > 0);
            string blockedSummary = preview.Blocked > 0
                ? system.SummarizePlanEntries(preview.BlockedEntries)
                : string.Empty;
            pulseCannotPlaceRow = AddWrappedInfo(context, "Cannot place",
                blockedSummary, preview.Blocked > 0
                    ? system.PulseBlockReason(preview, system.Continuous)
                    : null);
            SetActive(pulseCannotPlaceRow?.Root, preview.Blocked > 0);
            int clearing = preview.RelocationsReady
                + preview.RelocationsInProgress + preview.RelocationsWaiting;
            pulseSiteClearingRow = context.AddInfo(IconId.WMove,
                "Site clearing", preview.RelocationsReady + " ready · "
                    + preview.RelocationsInProgress + " moving · "
                    + preview.RelocationsWaiting + " require clearing",
                "Blocking buildings use native Stardeus relocation when possible. They wait for compatible staging when one is designated and full, or use native deconstruction when no staging is designated. Same-layer wall, door, and floor changes use native replacement jobs.");
            SetActive(pulseSiteClearingRow, clearing > 0);
        }

        IReadOnlyList<MaterialRequirement> shownMaterials = state.ShownMaterials;
        bool hasMaterials = shownMaterials.Count > 0;
        if (hasMaterials && !pulseHadMaterials)
            sectionExpanded["Pulse.Materials"] = true;
        pulseHadMaterials = hasMaterials;
        bool materialBlocked = shownMaterials.Any(item => item.Missing > 0);
        if (AddSectionHeader(context, "Pulse.Materials",
                "Material gate · " + MaterialSummary(shownMaterials),
                shownMaterials.Count > 0, materialBlocked,
                header => pulseMaterialsHeader = header))
        {
            context.AddInfo("Icons/Color/Info", "Start rule",
                "Normal cost + 10% buffer",
                "The buffer is rounded up separately for each material, is required before work starts, and remains in inventory.");
            bool hasBatch = construction != null && construction.Entries.Count > 0;
            string phase = hasBatch && !string.IsNullOrWhiteSpace(construction.PhaseName)
                ? construction.PhaseName : "equipment";
            phase = char.ToUpperInvariant(phase[0]) + phase.Substring(1);
            pulseBatchRow = context.AddInfo("Icons/Color/Info",
                pausedInspection ? "Selected room material wait"
                    : "Next new construction batch",
                hasBatch
                    ? construction.RoomName + " · " + phase + " · "
                        + construction.Entries.Count + " buildings"
                    : string.Empty,
                pausedInspection
                    ? "This is the material-limited batch retained for the selected room. Inventory values are refreshed while the game is paused."
                    : "These entries have no reusable matching building available. Continuous construction starts each affordable portion while preserving the 10% material buffer.");
            SetActive(pulseBatchRow, hasBatch);
            if (hasBatch)
            {
                foreach (var group in construction.Entries
                             .GroupBy(entry => entry.DefId ?? string.Empty,
                                 StringComparer.Ordinal)
                             .OrderBy(group => system.EntryName(group.First()),
                                 StringComparer.OrdinalIgnoreCase))
                {
                    PlanEntry first = group.First();
                    string locations = string.Join(", ", group.Take(12)
                        .Select(entry => Pos.String(entry.Position)));
                    if (group.Count() > 12) locations += ", ...";
                    context.AddInfo(system.EntryIcon(first),
                        system.EntryName(first), group.Count() + " to construct",
                        "Planned locations: " + locations);
                }
            }

            pulseNoMaterialsRow = context.AddInfo("Icons/Color/Info",
                "No material cost to show", "—");
            SetActive(pulseNoMaterialsRow, shownMaterials.Count == 0);
            if (shownMaterials.Count > 0)
            {
                foreach (MaterialRequirement item in shownMaterials)
                {
                    WIconText2 materialRow = context.AddInfo(item.Missing == 0
                            ? "Icons/Color/Check" : "Icons/Color/Warning",
                        item.Type.NameT, MaterialValue(item),
                        MaterialTooltip(item));
                    pulseMaterialRows[item.Type] = materialRow;
                }
            }
        }

        if (system.Continuous)
        {
            AddWrappedInfo(context, "Manual Pulse Wave unavailable",
                "Turn Continuous construction off.");
        }
        else if (preview.CanStart)
        {
            context.AddButton("Icons/Color/Play", "Start Pulse Wave",
                preview.Room?.Name ?? string.Empty,
                system.StartManualPulse,
                "Rechecks every material immediately, then queues this room's remaining floors, walls, and doors through normal Stardeus construction.");
        }
        else
        {
            string reason = system.PulseBlockReason(preview, false);
            AddWrappedInfo(context, "Pulse Wave unavailable", reason, reason);
        }
    }

    private PulseUiState CreatePulseUiState()
    {
        bool pausedInspection = system.IsGamePaused;
        bool showingContinuousRoom = system.Continuous && !pausedInspection
            && system.ContinuousRoomId != 0;
        int previewRoomId = showingContinuousRoom
            ? system.ContinuousRoomId : system.CurrentRoomId;
        PulsePreview preview = system.BuildPulsePreview(previewRoomId,
            assessRelocations: !system.Continuous);
        EquipmentConstructionPreview construction;
        string inspectionStatus = string.Empty;
        if (pausedInspection)
            inspectionStatus = system.InspectPausedRoom(previewRoomId, preview,
                out construction);
        else
            construction = system.Continuous
                    && system.ContinuousConstructionPreview?.RoomId == previewRoomId
                ? system.ContinuousConstructionPreview : null;
        IReadOnlyList<MaterialRequirement> shownMaterials = construction != null
            ? construction.Materials : preview.Materials;
        string roomTitle = (pausedInspection ? "Selected room · "
                : system.Continuous ? "Active room · " : "Next wave · ")
            + (preview.Room?.Name ?? "No room");
        return new PulseUiState
        {
            PausedInspection = pausedInspection,
            PausedInspectionStatus = inspectionStatus,
            Preview = preview,
            Construction = construction,
            ShownMaterials = shownMaterials,
            RoomTitle = roomTitle
        };
    }

    private static string StructuralSummary(PulsePreview preview)
    {
        return preview.StructuralFinished + " finished · " + preview.InProgress
            + " in progress · " + preview.Pending.Count + " waiting";
    }

    private static string MaterialSummary(
        IReadOnlyList<MaterialRequirement> materials)
    {
        return materials.Count == 0 ? "No cost"
            : materials.Count + " material"
                + (materials.Count == 1 ? string.Empty : "s");
    }

    private static string MaterialValue(MaterialRequirement item)
    {
        string value = "Build " + item.Cost + " · Start "
            + item.RequiredToStart + " · Have " + item.Available;
        if (item.Missing > 0) value += " · Missing " + item.Missing;
        return value;
    }

    private static string MaterialTooltip(MaterialRequirement item)
    {
        return "Build cost: " + item.Cost
            + "\nRequired to start (+10%): " + item.RequiredToStart
            + "\nAvailable: " + item.Available
            + "\nMissing: " + item.Missing;
    }

    private void BuildBlueprints(PanelContentContext context)
    {
        bool captureOpen = AddSectionHeader(context, "Blueprints.Capture",
            "Create from constructed ship", true, pendingCaptureConstructedShip);
        if (captureOpen)
        {
            context.AddInfo(IconId.WPlanning, "Capture source",
                "Ship Computer island",
                "Uses the constructed island containing the Ship Computer, or the largest constructed island when no Ship Computer is available. Enclosed Core areas become separate planning rooms; attached exterior buildings go into an Exterior room.");
            if (pendingCaptureConstructedShip)
            {
                AddWrappedInfo(context, "Replace the current Construction Plan?",
                    "The constructed ship will become the new plan. Name and save it below after capture.",
                    "This replaces the current rooms, entries, and staging design. It does not alter any constructed building.");
                context.AddButton("Icons/Color/Warning", "Confirm ship capture",
                    "Replace current plan", () =>
                    {
                        pendingCaptureConstructedShip = false;
                        system.CaptureConstructedShip();
                    });
                context.AddButton(IconId.WArrowLeft, "Keep current plan",
                    string.Empty, () =>
                    {
                        pendingCaptureConstructedShip = false;
                        panel.Refresh();
                    });
            }
            else
            {
                context.AddButton(IconId.WPlanning,
                    "Create plan from constructed ship",
                    "Scan constructed buildings", () =>
                    {
                        pendingCaptureConstructedShip = true;
                        panel.Refresh();
                    },
                    "Requires a second confirmation because the captured ship replaces the current Construction Plan.");
            }

            context.AddButton(IconId.CCopySettings, "Copy blueprint code",
                "Copy active plan", system.CopyBlueprintCode,
                "Copies the current active Construction Plan directly. A saved blueprint file is not required.");
        }

        IReadOnlyList<string> saved = system.SavedBlueprints();
        bool saveLoadOpen = AddSectionHeader(context, "Blueprints.SaveLoad",
            "Save/Load blueprint · " + saved.Count + " saved", true,
            pendingClearBlueprint);
        if (!saveLoadOpen) return;

        AddBlueprintSaveRow(context);

        if (pendingClearBlueprint)
        {
            AddWrappedInfo(context, "Clear the current blueprint from the plan?",
                "This removes every Construction Plan room and building ghost, staging designation, and persisted Planning Overhaul palette mark. Saved blueprints, constructed buildings, and existing native jobs are preserved.");
            context.AddButton("Icons/Color/Warning",
                "Confirm clear blueprint from plan", "Remove the full design",
                () =>
                {
                    pendingClearBlueprint = false;
                    system.ClearBlueprintPlan();
                });
            context.AddButton(IconId.WArrowLeft, "Keep current blueprint",
                string.Empty,
                () =>
                {
                    pendingClearBlueprint = false;
                    panel.Refresh();
                });
        }
        else
        {
            context.AddButton(IconId.WPlanningClear,
                "Clear blueprint from plan", "Remove the full design", () =>
                {
                    pendingClearBlueprint = true;
                    panel.Refresh();
                },
                "Requires confirmation. Saved blueprint files, constructed buildings, and existing native jobs are preserved.");
        }

        if (AddSectionHeader(context, "Blueprints.Import",
                "Import a shared code", false))
        {
            context.AddInfo("Icons/Color/Info", "Current map size",
                system.CurrentMapSize,
                "Imported blueprints keep their tile layout relative to the map center. A blueprint saved on a larger map can load when every building footprint, Transporter target, and staging cell fits the centered current map without entering its corner no-build zones. Blueprints are never scaled or clipped.");
            AddIsolatedInput(context, IconId.WArrowUp, "Paste blueprint code",
                importCode, value => importCode = value ?? string.Empty,
                tooltip: "Paste a complete dimensioned Planning Overhaul blueprint code, then choose Import.");
            context.AddButton(IconId.WArrowUp, "Import pasted blueprint",
                string.Empty, () => system.ImportBlueprintCode(importCode),
                "Replaces the current unexecuted construction plan only after verifying that all saved content fits the centered current construction area.");
        }

        if (AddSectionHeader(context, "Blueprints.Saved",
                "Saved blueprints · " + saved.Count, true))
        {
            if (saved.Count == 0)
            {
                context.AddInfo("Icons/Color/Info",
                    "No reusable blueprints saved", "Ready");
            }
            else
            {
                VerticalScrollViewResult scroll = context.AddVerticalScroll(
                    "PlanningOverhaul_SavedBlueprintScroll", 300f, 600f, 1f,
                    savedBlueprintScrollPosition,
                    value => savedBlueprintScrollPosition = value);
                LayoutElement size = scroll.Root.GetComponent<LayoutElement>();
                float height = Mathf.Clamp(saved.Count * 47f, 47f, 235f);
                size.minHeight = size.preferredHeight = height;
                size.flexibleHeight = 0f;
                int row = 0;
                foreach (string name in saved)
                    AddSavedBlueprintRow(context, scroll.Content, row++, name);
            }
        }
    }

    private void AddBlueprintSaveRow(PanelContentContext context)
    {
        GameObject root = context.CreateOwned("PlanningOverhaul_BlueprintSaveRow");
        RectTransform rect = root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        LayoutElement size = root.AddComponent<LayoutElement>();
        size.minHeight = size.preferredHeight = 40f;
        size.flexibleHeight = 0f;
        HorizontalLayoutGroup layout = root.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 4f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;

        WInputWithIcon input = UISpawn.Widget<WInputWithIcon>(context.Lifetime,
            "WContent2Input", CreateInlineSlot(rect, "BlueprintName", 0f), 0);
        input.Icon.Load(IconId.CLabel);
        input.Input.Load("Blueprint name", system.BlueprintName, _ => { });
        BindIsolatedInput(input, system.SetBlueprintName);
        UIComponentFactory.SetTip(input,
            "Name the current active Construction Plan before saving it as a reusable blueprint.");

        WButtonIconText2 save = UISpawn.Widget<WButtonIconText2>(context.Lifetime,
            UIPrefabId.WContent4Button, CreateInlineSlot(rect, "Save", 190f), 0);
        save.Load(IconId.WSave, "Save blueprint", string.Empty,
            system.SaveNamedBlueprint);
        UIComponentFactory.SetTip(save,
            "Saves the current active Construction Plan in the Stardeus persistent-data folder.");
    }

    private void AddSavedBlueprintRow(PanelContentContext context,
        RectTransform parent, int siblingIndex, string name)
    {
        var root = new GameObject("PlanningOverhaul_SavedBlueprintRow");
        RectTransform rect = root.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.SetSiblingIndex(siblingIndex);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        LayoutElement size = root.AddComponent<LayoutElement>();
        size.minHeight = size.preferredHeight = 40f;
        size.flexibleHeight = 0f;
        HorizontalLayoutGroup layout = root.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 4f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;

        // Size owned slots rather than modifying the native pooled widgets.
        // The content lifetime returns both widgets before the row is removed.
        WButtonIconText2 load = UISpawn.Widget<WButtonIconText2>(context.Lifetime,
            UIPrefabId.WContent4Button, CreateInlineSlot(rect, "Load", 0f), 0);
        load.Load(IconId.WConfigLoad, name, "Load blueprint",
            () => system.LoadNamedBlueprint(name));

        WButtonIcon delete = UISpawn.Widget<WButtonIcon>(context.Lifetime,
            UIPrefabId.WButtonIcon, CreateInlineSlot(rect, "Delete", 40f), 0);
        delete.Load(IconId.CDemolition, () => system.DeleteNamedBlueprint(name));
        UIComponentFactory.SetTip(delete, "Delete saved blueprint “" + name
            + "”. The active Construction Plan is preserved.");
    }

    private static RectTransform CreateInlineSlot(RectTransform parent,
        string name, float width)
    {
        var root = new GameObject(name);
        RectTransform rect = root.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        LayoutElement size = root.AddComponent<LayoutElement>();
        size.minWidth = size.preferredWidth = width;
        size.flexibleWidth = width > 0f ? 0f : 1f;
        size.minHeight = size.preferredHeight = 40f;
        VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = true;
        return rect;
    }

    private static string LayerName(int layer)
    {
        if (layer == WorldLayer.Floor) return "Floor";
        if (layer == WorldLayer.Walls) return "Wall / door";
        if (layer == WorldLayer.Objects) return "Equipment";
        return "Layer " + layer;
    }

    private static void AddIsolatedInput(PanelContentContext context, string icon,
        string placeholder, string value, Action<string> committed,
        string tooltip = null, Action<string> submitted = null)
    {
        // WContent2Input is pooled by Core. Remove callbacks left by an earlier
        // panel row before binding this row so one field cannot update another.
        var input = context.AddInput(icon, placeholder, value,
            _ => { }, tooltip: tooltip);
        BindIsolatedInput(input, committed, submitted);
    }

    private static void BindIsolatedInput(WInputWithIcon input,
        Action<string> committed, Action<string> submitted = null)
    {
        input.Input.InputField.onEndEdit.RemoveAllListeners();

        int lastCommitFrame = -1;
        void CommitOnce(string current, bool wasSubmitted)
        {
            // Enter raises both submit and end-edit during the same frame.
            if (lastCommitFrame == Time.frameCount) return;
            lastCommitFrame = Time.frameCount;
            if (wasSubmitted && submitted != null) submitted(current);
            else committed?.Invoke(current);
        }

        input.Input.SetOnSubmit(current => CommitOnce(current, true));
        input.Input.InputField.onEndEdit.AddListener(
            current => CommitOnce(current, false));
    }

    private bool AddSectionHeader(PanelContentContext context, string key,
        string title, bool defaultExpanded, bool forceOpen = false,
        Action<WText> created = null)
    {
        if (!sectionExpanded.TryGetValue(key, out bool expanded))
        {
            expanded = defaultExpanded;
            sectionExpanded[key] = expanded;
        }
        bool shownExpanded = forceOpen || expanded;
        WText header = context.AddCollapsibleHeader(title, shownExpanded,
            value => sectionExpanded[key] = value,
            shownExpanded ? "Collapse this section." : "Expand this section.",
            !forceOpen);
        created?.Invoke(header);
        return shownExpanded;
    }

    private WrappedInfoRow AddWrappedInfo(PanelContentContext context, string label,
        string value, string tooltip = null)
    {
        string detail = value ?? string.Empty;
        GameObject root = context.CreateOwned("PlanningOverhaul_WrappedInfo");
        RectTransform rect = root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        LayoutElement layout = root.AddComponent<LayoutElement>();
        layout.minHeight = 56f;
        layout.flexibleHeight = 0f;
        // Native layout uses TMP's preferred height at the resolved row width.
        // A character-count estimate can leave too little space for one line.
        VerticalLayoutGroup contentLayout = root.AddComponent<VerticalLayoutGroup>();
        contentLayout.padding = new RectOffset(12, 12, 3, 5);
        contentLayout.spacing = 2f;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        Image background = root.AddComponent<Image>();
        FrameworkTheme.ApplyImage(background, "PanelContent");

        TextMeshProUGUI title = CreateWrappedText(rect, label, 15f,
            FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
        LayoutElement titleLayout = title.gameObject.AddComponent<LayoutElement>();
        titleLayout.minHeight = 22f;
        titleLayout.flexibleHeight = 0f;

        TextMeshProUGUI body = CreateWrappedText(rect, detail, 14f,
            FontStyles.Normal, TextAlignmentOptions.TopLeft);
        LayoutElement bodyLayout = body.gameObject.AddComponent<LayoutElement>();
        bodyLayout.minHeight = 24f;
        bodyLayout.flexibleHeight = 0f;
        if (label == "Status")
        {
            statusBody = body;
            statusBackground = background;
        }

        UIComponentFactory.SetTip(background,
            string.IsNullOrWhiteSpace(tooltip) ? detail : tooltip);
        return new WrappedInfoRow(root, title, body, background);
    }

    private static TextMeshProUGUI CreateWrappedText(RectTransform parent,
        string value, float size, FontStyles style, TextAlignmentOptions alignment)
    {
        var child = new GameObject("Text");
        child.transform.SetParent(parent, false);
        RectTransform rect = child.AddComponent<RectTransform>();
        var text = child.AddComponent<TextMeshProUGUI>();
        text.text = value ?? string.Empty;
        text.fontSize = size;
        text.fontStyle = style;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        FrameworkTheme.ApplyText(text, "Unsized");
        return text;
    }

    private sealed class PulseUiState
    {
        internal bool PausedInspection;
        internal string PausedInspectionStatus;
        internal PulsePreview Preview;
        internal EquipmentConstructionPreview Construction;
        internal IReadOnlyList<MaterialRequirement> ShownMaterials;
        internal string RoomTitle;
    }

    private sealed class WrappedInfoRow
    {
        internal readonly GameObject Root;
        private readonly TextMeshProUGUI title;
        private readonly TextMeshProUGUI body;
        private readonly Image background;

        internal WrappedInfoRow(GameObject root, TextMeshProUGUI title,
            TextMeshProUGUI body, Image background)
        {
            Root = root;
            this.title = title;
            this.body = body;
            this.background = background;
        }

        internal void Set(string label, string detail, string tooltip,
            bool visible)
        {
            if (Root.activeSelf != visible) Root.SetActive(visible);
            if (!visible) return;
            title.SetText(label ?? string.Empty);
            body.SetText(detail ?? string.Empty);
            UIComponentFactory.SetTip(background,
                string.IsNullOrWhiteSpace(tooltip) ? detail : tooltip);
        }
    }

    public void Dispose()
    {
        widget?.Dispose();
        panel?.Dispose();
    }
}
