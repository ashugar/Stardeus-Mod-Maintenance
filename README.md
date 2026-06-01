# Stardeus Mod Maintenance

PowerShell maintenance and compatibility repair script for selected Stardeus Workshop mods.

## Supported Mods

- Prosthetics & Androids Expanded
- Molecular Assembler Fixed
- MetalHusksRevisited
- IRONHUSK
- Fast IRONHUSK
- Chromanite Material

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
