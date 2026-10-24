# Auto Utilities Mod Framework

Shared infrastructure for compatible Stardeus mods.

## 1.4.0 - validated popup compatibility repair (2026-10-24)

The user confirmed that widgets open their panels correctly again, including
CARAU, and accepted the fix. The reported panel-opening failure is closed.

The previous DLL referenced a five-parameter `UIUtils.AfterShowPopup` method
that is absent from the installed Stardeus `0.15.0-rc344.3226` Core. AUMF now
uses Core's six-parameter call with `keepOffMouse: false`, allowing explicitly
opened panels to move beneath the cursor when native screen-edge correction
is needed. Initial centering, remembered geometry, and pooled cleanup are
unchanged. See [repair PR #48](https://github.com/ashugar/Stardeus-Mod-Maintenance/pull/48).

This is internal compatibility maintenance, not a new feature: AUMF remains
`1.4.0` (assembly/file `1.4.0.0`). No consumer code, dependency declaration,
public API, or test-UI setting change is required.

The repair passed its installed-Core reference check, unchanged-public-API
checks, compiled-reference checks for 16 consumers, and all 15 isolated
pooled-input tests. The popup regression check also rejects the old DLL.
The accepted DLL SHA-256 is
`23A23397D8E23B714768E51645CC59261B7F08CE1D14B5CA9CD6C79BD67A68BD`.

Runtime acceptance covers restored widget/panel opening, including CARAU; it
does not imply an exhaustive retest of every consumer control or independently
close the separate pooled-input test record below. Workshop publication
remains a separate user action.

## 1.4.0 maintenance test - pooled input callbacks (2026-09-11)

`PanelContentContext.AddInput` now removes its own end-edit listener when the
content lifetime ends, releases captured consumer state, and ignores events
from disposed content. Reusing a native input no longer accumulates AUMF commit
callbacks from earlier rows. Core and consumer-owned listeners are preserved.

This is an internal repair with no public API, consumer declaration, version,
or test-UI setting change. The earlier focus repair and accepted theme behavior
remain unchanged. Build and isolated regression checks pass; fresh in-game
acceptance of this repair is pending. Restart Stardeus before testing.
See [INPUT_LIFECYCLE_TESTING.md](INPUT_LIFECYCLE_TESTING.md) for the evidence,
read-only consumer audit, backup, and test checklist. No Workshop upload was made.

## 1.4.0 - validated theme-aware UI

The user accepted AUMF's theme update on 2026-09-05, confirming readability
across Default, Dark, Elysium, Grey and Neon Nights, including the previously
difficult Normal/Unavailable distinction. Manifest/API/product version is
`1.4.0`; project/assembly/file version is `1.4.0.0`.

After installation of the promoted 1.4.0 build with the test UI disabled, the
user confirmed the update was successful and complete. AUMF 1.4.0 is the
validated maintenance baseline; consumer migrations and Workshop publication
remain separate.

- Existing framework tabs, vertical scrollbars and collapsible-header indicators
  follow the selected Stardeus theme and live theme reloads automatically.
- New `FrameworkTheme` bindings, `CreateThemedTabButton` and reusable state cards
  are the public `ThemeAwareUi` capability introduced in 1.4.0. Bindings preserve
  custom layout, typography, values and callbacks.
- Neutral strips use light grey `#D3D3D3`, Unavailable strips/buttons use dark grey
  `#555555`, and Warning strips use orange `#FFA500`. Other button interaction
  colors and text retain their theme styling; native Core widgets are not patched.
- The test UI is disabled. Its five-tab panel, half-height demonstration slider
  fill, 16 fixed swatches and 66-entry Core palette remain in the source for
  future development testing.

Earlier validation covered TACC, Auto Hauling, Super Cleaning, Auto Factory,
Intelligent Recycling's tabs, and AUMF Controls/Layout/Sections/Scroll. CARAU's
custom surfaces and Intelligent Recycling's custom card titles still require
consumer-owned adoption. The legacy `CreateTabButton` retains its color-override
behavior, protecting RTC graph nodes. No consumers were modified.

Older same-major consumers may continue using stable helpers, including their
automatic theme improvements, with the existing compatibility warning. Adopt
new opt-in APIs only after declaring/testing 1.4.0; feature gates consider both
the installed version and the consumer declaration. Do not silently relabel
untested consumers. See [USAGE.md](USAGE.md) and [THEME_TESTING.md](THEME_TESTING.md)
for usage, migration audit, validation and release checks. Workshop publication
remains a separate user action; no upload was performed.

## Current capability

Version `1.4.0` provides cooperative top-bar widgets, centralized guarded
keyboard shortcuts, and pooled-safe collapsible section headers. It
creates widgets beside the electricity display, respects other active widgets,
supports dynamic icon/tint/tooltip providers, invokes a consumer callback on
click, and owns creation retry and cleanup behavior.

It also provides:

- Managed native popup lifecycle and content rebuilding
- Freely resizable panels with configurable bounds
- Optional in-session panel position and size restoration
- Native header, information, toggle, button, and input builders
- Native collapsible headers for ordinary panel rows and nested scroll content
- Reusable tab rows and tab buttons
- Theme-aware custom images/text/buttons/sliders/scrollbars and semantic state cards
- Responsive fixed-cell flow layout
- Vertical scrolling with position restoration
- Resized-height allocation for flexible managed panel regions
- Scale-preserving cleanup for native pooled widgets inside custom UI roots
- Permanent-tooltip attachment
- Enter/deselect input commit and pointer-up slider commit components
- Recursive native-child lookup
- A stable consumer compatibility handshake
- One consolidated, reopenable compatibility warning for affected mods
- Graceful same-major fallback to stable framework features
- Version-gated access to features introduced in newer AUMF releases
- Persistent, user-assignable keyboard shortcuts with capture and conflict guards
- A canonical feature/version catalog used by code, documentation, and alerts
- Installed consumer release-version reporting in compatibility details
- Direct access to the AUMF Steam Workshop page from the warning panel

The framework retains a demonstration widget and five-tab UI test panel for
development testing without changing any existing consumer mod. It is normally
disabled in Workshop builds through `EnableDemonstrationUi` in `TopBarWidgetSys`;
the flag is `false` in 1.4.0, as explicitly requested after acceptance. The
palette is retained and becomes available when a developer re-enables the harness.

See `USAGE.md` for complete consumer examples, lifecycle guidance, and detailed
test-harness instructions. The public source also contains XML documentation
and short examples suitable for editor tooltips.

## Validated maintenance corrections

The following corrections were accepted during 1.3.0 and are retained unchanged
in 1.4.0; their historical version references are not the current release number.

AUMF `1.3.0` includes a validated cleanup correction for native pooled widgets
nested beneath framework-owned custom UI roots, especially managed vertical
scroll content. Cleanup now preserves world transforms both when detaching the
custom root and when parking inactive native widgets beneath the permanent pool
container.

Before this correction, a non-unit Stardeus UI scale could be lost while a
widget was parked. At UI scale `0.8`, the pool could preserve world scale during
the next spawn and produce the reciprocal local scale `1.25`. Core then logged
and repaired the affected `WContent2Header`, `WContent2Text2`, and
`WContent2Input` controls. Repeated scrolling, refresh, collapse/expand,
filtering, and panel reopen testing at UI scale `0.8` now completes without
those scale-repair messages or a visible/behavioral UI regression.

This is corrective lifecycle behavior, not a new public feature. The framework
and consumer compatibility level therefore remained `1.3.0` at that time; consumers should
not add their own scale workaround or update their tested AUMF version solely
for this correction.

AUMF `1.3.0` also makes each framework-managed content block at least as tall
as the native popup's visible viewport. When the panel is enlarged, Unity can
therefore distribute the added height among managed scrolls or other children
with positive `flexibleHeight`, while fixed rows retain their preferred sizes.
Compact panels still use the native outer scroll when their preferred content
is taller than the viewport. This internal correction keeps the existing
`AddVerticalScroll(...)` API and consumer compatibility level unchanged.

While a managed content block is active, AUMF disables the native pooled
layout's forced vertical child expansion. This ensures children with
`flexibleHeight = 0` remain fixed and only positively flexible children receive
surplus resized height. The framework restores the layout's original expansion
setting before returning the content block to Stardeus's global pool, so later
framework, mod, and Vanilla popups do not inherit the override.

Runtime validation covered Auto Factory and Auto Hauling managed scrolls,
CARAU panels with mixed fixed and flexible top-level regions, and fixed-only
panels in other AUMF consumers. Flexible regions continued to reach the resized
bottom edge, fixed rows retained their declared heights, and repeated panel
reuse produced no scale, missing/destroyed-reference, or layout-rebuild
diagnostics in the fresh log.

AUMF `1.3.0` also releases Core's text-input focus before returning a
framework panel's pooled contents. This closes a refresh race in which a native
input callback could request `FrameworkPanel.Refresh()`, the field could be
pooled on the deferred rebuild, and Core could briefly retain that now-inactive
field as its global text-focus owner. The correction is internal lifecycle
maintenance: consumers may continue refreshing directly from input callbacks
without adding focus cleanup or changing their AUMF compatibility level.

Runtime validation exercised repeated native input interactions and panel
refreshes through Auto Factory's Production Goals UI. The fresh `Player.log`
loaded AUMF `1.3.0` and recorded Core's normal `WInput` interaction messages,
but contained zero `TextInputFocus not ready but field game object is
inactive!` messages and no relevant text-input or framework exceptions.

## Internal maintenance optimizations

AUMF `1.3.0` caches shortcut and top-bar registration snapshots and rebuilds
them only when a handle is registered or disposed. Shortcut dispatch keeps one
stable frame-local snapshot, preserving safe callbacks that mutate the registry
without allocating a new array or running a LINQ capture search every rendered
frame.

The top-bar service retains its Core UI-rebuild recovery check on every tick or
frame, but the steady-state liveness path is allocation-free. Widget source
discovery now runs only when a widget must be created or recovered, and manual
reflow runs only when registration order, parentage, sibling order, visibility,
requested size, or the electricity widget's geometry requires it. Explicit
consumer `Refresh()` calls still update icons, tints, badges, and tooltips
immediately. These are internal maintenance changes; the public API and consumer
compatibility level remained `1.3.0` for those optimizations, which are retained in 1.4.0.

Runtime validation confirmed that the optimized build produced no visible or
functional change in the participating consumer UI. The post-install log loaded
AUMF successfully and contained no widget-refresh or shortcut-callback failures,
duplicate registrations, missing/destroyed-reference errors, scale repairs, or
layout-rebuild diagnostics. This confirms behavioral compatibility and clean
runtime operation; the expected reduction in steady-state managed allocations is
established by the implementation and was not separately quantified with an
in-game profiler capture.

## Consumer version safety

Every dependent mod must:

1. Add `AutoUtilitiesModFramework` to `ModInfo.json` dependencies.
2. Reference `AutoUtilitiesModFramework.dll` at build time without copying it
   into the consumer's `Libraries` folder.
3. Hardcode the exact framework version against which it was built and tested.
4. Call `AutoUtilitiesApi.TryRegisterConsumer` before using AUMF.
5. Stop its framework-dependent initialization only when that method returns
   `false`.
6. Guard optional newer sections with `AutoUtilitiesApi.IsFeatureAvailable`.

An exact match means the consumer has been tested against the installed AUMF.
An older consumer from the same AUMF major version receives a warning but may
continue using the stable API. Features introduced after the version declared
by that consumer remain unavailable. A consumer requiring a newer AUMF, or a
consumer from a different AUMF major version, must not initialize its
framework-dependent functionality.

The version supplied to the handshake is the consumer's declared AUMF
compatibility level, not the consumer mod's own release version. AUMF does not
maintain or validate dependent-mod release versions.

## Framework release numbering

AUMF uses `MajorFeature.MinorFeature.NotUsed`:

- Bug fixes do not increment the framework version. Consumers therefore do not
  need to update their recognized AUMF version for corrective releases.
- Each newly exposed feature increments the minor-feature component and must be
  documented in the canonical feature catalog.
- After ten new features in a major line, or when a major/breaking capability
  is introduced, increment the major-feature component and reset minor to zero.
- The third component is reserved and remains zero.

This policy lets consumers remain on their last tested feature level when they
do not need newer helpers, while receiving compatible bug fixes without code or
metadata maintenance.
