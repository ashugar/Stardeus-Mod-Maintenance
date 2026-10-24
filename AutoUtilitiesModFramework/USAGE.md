# Auto Utilities Mod Framework Usage

This guide shows the recommended consumer lifecycle. The source files contain
XML documentation for every public function and lower-level component.

Validated theme integration is available in AUMF 1.4.0 and documented in
[THEME_TESTING.md](THEME_TESTING.md). The new opt-in APIs require a tested 1.4.0
consumer declaration; existing helpers retain their signatures. The earlier
1.3.0 testing builds do not establish availability in the published 1.3.0 API.

## 1. Declare and reference the framework

Add `AutoUtilitiesModFramework` to the consumer's `ModInfo.json` dependencies.
`Core` is the game itself and is not a mod dependency.

Reference `AutoUtilitiesModFramework/Libraries/AutoUtilitiesModFramework.dll`
from the consumer project with `Private` set to `False`. Do not copy the
framework DLL into the consumer mod.

Every consumer must hardcode the exact AUMF version it has tested and register
that declaration through the permanent compatibility handshake:

```csharp
private const string ConsumerId = "MyMod";
private const string TestedUtilitiesVersion = "1.4.0"; // After building/testing this consumer with 1.4.0.

if (!AutoUtilitiesApi.TryRegisterConsumer(
        ConsumerId,
        "My Mod",
        typeof(MyModSystem).Assembly.GetName().Version.ToString(3),
        TestedUtilitiesVersion))
{
    // AUMF has logged and queued a visible warning. Do not initialize the
    // parts of this mod that require AUMF.
    return;
}
```

An exact match is fully validated. An older consumer in the same AUMF major
version receives a consolidated in-game warning but may continue using stable
features. A consumer that requires a newer AUMF, or belongs to a different
AUMF major version, receives a warning and the handshake returns `false`.

The consumer mod's own version remains independent. `TestedUtilitiesVersion`
is the AUMF compatibility level the consumer has explicitly tested. The
consumer's assembly version is displayed in compatibility details so players
can identify the exact dependent-mod build they have installed; it does not
control compatibility by itself.

### Gate features introduced by newer AUMF versions

Wrap each optional UI section or behavior that depends on a newer framework
feature. Both the installed AUMF and the registered consumer declaration must
meet the feature's introduction version:

```csharp
if (AutoUtilitiesApi.IsFeatureAvailable(ConsumerId, "1.2.0"))
{
    BuildFeatureIntroducedInAumf12(ui);
}
else
{
    // Omit the unsupported section. All older stable sections continue.
}
```

Do not use the consumer mod's release version in this check. When the consumer
has been tested with the newer feature, update `TestedUtilitiesVersion`, build,
and test that consumer. Until then, the feature remains unavailable even if a
newer AUMF happens to be installed.

### Maintain the feature catalog

Every newly introduced public helper or optional capability must receive an
entry in `Code/Compatibility/FrameworkFeatureCatalog.cs` containing:

- A stable feature ID
- A short player-facing name
- The first AUMF version containing it
- A concise description of the functionality consumers are missing

`ThemeAwareUi` is introduced in 1.4.0. Use
`IsFeatureAvailable(ConsumerId, "1.4.0")` around optional sections using the new
theme helpers. It is true only when the installed framework and the registered
consumer meet that level. Existing consumers declaring 1.3.0 may keep using
stable APIs with a compatibility warning, but the 1.4.0 gate remains false for
them until they adopt and test the newer level. Internal theme updates to
existing `AddTabs`/scroll/header helpers do not require new call signatures.

The compatibility panel derives its unavailable-feature section from this
table. `AutoUtilitiesApi.GetFeatureCatalog()` exposes the same documentation to
consumers, while `IsFeatureAvailable` remains the authoritative activation
check. Do not add a public feature without cataloging and version-gating it.

## 2. Register a widget and panel

Create these once in the consumer system after the compatibility handshake. Keep
both references for refresh and cleanup.

```csharp
private FrameworkPanel panel;
private ITopBarWidgetHandle widget;
private bool enabled = true;
private int targetCount;

private void CreateUi()
{
    panel = new FrameworkPanel(new FrameworkPanelOptions
    {
        Id = "MyMod.ControlPanel",
        Title = "My Mod Controls",
        MinSize = new Vector2(380f, 260f),
        MaxSize = new Vector2(1000f, 800f),
        RememberGeometry = true
    }, BuildPanel);

    widget = AutoUtilitiesApi.RegisterTopBarWidget(
        new TopBarWidgetRegistration
        {
            Id = "MyMod.StatusWidget",
            Icon = () => GraphicsId.IconsColorInfo,
            Tint = () => enabled ? Color.white : Color.gray,
            Tooltip = () => "My Mod: " + (enabled ? "ON" : "OFF")
                + "\nTargets: " + targetCount
                + "\nClick for controls.",
            // Optional: omit for the original icon-only presentation.
            BadgeText = () => targetCount.ToString(),
            OnClick = () => panel.Toggle(),
            Width = 32f,
            Order = 0
        });
}

private void BuildPanel(PanelContentContext ui)
{
    ui.AddHeader("Automation");
    ui.AddToggle(GraphicsId.IconsColorInfo, "Enable automation",
        enabled ? "On" : "Off", enabled, value =>
        {
            enabled = value;
            widget.Refresh(); // Re-evaluates tint and tooltip providers.
            panel.Refresh();  // Rebuilds displayed panel values.
        });
    ui.AddInfo(GraphicsId.IconsColorInfo, "Available targets",
        targetCount.ToString());
    ui.AddButton(GraphicsId.IconsColorCopySettings, "Run scan", "", () =>
        {
            RunScan();
            widget.Refresh();
            panel.Refresh();
        });
}
```

Provider functions are evaluated again by `widget.Refresh()`. A currently
visible tooltip is updated in place. Widget creation may happen later than
registration while the game builds its UI; the framework handles retries.

## 3. Clean up during unload

The consumer owns the returned controllers:

```csharp
public override void Unload()
{
    panel?.Dispose();
    panel = null;
    widget?.Dispose();
    widget = null;
}
```

Dispose prevents callbacks and UI objects from surviving a mod reload.

### Core popup compatibility maintenance

The validated 1.4.0 maintenance repair for Stardeus `0.15.0-rc344.3226`
updates AUMF's internal `FrameworkPanel.Show()` integration to Core's current
six-parameter `AfterShowPopup` contract. Existing consumers continue calling
`Show()` or `Toggle()` normally; no consumer workaround, rebuild, or framework
version-declaration change is required for this repair. AUMF version numbers
advance for new features, not bugfixes or compatibility maintenance.
See the [README validation record](README.md#140---validated-popup-compatibility-repair-2026-10-24)
for the accepted scope and build identity.

## 4. Inputs, tabs, flow layout, and scrolling

`AddInput` commits once when Enter is pressed or the field loses focus:

```csharp
ui.AddInput(GraphicsId.IconsColorInfo, "Enter label", savedLabel,
    value => { savedLabel = value; panel.Refresh(); });
```

The 2026-09-11 maintenance repair scopes AUMF's commit binding to the content
lifetime. AUMF removes only its own end-edit listener and releases the consumer
callback on disposal, preventing callbacks from earlier pooled uses from
accumulating. Events raised while the lifetime is being disposed are ignored.
The once-per-frame Enter/end-edit deduplication remains per binding.

Core exposes a setter but no ownership check for its single submit callback.
After disposal, an AUMF submit delegate left in that slot is inert and holds no
consumer, input field, or lifetime reference; the next binding replaces it.
AUMF does not clear that slot during cleanup, because a consumer may have
replaced it. Other callbacks added directly to a returned native field remain
the consumer's responsibility. No API or version declaration change is required
for existing `AddInput` users. Fresh in-game acceptance of this maintenance
repair is pending; see [INPUT_LIFECYCLE_TESTING.md](INPUT_LIFECYCLE_TESTING.md).

`FrameworkPanel.Refresh()` is safe to call directly from input, toggle, and
button callbacks. AUMF coalesces requests made during the same frame and
rebuilds on the next UI frame, after the active native callback has completed.
Before pooled content is returned, AUMF also releases any Core text-focus owner
inside that panel so the rebuilt field cannot remain registered after becoming
inactive. Consumers do not need to add their own focus cleanup, delay, or
deferred-refresh helper.

This lifecycle correction was runtime-validated with repeated native input
interactions and refreshes during 1.3.0 and is retained in 1.4.0. That correction
alone required no consumer API or metadata change.

Keep active-tab state in the consumer. `AddTabs` rebuilds automatically after
the selection callback saves the new ID:

```csharp
ui.AddTabs(new[]
{
    new PanelTabDefinition { Id = "Controls", Label = "Controls" },
    new PanelTabDefinition { Id = "Status", Label = "Status" }
}, activeTab, id => activeTab = id);

if (activeTab == "Controls") BuildControls(ui);
else BuildStatus(ui);
```

In AUMF 1.4.0, `AddTabs` follows Core's Tab/Tab_Active and TabPanelButtons
colors, including button interaction states and label contrast, while retaining
the existing dimensions and typography. `VerticalScrollView` and disclosure
arrows also follow theme reloads. A custom `CreateTabButton` call keeps its
historical color behavior; opt in with `CreateThemedTabButton` and style its row
using `FrameworkTheme.ApplyImage(rowImage, "TabPanelButtons")`. Unavailable
themed buttons use opaque `#555555`; other states and text follow Core.
See the theme guide for explicit-part slider/scrollbar binding and state cards.

For resizable card collections, add child objects to the returned flow parent:

```csharp
RectTransform cards = ui.AddResponsiveFlow("TargetCards",
    new Vector2(150f, 76f), new Vector2(8f, 8f));
card.transform.SetParent(cards, false);
```

For long content, retain normalized scroll position in consumer state:

```csharp
var scroll = ui.AddVerticalScroll("History", 300f, 600f, 1f,
    scrollPosition, value => scrollPosition = value);
row.transform.SetParent(scroll.Content, false);
```

Inside `FrameworkPanel`, AUMF keeps the managed content block at least as tall
as the popup's visible inner viewport. A scroll's default
`flexibleHeight = 1` can therefore consume height added by panel resizing.
Additional flexible children share spare height according to their flexible
weights, and fixed controls before or after the scroll retain their preferred
sizes. If preferred content is taller than a compact panel, the panel's native
outer scroll remains available.

Consumers may still set the returned root's `minHeight`, `preferredHeight`, or
`flexibleHeight` as layout preferences. A fixed preferred height or a local
panel-height fitter is not required merely to make a managed scroll follow the
resized panel. Standalone `VerticalScrollView.Create(...)` calls remain driven
by their parent layout because they have no `FrameworkPanel` viewport contract.

For top-level managed rows, use `flexibleHeight = 0` for controls that must keep
their requested height and a positive value only for regions that should consume
surplus resized height. `FrameworkPanel` temporarily disables the native pooled
content layout's forced vertical child expansion while the block is active, then
restores the original setting before despawn. Consumers do not need to modify or
restore the native content layout themselves.

Native rows created with the current `PanelContentContext.Lifetime` may be
parented directly beneath `scroll.Content`; AUMF separates custom scroll roots
from pooled native content during refresh and owns their cleanup.

A validated AUMF `1.3.0` maintenance correction preserves world transforms
while custom roots are detached and their inactive native rows are parked for
pool reuse. This keeps pooled headers, text rows, and inputs at local scale one
after they return to a non-unit Stardeus UI hierarchy. The reproducing
`ui.scale = 0.8` case no longer produces reciprocal `1.25` local scales or
Core's `Fixing scale` diagnostics during repeated rebuilds. Consumers should
continue parenting lifetime-owned native rows directly under `scroll.Content`;
do not apply a consumer-specific scale adjustment before or after
`UISpawn.Widget`.

### Collapsible sections (AUMF 1.3.0)

Register and test the consumer against AUMF `1.3.0` or newer before using collapsible
sections. Keep expanded/collapsed state in the consumer; AUMF owns the native
header, arrow, interaction, pooled lifecycle, and deferred panel rebuild:

```csharp
ui.AddCollapsibleHeader("Advanced", advancedExpanded,
    value => advancedExpanded = value,
    advancedExpanded ? "Collapse Advanced" : "Expand Advanced");

if (advancedExpanded)
{
    ui.AddInfo(GraphicsId.IconsColorInfo, "Mode", currentMode);
}
```

The callback stores the new state. `AddCollapsibleHeader` automatically calls
the enclosing panel's deferred, coalesced refresh after the callback returns;
do not schedule a second refresh for the same click.

Use the explicit-parent overload for grouped rows under a managed scroll:

```csharp
int row = 0;
ui.AddCollapsibleHeader(scroll.Content, row++, "Components",
    componentsExpanded,
    value => componentsExpanded = value,
    componentsExpanded ? "Collapse Components" : "Expand Components");

if (componentsExpanded)
{
    // Spawn the consumer-owned rows beneath scroll.Content using row++.
}
```

Pass `interactable: false` when a consumer temporarily forces a section open,
such as while search results are active. This disables state changes without
moving category names, grouping, filter rules, rendered rows, or save behavior
into AUMF. The explicit-parent overload does not advance the context's normal
top-level row counter.

Use `CreateOwned` for custom objects inside a panel rebuild. Such objects are
destroyed automatically on refresh and close. Prefer the context helpers over
the lower-level classes unless a custom UI cannot use `FrameworkPanel`.

## 5. Register a guarded keyboard shortcut (AUMF 1.2.0)

Register the consumer with a tested AUMF version of at least `1.2.0`, then
create one handle for each shortcut action:

```csharp
private IShortcutHandle shortcut;

shortcut = AutoUtilitiesApi.RegisterShortcut(new ShortcutRegistration
{
    ConsumerId = ConsumerId,             // Same ID used at registration.
    Id = "TogglePanel",                  // Stable and unique within this mod.
    DisplayName = "open or close My Mod",
    PreferenceKey = "mymod.ui.shortcut", // Globally unique preference key.
    GameState = S,
    OnPressed = TogglePanel
});
```

The consumer's settings UI reads `DisplayName`, `StatusMessage`,
`HasShortcut`, and `IsCapturing`. Its buttons call `BeginCapture()`,
`CancelCapture()`, and `Clear()`. Subscribe to `StateChanged` when the panel
should redraw immediately after an assignment, rejection, cancellation, or
clear operation.

AUMF owns the saved control path and immediately persists it through Stardeus
global preferences. It rejects duplicate consumer/action IDs and preference
keys; prevents two registered mods from sharing a key; checks current native
game bindings and F1-F10 overlay keys; reserves F12 for Steam screenshots;
ignores modifier-only assignments; suppresses shortcuts during text entry or
any shortcut capture; dispatches only the Input System's once-per-press event;
isolates callback exceptions; and disposes its `InputAction` when the handle is
disposed. Consumers must not add their own frame polling or second
`InputAction` around the same shortcut.

Always dispose the handle from the consumer's unload path:

```csharp
shortcut?.Dispose();
shortcut = null;
```

This feature intentionally supports one unmodified keyboard key. Modifier
combinations are not part of the 1.2.0 contract.

## 6. Enable the built-in test widget and UI

The test harness is disabled in 1.4.0 so players do not see an extra framework
widget. Its code and the Theme palette remain available for future development.

1. Open `Code/UI/TopBarWidgetSys.cs`.
2. Change `EnableDemonstrationUi` from `false` to `true`.
3. Build `.vscode/mod.csproj` in Release configuration.
4. Restart Stardeus or fully reload the mod assembly, then load a game.
5. Find the cyan copy/settings icon in the top bar. Hover it to test the live
   tooltip and click it to open the five-tab test panel.
6. Exercise Controls, Layout, Sections, resizing, Scroll, and Theme.
7. Change the flag back to `false` and rebuild before a Workshop release.

Do not disable `TopBarWidgetSys` itself. Dependent widgets require that system;
the flag controls only the framework's demonstration widget and panel.

`Code/UI/Demo/FrameworkUiTestPanel.cs` is the living implementation example.
Its `BuildControls`, `BuildLayout`, `BuildSections`, `BuildScroll`, and `BuildTheme`
methods demonstrate the public functions, including the validated 1.4.0 theme APIs.
The Theme page retains its half-height slider fill, 16 fixed comparison swatches
and expandable 66-entry Core palette with live color codes and text samples.

## 7. Framework release-number policy

AUMF versions use `MajorFeature.MinorFeature.NotUsed`.

- Do not increment AUMF for bug fixes.
- Increment the minor component only for a new cataloged feature.
- Increment the major component after ten new features in the current major
  line, or sooner for a major/breaking capability.
- Keep the third component at zero.

Consumers only update `TestedUtilitiesVersion` when they choose to adopt and
test a newer feature level. They may continue using older stable capabilities
without recognizing every newer AUMF feature release.
