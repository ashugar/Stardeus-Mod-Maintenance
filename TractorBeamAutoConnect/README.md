# Tractor Beam Auto Connect

Tractor Beam Auto Connect restores missing Tractor Beam target assignments
after construction, reconstruction, electrical-grid reconnection, replacement,
or another vanilla event clears a connection. Existing valid assignments are
never rebalanced or replaced.

## Requirements

- Stardeus 0.15.x
- Auto Utilities Mod Framework feature level 1.4.0

The mod uses AUMF's four-argument consumer compatibility handshake and declares
1.4.0 as its tested feature level. It will not initialize framework-dependent
features when the installed AUMF is incompatible or older than required.

## Behavior

- Considers only constructed Tractor Beams that currently have no target.
- Uses only powered, constructed Tractor Beam Targets that vanilla considers
  valid.
- Prioritizes targets with no assigned Tractor Beam.
- Reuses targets randomly only after every eligible target has an assignment.
- Reacts when a Tractor Beam Target finishes construction.
- Performs an initial validation shortly after loading and a lightweight
  ten-second fallback scan while the game is advancing.
- Uses Stardeus's native `SetTargetSpot` assignment path.
- Preserves player intent by never changing an existing valid assignment.

## Interface

The mod uses AUMF for its cooperative top-bar widget and managed resizable
control panel. The panel provides:

- An automation toggle
- The current unassigned Tractor Beam count
- The number of assignments made by the last scan
- A manual **Validate Connections** action
- A configurable, conflict-checked shortcut for opening or closing the panel

Manual validation follows the same conservative rules as automatic recovery;
it repairs missing connections and does not rebalance the network.

## Release history

### 1.2.2

- Validated the existing widget, managed panel rows, and centralized shortcut
  integration against Auto Utilities Mod Framework 1.4.0.
- Updated the permanent consumer handshake and documented requirement to AUMF
  feature level 1.4.0.
- Confirmed the mod's existing native and framework-managed controls retain
  Core theme behavior without requiring the new opt-in custom-theme helpers.
- Preserved all interface, scanning, target selection, settings, and
  non-rebalancing assignment behavior.
- Synchronized the manifest, project, explicit assembly attributes,
  documentation, compatibility constant, and compiled DLL metadata.

### 1.2.1

- Validated the existing widget, panel, and centralized shortcut integration
  against Auto Utilities Mod Framework 1.3.0.
- Updated the permanent consumer handshake and documented requirement to AUMF
  feature level 1.3.0.
- Deliberately did not adopt AUMF's optional collapsible-section controls.
- Preserved all interface, scanning, target selection, settings, and
  non-rebalancing assignment behavior.
- Synchronized the manifest, project, explicit assembly attributes,
  documentation, compatibility constant, and compiled DLL metadata.

### 1.2.0

- Adopted AUMF's centralized guarded keyboard-shortcut API.
- Added panel controls to assign, replace, clear, and inspect the shortcut used
  to open or close Tractor Beam Auto Connect.
- Delegated shortcut persistence, key capture, conflict detection, text-entry
  suppression, and once-per-press dispatch to AUMF.
- Updated the tested AUMF feature level and requirement to 1.2.0.
- Preserved all automatic and manual assignment behavior, including the rule
  that existing Tractor Beam assignments are never rebalanced.
- Synchronized the manifest, project, explicit assembly attributes,
  documentation, compatibility constant, and compiled DLL metadata.

### 1.1.2

- Validated the complete mod against Auto Utilities Mod Framework 1.1.0.
- Adopted AUMF's four-argument consumer compatibility handshake with the
  compiled Tractor Beam Auto Connect release version.
- Updated the tested AUMF feature level to 1.1.0 so the validated consumer no
  longer appears in AUMF's compatibility-warning interface.
- Preserved the complete Workshop description, conservative assignment rules,
  automatic and manual validation, settings, widget, and panel behavior.
- Synchronized the manifest, project, assembly, file, product, documentation,
  and source compatibility versions.

### 1.1.1

- Updated the tested AUMF compatibility level to 1.0.0.
- Synchronized the manifest, project, assembly, file, product, and documentation
  versions.
- Preserved the complete scanning, assignment, settings, target-distribution,
  and interface behavior through the framework update.

### 1.1.0

- Converted the cooperative top-bar widget and control panel to Auto Utilities
  Mod Framework.
- Declared AUMF as a required dependency and added an exact compatibility check.
- Removed the superseded local widget reflection, cloning, positioning, retry,
  tooltip, and panel-lifecycle code.
- Kept the framework DLL external to the consumer package.

### 1.0.0

- Released automatic recovery for missing Tractor Beam assignments.
- Added unused-target-first distribution without rebalancing existing links.
- Added automatic, fallback, and manual validation paths.
- Added persistent automation controls and status reporting.
