# Local Log Mod

Local Log Mod writes diagnostic events to `LocalLogMod/Logs`. Its hooks observe Core results without changing them. It has no dependency on another mod and does not write to other mod folders. Mod failures are reported to `Player.log`.

Enable the mod in Stardeus and restart the game. Edit `Settings.json` before launch to turn each category on or off. The settings are read once per game process. Every `.log` file under this mod's `Logs` folder is cleared on the first mod initialization of that process. A new game or save loaded without restarting Stardeus continues the same session logs.

## Files

| File | Events |
| --- | --- |
| `Session.log` | Startup switches and installed hook counts. |
| `Research.log` | Initial progress state, discovery, unlock attempts and results, bookmarks, removals, and tree reloads. |
| `Manufacturing/<definition>_<entity ID>.log` | Crafter registration, orders, ingredient delivery, order state and progress bands, output, and related task changes. |
| `Power/<definition>_<entity ID>.log` | Node registration, connection attempts, switches, and changed grid, production, consumption, deficit, demand, and battery values. |
| `Tasks.log` | Initial colony goals, goal creation and registration, assignments, priority and state changes, and removal. |
| `Beings.log` | Restored beings with group, avatar visibility, and incapacitation state; actions, deaths, combat starts, and applied attack damage. |
| `CrewUI.log` | Crew widget enable/disable events, rebuild requests and results, group and avatar visibility, visibility toggle changes, group changes, and ship combat mode changes. |
| `UIEvents.log` | Task hover boosts, entity selection changes, explicit deselection calls, selected-entity removal, panel reload requests and results, and panel hides. Caller chains are included for destructive UI changes. |
| `Audio.log` | UI and world sound requests with sound asset names, positions, volume, and short caller chains. Routine hover, nanobot work, breakdown-work, and construction-complete sounds are omitted. |
| `ShipCombat.log` | Player combat starts and ends, weapon phases, shots, and impacts on the player ship. |
| `Inventory.log` | Core's global material and object production and consumption signals. These signals do not name the source building. |
| `Lifecycle.log` | State load, save start, and unload. |
| `StoryEvents.log` | Story-event calls, state changes, descriptions, hidden log entries, outcomes, seeds, factions, participants, and designated tile, being, object, space, or other targets. |
| `Fire.log` | Existing fires at load, ignition cause, spread source and target, damage context, local temperature and oxygen, extinguishing, and explosions. |
| `Timings.log` | Slow calls and 300-call windows for AI, crafting, electricity, beings, and object systems. |

Each entry has a local timestamp and game tick. The tick may be `0` during early initialization. Manufacturing and power files are separated by building instance, so similarly named buildings remain distinguishable. The logger buffers entries and flushes every ten game ticks, when a buffer reaches 8 KB, on save, and on unload. A crash can lose the final buffered entries.

`TimingWarningMs` in `Settings.json` sets the per-call warning threshold. The `investigate=true` field indicates a system that crossed the threshold in that window or averaged at least half of it. It is a prompt to compare a fresh run with Optimization Mod diagnostics before changing optimization settings or code. Local Log Mod's own overhead affects these measurements.

The categories cover the listed Core transitions and signals. They are not a trace of every internal function call. In particular, Manufacturing does not enumerate every inventory search or haul decision, Power records changed node values rather than every grid calculation, and Beings records action changes rather than every movement step. Add a focused observer when a specific incident needs a finer trace. The installed Core version may change private method names; a failed hook is reported in `Player.log`, while `Session.log` records the installed category counts.

`StoryEvents.log` records the central Core story lifecycle. `call` names the exact event implementation and `definition` names its configured event. When Core supplies a player-facing title and description, the logger records cleaned text; hidden or descriptionless events receive a readable description derived from the event name and outcome. Start and initialized entries show targets both before and after the event's `OnStart` call, while later state entries make ongoing and failed events traceable to completion.

`CrewUI.log` records Core's rendered result after each full crew widget rebuild. `result=group_icon_rendered_all_avatars_hidden` means Core preserved access to a group whose individual avatars are all hidden. `result=missing_all_hidden_at_10_or_less` identifies the former missing-button condition. Visibility change entries identify changes made through Core's crew group popout. If another mod changes a saved visibility flag directly, the next rebuild records the resulting flag but cannot identify the writer.

`UIEvents.log` and `Audio.log` are focused incident-tracing categories. For a selected panel that closes unexpectedly, compare `hover_boost`, `multi_select`, `deselect_*`, `entity_removed_selected`, `panel_reload_*`, and `panel_hidden` entries against nearby audio requests. Failed selected-panel checks are omitted; only a successful reload of the currently selected entity is recorded. The caller chain is diagnostic context and does not change Core behavior. Disable either category after testing if the additional detail is no longer needed.

Power values are compared at the same three-decimal precision used in the log. Smaller floating-point movement is omitted. Manufacturing omits calls that do not change the selected order and confirmed `Haul Ingredients` retry churn caused by missing materials. Ingredient delivery, actual output, order changes, and other task failures remain logged.

Core writes every failed surgery roll to `Player.log`, including routine wounds and crippling. Local Log Mod does not duplicate those messages. CARAU separately classifies and tracks outcomes for operations it owns; its ordinary recoverable surgery results should only be investigated when the operation ends in an unexpected state.

`Fire.log` records state transitions rather than every fire-damage tick. `cause=spread` includes the burning source entity when Core exposes it. Direct ignitions are classified as damage, device malfunction, temperature self-combustion, story event, explosion, fire transfer, command, or unknown, with a caller chain for verification. Temperatures are recorded at the affected entity when the transition occurs; this makes a brief heat spike distinguishable from a continuing nearby fire.
