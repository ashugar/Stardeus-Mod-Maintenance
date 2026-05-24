# Repairs known stale/broken Stardeus Workshop mod references after reinstall.
#
# Current repair coverage:
# - Prosthetics & Androids Expanded:
#   - Rebuilds Android looks config into the current direct JSON format.
#   - Restores / validates Quantum Nexus brain compatibility for Androids.
#   - Adds Quantum Nexus Core support for robotic lifeforms.
#   - Adds Quantum Nexus and Quantum Nexus Core support to Mind Transfer and Machine Learning.
#   - Adds ObeyChip research/craft unlock override.
#   - Normalizes MakeShiftJaw casing in filenames and file contents.
#   - Fixes Gearss -> Gears typo.
#   - Fixes zero ToiletRate value on AfterBurner-style intestine parts.
#   - Removes invalid market item files for IgnoreMarket parts.
#   - Fixes missing default right lung/right kidney in manufactured Androids.
#   - Removes unfinished orphan body part configs that have no matching object definitions.
# - Molecular Assembler Fixed:
#   - Adds IgnoreMarket: true to MolecularAssemblable recipes to avoid economy validation errors.
# - MetalHusksRevisited:
#   - Repairs repeated PlanterImprovedImproved strings.
#   - Updates Planter -> PlanterImproved research prerequisites.
# - IRONHUSK:
#   - Updates Planter -> PlanterImproved research prerequisites.
#   - Fixes IsTemperatureResistent -> IsTemperatureResistant typo.
# - Chromanite Material:
#   - Fixes IsTemperatureResistent -> IsTemperatureResistant typo.
# - Fast IRONHUSK:
#   - Removes deprecated Flammable keys no longer found in Core.
#
# Validation coverage:
# - Verifies known mods do not reference missing Research IDs.
# - Scans all installed Workshop mods for Flammable keys not found in Core.
# - Separates known deprecated Flammable keys from unknown keys needing review.
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
  Ironhusk            = "$workshopRoot\2874508310" # IRONHUSK
  FastIronhusk        = "$workshopRoot\2876676062" # Fast IRONHUSK
  AndroidsExpanded    = "$workshopRoot\3415188497" # Prosthetics & Androids Expanded
  MolecularAssembler  = "$workshopRoot\3306928875" # Molecular Assembler Fixed
}

# Update this to $true if you have the Mod listed. $false if you don't.
$Enable_MetalHusksRevisited = $false
$Enable_Chromanite = $false
$Enable_Ironhusk = $false
$Enable_FastIronhusk = $false
$Enable_AndroidsExpanded = $false
$Enable_MolecularAssembler = $false

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

# =============================================================================
# Repair process
# =============================================================================

if ($Enable_AndroidsExpanded)
{
  Write-Section "Patch Prosthetics & Androids Expanded"

  if ($mods.ContainsKey("AndroidsExpanded")) {
    $androidRoot = $mods["AndroidsExpanded"]

    if (!(Test-Path $androidRoot)) {
      Write-Warning "AndroidsExpanded folder not found: $androidRoot"
    }
    else {
      # Replaces the old component/property-style Looks file with the current direct format.
      # This fixes missing/invalid Android rendering parts.
      Backup-And-WriteFile `
        -Path (Join-Path $androidRoot "Config\Species\Looks\Androids.json") `
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

      # Restores QuantumNexus as an Android brain part.
      Backup-And-WriteFile `
        -Path (Join-Path $androidRoot "Config\Body\Parts\Brain\QuantumNexus.json") `
        -Content @'
{
  "SlotTypeId" : "Brain",
  "Compatibility" : "TargetSpecies",
  "TargetSpecies" : [ "Androids" ],
  "PartFlags" : [ "HoldsMind", "SupportsMindTransfer", "SupportsMLBooth" ],
  "ResistanceBase" : 6.0,
  "Tier" : 5
}
'@

      # Adds a Core-slot equivalent Quantum Nexus for robotic lifeforms that use Core instead of Brain.
      Backup-And-WriteFile `
        -Path (Join-Path $androidRoot "Config\Body\Parts\Core\QuantumNexusCore.json") `
        -Content @'
{
  "SlotTypeId" : "Core",
  "Compatibility" : [ "TargetSpecies" ],
  "TargetSpecies" : [
    "CleaningBot",
    "Drone",
    "Orbotron",
    "Robot",
    "Sentry",
    "Carrier"
  ],
  "PartFlags" : [ "HoldsMind", "SupportsMindTransfer", "SupportsMLBooth" ],
  "ResistanceBase" : 6.0,
  "Tier" : 5
}
'@

      # Adds object definition for QuantumNexusCore.
      Backup-And-WriteFile `
        -Path (Join-Path $androidRoot "Definitions\Obj\Parts\Core\QuantumNexusCore.json") `
        -Content @'
{
  "Layer" : "FreeEntities",
  "ParentId" : "Obj/Parts/BasePartRobotic",
  "NameKey" : "bodypart.Core.QuantumNexusCore",
  "Researchable" : {
    "Prerequisites" : [ "Research/Robotics/QuantumNexus" ]
  },
  "Components" : [
    {
      "Component" : "ObjGraphics",
      "Properties" : [
        { "Key" : "Graphic", "String": "Obj/BodyParts/Robotic3" }
      ]
    },
    {
      "Component" : "BodyPart",
      "Properties" : [
        { "Key" : "BodyPartDefId", "String" : "Core/QuantumNexusCore" }
      ]
    }
  ]
}
'@

      # Adds craftable recipe for QuantumNexusCore.
      Backup-And-WriteFile `
        -Path (Join-Path $androidRoot "Config\Craftable\BodyParts\Core\QuantumNexusCore.json") `
        -Content @'
{
  "Type" : "Robotics3",
  "ProductDefId" : "Obj/Parts/Core/QuantumNexusCore",
  "ProductionTimeHours" : 40,
  "EnergyCost" : 100,
  "IgnoreMarket" : true,
  "Ingredients" : [
    { "TypeId" : "TitaniumPlate", "StackSize" : 3 },
    { "TypeId" : "PlatinumIngot", "StackSize" : 8 },
    { "TypeId" : "GoldIngot", "StackSize" : 2 },
    { "TypeId" : "Phasium", "StackSize" : 1 },
    { "TypeId" : "Transistor", "StackSize" : 40 },
    { "TypeId" : "OpticalFiber", "StackSize" : 10 },
    { "TypeId" : "Microchip3", "StackSize" : 6 }
  ]
}
'@

      # Overrides ObeyChip definition so it becomes research-gated behind QuantumNexus.
      # The Core craftable recipe already exists, so this only supplies the modded definition behavior.
      Backup-And-WriteFile `
        -Path (Join-Path $androidRoot "Definitions\Obj\Parts\Implant\ObeyChip.json") `
        -Content @'
{
  "Layer" : "FreeEntities",
  "ParentId" : "Obj/Parts/BasePartRobotic",
  "NameKey" : "bodypart.Implant.ObeyChip",
  "Researchable" : {
    "Prerequisites" : [ "Research/Robotics/QuantumNexus" ]
  },
  "Components" : [
    {
      "Component" : "ObjGraphics",
      "Properties" : [
        { "Key" : "Graphic", "String": "Obj/BodyParts/Robotic2" }
      ]
    },
    {
      "Component" : "BodyPart",
      "Properties" : [
        { "Key" : "BodyPartDefId", "String" : "Implant/ObeyChip" }
      ]
    }
  ]
}
'@

      # Adds translation rows for QuantumNexusCore after the existing QuantumNexus brain text.
      Ensure-CsvLineAfterKey `
        -Path (Join-Path $androidRoot "Translations\English.csv") `
        -AfterKey "bodypart.Brain.QuantumNexus.desc" `
        -LinesToEnsure @(
          "bodypart.Core.QuantumNexusCore,Quantum Nexus Core,,"
          "bodypart.Core.QuantumNexusCore.desc,A nexus to generate free will.,,"
        )
    }
  }

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

  Write-Section "Patch Androids Gearss typo"

  Get-ModJsonFiles -ModRoot $mods.AndroidsExpanded |
    ForEach-Object {
      Backup-And-Replace `
        -Path $_.FullName `
        -Find "Gearss" `
        -Replace "Gears"
  }

  Write-Section "Patch Androids zero ToiletRate values"

  Patch-RelativeFiles `
    -ModRoot $mods.AndroidsExpanded `
    -RelativePaths @(
      "Config\Body\Parts\Intestines\AfterBurner.json"
      "Config\Body\Parts\Intestines\BionicsIntestines.json"
      "Config\Body\Parts\Intestines\MakeshiftIntestines.json"
    ) `
    -Find '"Component" : "ToiletRate",
  "Properties" : [
  { "Key" : "AmountPerHour", "Float" : 0.0 }
  ]' `
  -Replace '"Component" : "ToiletRate",
  "Properties" : [
  { "Key" : "AmountPerHour", "Float" : 0.001 }
  ]'

  Write-Section "Remove Androids invalid IgnoreMarket market items"

  # These files point to defs that should not be market-listed, causing load warnings.
  $invalidAndroidMarketItems = @(
    "Config\Economy\MarketItems\BodyParts\Head\AndroidsHead.json"
    "Config\Economy\MarketItems\BodyParts\Jaw\MolecularSensor.json"
    "Config\Economy\MarketItems\BodyParts\Jaw\BionicsJaw.json"
    "Config\Economy\MarketItems\BodyParts\Jaw\MakeShiftJaw.json"
  )

  foreach ($relativePath in $invalidAndroidMarketItems) {
    Remove-FileIfExists `
      -Path (Join-Path $mods.AndroidsExpanded $relativePath)
  }

  Write-Section "Patch Androids default missing right lung and kidney"

  # Manufactured Androids were missing Lung_R and Kidney_R by default.
  Patch-RelativeFiles `
    -ModRoot $mods.AndroidsExpanded `
    -RelativePaths @(
      "Config\Body\Configurations\Androids_Default.json"
    ) `
    -Find '{ "Slot" : "Lung_R", "Empty" : true }' `
    -Replace '{ "Slot" : "Lung_R", "Part" : "Lung/MakeshiftLung" }'

  Patch-RelativeFiles `
    -ModRoot $mods.AndroidsExpanded `
    -RelativePaths @(
      "Config\Body\Configurations\Androids_Default.json"
    ) `
    -Find '{ "Slot" : "Kidney_R", "Empty" : true }' `
    -Replace '{ "Slot" : "Kidney_R", "Part" : "Kidney/MakeshiftKidney" }'

  Write-Section "Remove Androids UNFINISHED orphan body part configs - REMOVE THIS SECTION IF IMPLEMENTED LATER"

  # These body part configs have no matching Definitions\Obj\Parts, craftable files, or market files.
  # They appear to be unfinished TODO content from the mod author.
  $orphanAndroidBodyParts = @(
    "Foot\BladeFootHuman.json"
    "Foot\FootMecha.json"
    "Foot\FootRoller.json"
    "Hip\HipMecha.json"
    "Implant\BiologicalNull.json"
    "Implant\JoyWire.json"
    "Implant\RegenFactor.json"
    "Implant\SkinArmor.json"
    "Mobilizer\MegaJet.json"
    "Mobilizer\SpeedTracks.json"
  )

  foreach ($relativePath in $orphanAndroidBodyParts) {
    Remove-FileIfExists `
      -Path (Join-Path $mods.AndroidsExpanded "Config\Body\Parts\$relativePath")
  }
}
else
{
  Write-Section "Skipping Androids Expanded repairs (disabled)."
}

if ($Enable_MolecularAssembler)
{
  Write-Section "Patch MolecularAssemblable recipes to ignore market validation"

  # These recipes are matter-conversion recipes and intentionally do not represent normal economy production.
  # IgnoreMarket prevents Stardeus market validation from treating them as zero-cost production loops.
  $molRoot = Join-Path $mods.MolecularAssembler "Config\Craftable\MolecularAssemblable"

  if (!(Test-Path $molRoot)) {
    Write-Warning "MolecularAssemblable recipe folder not found: $molRoot"
  }
  else {
    Get-ChildItem -Path $molRoot -Filter *.json -File |
      ForEach-Object {
        $path = $_.FullName
        $raw = Get-Content $path -Raw

        if ($raw -match '"IgnoreMarket"\s*:') {
          Write-Host "No change needed: $path" -ForegroundColor DarkYellow
          return
        }

        $replacement = '$1,' + "`r`n" + '    "IgnoreMarket": true'
        $fixed = $raw -replace '(\s*"EnergyCost"\s*:\s*[0-9.]+)', $replacement

        if ($raw -cne $fixed) {
          Copy-Item $path "$path.bak" -Force
          Set-Content -Path $path -Value $fixed -Encoding UTF8
          Write-Host "Patched: $path" -ForegroundColor Green
          Write-Host "  Added IgnoreMarket: true" -ForegroundColor Gray
        }
        else {
          Write-Warning "Could not patch IgnoreMarket into: $path"
        }
    }
  }

  Write-Section "Patch Molecular Assembler missing ExtraInfo component"

  $massemblerDef = Join-Path $mods.MolecularAssembler "Definitions\Objects\Devices\Massembler.json"

  Ensure-JsonComponentBeforeComponent `
    -Path $massemblerDef `
    -ComponentToEnsure "ExtraInfo" `
    -BeforeComponent "MultiCrafter"

  Write-Section "Patch Molecular Assembler obsolete Flammable curve values"

  Patch-RelativeFiles `
    -ModRoot $mods.MolecularAssembler `
    -RelativePaths @(
      "Definitions\Objects\Devices\Massembler.json"
    ) `
    -Find '"String": "FlammabilityCurve_Device"' `
    -Replace '"String": "Device"'

  Patch-RelativeFiles `
    -ModRoot $mods.MolecularAssembler `
    -RelativePaths @(
      "Definitions\Objects\Devices\Massembler.json"
    ) `
    -Find '"String": "ExplosivenessCurve_Device"' `
    -Replace '"String": "Device"'

  Write-Section "Patch Molecular Assembler Flammable properties"

  $massemblerDef = Join-Path $mods.MolecularAssembler "Definitions\Objects\Devices\Massembler.json"

  Patch-RelativeFiles `
    -ModRoot $mods.MolecularAssembler `
    -RelativePaths @(
      "Definitions\Objects\Devices\Massembler.json"
    ) `
    -Find '"Component": "Flammable",
  "Properties": [
  {
    "Key": "IgnitionTemperatureCelsius",
    "Int": 250
  },
  {
    "Key": "FireChance",
    "Float": 0.6
  },
  {
    "Key": "FlammabilityCurve",
    "String": "Device"
  },
  {
    "Key": "ExplosivenessCurve",
    "String": "Device"
  }
  ]' `
  -Replace '"Component": "Flammable",
  "Properties": [
  {
    "Key": "IgnitionTemperatureCelsius",
    "Int": 250
  },
  {
    "Key": "FireChance",
    "Float": 0.6
  },
  {
    "Key": "BaseExplosionLevel",
    "Int": 2
  },
  {
    "Key": "FlammabilityCurve",
    "String": "Device"
  },
  {
    "Key": "ExplosivenessCurve",
    "String": "Device"
  }
  ]'

  Write-Section "Patch Molecular Assembler TileTransform logistics ports"

  Patch-RelativeFiles `
    -ModRoot $mods.MolecularAssembler `
    -RelativePaths @(
      "Definitions\Objects\Devices\Massembler.json"
    ) `
    -Find '"Component": "TileTransform",
  "Properties": [
  {
    "Key": "IsRotatable",
    "Bool": true
  },
  {
    "Key": "WorkSpot",
    "Vector2": {
      "x": 0,
      "y": -1
    }
  },
  {
    "Key": "Height",
    "Int": 2
  },
  {
    "Key": "Width",
    "Int": 2
  }
  ]' `
  -Replace '"Component": "TileTransform",
  "Properties": [
  {
    "Key": "IsRotatable",
    "Bool": true
  },
  {
    "Key": "WorkSpot",
    "Vector2": {
      "x": 0,
      "y": -1
    }
  },
  {
    "Key": "Height",
    "Int": 2
  },
  {
    "Key": "Width",
    "Int": 2
  },
  {
    "Key" : "CoverPercent",
    "Int" : 0
  },
  {
    "Key" : "InPort",
    "Vector2" : { "x" : 0, "y" : -1 }
  },
  {
    "Key" : "OutPort",
    "Vector2" : { "x" : 0, "y" : -1 }
  }
  ]'
}
else
{
  Write-Section "Skipping Molecular Assembler repairs (disabled)."
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
  Write-Section "Skipping Metal Husk Revisited repairs (disabled)."
}

if ($Enable_Ironhusk)
{
  Write-Section "Patch IRONHUSK Planter research prerequisites"

  Get-ModJsonFiles -ModRoot $mods.Ironhusk |
    ForEach-Object {
      Backup-And-Replace `
        -Path $_.FullName `
        -Find '"Research/LifeSupport/Planter"' `
        -Replace '"Research/LifeSupport/PlanterImproved"'
  }


  Write-Section "Patch IRONHUSK Flammable typo"

  Patch-RelativeFiles `
    -ModRoot $mods.Ironhusk `
    -RelativePaths @(
      "Definitions\Obj\Plants\Ironhusk.json"
    ) `
    -Find "IsTemperatureResistent" `
    -Replace "IsTemperatureResistant"

}
else
{
  Write-Section "Skipping Ironhusk repairs (disabled)."
}

if ($Enable_Chromanite)
{
  Write-Section "Patch Chromanite Flammable typo"

  Patch-RelativeFiles `
    -ModRoot $mods.Chromanite `
    -RelativePaths @(
      "Definitions\Obj\Materials\ChromanitePlate.json"
      "Definitions\Obj\Materials\ChromaniteSand.json"
    ) `
    -Find "IsTemperatureResistent" `
    -Replace "IsTemperatureResistant"
}
else
{
  Write-Section "Skipping Chromanite repairs (disabled)."
}

if ($Enable_FastIronhusk)
{
  Write-Section "Remove Fast IRONHUSK deprecated Flammable keys"

  $fastIronhuskFile = Join-Path $mods.FastIronhusk "Definitions\Obj\Plants\Ironhusk.json"

  Remove-LinesContainingText `
    -Path $fastIronhuskFile `
    -Needles $knownDeprecatedFlammableKeys
}
else
{
  Write-Section "Skipping Fast Ironhusk repairs (disabled)."
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
