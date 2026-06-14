# Repairs known stale/broken Stardeus Workshop mod references after reinstall.
#
# Target version:
# - Stardeus v0.15 private/beta compatibility testing.
#
# Current repair coverage:
#
# Better Storage
# - Repairs stale StorageCold research references.
# - Removes blocked full Core definition overrides.
#
# Orbotron
# - Removes deprecated Species.Type field.
# - Removes deprecated SpawnRequirements.Check field.
#
# MetalHusksRevisited
# - Repairs stale Planter research references.
#
# Molecular Assembler
# - Removes deprecated Craftable.Level fields.
# - Adds IgnoreEnergyOutput validation flag.
# - Adds IgnoreMarket validation flag.
#
# Chromanite
# - Removes deprecated MatType.NameKey fields.
# - Removes deprecated Craftable.Level fields.
# - Renames IgnoreStockMarket to IgnoreMarket.
# - Removes deprecated EnergyOutputComment fields.
# - Removes deprecated FavGroupId fields.
# - Removes deprecated ProductOf array fields.
# - Repairs IsTemperatureResistent -> IsTemperatureResistant typo.
# - Adds missing GroupId values to material definitions.
#
# Androids Expanded
# - Replaces deprecated Looks wrapper with direct SpeciesLooksDef format.
# - Removes full ObeyChip Core override blocked by v0.15.
# - Removes deprecated StasisWakeUpChance field.
# - Restores Gearss -> Gears typo fix.
# - Repairs MakeshiftJaw -> MakeShiftJaw casing mismatch.
# - Removes deprecated Species.Type field safely.
# - Removes deprecated NamePartsComment field.
# - Removes deprecated Comment fields.
# - Removes deprecated gender/attraction blocks.
# - Temporarily removes deprecated AdjustsSkills fields.
#
# TODO:
# - Investigate the v0.15 replacement for Androids AdjustsSkills body-part bonuses.
# - Investigate Androids BodyPartSlotViewOffsets warning.
# - Investigate Androids BodyPartSlotLines warning.
# - Investigate Androids market definition warnings:
#     Obj/Parts/Jaw/BionicsLimbs
#     Obj/Parts/Jaw/MakeshiftLimbs
# - Determine whether remaining Chromanite economic validation warnings
#   can be resolved or intentionally suppressed.
# - Convert intended ObeyChip prerequisite change to v0.15 JSON patch format if needed.
# - Runtime-test Android production, surgery, implants, and body-part rendering.
#
# Validation coverage:
# - Verifies known mods do not reference missing Research IDs.
# - Scans installed Workshop mods for Flammable keys not found in Core.
# - Separates known deprecated Flammable keys from unknown keys needing review.
#
# Notes:
# - Load-blocking v0.15 mod initialization errors currently resolved.
# - Remaining issues are primarily runtime validation warnings,
#   balance validation messages, or gameplay-path testing.
#
# Safe to run multiple times.

cls

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Core Stardeus mod data. Used as the reference source for validation.
$coreRoot = "D:\Program Files (x86)\Steam\steamapps\common\Stardeus\Stardeus_Data\StreamingAssets\Mods\Core"

# Steam Workshop root for Stardeus. Individual mod folders are keyed below.
$workshopRoot = "D:\Program Files (x86)\Steam\steamapps\workshop\content\1380910"

# Known Workshop mods targeted by this repair script.
$mods = @{
  MetalHusksRevisited = "$workshopRoot\3465483016" # MetalHusksRevisited
  Chromanite          = "$workshopRoot\2946987731" # Chromanite Material
  AndroidsExpanded    = "$workshopRoot\3415188497" # Prosthetics & Androids Expanded
  MolecularAssembler  = "$workshopRoot\3306928875" # Molecular Assembler Fixed
  BetterStorage       = "$workshopRoot\2878153161" # Better Storage
  Orbotron            = "$workshopRoot\3318634071" # Orbotron / More Orbotrons
}

# Update this to $true if you have the Mod listed. $false if you don't.
$Enable_BetterStorage = $true
$Enable_Orbotron = $true
$Enable_MetalHusksRevisited = $true
$Enable_Chromanite = $true
$Enable_AndroidsExpanded = $true
$Enable_MolecularAssembler = $true

# Flammable keys known to be stale/deprecated in older mods.
# These are separated from truly unknown keys during validation.
$knownDeprecatedFlammableKeys = @(
  "FireDamageMinHealthPercent"
  "MeltingTemperatureCelsius"
)

# =============================================================================
# Helper functions
# =============================================================================

# Prints a consistent visible section header.
function Write-Section {
  param(
    [Parameter(Mandatory)]
    [string]$Title
  )

  Write-Host ""
  Write-Host ("=" * 80) -ForegroundColor DarkGray
  Write-Host $Title -ForegroundColor Cyan
  Write-Host ("=" * 80) -ForegroundColor DarkGray
}

# Performs a regex-escaped string replacement in one file, with backup and rerun-safe output.
function Backup-And-Replace {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$Find,

    [Parameter(Mandatory)]
    [string]$Replace,

    [string]$BackupSuffix = ".bak"
  )

  if (!(Test-Path $Path)) {
    Write-Warning "Missing file: $Path"
    return
  }

  $raw = Get-Content $Path -Raw
  $fixed = $raw -replace [regex]::Escape($Find), $Replace

  if ($raw -eq $fixed) {
    Write-Host "No change needed: $Path" -ForegroundColor DarkYellow
    return
  }

  Copy-Item $Path "$Path$BackupSuffix" -Force
  Set-Content -Path $Path -Value $fixed -Encoding UTF8

  Write-Host "Patched: $Path" -ForegroundColor Green
  Write-Host "  $Find -> $Replace" -ForegroundColor Gray
}

# Removes any line containing one of the supplied search strings.
# Used for old config keys that should be deleted rather than renamed.
function Remove-LinesContainingText {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string[]]$Needles,

    [string]$BackupSuffix = ".bak.deprecated"
  )

  if (!(Test-Path $Path)) {
    Write-Warning "Missing file: $Path"
    return
  }

  $lines = @(Get-Content $Path)
  $filtered = @(
    foreach ($line in $lines) {
      $shouldRemove = $false

      foreach ($needle in $Needles) {
        if ($line -match [regex]::Escape($needle)) {
          $shouldRemove = $true
          break
        }
      }

      if (-not $shouldRemove) {
        $line
      }
    }
  )

  if ($lines.Count -eq $filtered.Count) {
    Write-Host "No deprecated lines found: $Path" -ForegroundColor DarkYellow
    return
  }

  Copy-Item $Path "$Path$BackupSuffix" -Force
  Set-Content -Path $Path -Value $filtered -Encoding UTF8

  Write-Host "Removed deprecated lines from: $Path" -ForegroundColor Green
  $Needles | ForEach-Object {
    Write-Host "  $_" -ForegroundColor Gray
  }
}

# Returns all JSON files for a mod, while handling missing mod folders cleanly.
function Get-ModJsonFiles {
  param(
    [Parameter(Mandatory)]
    [string]$ModRoot
  )

  if (!(Test-Path $ModRoot)) {
    Write-Warning "Mod folder not found: $ModRoot"
    return @()
  }

  return @(Get-ChildItem -Path $ModRoot -Recurse -Filter *.json -File)
}

# Applies the same replacement across a known list of relative paths inside a mod.
function Patch-RelativeFiles {
  param(
    [Parameter(Mandatory)]
    [string]$ModRoot,

    [Parameter(Mandatory)]
    [string[]]$RelativePaths,

    [Parameter(Mandatory)]
    [string]$Find,

    [Parameter(Mandatory)]
    [string]$Replace
  )

  if (!(Test-Path $ModRoot)) {
    Write-Warning "Mod folder not found: $ModRoot"
    return
  }

  foreach ($relativePath in $RelativePaths) {
    $path = Join-Path $ModRoot $relativePath

    Backup-And-Replace `
      -Path $path `
      -Find $Find `
      -Replace $Replace
  }
}

# Reads a mod display name from ModInfo.json, falling back to the folder name.
function Get-ModName {
  param(
    [Parameter(Mandatory)]
    [string]$ModRoot
  )

  $modInfoPath = Join-Path $ModRoot "ModInfo.json"

  if (!(Test-Path $modInfoPath)) {
    return Split-Path $ModRoot -Leaf
  }

  $raw = Get-Content $modInfoPath -Raw
  $nameMatch = [regex]::Match($raw, '"Name"\s*:\s*"([^"]+)"', "Singleline")

  if ($nameMatch.Success) {
    return $nameMatch.Groups[1].Value
  }

  return Split-Path $ModRoot -Leaf
}

# Extracts Flammable component property keys from JSON files under a root folder.
# Used to compare mod Flammable keys against Core-supported keys.
function Get-FlammableKeyUsageFromRoot {
  param(
    [Parameter(Mandatory)]
    [string]$Root
  )

  $results = New-Object System.Collections.ArrayList

  if (!(Test-Path $Root)) {
    return @($results)
  }

  Get-ChildItem -Path $Root -Recurse -Filter *.json -File |
    ForEach-Object {
      $filePath = $_.FullName
      $raw = Get-Content $filePath -Raw

      $blocks = [regex]::Matches(
        $raw,
        '"Component"\s*:\s*"Flammable"\s*,\s*"Properties"\s*:\s*\[(.*?)\]',
        "Singleline"
      )

      foreach ($block in $blocks) {
        $keyMatches = [regex]::Matches(
          $block.Groups[1].Value,
          '"Key"\s*:\s*"([^"]+)"',
          "Singleline"
        )

        foreach ($match in $keyMatches) {
          [void]$results.Add([PSCustomObject]@{
            Key = $match.Groups[1].Value
            File = $filePath
          })
        }
      }
  }

  return @($results)
}

# Writes a full file body, creating parent folders if needed.
# Used when a broken/missing mod file needs a complete known-good definition.
function Backup-And-WriteFile {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$Content,

    [string]$BackupSuffix = ".bak"
  )

  $folder = Split-Path $Path -Parent
  if (!(Test-Path $folder)) {
    New-Item -Path $folder -ItemType Directory -Force | Out-Null
  }

  if (Test-Path $Path) {
    $raw = Get-Content $Path -Raw

    if ($raw.Trim() -eq $Content.Trim()) {
      Write-Host "No change needed: $Path" -ForegroundColor DarkYellow
      return
    }

    Copy-Item $Path "$Path$BackupSuffix" -Force
  }

  Set-Content -Path $Path -Value $Content -Encoding UTF8
  Write-Host "Wrote: $Path" -ForegroundColor Green
}

# Ensures translation rows exist, inserting them after a known key when possible.
function Ensure-CsvLineAfterKey {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$AfterKey,

    [Parameter(Mandatory)]
    [string[]]$LinesToEnsure
  )

  if (!(Test-Path $Path)) {
    Write-Warning "Missing CSV file: $Path"
    return
  }

  $lines = @(Get-Content $Path)
  $existing = [System.Collections.Generic.HashSet[string]]::new()

  foreach ($line in $lines) {
    if ($line.Trim()) {
      $key = ($line -split ',', 2)[0]
      [void]$existing.Add($key)
    }
  }

  $missingLines = @(
    foreach ($line in $LinesToEnsure) {
      $key = ($line -split ',', 2)[0]
      if (-not $existing.Contains($key)) {
        $line
      }
    }
  )

  if ($missingLines.Count -eq 0) {
    Write-Host "No translation changes needed: $Path" -ForegroundColor DarkYellow
    return
  }

  $insertIndex = -1
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $key = ($lines[$i] -split ',', 2)[0]
    if ($key -eq $AfterKey) {
      $insertIndex = $i + 1
      break
    }
  }

  if ($insertIndex -lt 0) {
    Write-Warning "Could not find key '$AfterKey'. Appending translation lines to end of file."
    $insertIndex = $lines.Count
  }

  Copy-Item $Path "$Path.bak" -Force

  $newLines = @()
  if ($insertIndex -gt 0) {
    $newLines += $lines[0..($insertIndex - 1)]
  }

  $newLines += $missingLines

  if ($insertIndex -lt $lines.Count) {
    $newLines += $lines[$insertIndex..($lines.Count - 1)]
  }

  Set-Content -Path $Path -Value $newLines -Encoding UTF8

  Write-Host "Added translation lines to: $Path" -ForegroundColor Green
  $missingLines | ForEach-Object {
    Write-Host "  $_" -ForegroundColor Gray
  }
}

# Repairs repeated text caused by earlier non-idempotent replacements.
function Repair-RepeatedTextPattern {
  param(
    [Parameter(Mandatory)]
    [string]$ModRoot,

    [Parameter(Mandatory)]
    [string]$Pattern,

    [Parameter(Mandatory)]
    [string]$Replacement,

    [Parameter(Mandatory)]
    [string]$Label
  )

  $patchedCount = 0

  Get-ModJsonFiles -ModRoot $ModRoot |
    ForEach-Object {
      $path = $_.FullName
      $raw = Get-Content $path -Raw
      $fixed = $raw -replace $Pattern, $Replacement

      if ($raw -ne $fixed) {
        Copy-Item $path "$path.bak.fix" -Force
        Set-Content -Path $path -Value $fixed -Encoding UTF8
        Write-Host "Repaired: $path" -ForegroundColor Green
        $patchedCount++
      }
  }

  if ($patchedCount -eq 0) {
    Write-Host "No $Label repairs needed." -ForegroundColor DarkYellow
  }
}

# Renames a file when only casing differs. This requires a temp rename on Windows.
function Rename-FileExactCasing {
  param(
    [Parameter(Mandatory)]
    [string]$OldPath,

    [Parameter(Mandatory)]
    [string]$NewPath
  )

  $directory = Split-Path $NewPath -Parent
  $oldName = Split-Path $OldPath -Leaf
  $newName = Split-Path $NewPath -Leaf

  if (!(Test-Path $directory)) {
    Write-Warning "Directory not found: $directory"
    return
  }

  $actualItem = Get-ChildItem -Path $directory -File |
    Where-Object { $_.Name -ieq $oldName -or $_.Name -ieq $newName } |
    Select-Object -First 1

  if (!$actualItem) {
    Write-Host "No rename needed. File not found: $OldPath" -ForegroundColor DarkYellow
    return
  }

  if ($actualItem.Name -ceq $newName) {
    Write-Host "No rename needed: $($actualItem.FullName)" -ForegroundColor DarkYellow
    return
  }

  $tempName = "__temp_rename__$($actualItem.Extension)"
  $tempPath = Join-Path $directory $tempName

  Rename-Item -Path $actualItem.FullName -NewName $tempName
  Rename-Item -Path $tempPath -NewName $newName

  Write-Host "Renamed casing: $($actualItem.FullName) -> $NewPath" -ForegroundColor Green
}

# Normalizes a text pattern across files with case-insensitive matching.
# Uses case-sensitive comparison so casing-only changes are still written.
function Normalize-TextInFiles {
  param(
    [Parameter(Mandatory)]
    [string]$Root,

    [Parameter(Mandatory)]
    [string]$Pattern,

    [Parameter(Mandatory)]
    [string]$Replacement,

    [string[]]$Extensions = @(".json", ".csv"),

    [string]$BackupSuffix = ".bak.normalize"
  )

  $patchedCount = 0

  Get-ChildItem -Path $Root -Recurse -File |
    Where-Object { $Extensions -contains $_.Extension.ToLower() } |
    ForEach-Object {
      $path = $_.FullName
      $raw = Get-Content $path -Raw
      $fixed = $raw -ireplace [regex]::Escape($Pattern), $Replacement

      if ($raw -cne $fixed) {
        Copy-Item $path "$path$BackupSuffix" -Force
        Set-Content -Path $path -Value $fixed -Encoding UTF8

        Write-Host "Patched: $path" -ForegroundColor Green
        Write-Host "  $Pattern -> $Replacement" -ForegroundColor Gray
        $patchedCount++
      }
  }

  if ($patchedCount -eq 0) {
    Write-Host "No normalization changes needed for: $Pattern -> $Replacement" -ForegroundColor DarkYellow
  }
}

# Removes a file after backing it up. Used for invalid/orphan config files.
function Remove-FileIfExists {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [string]$BackupSuffix = ".bak.removed"
  )

  if (!(Test-Path $Path)) {
    Write-Host "No removal needed: $Path" -ForegroundColor DarkYellow
    return
  }

  Copy-Item $Path "$Path$BackupSuffix" -Force
  Remove-Item $Path -Force

  Write-Host "Removed: $Path" -ForegroundColor Green
}

# Removes a JSON property object from a Properties array by matching its Key value.
# Intended for one-line/multi-line blocks shaped like:
# {
#   "Key": "SomeKey",
#   "String": "SomeValue"
# }
function Remove-JsonPropertyBlockByKey {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string[]]$Keys,

    [string]$BackupSuffix = ".bak.removedproperty"
  )

  if (!(Test-Path $Path)) {
    Write-Warning "Missing file: $Path"
    return
  }

  $raw = Get-Content $Path -Raw
  $fixed = $raw

  foreach ($key in $Keys) {
    $pattern = '(?s),?\s*\{\s*"Key"\s*:\s*"' + [regex]::Escape($key) + '"\s*,\s*"[^"]+"\s*:\s*"[^"]*"\s*\}'
    $fixed = $fixed -replace $pattern, ''
  }

  # Clean up accidental trailing comma before a closing array.
  $fixed = $fixed -replace '(?s),\s*(\])', '$1'

  if ($raw -cne $fixed) {
    Copy-Item $Path "$Path$BackupSuffix" -Force
    Set-Content -Path $Path -Value $fixed -Encoding UTF8

    Write-Host "Removed obsolete property block(s) from: $Path" -ForegroundColor Green
    $Keys | ForEach-Object {
      Write-Host "  $_" -ForegroundColor Gray
    }
  }
  else {
    Write-Host "No obsolete property blocks found: $Path" -ForegroundColor DarkYellow
  }
}

function Ensure-JsonComponentBeforeComponent {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$ComponentToEnsure,

    [Parameter(Mandatory)]
    [string]$BeforeComponent,

    [string]$BackupSuffix = ".bak.component"
  )

  if (!(Test-Path $Path)) {
    Write-Warning "Missing file: $Path"
    return
  }

  $raw = Get-Content $Path -Raw

  if ($raw -match ('"Component"\s*:\s*"' + [regex]::Escape($ComponentToEnsure) + '"')) {
    Write-Host "No change needed: $Path" -ForegroundColor DarkYellow
    return
  }

  $componentBlock = @"
        { "Component" : "$ComponentToEnsure" },

"@

  $pattern = '(\s*\{\s*"Component"\s*:\s*"' + [regex]::Escape($BeforeComponent) + '")'
  $fixed = $raw -replace $pattern, ($componentBlock + '$1')

  if ($raw -cne $fixed) {
    Copy-Item $Path "$Path$BackupSuffix" -Force
    Set-Content -Path $Path -Value $fixed -Encoding UTF8

    Write-Host "Inserted $ComponentToEnsure before $BeforeComponent in: $Path" -ForegroundColor Green
  }
  else {
    Write-Warning "Could not find component $BeforeComponent in: $Path"
  }
}

# Performs a regex-escaped string removal in one file, with backup and rerun-safe output.
function Backup-And-RemoveText {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$Find,

    [string]$BackupSuffix = ".bak"
  )

  if (!(Test-Path $Path)) {
    Write-Warning "Missing file: $Path"
    return
  }

  $raw = Get-Content $Path -Raw
  $fixed = $raw -replace [regex]::Escape($Find), ""

  if ($raw -eq $fixed) {
    Write-Host "No change needed: $Path" -ForegroundColor DarkYellow
    return
  }

  Copy-Item $Path "$Path$BackupSuffix" -Force
  Set-Content -Path $Path -Value $fixed -Encoding UTF8

  Write-Host "Removed text from: $Path" -ForegroundColor Green
  Write-Host "  $Find" -ForegroundColor Gray
}

function Backup-And-RemoveRegex {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$Pattern,

    [string]$BackupSuffix = ".bak"
  )

  if (!(Test-Path $Path)) {
    Write-Warning "Missing file: $Path"
    return
  }

  $raw = Get-Content $Path -Raw
  $matches = [regex]::Matches($raw, $Pattern, "Multiline")

  if ($matches.Count -eq 0) {
    Write-Host "No change needed: $Path" -ForegroundColor DarkYellow
    return
  }

  $fixed = [regex]::Replace($raw, $Pattern, "", "Multiline")

  Copy-Item $Path "$Path$BackupSuffix" -Force
  Set-Content -Path $Path -Value $fixed -Encoding UTF8

  Write-Host "Removed regex match from: $Path" -ForegroundColor Green

  foreach ($match in $matches) {
    $removed = $match.Value.TrimEnd("`r", "`n")
    Write-Host "  Removed: $removed" -ForegroundColor Gray
  }
}

function Ensure-JsonTopLevelPropertyAfter {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$AfterProperty,

    [Parameter(Mandatory)]
    [string]$PropertyName,

    [Parameter(Mandatory)]
    [string]$PropertyValue,

    [string]$BackupSuffix = ".bak"
  )

  if (!(Test-Path $Path)) {
    Write-Warning "Missing file: $Path"
    return
  }

  $raw = Get-Content $Path -Raw

  if ($raw -match "`"$([regex]::Escape($PropertyName))`"\s*:") {
    Write-Host "No change needed: $Path" -ForegroundColor DarkYellow
    return
  }

  $pattern = "(`"$([regex]::Escape($AfterProperty))`"\s*:\s*`"[^`"]+`")"
  $replacement = "`$1,`r`n    `"$PropertyName`" : `"$PropertyValue`""
  $fixed = $raw -replace $pattern, $replacement

  if ($raw -eq $fixed) {
    Write-Warning "Could not add $PropertyName to: $Path"
    return
  }

  Copy-Item $Path "$Path$BackupSuffix" -Force
  Set-Content -Path $Path -Value $fixed -Encoding UTF8

  Write-Host "Patched: $Path" -ForegroundColor Green
  Write-Host "  Added $PropertyName : $PropertyValue" -ForegroundColor Gray
}

# =============================================================================
# Repair process
# =============================================================================

if($Enable_BetterStorage){
  Write-Section "Patch Better Storage stale StorageCold research prerequisite"

  Get-ModJsonFiles -ModRoot $mods.BetterStorage |
    ForEach-Object {
      Backup-And-Replace `
        -Path $_.FullName `
        -Find '"Research/Habitation/StorageCold"' `
        -Replace '"Research/Habitation/Storage"'
    }

  Write-Section "Remove Better Storage full Core def overrides blocked by v0.15"

  $betterStorageBlockedOverrides = @(
    "Definitions\Objects\Devices\BeverageCooler.json"
    "Definitions\Objects\Devices\Fridge.json"
    "Definitions\Objects\Devices\Storage.json"
  )

  foreach ($relativePath in $betterStorageBlockedOverrides) {
    Remove-FileIfExists `
      -Path (Join-Path $mods.BetterStorage $relativePath)
  }
}
else{
  Write-Host "Better Storage mod not selected for patching (disabled)."
}

if($Enable_Orbotron){
  Write-Section "Remove Orbotron deprecated Species Type field"

  Backup-And-RemoveText `
    -Path (Join-Path $mods.Orbotron "Config\Species\Types\Orbotron.json") `
    -Find '    "Type" : "Orbotron",
'

  Write-Section "Remove Orbotron deprecated SpawnRequirements Check field"

  Backup-And-RemoveText `
    -Path (Join-Path $mods.Orbotron "Config\Species\Types\Orbotron.json") `
    -Find '        "Check" : true,
'

}
else{
  Write-Host "Orbotron mod not selected for patching (disabled)."
}

if ($Enable_MetalHusksRevisited)
{
  Write-Section "Repair repeated MetalHusksRevisited PlanterImproved entries"

  Repair-RepeatedTextPattern `
    -ModRoot $mods.MetalHusksRevisited `
    -Pattern 'Research/LifeSupport/PlanterImproved(?:Improved)+' `
    -Replacement 'Research/LifeSupport/PlanterImproved' `
    -Label "repeated PlanterImproved"

  Write-Section "Patch MetalHusksRevisited Planter research prerequisites"

  Get-ModJsonFiles -ModRoot $mods.MetalHusksRevisited |
    ForEach-Object {
      Backup-And-Replace `
        -Path $_.FullName `
        -Find '"Research/LifeSupport/Planter"' `
        -Replace '"Research/LifeSupport/PlanterImproved"'
  }
}
else
{
  Write-Section "Metal Husk Revisited not selected for patching (disabled)."
}

# TODO (Stardeus v0.15+)
# Add IgnoreEnergyOutput: true to MolecularAssemblable recipes
# once the property becomes available in the public release.
if ($Enable_MolecularAssembler)
{
  Write-Section "Remove Molecular Assembler deprecated Craftable Level field"

  Get-ModJsonFiles -ModRoot $mods.MolecularAssembler |
    ForEach-Object {
      Backup-And-RemoveRegex `
        -Path $_.FullName `
        -Pattern '(?m)^\s*"Level"\s*:\s*\d+\s*,\r?\n?'
    }

  Write-Section "Patch Molecular Assembler validation flags"

  Get-ChildItem -Path (Join-Path $mods.MolecularAssembler "Config\Craftable\MolecularAssemblable") -Filter *.json -File |
    ForEach-Object {
      $path = $_.FullName
      $raw = Get-Content $path -Raw

      $hasIgnoreEnergyOutput = $raw -match '"IgnoreEnergyOutput"\s*:'
      $hasIgnoreMarket       = $raw -match '"IgnoreMarket"\s*:'

      if ($hasIgnoreEnergyOutput -and $hasIgnoreMarket) {
        Write-Host "No change needed: $path" -ForegroundColor DarkYellow
        return
      }

      $fixed = $raw

      if (-not $hasIgnoreEnergyOutput) {
        $fixed = $fixed -replace `
        '("EnergyCost"\s*:\s*[0-9.]+)', `
        "`$1,`r`n    `"IgnoreEnergyOutput`" : true"
      }

      if (-not $hasIgnoreMarket) {
        $fixed = $fixed -replace `
        '("IgnoreEnergyOutput"\s*:\s*true)', `
        "`$1,`r`n    `"IgnoreMarket`" : true"
      }

      if ($raw -eq $fixed) {
        Write-Warning "Could not patch validation flags: $path"
        return
      }

      Copy-Item $path "$path.bak" -Force
      Set-Content -Path $path -Value $fixed -Encoding UTF8

      Write-Host "Patched: $path" -ForegroundColor Green
      
      if (-not $hasIgnoreEnergyOutput) {
        Write-Host "  Added IgnoreEnergyOutput" -ForegroundColor Gray
      }

      if (-not $hasIgnoreMarket) {
        Write-Host "  Added IgnoreMarket" -ForegroundColor Gray
      }
    }

}
else
{
  Write-Section "Molecular Assembler not selected for patching (disabled)."
}

if ($Enable_Chromanite) {
  Write-Section "Remove Chromanite deprecated MatType NameKey fields"

  $deprecatedChromaniteNameKeys = @(
    '    "NameKey" : "material.ChromanitePulp",
'
    '    "NameKey" : "material.ChromaniteWood",
'
    '    "NameKey" : "material.ChromaniteSand",
'
    '    "NameKey" : "material.ChromanitePlate",
'
  )

  foreach ($nameKeyLine in $deprecatedChromaniteNameKeys) {
    Get-ModJsonFiles -ModRoot $mods.Chromanite |
      ForEach-Object {
        Backup-And-RemoveText `
          -Path $_.FullName `
          -Find $nameKeyLine
      }
  }

  Write-Section "Remove Chromanite deprecated Craftable Level field"

  Get-ModJsonFiles -ModRoot $mods.Chromanite |
    ForEach-Object {
      Backup-And-RemoveRegex `
        -Path $_.FullName `
        -Pattern '(?m)^\s*"Level"\s*:\s*1\s*,\r?\n?'
    }

  Write-Section "Patch Chromanite deprecated IgnoreStockMarket field"

  Get-ModJsonFiles -ModRoot $mods.Chromanite |
    ForEach-Object {
      Backup-And-Replace `
        -Path $_.FullName `
        -Find '"IgnoreStockMarket"' `
        -Replace '"IgnoreMarket"'
    }

  Write-Section "Remove Chromanite deprecated MatType fields"

  Get-ModJsonFiles -ModRoot $mods.Chromanite |
    ForEach-Object {
      Backup-And-RemoveRegex `
        -Path $_.FullName `
        -Pattern '(?m)^\s*"EnergyOutputComment"\s*:\s*"[^"]*"\s*,\r?\n?'
    }

  Get-ModJsonFiles -ModRoot $mods.Chromanite |
    ForEach-Object {
      Backup-And-RemoveRegex `
        -Path $_.FullName `
        -Pattern '(?m)^\s*"FavGroupId"\s*:\s*"[^"]*"\s*,\r?\n?'
    }

  Write-Section "Remove Chromanite deprecated MarketItem ProductOf field"

  Get-ModJsonFiles -ModRoot $mods.Chromanite |
    ForEach-Object {
      Backup-And-RemoveRegex `
        -Path $_.FullName `
        -Pattern '(?m)^\s*"ProductOf"\s*:\s*\[[^\]]*\]\s*,?\r?\n?'
    }

  Write-Section "Patch Chromanite Material GroupId fields"

  $chromaniteGroupIds = @(
    @{
      RelativePath = "Config\Materials\Building\ChromanitePlate.json"
      GroupId      = "Plates"
    }
    @{
      RelativePath = "Config\Materials\Ore\ChromaniteSand.json"
      GroupId      = "Ore"
    }
    @{
      RelativePath = "Config\Materials\Organic\ChromanitePulp.json"
      GroupId      = "Components"
    }
    @{
      RelativePath = "Config\Materials\Organic\ChromaniteWood.json"
      GroupId      = "Plants"
    }
  )

  foreach ($entry in $chromaniteGroupIds) {
    Ensure-JsonTopLevelPropertyAfter `
      -Path (Join-Path $mods.Chromanite $entry.RelativePath) `
      -AfterProperty "Id" `
      -PropertyName "GroupId" `
      -PropertyValue $entry.GroupId
  }

  Write-Section "Patch Chromanite Flammable temperature resistance typo"

  Patch-RelativeFiles `
    -ModRoot $mods.Chromanite `
    -RelativePaths @(
      "Definitions\Obj\Materials\ChromanitePlate.json"
      "Definitions\Obj\Materials\ChromaniteSand.json"
    ) `
    -Find "IsTemperatureResistent" `
    -Replace "IsTemperatureResistant"

}
else {
  Write-Section "Chromanite not selected for patching (disabled)."
}

if($Enable_AndroidsExpanded){
  Write-Section "Replace Androids deprecated wrapper for Looks\Androids file."

  # Replaces the old component/property-style Looks file with the current direct format.
  # This fixes missing/invalid Android rendering parts.
  Backup-And-WriteFile `
    -Path (Join-Path $mods.AndroidsExpanded "Config\Species\Looks\Androids.json") `
    -Content @'
{
  "BaseOffset": -0.4,
  "BarOffset": { "x" : 0, "y" : -0.33},
  "PreviewRot": "D",
  "PreviewScale": 2.5,
  "PreviewOffset": { "x": 0.0, "y": -0.2 },
  "SelectOffset": { "x": 0, "y": -0.1 },
  "SelectSize": { "x": 0.9, "y": 0.9 },
  "BubbleOffset": { "x": 0.4, "y": 0.3 },
  "CarryOffset": { "x": 0, "y": 0.5 },
  "WeaponOffsets" : [
    { "x" : -0.2, "y" : -0.1 },
    { "x" : -0.1, "y" : 0.15 },
    { "x" : 0.15, "y" : 0.15 },
    { "x" : 0.1, "y" : -0.1 }
  ],
  "FaceOffsetsH" : [
    { "x" : 0, "y" : 0.19 },
    { "x" : 0.05, "y" : 0.2 },
    { "x" : 0, "y" : 0.18 },
    { "x" : -0.05, "y" : 0.2 }
  ],
  "HasHorizontal": true,
  "Cache": true,
  "RebuildForConditions" : [
    "Sleeping"
  ],
  "EyeOverrides" : [
    {
      "Id" : "Removed",
      "SpriteId" : "Beings/Eyes/Human/Eye_Dead",
      "HasLR" : false,
      "CanBeClosed" : false,
      "IgnoreColor" : true,
      "Removed" : true
    },
    {
      "Id" : "Robotic",
      "SpriteId" : "Beings/Eyes/Human/Eye_Robo",
      "HasLR" : true,
      "CanBeClosed" : true,
      "ForRobotic" : true,
      "IgnoreColor" : true
    }
  ],
  "PartDefs" : [
    {
      "PartType" : "Body",
      "PositionFilter" : "",
      "RenderSorted" : true,
      "Offset" : { "x" : 0, "y" : 0 },
      "Scale" : 1
    },
    {
      "PartType" : "Clothing",
      "PositionFilter" : "",
      "RenderSorted" : true,
      "Offset" : { "x" : 0, "y" : 0 },
      "Scale" : 1
    },
    {
      "PartType" : "Weapon",
      "PositionFilter" : "",
      "Offset" : { "x" : 0, "y" : 0 },
      "Scale" : 1
    },
    {
      "PartType" : "Head",
      "PositionFilter" : "",
      "RenderSorted" : true,
      "Offset" : { "x" : 0, "y" : 0 },
      "Scale" : 1
    },
    {
      "PartType" : "Hair",
      "PositionFilter" : "",
      "RenderSorted" : true,
      "Offset" : { "x" : 0, "y" : 0 },
      "Scale" : 1
    },
    {
      "PartType" : "Hat",
      "PositionFilter" : "",
      "RenderSorted" : true,
      "Offset" : { "x" : 0, "y" : 0 },
      "Scale" : 1
    }
  ]
}
'@

  Write-Section "Remove Androids deprecated StasisWakeUpChance field"

  Backup-And-RemoveRegex `
    -Path (Join-Path $mods.AndroidsExpanded "Definitions\Beings\Androids01.json") `
    -Pattern '(?m)^\s*"StasisWakeUpChance"\s*:\s*[0-9.]+\s*,?\r?\n?'

  Write-Section "Remove Androids full ObeyChip Core override blocked by v0.15"

  Remove-FileIfExists `
    -Path (Join-Path $mods.AndroidsExpanded "Definitions\Obj\Parts\Implant\ObeyChip.json")

  Write-Section "Patch Androids Gearss typo"

  Get-ModJsonFiles -ModRoot $mods.AndroidsExpanded |
    ForEach-Object {
      Backup-And-Replace `
        -Path $_.FullName `
        -Find "Gearss" `
        -Replace "Gears"
  }

  Write-Section "Remove Androids deprecated Species Type field"

  Backup-And-RemoveText `
    -Path (Join-Path $mods.AndroidsExpanded "Config\Species\Types\Androids.json") `
    -Find '    "Type" : "Androids",
'

  Write-Section "Remove Androids deprecated Comment field"

  Backup-And-RemoveRegex `
    -Path (Join-Path $mods.AndroidsExpanded "Config\Body\Parts\Body\AndroidsFrame.json") `
    -Pattern '(?m)^\s*"Comment"\s*:\s*"[^"]*"\s*,?\r?\n?'

  Write-Section "Remove Androids deprecated NamePartsComment field"

  Backup-And-RemoveRegex `
    -Path (Join-Path $mods.AndroidsExpanded "Config\Species\Types\Androids.json") `
    -Pattern '(?m)^\s*"NamePartsComment"\s*:\s*\[[^\]]*\]\s*,?\r?\n?'

  # TODO v0.15:
  # AdjustsSkills was removed to satisfy the new BodyPartDef schema.
  # Investigate the v0.15 replacement for body-part skill bonuses so this
  # functionality can be restored instead of permenantly removed.
  Write-Section "Remove Androids deprecated AdjustsSkills fields"

  $androidAdjustsSkillsFiles = @(
    "Config\Body\Parts\Eye\SensorArray.json"
    "Config\Body\Parts\Heart\PowerNexus.json"
    "Config\Body\Parts\Jaw\MolecularSensor.json"
  )

  foreach ($relativePath in $androidAdjustsSkillsFiles) {
    Backup-And-RemoveRegex `
      -Path (Join-Path $mods.AndroidsExpanded $relativePath) `
      -Pattern '(?s)\s*"AdjustsSkills"\s*:\s*\[.*?\]\s*,?'
  }

  Write-Section "Remove Androids deprecated gender and attraction blocks"

  Backup-And-RemoveRegex `
    -Path (Join-Path $mods.AndroidsExpanded "Config\Species\Types\Androids.json") `
    -Pattern '(?s)\s*,?\s*"Sexes"\s*:\s*\[.*?\]\s*,\s*"SexualOrientations"\s*:\s*\[.*?\]\s*,\s*"SexualAttractions"\s*:\s*\[.*?\]\s*'

  Write-Section "Patch Androids MakeShiftJaw filename casing"

  Rename-FileExactCasing `
    -OldPath (Join-Path $mods.AndroidsExpanded "Config\Craftable\BodyParts\Jaw\MakeshiftJaw.json") `
    -NewPath (Join-Path $mods.AndroidsExpanded "Config\Craftable\BodyParts\Jaw\MakeShiftJaw.json")

  Write-Section "Patch Androids MakeShiftJaw content casing"

  Normalize-TextInFiles `
    -Root $mods.AndroidsExpanded `
    -Pattern "MakeshiftJaw" `
    -Replacement "MakeShiftJaw"

  Write-Section "Verify remaining Androids MakeshiftJaw references"

  $remainingMakeShiftJawRefs = @(
    Get-ChildItem -Path $mods.AndroidsExpanded -Recurse -File |
      Where-Object { $_.Extension.ToLower() -in @(".json", ".csv") } |
      Select-String -Pattern "MakeshiftJaw" -SimpleMatch -CaseSensitive
  )

  if ($remainingMakeShiftJawRefs.Count -eq 0) {
    Write-Host "No remaining case-sensitive MakeshiftJaw references found." -ForegroundColor Green
  }
  else {
    $remainingMakeShiftJawRefs |
      Select-Object Path, LineNumber, Line |
      Format-Table -AutoSize
  }

  Write-Section "Verify Androids MakeShiftJaw files"

  Write-Host "This section is safe to ignore, it's just validation of the previous process."
  Get-ChildItem -Path $mods.AndroidsExpanded -Recurse -File |
    Where-Object {
      ($_.Name -like "*MakeshiftJaw*" -or $_.Name -like "*MakeShiftJaw*") -and
      $_.Extension -in ".json", ".csv"
  } | 
    Select-Object FullName |
    Format-Table -AutoSize

}
else{
  Write-Section "Androids Expanded not selected for patching (disabled)."
}

# =============================================================================
# Validation process
# =============================================================================

Write-Section "Verify missing research IDs after patching"
Write-Host "This section is not mod dependent, but only validates keys used to help with troubleshooting."
Write-Host "This is safe to ignore if you don't want to do any troubleshooting."

$knownResearchIds = New-Object System.Collections.Generic.HashSet[string]

# Gather Core research IDs.
Get-ChildItem "$coreRoot\Definitions\Research" -Recurse -Filter *.json -File |
  ForEach-Object {
    $raw = Get-Content $_.FullName -Raw
    $match = [regex]::Match($raw, '"Id"\s*:\s*"([^"]+)"')

    if ($match.Success) {
      [void]$knownResearchIds.Add($match.Groups[1].Value)
    }
}

# Gather mod-owned research IDs so inter-mod references do not show as false positives.
foreach ($modName in $mods.Keys) {
  $modRoot = $mods[$modName]

  if (!(Test-Path $modRoot)) {
    continue
  }

  $researchRoot = Join-Path $modRoot "Definitions\Research"

  if (!(Test-Path $researchRoot)) {
    continue
  }

  Get-ChildItem $researchRoot -Recurse -Filter *.json -File |
    ForEach-Object {
      $raw = Get-Content $_.FullName -Raw
      $match = [regex]::Match($raw, '"Id"\s*:\s*"([^"]+)"')

      if ($match.Success) {
        [void]$knownResearchIds.Add($match.Groups[1].Value)
      }
  }
}

# Compare every Research/... reference in known mods against known Core/mod research IDs.
foreach ($modName in $mods.Keys) {
  $modRoot = $mods[$modName]

  if (!(Test-Path $modRoot)) {
    continue
  }

  Write-Host ""
  Write-Host "Checking research references for: $modName" -ForegroundColor Yellow

  $modRefs = Get-ModJsonFiles -ModRoot $modRoot |
    Select-String -Pattern '"Research/[^"]+"' |
    ForEach-Object {
      [regex]::Matches($_.Line, '"(Research/[^"]+)"') |
        ForEach-Object { $_.Groups[1].Value }
  } |
    Sort-Object -Unique

  $missing = $modRefs | Where-Object { -not $knownResearchIds.Contains($_) }

  if ($missing) {
    Write-Warning "Missing research IDs found in ${modName}:"
    $missing | ForEach-Object {
      Write-Host "  $_" -ForegroundColor Red
    }
  }
  else {
    Write-Host "No missing research IDs found for $modName." -ForegroundColor Green
  }
}

Write-Section "Collecting Core Flammable keys"
Write-Host "This section is not mod dependent, but only validates keys used to help with troubleshooting."
Write-Host "This is safe to ignore if you don't want to do any troubleshooting."

$coreUsage = @(Get-FlammableKeyUsageFromRoot -Root $coreRoot)
$coreKeys = [System.Collections.Generic.HashSet[string]]::new()

$coreUsage |
  ForEach-Object {
    [void]$coreKeys.Add($_.Key)
}

$coreKeys |
  Sort-Object |
  ForEach-Object {
    Write-Host "  $_"
}

Write-Section "Scanning Workshop mods for Flammable keys"
Write-Host "This section is not mod dependent, but only validates keys used to help with troubleshooting."
Write-Host "This is safe to ignore if you don't want to do any troubleshooting."

$modFolders = Get-ChildItem -Path $workshopRoot -Directory |
  Sort-Object Name

$deprecatedFlammableUsages = New-Object System.Collections.ArrayList
$unknownFlammableUsages = New-Object System.Collections.ArrayList
$modsWithFlammable = 0

foreach ($modFolder in $modFolders) {
  $modRoot = $modFolder.FullName
  $modName = Get-ModName -ModRoot $modRoot
  $modUsage = @(Get-FlammableKeyUsageFromRoot -Root $modRoot)

  if ($modUsage.Count -eq 0) {
    continue
  }

  $modsWithFlammable++

  foreach ($item in $modUsage) {
    if ($coreKeys.Contains($item.Key)) {
      continue
    }

    $record = [PSCustomObject]@{
      ModName = $modName
      ModId = $modFolder.Name
      Key = $item.Key
      File = $item.File
    }

    if ($knownDeprecatedFlammableKeys -contains $item.Key) {
      [void]$deprecatedFlammableUsages.Add($record)
    }
    else {
      [void]$unknownFlammableUsages.Add($record)
    }
  }
}

if ($modsWithFlammable -eq 0) {
  Write-Host "No Flammable components found in installed Workshop mods." -ForegroundColor DarkYellow
}
else {
  Write-Host "Scanned Flammable components in $modsWithFlammable Workshop mod(s)." -ForegroundColor Green
}

Write-Section "Known deprecated Flammable keys still present"
Write-Host "This section is not mod dependent, but only validates keys used to help with troubleshooting."
Write-Host "This is safe to ignore if you don't want to do any troubleshooting."
if ($deprecatedFlammableUsages.Count -eq 0) {
  Write-Host "No known deprecated Flammable keys found in installed Workshop mods." -ForegroundColor Green
}
else {
  $deprecatedFlammableUsages |
    Sort-Object ModName, Key, File |
    Format-Table -AutoSize
}

Write-Section "Flammable keys needing review"
Write-Host "This section is not mod dependent, but only validates keys used to help with troubleshooting."
Write-Host "This is safe to ignore if you don't want to do any troubleshooting."
if ($unknownFlammableUsages.Count -eq 0) {
  Write-Host "No unknown Flammable keys found in installed Workshop mods." -ForegroundColor Green
}
else {
  $unknownFlammableUsages |
    Sort-Object ModName, Key, File |
    Format-Table -AutoSize
}

Write-Section "Summary"

Write-Host "Core Flammable key count: $($coreKeys.Count)"
Write-Host "Workshop mod folders scanned: $($modFolders.Count)"
Write-Host "Known deprecated Flammable key usages found: $($deprecatedFlammableUsages.Count)"
Write-Host "Unknown Flammable key usages needing review: $($unknownFlammableUsages.Count)"

Write-Section "Patch complete"
