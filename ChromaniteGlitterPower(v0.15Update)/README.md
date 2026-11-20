# Chromanite Power 2.1.0

Adds glitter solar panels in three sizes. Requires Core and Chromanite Material.

## In-place solar replacements

| Existing building | Replacement |
| --- | --- |
| Solar Panel (single) | Glitter Panel (single) |
| Solar Panel Array (four panels, 2x2) | Glitter Panel Array (four panels, 2x2) |
| Solar Panel Array (nine panels, 3x3) | Glitter Panel Array (nine panels, 3x3) |

1. Unlock the matching glitter panel through its existing research.
2. Select it in the Energy build menu and place it directly over the existing same-size solar panel.
3. Let workers deliver the target materials and complete the replacement work.

The original panel stays in place until completion. This uses the full target construction recipe and work amount, then applies normal salvage rules to the old panel. It does not charge only the difference in materials. Panel prices, research, power output and construction work are unchanged.

Each size has its own replacement pair. Matching reverse replacements are supported when the solar target is unlocked. Different sizes and unrelated buildings do not share these groups.

Core owns placement, hauling, construction, cancellation and pending-work save/load. Cancelling a pending replacement leaves the original panel and returns delivered target materials through the normal game process.

Completion creates a new building. Core attempts to reconnect compatible electrical links and copies supported settings; check connections afterward. Installed facility upgrades eject for recovery. Dust, damage and other original state are not all transferred.

## Release history

### 2.1.0

- Added native same-size solar/glitter replacement pairs for single, four-panel and nine-panel facilities.
- Kept existing research, recipes, work requirements, output, graphics and Workshop identity.

Existing compatibility and author attribution are retained in the mod description. This is a continuation of the original Chromanite Power mod: https://steamcommunity.com/sharedfiles/filedetails/?id=2946988185.
