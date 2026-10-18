# Theme-aware UI - AUMF 1.4.0

This records the accepted 2026-09-05 theme release. The separate 2026-09-11
[pooled-input repair](INPUT_LIFECYCLE_TESTING.md) awaits fresh runtime acceptance;
the theme-release DLL hashes below are historical, not the maintenance build.

Status: complete and validated. AUMF's theme update was accepted in-game by the
user on 2026-09-05 and promoted to 1.4.0 at their request. The user subsequently
confirmed the installed 1.4.0 update was successful and complete, with the test
UI disabled. The user confirms proper readability across themes,
including Elysium, Dark and Grey after the unavailable-button color change.
Testing deliberately stayed at 1.3.0; those temporary builds are historical,
not the introduction version of the public API. `ThemeAwareUi` is now the
accepted 1.4.0 feature-catalog entry.

Manifest/API/product version is 1.4.0; project/assembly/file version is 1.4.0.0.
The test widget/panel is disabled, as separately requested by the user. Its
source, palette and slider sample remain available for later development.
No consumer mods were changed, and no Workshop upload was performed.

## Automatically updated surfaces

- `PanelContentContext.AddTabs`: row background, normal/selected buttons, hover,
  pressed and disabled button colors, and corresponding text contrast.
- `VerticalScrollView.Create` and `AddVerticalScroll`: scrollbar background,
  handle and interaction colors; dimensions, scrolling and position restoration
  retain their existing contract.
- Collapsible header arrows: match the native header after live theme reload,
  including reloads while the pooled header is inactive.
- AUMF's demonstration cards and scrolling rows use the same theme primitives.

Bindings derive from Core's UIThemedImage/Text/Button/Slider/Scrollbar types.
Core's normal reload pass finds these components, including inactive ones.
There is no theme polling, global patch, new game preference, or recursive
recoloring of consumer panels. Only explicit target graphics/labels are styled.
Colors and ColorTint states follow Core except the chosen `#555555` unavailable
button tint; custom sprites, typography, geometry,
values, callbacks and interactability remain owned by the control/consumer.
An unavailable state card intentionally blocks its click; a disabled setting
card remains clickable so it can be enabled again.

The original six-parameter `CreateTabButton` method remains binary-compatible
and retains its historical implementation. This protects RTC's graph nodes,
which call it and then assign a connection-state color. Custom tabs opt in
through the separate `CreateThemedTabButton` method.

## Read-only installed-consumer audit

Source calls were cross-checked against installed consumer DLL member references.

| Consumer | Automatic changes with this build | Remaining migration |
| --- | --- | --- |
| TACC / Trait and Clothing Compatibility | Tabs and managed scrollbars | None for requested surfaces |
| Auto Factory | Tabs, managed scrollbars, collapsible arrows | None for requested surfaces |
| Auto Hauling | Tabs, managed scrollbars, collapsible arrows | None for requested surfaces |
| Super Cleaning | Tabs and collapsible arrows | None for requested surfaces |
| Intelligent Recycling | Tabs | Custom body-part title bars and custom state-card styles |
| CARAU Main | Being-list scrollbar | Custom tab row/buttons, cards, headers, actions, sliders, shortcut and Debug sections |
| CARAU Tracked Operations | Being-list scrollbar; native outer panel already themed | Custom list selections, separators, headers, operation cards and action buttons |
| Reconnect Teleporter Connections | Framework tab row and managed scrollbars | Graph-node state colors deliberately remain consumer-owned |

AACU, Bot Auto Hack, Part Restoration Unit, Tractor Beam Auto Connect, and
Tractor Beam Overhaul reference AUMF but do not call the changed tab/scroll/
collapsible helpers in their installed DLLs. Their native widgets continue
following their existing Core theme behavior.

CARAU's direct tab helper is in `.vscode/UI/ColonistAndBotsAutoUpgradePanel.cs`;
its custom tab-row background is constructed separately in `DrawTabs`.
IR's title colors are in `.vscode/Code/IntelligentRecyclingBodyPartCard.cs`.
CARAU's other principal migration targets are `SlotCardView`,
`SlotToggleCardView`, `QualityThresholdView`, `RetryPolicyCardView`,
`DebugCategoryCardView`, and `CARAUOperationTrackingPanel`.
All consumer files and DLLs remain outside this change.

## Consumer usage - introduced in 1.4.0

Build against the 1.4.0 DLL without bundling it, declare the framework version
actually tested by that consumer, and use the four-argument compatibility
handshake. Gate optional new-helper entry points with
`IsFeatureAvailable(ConsumerId, "1.4.0")`. The gate is true only if both the
installed framework and registered consumer meet 1.4.0. Older same-major
consumers keep stable functionality with the standard mismatch warning; their
1.4.0 gate remains false. Automatic theme updates to existing tab/scroll/header
helpers retain their call signatures. Do not weaken checks or mark an untested
consumer as 1.4.0-compatible merely to suppress a warning.

Use bindings on custom GameObjects that are destroyed with their owning UI,
preferably below `CreateOwned` or `AddResponsiveFlow`. Native pooled controls
already have their own styles: do not attach these bindings to them. A binding
rejects a competing native theme component on its target. Reapplying the same
helper changes its role without adding another binding. Do not write `.color`
after binding: Core will reapply the chosen role on the next theme change.

```csharp
// Custom row/card backgrounds and labels. Text size, wrapping, margins,
// font style and alignment are preserved by the color bindings.
FrameworkTheme.ApplyImage(rowImage, "PanelContent");
FrameworkTheme.ApplyImage(headerImage, "PanelHeader");
FrameworkTheme.ApplyText(titleText, "Unsized");
FrameworkTheme.ApplyImageColor(statusMarker, "Green");
FrameworkTheme.ApplyTextColor(statusText, "Red");

// Only the named labels receive this button's foreground color.
// Do not separately bind its background or these labels.
FrameworkTheme.ApplyButton(actionButton, "Panel", actionLabel);
FrameworkTheme.ApplyButton(cancelButton, "Red", cancelLabel);

// Explicit part references preserve custom hierarchies.
// Set targetGraphic to the handle when constructing either control.
FrameworkTheme.ApplySlider(slider, trackImage, fillImage, handleImage);
FrameworkTheme.ApplyScrollbar(scrollbar, scrollBackground, scrollHandle);

// Custom CARAU-style tab rows can opt in without changing their layout.
FrameworkTheme.ApplyImage(tabRowImage, "TabPanelButtons");
UIComponentFactory.CreateThemedTabButton(tabRow, "Main", active, onClick);

// Generic cards: consumers retain settings, state decisions and callbacks.
RectTransform cards = ui.AddResponsiveFlow("SettingsCards",
    new Vector2(175f, 100f), new Vector2(8f, 8f));
FrameworkStateCard card = UIComponentFactory.CreateStateCard(cards,
    "Automation", "Current setting", FrameworkCardState.Enabled,
    () => { ToggleAutomation(); panel.Refresh(); });
// An existing card may also change visually without rebuilding:
card.SetState(FrameworkCardState.Disabled);
```

Available card states are Neutral, Selected, Enabled, Disabled, Unavailable,
Warning, and Danger. Each has a text state label as well as a semantic marker;
selected cards use the theme accent with inverse text. Enabled and Disabled/
Danger markers retain Core Green/Red. Neutral uses opaque light grey `#D3D3D3`,
Unavailable uses opaque dark grey `#555555`, and Warning uses opaque orange
`#FFA500`; these three semantic colors stay constant across themes. Full-color item icons,
portraits, and domain progress bars must only be themed when their consumer
explicitly chooses the appropriate meaning.

Style strings refer to Core's Images/Texts/Buttons/Sliders/Scrollbars collections;
color strings refer to Core's Colors collection (including its modifiers) or
Core-supported hexadecimal literals (`#RRGGBB` / `#RRGGBBAA`).
Styles missing in another theme inherit Core's Default definitions. Unknown
IDs follow Core's ordinary diagnostic/fallback behavior; do not invent IDs.

## User testing results - 2026-09-05

Reported successful by the user:

- TACC, Auto Hauling, Super Cleaning, and Auto Factory.
- Intelligent Recycling: tabs only; custom card titles are not included.
- AUMF Controls, Layout, Sections, and Scroll pages. Layout cards flow and
  resize correctly.

The user supplied the Theme page in all five available themes. The screenshots
show the palette and selected-state background changing as intended. The user
found none of them particularly bad, but some states harder to read; this is
positive visual feedback, not a rejection or final acceptance of state styling.
CARAU was reported as not altered and is not counted as an accepted migration.
RTC and the remaining interaction/log checks below have not been confirmed by
this feedback.

### Follow-up theme review and palette request

After testing the strip/fill refinement, the user reports easy readability in
Default and Neon Nights. Dark, Elysium, and Grey still make Normal and Unavailable
buttons difficult to distinguish at a glance; the other state appearances look
good. Elysium's Neutral/Unavailable strip distinction is explicitly improved,
and the orange Warning strip was positively accepted. The screenshots show the
half-height slider fill and distinct full-height handle; exhaustive dragging
and resize acceptance is not inferred from still images.

At that stage the requested next step was a color palette, not a chosen
replacement for the Unavailable button. The palette revision itself retained
Core-derived button styling. All changes in that revision were confined to the AUMF demonstration UI,
its verification script, and documentation; no consumer/API migration is needed.

### Selected unavailable-button color

The user has now chosen opaque dark grey `#555555` for unavailable buttons,
matching the existing Unavailable slot-card strip, and asked to retain the
palette in the testing UI. The user subsequently verified proper readability
across themes and explicitly accepted AUMF's update for promotion to 1.4.0.

`FrameworkThemedButton` copies Core's ColorBlock and replaces only its disabled
color. Normal, highlighted, pressed and selected tints, text, fade timing,
callbacks, sprites and interactability are unchanged. Unity's normal disabled
state handles both `Button.interactable = false` and disabled parent CanvasGroups;
no refresh callback or per-frame polling is added. Core's theme reload reapplies
the chosen disabled tint. The four supported stock button styles have a white
image base in all five stock themes, so the final opaque tint is `#555555`.

This is internal shared behavior for `FrameworkTheme.ApplyButton`,
`CreateThemedTabButton`, and `PanelContentContext.AddTabs`. It does not patch
Core/native pooled buttons, legacy unbound `CreateTabButton` controls, sliders,
scrollbars, or consumer-owned colors. Consumers already using themed helpers
need no API or version-metadata changes. A card whose setting is Disabled still
uses its red strip and remains clickable; that is distinct from an unavailable
button. No consumer was edited during this update.

### Palette comparison guide

Scroll below the existing Theme samples to **Color palette - fixed comparison
colors**. Sixteen fixed swatches include pink, hot pink, cyan, soft cyan, the
current light/dark greys and orange, plus other reference colors. Their RGB
values stay constant across themes, while the card labels and `Aa` samples use
the current theme's normal text color. Low contrast in an `Aa` sample is part
of the comparison; names and color codes remain outside the colored area.

Expand **All Core theme colors (66)** to see every named color in the five
currently installed stock theme definitions: 65 common/fallback names plus
`RedPanel`, which only the four alternative themes define. Under Default,
`RedPanel` shows `Not defined` without requesting a missing color from Core.
The other swatches and their hexadecimal codes update live when the theme
changes. An eight-digit code includes transparency, shown blended over the
theme's PanelFooter background. Hover for the full name and value when a long
label is shortened. The list is a build-verified catalog of these five themes,
not an enumerator of private Core state or arbitrary future custom-theme keys.

Swatches are non-interactive references. They do not select a color, fire an
action, change settings, or assign a new unavailable-state style. Both palettes
reflow on resize and use the panel's normal outer scrolling. The full Core list
starts collapsed; expanding it is remembered across demo rebuilds during the
current session. Colors/values use Core's normal theme reload, with no per-frame
palette polling or new global patches.

### Theme-page readability and remaining review

The state-card design intentionally separates selection from setting state:

- Selected uses a full theme-accent background and inverse text.
- Enabled, Disabled, Unavailable, Warning, and Danger retain the normal theme
  background, with a colored side stripe and an explicit state label.
- Disabled means a setting is off and may still be clicked to enable it;
  Unavailable prevents interaction. Disabled and Danger both use red markers,
  so the text label matters.

The stripe remains 4 pixels wide and the state label remains 12-point text.
Readability should be assessed separately from successful palette application.
The prior requested experiment changed three strip colors and the demo slider
fill geometry without widening markers or changing typography. The palette
revision leaves those controls intact while supporting the next color decision.

A separate demo-only slider presentation issue was identified during screenshot
review: its fill appeared as a tall block without a clearly distinct thumb. The
initial demo created its fill directly under the full-height slider root. Inspection of
the installed Unity `Slider.UpdateVisuals` confirms that it drives both fill
anchor axes, replacing the demo's thin vertical anchors. The refinement puts the
fill inside a dedicated container anchored from 25% to 75% of the slider height,
keeping it centered and exactly half-height when the value or panel size changes.
The handle remains full-height and 16 pixels wide, drawn above the fill.
`FrameworkTheme.ApplySlider` only applies colors and
does not construct or resize those parts. This finding does not invalidate the
accepted consumer tabs or Controls/Layout/Sections/Scroll results. The geometry
repair is limited to AUMF's demonstration slider, not consumer-owned sliders.

The development refinements above were tested while the version remained 1.3.0.
The accepted UI implementation is retained in 1.4.0; promotion changes version
metadata/catalog wording and disables the demonstration UI, not the validated
theme behavior. No fresh Player.log review or additional in-game session was
performed during release preparation; user acceptance and build/package evidence
are recorded separately.

## Retained regression checklist - developer use

The user has accepted the current update. For future regression testing, verify
the **Unavailable button background is dark grey
`#555555` across all five themes**, matches the Unavailable strip, and remains
non-interactive. Normal/hover/pressed/selected appearances and text should retain
the previous theme behavior. The palette stays available below the existing samples.

1. In a development build, enable the test harness using USAGE.md, rebuild,
   fully restart Stardeus and load a game. Keep it disabled in normal releases.
2. Open the AUMF copy/settings demonstration widget and its Theme tab. The
   harness is enabled only for that development build by `EnableDemonstrationUi = true`.
3. Change the game's Theme between Default, Elysium, Dark, Grey and Neon Nights.
   Check open panels, reopen them, and switch tabs. Foreground/background
   contrast and active-tab distinction should follow the selected theme.
4. Compare fixed pink/cyan candidates and the other swatches across themes.
   Check both the color appearance and the `Aa` text contrast. No swatch should
   change the actual Normal/Unavailable buttons, settings, or action count.
5. Expand All Core theme colors and switch themes with it open. Check that
   swatches and color codes update together, scroll to the last entry, and resize.
   `RedPanel` should say `Not defined` only in Default among the five stock themes.
6. Confirm Neutral is light grey, Unavailable dark grey, and Warning orange in
   every theme. Other states, marker widths, labels and click behavior should
   match the previous preview. Decide whether the distinction is easier to read.
7. Drag the slider to 0, 25, 50, 75 and 100, and resize the panel. Its centered
   fill should remain 50% of the slider height, with a distinct full-height
   handle. At zero there should be no filled width; at 100 it should reach the end.
8. On Theme, test all card states, Toggle in place, button hover/press/disabled
   states and slider dragging. Switching theme must not change the slider value,
   fire actions or enable the unavailable button/card. The Legacy custom sample
   must retain its green override. Recheck the other four AUMF demo pages for
   regressions without introducing consumer migrations into this test pass.
9. Review a fresh Player.log for theme-style failures, missing/destroyed objects,
   pooling/scale/input-focus regressions, and framework callback exceptions.

Consumer adoption and additional consumer testing remain separate owner tasks;
AUMF acceptance does not modify or validate consumer migrations. Later checks
include RTC tab/scroll behavior and preservation
of its legacy graph-node colors, plus CARAU's managed scrollbars and any explicitly
adopted custom theme helpers. Existing consumer acceptance above is preserved;
it does not imply acceptance of a future consumer migration.

## Static verification and rollback

Release compilation: 0 errors, 5 established game-managed-reference warnings.
The release's assembly/file/product versions are 1.4.0.0/1.4.0.0/1.4.0.
The metadata check preserves all 270 published public API entries and all 329
entries in the previous preview, verifies the
legacy tab method's IL is unchanged, resolves all 410 AUMF member references
across 12 installed consumer assemblies, checks required Core fallback style
IDs and confirms all five binding types participate in Core theme reload.
These are build/static checks; in-game acceptance comes from the user's report.

Installed 1.4.0 DLL SHA-256 (matches the staged release build):
`1A1664605F4BCBB69BAA588E81D1868C5815DBA70D1455B866549D74D51904BD`.
Promotion preserves 436 runtime method bodies after normalizing embedded version
text. The remaining changes are the feature-catalog description, the disabled
demonstration flag, and two retained demo captions; accepted theme control logic
is unchanged. The compiled readonly demonstration flag has its CLR false default
and no assignments. Both palette implementations remain bytecode-equivalent.

Twenty isolated compatibility/catalog assertions passed using the actual source
files with game logging/UI notifications stubbed out: 1.3.0 consumers retain
stable APIs with a warning, 1.4.0 declarations enable the new feature gate,
future/different-major requirements are blocked, and re-registration clears the
relevant warning. These are deterministic source-level tests outside the game,
not a fresh gameplay/log review. The auxiliary test restore encountered a local
NuGet permission restriction; it was bypassed with the installed compiler and
reference assemblies, without accessing packages or changing game state.

Accepted pre-promotion test DLL SHA-256 (historical 1.3.0 metadata):
`68203CF13632F29B218FEF22C885A3A44738F1AE2323D5D3D346DB71C126B0E6`.
Palette checks confirm all 66 color names across five stock definitions, 16
fixed candidates including the requested pink/cyan, safe handling of the one
theme-specific name, and native live-reload inheritance without frame polling.
In the palette revision, all 309 checked non-demo method bodies were identical
to its prior build. In the unavailable-button revision, the only changed
runtime method was `FrameworkThemedButton.ApplyTheme`; all other 439 method bodies
(including nested types) were unchanged from the pre-button-color build. The
compiled assignment targets only `ColorBlock.disabledColor`, with the literal
`#555555`, and does not write enabled/interactable state. All 20 combinations
of five stock themes and four supported button styles have neutral-white image
bases. Both palette types (10 method bodies), state cards, demo layout, and the
half-height slider are unchanged. No new public API or consumer usage requirement
was introduced.
During the initial preview, all 260 checked files across TACC, Auto Factory,
Auto Hauling, Super Cleaning, Intelligent Recycling, CARAU, and RTC matched
their pre-change hashes. This follow-up also edits only AUMF.

Repeat the read-only binary/style check with `.vscode/VerifyThemePreview.ps1`,
passing `-BaselineDll`, `-CandidateDll`, and the installed ILSpy `-CecilDll`.
Its current default is `-ExpectedVersion 1.4.0`; use `1.3.0` only for historical
test binaries. Only the canonical FrameworkVersion constant is exempted from
value equality during a version increment; API shape and other constants remain checked.
Run `.vscode/VerifyThemePalette.ps1` with `-PaletteOnly` and the same arguments
against the accepted pre-promotion backup below to verify catalog coverage
and unchanged palette implementations. Omit `-PaletteOnly` only when reproducing
the original palette revision's broader non-demo scope check against its own baseline.

The accepted pre-promotion package (47 files, all backup hashes verified) is
retained outside the game Mods folder:
`C:\Users\jleas\AppData\Local\Temp\aumf-release-1.4.0-20260905-135052\baseline`.
The 1.4.0 build and build log are in that release folder's `build` directory
and `release-build.log`, respectively. Workshop ID remains `3776751765`.

The complete pre-button-color preview (47 files, all backup hashes verified) is
retained outside the game Mods folder:
`C:\Users\jleas\AppData\Local\Temp\aumf-unavailable-button-20260905-133604\baseline`.
Pre-button-color DLL SHA-256:
`84D64D2E7DAAC45FBB3E006626AEC8B698E28AF065E5A22F3932CBABD4D922DC`.
The new build and build log are in that revision folder's `build` directory
and `theme-build.log`, respectively.

The complete pre-palette preview (45 files, all backup hashes verified) is retained
outside the game Mods folder:
`C:\Users\jleas\AppData\Local\Temp\aumf-theme-palette-20260905-131526\baseline`.
Pre-palette DLL SHA-256:
`88843940046F4DED95052545A3E383E0E7ED781CAAD9C3A8E7D13EAF3EAE3360`.
The palette build and build log are in that revision folder's `build` directory
and `theme-build.log`, respectively.

The earlier pre-strip/fill preview (45 files, all backup hashes verified) is retained
outside the game Mods folder:
`C:\Users\jleas\AppData\Local\Temp\aumf-theme-refinement-20260905-120658\baseline`.
Pre-strip/fill preview DLL SHA-256:
`00243DEF8C329586EBAD7F98E3D90ED4A90F5F0794EC3D610DC61F24B45F9D0F`.
The refined build and build log are in that revision folder's `build` directory
and `theme-build.log`, respectively.

The original pre-theme AUMF package remains backed up outside the game Mods folder:
`C:\Users\jleas\AppData\Local\Temp\aumf-theme-testing-20260905-110658\baseline`.
Baseline DLL SHA-256:
`FDA1CA012FE37737E681FECFAA18A4700D32D216E213D23EF4B3810CDAF7432A`.
The initial preview build is in the sibling `build` directory. Keep the backups
until acceptance. Roll back the DLL with the game stopped; restoring the whole
source package also requires removing only the explicitly added preview files.

Release promotion completed the requested version/catalog/documentation update
and disabled the test UI. The user's subsequent validation closes out the
installed 1.4.0 update; no AUMF implementation or acceptance work remains for
this release. This confirmation is separate from the build/package checks
recorded above. Workshop upload is a separate user action. Consumer
migrations, their declarations and their gameplay acceptance remain with their
respective owners; the read-only audit above identifies relevant targets.
