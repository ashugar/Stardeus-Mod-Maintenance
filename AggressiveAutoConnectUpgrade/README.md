# Aggressive Auto Connect Upgrade

Aggressive Auto Connect Upgrade periodically asks Stardeus's current Auto Connect planner whether useful electrical reconnection work is available. When it is, AACU invokes the vanilla **Try Auto Connect** operation. This restores connectable equipment after construction, rebuilding, or combat damage without repeatedly pulsing for isolated devices that Core cannot currently connect.

## Requirements

- Stardeus 0.15.0-rc322 or later
- Auto Utilities Mod Framework feature level 1.4.0
- The vanilla Auto Connect Upgrade installed in a Ship Computer

## Features

- Configurable automatic reconnection interval from 0.25 to 3600 seconds
- Persistent top-bar status widget provided by Auto Utilities Mod Framework
- Framework-based settings panel
- Persistent keyboard shortcut for enabling or disabling automatic pulses
- Core-planned connection checks that avoid pointless pulses when no legal connection exists
- Optional Complete Grid Reconnect for recovering devices stranded behind stale electrical-grid connections and safely adding isolated connectors when nearby grid connectors are full
- Two-pulse confirmation before a complete electrical teardown
- Protection against overlapping complete-reconnect operations
- One complete reconnect per recovery incident until the condition clears
- Defers complete electrical teardown while entering or traveling through hyperspace, including warp-drive jumps
- Uses Core's bulk grid-disconnect routine before the vanilla reconnection pass
- Optional, default-off recovery for devices switched off by a colonist during the Lights Out mental breakdown

AACU uses Core's current Auto Connect planner for pulse eligibility. Core therefore owns nearest-connector selection, Ship Computer limits, switched-off Switch boundaries, and the final connection operation.

Complete Grid Reconnect is intentionally forceful. It disconnects and rebuilds the electrical network when its recovery condition is met and may override Switch-controlled subgrids.

Complete Grid Reconnect requires the same legal recovery candidate to remain across two automatic pulses. After one full rebuild, it will not run again until the recovery condition clears. This gives normal construction and connection restoration time to settle without repeated grid-wide operations.

Complete Grid Reconnect can also recover a constructed connector with no links when Core cannot attach it because every compatible Ship-Computer-reachable connector in range is full. This narrow rebalance path requires an existing device link that can move to the isolated connector and enough capacity for the connector's grid uplink, the moved device, and at least one additional connection. It preserves switched-off Switch boundaries and does not escalate while an ordinary free connector is available.

To avoid interrupting powered engines, Complete Grid Reconnect is deferred during hyperspace entry and travel. Normal reconnection pulses continue, and a still-needed complete reconnect can run after travel ends.

Lights Out recovery is a separate, disabled-by-default option. It switches a device back on only after Core's Lights Out mental breakdown turns that device off. It operates independently of automatic connection pulses and leaves player-controlled and other shutdowns untouched.

The mod declares AUMF 1.4.0 as its tested feature level. It continues to use
AUMF's stable widget, panel, and centralized shortcut APIs. AUMF's 1.4.0 API
documentation identifies AACU as not calling the changed tab, scroll, or
collapsible helpers. AACU also does not require the new opt-in Theme-Aware UI
helpers, so its existing framework-managed controls need no theme migration.

## Release history

### 1.2.2

- Added a narrow Complete Grid Reconnect path for constructed, zero-link electrical connectors that Core cannot attach because every compatible Ship-Computer-reachable connector in range is full.
- Requires an existing device link that can move to the isolated connector and enough capacity to retain a net connection gain after the grid uplink and moved device.
- Preserves Core's switched-off Switch boundaries, ordinary Auto Connect priority, two-pulse confirmation, one-reconnect-per-incident latch, and hyperspace/warp safeguards.
- Runtime-validated that qualifying isolated connectors were successfully pulled into the powered grid.

### 1.2.1

- Updated AACU for Stardeus 0.15.0-rc322's rewritten electrical Auto Connect system.
- Replaced the removed per-node candidate API with Core's `AutoConnectPlanner`, preserving nearest-connector selection, Ship Computer limits, and switched-off Switch boundaries.
- Updated Complete Grid Reconnect to use Core's new one-frame `TryAutoConnectAll` operation and removed the obsolete follow-up completion passes.
- Updated the guarded legacy teardown fallback for Core's component-based disconnect API.
- Runtime-validated the affected save with no repeated popup, missing-method exception, listener failure, or focused AACU error in the fresh log.

### 1.2.0

- Added the optional **Restore colonist-disabled devices** setting, disabled by default.
- Restores only devices switched off by Core's Lights Out mental breakdown while leaving player-controlled and other shutdowns untouched.
- Runs independently of automatic connection pulses and the Auto Connect Upgrade requirement.
- Runtime-validated multiple device restorations with manual shutdown behavior preserved and no AACU patch errors.

### 1.1.13

- Replaced Complete Grid Reconnect's connection-by-connection teardown with Core's bulk grid-disconnect routine, removing AACU's large-grid teardown bottleneck.
- Retained the previous teardown path as a guarded fallback for inconsistent electrical state.
- Added phase timing diagnostics for bulk teardown, Core auto-connect, completion passes, and total reconnect duration.
- Runtime-validated nine full reconnects with no fallback use or AACU errors while preserving the completion pass, confirmation latch, and hyperspace protection.

### 1.1.12

- Validated AACU's widget, panel, and centralized shortcut integration against Auto Utilities Mod Framework 1.4.0 and updated the compatibility handshake.
- Confirmed that AACU does not use the changed tab, scroll, collapsible, or new opt-in Theme-Aware UI helpers, so no theme migration or feature gate is required.
- Preserved all automatic reconnection, Complete Grid Reconnect, and hyperspace-safety behavior.

### 1.1.11

- Required the same legal complete-reconnect candidate to persist across two automatic pulses, allowing Core's connection restoration and normal Auto Connect state to settle after construction or relocation.
- Prevented repeated full-grid teardown until the previous recovery condition clears.
- Added diagnostics identifying the confirmed stranded device and reachable socket while preserving normal pulses and hyperspace protection.

### 1.1.10

- Deferred Complete Grid Reconnect while the ship is in or preparing to enter hyperspace, preventing transient engine-power loss from aborting FTL and warp-drive travel.
- Preserved normal vanilla auto-connect pulses during travel and resumed any still-needed complete reconnect on a later pulse after travel ended.

### 1.1.9

- Reduced repeated electrical-node scans during automatic-pulse eligibility by capturing one reusable active-node snapshot per pulse.
- Reused field-owned list and dictionary buffers for normal and Complete Grid Reconnect eligibility while preserving reconnect behavior and player-selected intervals.

### 1.1.8

- Updated AACU's compatibility handshake and documented requirement to Auto Utilities Mod Framework 1.3.0.
- Retained the existing widget, panel, and centralized shortcut behavior without adopting the optional Collapsible Sections feature.

### 1.1.7

- Migrated persistent shortcut capture, conflict detection, input suppression, dispatch, and cleanup to AUMF 1.2.0's centralized guarded-shortcut API.
- Removed AACU's duplicate shortcut polling component while preserving its existing preference key and settings-panel behavior.

### 1.1.6

- Replaced the clipped shortcut-status sentence with a concise visible state and a full explanatory tooltip.

### 1.1.5

- Rejects shortcut keys already assigned to configurable Stardeus actions, including player-rebound controls.
- Protects the fixed F1-F10 visual-overlay shortcuts and continues reserving F12 for Steam screenshots.
- Disables a stored shortcut if a later game rebinding creates a conflict, while keeping the assignment visible for replacement.

### 1.1.4

- Validated the existing widget, settings panel, input, tooltip, and cleanup integration against Auto Utilities Mod Framework 1.1.0.
- Updated the compatibility handshake declaration so AUMF reports AACU as tested at the current feature level while continuing to display AACU's compiled release version independently.

### 1.1.3

- Updated the tested compatibility level to Auto Utilities Mod Framework 1.0.0.
- Synchronized explicit assembly, file, and informational version metadata.

### 1.1.2

- Refactored the top-bar widget and settings panel to use Auto Utilities Mod Framework.
- Added an exact framework-version compatibility check and framework dependency.
- Removed the mod's former custom widget creation, positioning, retry, and tooltip infrastructure.

### 1.1.1

- Moved the Complete Grid Reconnect explanation into a tooltip.
- Prevented F12 from being assigned as the mod shortcut because Steam commonly reserves it for screenshots.

### 1.1.0

- Added the optional Complete Grid Reconnect recovery mode.
- Added safeguards against overlapping reconnect operations.

### 1.0.0

- Initial release with upgrade-gated reconnection pulses, configurable intervals, shortcut support, settings panel, and persistent status widget.
