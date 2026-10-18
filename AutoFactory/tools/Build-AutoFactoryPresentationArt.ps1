param(
    [Parameter(Mandatory = $true)]
    [string] $NanobotSource,

    [Parameter(Mandatory = $true)]
    [string] $CompatibilityBadge
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$modRoot = Split-Path -Parent $PSScriptRoot
$iconDir = Join-Path $modRoot 'Graphics\Icons\Color'
New-Item -ItemType Directory -Path $iconDir -Force | Out-Null

function New-Canvas([int] $width, [int] $height) {
    return [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function Save-Png([System.Drawing.Bitmap] $bitmap, [string] $path) {
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# Search/filter funnel: a broad, readable silhouette with the cyan-white Auto Factory accent.
$funnel = New-Canvas 128 128
$g = [System.Drawing.Graphics]::FromImage($funnel)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(235, 12, 22, 30), 14)
$accent = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 136, 239, 255), 8)
$fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 226, 249, 255))
$points = [System.Drawing.PointF[]] @(
    [System.Drawing.PointF]::new(18, 23),
    [System.Drawing.PointF]::new(110, 23),
    [System.Drawing.PointF]::new(76, 66),
    [System.Drawing.PointF]::new(76, 108),
    [System.Drawing.PointF]::new(52, 96),
    [System.Drawing.PointF]::new(52, 66)
)
$g.FillPolygon($fill, $points)
$g.DrawPolygon($outline, $points)
$g.DrawLines($accent, [System.Drawing.PointF[]] @($points[0], $points[1], $points[2]))
$g.Dispose(); $outline.Dispose(); $accent.Dispose(); $fill.Dispose()
Save-Png $funnel (Join-Path $iconDir 'AutoFactoryFilter.png')

# Production-goal equality icon. Its heavy strokes survive reduction better than a counter glyph.
$equals = New-Canvas 128 128
$g = [System.Drawing.Graphics]::FromImage($equals)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$shadow = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(245, 13, 25, 31), 30)
$line = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 167, 246, 255), 18)
$shadow.StartCap = $shadow.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$line.StartCap = $line.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
foreach ($y in @(45, 83)) {
    $g.DrawLine($shadow, 25, $y, 103, $y)
    $g.DrawLine($line, 25, $y, 103, $y)
}
$g.Dispose(); $shadow.Dispose(); $line.Dispose()
Save-Png $equals (Join-Path $iconDir 'AutoFactoryGoalEquals.png')

# Remove the generated magenta background, soften the fringe, and crop to the robot silhouette.
$source = [System.Drawing.Bitmap]::new($NanobotSource)
$transparent = New-Canvas $source.Width $source.Height
for ($y = 0; $y -lt $source.Height; $y++) {
    for ($x = 0; $x -lt $source.Width; $x++) {
        $c = $source.GetPixel($x, $y)
        $magentaStrength = [Math]::Min($c.R, $c.B) - $c.G
        if ($c.R -gt 205 -and $c.B -gt 180 -and $magentaStrength -gt 75) {
            continue
        }
        $a = 255
        if ($c.R -gt 170 -and $c.B -gt 150 -and $magentaStrength -gt 45) { $a = 120 }
        $transparent.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B))
    }
}
$source.Dispose()

$widget = New-Canvas 128 128
$g = [System.Drawing.Graphics]::FromImage($widget)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.DrawImage($transparent, [System.Drawing.Rectangle]::new(5, 5, 118, 118))
$g.Dispose(); $transparent.Dispose()
Save-Png $widget (Join-Path $iconDir 'AutoFactoryNanobot.png')

# Workshop cover: exact facility sprites and exact title over a bespoke Stardeus-family backdrop.
$coverSize = 1254
$cover = [System.Drawing.Bitmap]::new($coverSize, $coverSize, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($cover)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new(0, 0, $coverSize, $coverSize),
    [System.Drawing.Color]::FromArgb(255, 7, 15, 30),
    [System.Drawing.Color]::FromArgb(255, 44, 17, 73),
    55
)
$g.FillRectangle($background, 0, 0, $coverSize, $coverSize)
$background.Dispose()

$gridPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(35, 105, 214, 255), 2)
for ($p = -$coverSize; $p -lt $coverSize * 2; $p += 80) {
    $g.DrawLine($gridPen, $p, 300, $p + 700, 1000)
}
$gridPen.Dispose()

$halo = [System.Drawing.Drawing2D.PathGradientBrush]::new([System.Drawing.Point[]] @(
    [System.Drawing.Point]::new(627, 195), [System.Drawing.Point]::new(1120, 700),
    [System.Drawing.Point]::new(627, 1110), [System.Drawing.Point]::new(130, 700)
))
$halo.CenterColor = [System.Drawing.Color]::FromArgb(150, 37, 211, 255)
$halo.SurroundColors = [System.Drawing.Color[]] @([System.Drawing.Color]::FromArgb(0, 24, 40, 80))
$g.FillEllipse($halo, 120, 170, 1014, 930)
$halo.Dispose()

$fabricator = [System.Drawing.Image]::FromFile((Join-Path $modRoot 'Graphics\Objects\Devices\NanoFabricator_D.png'))
$foundry = [System.Drawing.Image]::FromFile((Join-Path $modRoot 'Graphics\Objects\Devices\NanoFoundry_D.png'))
$g.DrawImage($fabricator, [System.Drawing.Rectangle]::new(40, 310, 570, 760))
$g.DrawImage($foundry, [System.Drawing.Rectangle]::new(650, 315, 570, 760))
$fabricator.Dispose(); $foundry.Dispose()

$titleFont = [System.Drawing.Font]::new('Segoe UI Semibold', 108, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$title = 'Auto Factory'
$titleSize = $g.MeasureString($title, $titleFont)
$titleX = ($coverSize - $titleSize.Width) / 2
$titleShadow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(225, 0, 0, 0))
$titleBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.RectangleF]::new($titleX, 55, $titleSize.Width, 135),
    [System.Drawing.Color]::FromArgb(255, 255, 255, 255),
    [System.Drawing.Color]::FromArgb(255, 138, 222, 255),
    90
)
$g.DrawString($title, $titleFont, $titleShadow, $titleX + 6, 63)
$g.DrawString($title, $titleFont, $titleBrush, $titleX, 55)
$titleFont.Dispose(); $titleShadow.Dispose(); $titleBrush.Dispose()

$badge = [System.Drawing.Image]::FromFile($CompatibilityBadge)
$badgeSize = 250
$badgeX = $coverSize - $badgeSize - 28
$badgeY = $coverSize - $badgeSize - 28
$g.DrawImage($badge, [System.Drawing.Rectangle]::new($badgeX, $badgeY, $badgeSize, $badgeSize))
$badge.Dispose()

$borderOuter = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 124, 75, 218), 16)
$borderInner = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 125, 231, 255), 4)
$g.DrawRectangle($borderOuter, 12, 12, $coverSize - 24, $coverSize - 24)
$g.DrawRectangle($borderInner, 30, 30, $coverSize - 60, $coverSize - 60)
$borderOuter.Dispose(); $borderInner.Dispose()
$g.Dispose()

$coverPath = Join-Path $modRoot 'ModCover.jpg'
$encoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$parameters = [System.Drawing.Imaging.EncoderParameters]::new(1)
$parameters.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new([System.Drawing.Imaging.Encoder]::Quality, [long] 94)
$cover.Save($coverPath, $encoder, $parameters)
$parameters.Dispose(); $cover.Dispose()

Write-Output "Created presentation art in $iconDir and $coverPath"
