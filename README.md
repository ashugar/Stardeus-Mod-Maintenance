# Stardeus Mod Maintenance

PowerShell maintenance and compatibility repair script for selected Stardeus Workshop mods.

## Project Status

Discontinued. I've moved to the point where it's much more stable and convenient to simply maintain updated mods.

### Available Versions

| Script | Target Version | Status |
|----------|----------|----------|
| v0.14 | Stardeus 0.14 | Archived |
| v0.15 | Stardeus 0.15 Private/Beta | Archived |

The v0.15 script is currently focused on restoring compatibility
for abandoned or outdated Workshop mods while Stardeus v0.15
continues development.

## Supported Mods

- Prosthetics & Androids Expanded (Mod: https://steamcommunity.com/sharedfiles/filedetails/?id=3746773189)
- Molecular Assembler Fixed (Mod: https://steamcommunity.com/sharedfiles/filedetails/?id=3748810380)
- MetalHusksRevisited (Mod: https://steamcommunity.com/sharedfiles/filedetails/?id=3749399478)
- Better Storage (Mod: https://steamcommunity.com/sharedfiles/filedetails/?id=3748324441)
- Chromanite Material (Mod: https://steamcommunity.com/sharedfiles/filedetails/?id=3748921886)
- Orbotron / More Orbotrons (Mod: https://steamcommunity.com/sharedfiles/filedetails/?id=3748954299)

## What It Does

Updates supported Workshop mods to remain functional and compatible with the current public release of Stardeus at the time this script was published.

Repairs may include compatibility updates, stale reference fixes, deprecated property removal, restoration of intended functionality, validation cleanup, and other maintenance required to keep supported mods operational.

See the CHANGELOG for a detailed list of fixes and modifications.

## Setup

Edit these paths near the top of the script:

```powershell
$coreRoot = "..."
$workshopRoot = "..."
```
Then enable only the mods you use:

```powershell
$Enable_AndroidsExpanded = $true
```

## Warning

This script modifies Workshop mod files directly.

It was written against the current Workshop versions available at the time of release. Future Stardeus updates or Workshop mod updates may make some repairs unnecessary, incomplete, or incompatible.

Back up saves and mod files before use.

Use at your own risk.
