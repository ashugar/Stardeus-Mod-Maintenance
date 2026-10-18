param(
    [Parameter(Mandatory)][string]$GeneratedRoot,
    [string]$CoreDevices
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/LocalPaths.ps1')
if (-not (Test-Path -LiteralPath $GeneratedRoot -PathType Container)) { throw 'Provide the local source-art directory with -GeneratedRoot.' }
if (-not $CoreDevices) { $CoreDevices = Join-Path (Resolve-StardeusCoreRoot) 'Graphics/Objects/Devices' }
if (-not (Test-Path -LiteralPath $CoreDevices -PathType Container)) { throw 'CoreDevices must point to the installed Core device art.' }
Add-Type -AssemblyName System.Drawing

$modRoot = Split-Path -Parent $PSScriptRoot
$deviceOut = Join-Path $modRoot 'Graphics\Objects\Devices'
$animOut = Join-Path $modRoot 'Graphics\Objects\DeviceAnims'
New-Item -ItemType Directory -Force -Path $deviceOut, $animOut | Out-Null

$assets = @(
    @{ Family='NanoFabricator'; Dir='D'; Generated='exec-510d2262-8d17-4960-95ec-b13430c7acff.png'; Original='Fabricator_D.png' },
    @{ Family='NanoFabricator'; Dir='L'; Generated='exec-abdff0c0-277d-4eab-935c-4d042fd7e225.png'; Original='Fabricator_L.png' },
    @{ Family='NanoFabricator'; Dir='U'; Generated='exec-d1857d22-f995-41b8-b753-48cd33948e02.png'; Original='Fabricator_U.png' },
    @{ Family='NanoFabricator'; Dir='R'; Generated='exec-a7e54e90-c0e7-4690-beb7-13e3f15949e7.png'; Original='Fabricator_R.png' },
    @{ Family='NanoFoundry'; Dir='D'; Generated='exec-2147c08b-db91-4532-8fd2-22cf53f95696.png'; Original='Furnace_D.png' },
    @{ Family='NanoFoundry'; Dir='L'; Generated='exec-d75a5f42-79cf-441a-ae99-ac4059752ecc.png'; Original='Furnace_L.png' },
    @{ Family='NanoFoundry'; Dir='U'; Generated='exec-48d0a14b-006b-447a-8f18-f25a9c5d8592.png'; Original='Furnace_U.png' },
    @{ Family='NanoFoundry'; Dir='R'; Generated='exec-8302d4a1-7e9d-4bfc-aa2d-199942e7b5b6.png'; Original='Furnace_R.png' }
)

function Get-AlphaBounds([System.Drawing.Bitmap]$bitmap) {
    $minX = $bitmap.Width; $minY = $bitmap.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $bitmap.Height; $y += 2) {
        for ($x = 0; $x -lt $bitmap.Width; $x += 2) {
            if ($bitmap.GetPixel($x, $y).A -gt 12) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { throw 'No opaque pixels found.' }
    return [Drawing.Rectangle]::FromLTRB($minX, $minY, [Math]::Min($bitmap.Width, $maxX + 2), [Math]::Min($bitmap.Height, $maxY + 2))
}

function Remove-Chroma([string]$path) {
    $source = [Drawing.Bitmap]::FromFile($path)
    $clean = New-Object Drawing.Bitmap $source.Width, $source.Height, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            $c = $source.GetPixel($x, $y)
            $keyStrength = [Math]::Min($c.R, $c.B) - $c.G
            if ($c.R -gt 185 -and $c.B -gt 155 -and $c.G -lt 125 -and $keyStrength -gt 75) {
                $clean.SetPixel($x, $y, [Drawing.Color]::Transparent)
            } else {
                $clean.SetPixel($x, $y, [Drawing.Color]::FromArgb(255, $c.R, $c.G, $c.B))
            }
        }
    }
    $source.Dispose()
    return $clean
}

function Test-Cyan([Drawing.Color]$c) {
    return $c.A -gt 15 -and $c.B -gt 125 -and $c.G -gt 90 -and ($c.B - $c.R) -gt 38 -and ($c.G - $c.R) -gt 18
}

function Has-OriginalCyanNearby([Drawing.Bitmap]$original, [int]$x, [int]$y) {
    foreach ($oy in -4, 0, 4) {
        foreach ($ox in -4, 0, 4) {
            $px = [Math]::Max(0, [Math]::Min($original.Width - 1, $x + $ox))
            $py = [Math]::Max(0, [Math]::Min($original.Height - 1, $y + $oy))
            if (Test-Cyan $original.GetPixel($px, $py)) { return $true }
        }
    }
    return $false
}

function Write-AnimationFrames([Drawing.Bitmap]$base, [Drawing.Bitmap]$original, [string]$family, [string]$dir) {
    for ($frame = 0; $frame -lt 3; $frame++) {
        $overlay = New-Object Drawing.Bitmap $base.Width, $base.Height, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $frameGraphics = [Drawing.Graphics]::FromImage($overlay)
        $frameGraphics.Clear([Drawing.Color]::Transparent)
        $frameGraphics.Dispose()
        for ($y = 0; $y -lt $base.Height; $y++) {
            for ($x = 0; $x -lt $base.Width; $x++) {
                $c = $base.GetPixel($x, $y)
                if ($c.A -lt 16) { continue }
                $cyan = (Test-Cyan $c) -and -not (Has-OriginalCyanNearby $original $x $y)
                $packetBand = ((($x + (2 * $y) + (24 * $frame)) % 72) -lt 13)
                if ($cyan -and $packetBand) {
                    # Bright nanobot packet moving through the translucent hose.
                    $overlay.SetPixel($x, $y, [Drawing.Color]::FromArgb(235, 210, 250, 255))
                } elseif ($cyan) {
                    # Low, persistent fitting/trim glow between packets. This keeps
                    # the machinery readable without turning the whole overlay solid.
                    $overlay.SetPixel($x, $y, [Drawing.Color]::FromArgb(42, 74, 220, 255))
                }
            }
        }
        $animPath = Join-Path $animOut ("{0}_Anim_{1}_F{2}.png" -f $family, $dir, $frame)
        $overlay.Save($animPath, [Drawing.Imaging.ImageFormat]::Png)
        $overlay.Dispose()
    }
}

foreach ($asset in $assets) {
    $generated = Remove-Chroma (Join-Path $generatedRoot $asset.Generated)
    $original = [Drawing.Bitmap]::FromFile((Join-Path $coreDevices $asset.Original))
    $generatedBounds = Get-AlphaBounds $generated
    $originalBounds = Get-AlphaBounds $original

    $scale = [Math]::Min($originalBounds.Width / $generatedBounds.Width, $originalBounds.Height / $generatedBounds.Height)
    $drawWidth = [Math]::Max(1, [int][Math]::Round($generatedBounds.Width * $scale))
    $drawHeight = [Math]::Max(1, [int][Math]::Round($generatedBounds.Height * $scale))
    $drawX = [int][Math]::Round($originalBounds.X + (($originalBounds.Width - $drawWidth) / 2.0))
    $drawY = [int][Math]::Round($originalBounds.Y + (($originalBounds.Height - $drawHeight) / 2.0))

    $base = New-Object Drawing.Bitmap $original.Width, $original.Height, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($base)
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($generated, [Drawing.Rectangle]::new($drawX, $drawY, $drawWidth, $drawHeight), $generatedBounds, [Drawing.GraphicsUnit]::Pixel)
    $graphics.Dispose()

    $basePath = Join-Path $deviceOut ("{0}_{1}.png" -f $asset.Family, $asset.Dir)
    $base.Save($basePath, [Drawing.Imaging.ImageFormat]::Png)
    Write-AnimationFrames $base $original $asset.Family $asset.Dir

    $base.Dispose(); $generated.Dispose(); $original.Dispose()
    Write-Output ("Created {0} {1}" -f $asset.Family, $asset.Dir)
}
