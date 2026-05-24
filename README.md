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

- Repairs stale research references
- Fixes outdated Flammable properties
- Repairs Android body part definitions
- Adds Quantum Nexus Mind Transfer / Machine Learning support
- Fixes Molecular Assembler validation issues
- Removes invalid/orphaned mod config files
- Runs validation checks against installed Workshop mods

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

This script modifies Workshop mod files directly. It was written against the current Workshop versions available at the time of release. Future mod updates may make some fixes unnecessary or incorrect.

Use at your own risk. Back up saves and modified files first.
