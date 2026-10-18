# Reconnect Teleporter Connections

Reconnect Teleporter Connections gives every constructed Teleporter a readable identity, remembers its directed connection, and restores that connection when vanilla relocation or damage reconstruction would otherwise lose it.

## Behavior

- Every constructed Teleporter with no label receives the next unused `Teleporter N` label.
- Any non-empty label entered by the player is retained exactly and becomes the tracked label.
- Relocation uses Core's exact old-ID/new-ID event, updates the tracked position, and transfers every incoming and outgoing directed connection.
- Accidental destruction leaves a pending record containing the old label, position, and connection graph.
- A replacement completed at the tracked position inherits the old label when it is blank and restores applicable connections.
- A newly built Teleporter elsewhere can become a replacement when the player gives it the unique label of a missing Teleporter.
- Player-ordered deconstruction removes the Teleporter record and clears graph edges that refer to it.
- Restoration never overwrites a valid current outbound destination.

## Teleporter tab

The native Teleporter tab is enriched rather than replaced with an unrelated workflow. It displays labels and coordinates together, keeps the current destination and Unlink action visible, and always lists every other currently eligible destination with a Link button. Choosing a different destination uses Core's `TeleporterComp.SetOutbound` path, which replaces the prior outbound link.

The footer opens a resizable connection map. The map displays every tracked constructed or rebuilding Teleporter as a node and draws each live or remembered outbound connection as a directional arrow. Red cards have no outbound destination. Yellow cards and arrows point to constructed destinations. Cyan cards and arrows point to Teleporters waiting for reconstruction. Reciprocal connections between constructed Teleporters use two green arrows; while one endpoint is rebuilding, only the direction toward that missing endpoint turns cyan.

The Recovery view identifies every missing Teleporter, explains how a replacement will be matched, and lists both its remembered outbound destination and every incoming Teleporter waiting on it. Recovery-related events are repeated there for context. The History view lists all retained label, connection, relocation, removal, and recovery events as readable, time-stamped entries instead of only showing a count. Tracking and event history are stored in the save.

Recovery and History event cards follow the active Stardeus theme through AUMF's theme-aware custom UI bindings. Their layout, event ordering, and saved content are unchanged.

## Requirements

- Stardeus `0.15.0`
- Auto Utilities Mod Framework `1.4.0`

The AUMF DLL remains external and is not bundled in this mod.

## Release history

### 1.1.2

- Rebuilt against the current Stardeus 0.15.0 API after `EntityUtils.SelectAndFocus(Tile)` was replaced by `EntityUtils.SelectAndFocus(Entity)`.
- Restored destination-row and connection-map node focus actions without changing Teleporter linking, recovery behavior, or save data.

### 1.1.1

- Adopted AUMF 1.4.0's opt-in theme-aware custom UI bindings.
- Recovery and History event-card backgrounds, headings, and details now follow the active Stardeus theme, including live theme reloads.
- Preserved event layout, ordering, content, scrolling, recovery behavior, and save data.

### 1.1.0

- Added color-coded Teleporter cards and outbound arrows to the connection map.
- Red indicates no tracked outbound destination, yellow points to a constructed destination, cyan points to a Teleporter waiting for reconstruction, and green identifies a reciprocal direction whose target is constructed.
- Added pending recovery records to the map so remembered connections remain visible while a Teleporter is missing.
- Two-way constructed pairs render both directions in green; when one endpoint is missing, the direction toward it is cyan while the remembered return direction remains green.
- Added an in-panel color legend and rebuilding labels without changing recovery behavior or the save schema.

### 1.0.2

- Made the Recovery and History scroll boundaries grow and shrink with the resizable panel instead of remaining fixed at 520 UI units.
- Uses AUMF 1.3.0's managed flexible-height content allocation, retaining a compact minimum/preferred viewport while allowing both scrolls to consume resized space.
- Removed the consumer-specific panel-edge height fitter after the equivalent correction moved into AUMF's pooled-safe `FrameworkPanel` lifecycle.

### 1.0.1

- Fixed the Recovery and History scroll areas collapsing to zero height, which left their populated event collections invisible.
- Added detailed pending-recovery cards with device identity, replacement criteria, remembered outbound destination, and every waiting inbound source.
- Added readable, newest-first event cards with category and in-game time to both Recovery and History where applicable.
- Added structured, ordered event persistence while retaining and displaying events already stored by 1.0.0 saves.
- Clarified destruction, deconstruction, relinking, restoration, and manual-forget event messages.

### 1.0.0

- Initial release.
- Added unique default Teleporter labels while preserving player labels.
- Added save-specific label, position, and directed-connection tracking.
- Added exact relocation transfer and damage-rebuild recovery.
- Added player-deconstruction forget semantics.
- Enriched the native Teleporter destination UI.
- Added the graphical connection map, pending recovery view, and history.
