# Generates a Minecraft-style pixel-art hero background for the launcher.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\generate-hero.ps1
Add-Type -AssemblyName System.Drawing

$block = 4
$cols  = 400
$rows  = 200
$W = $cols * $block
$H = $rows * $block

$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None

function New-Brush([string]$hex) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    return New-Object System.Drawing.SolidBrush($c)
}

# Sky (smooth vertical gradient)
$skyRect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$cTop = [System.Drawing.ColorTranslator]::FromHtml("#6AA8DE")
$cBot = [System.Drawing.ColorTranslator]::FromHtml("#C7E8F4")
$skyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($skyRect, $cTop, $cBot, 90.0)
$g.FillRectangle($skyBrush, $skyRect)

$sun      = New-Brush "#FDF2B5"
$cloud    = New-Brush "#FFFFFF"
$stone    = New-Brush "#93A0AC"
$snow     = New-Brush "#F7FAFB"
$grass    = New-Brush "#7CBD4B"
$grassAlt = New-Brush "#6FAF40"
$dirt     = New-Brush "#8B5A2B"
$dirtAlt  = New-Brush "#7E5024"
$trunk    = New-Brush "#6B4423"
$leaf     = New-Brush "#4E9A2F"
$leafLight = New-Brush "#5DAF3A"

function Fill-Block($brush, $bx, $by, $bw, $bh) {
    $g.FillRectangle($brush, $bx * $block, $by * $block, $bw * $block, $bh * $block)
}

# Blocky sun
Fill-Block $sun 150 14 14 14

# Terrain and mountain heights (pre-computed, smooth noise via sine sums)
$terrain  = New-Object 'double[]' $cols
$mountain = New-Object 'double[]' $cols
for ($x = 0; $x -lt $cols; $x++) {
    $terrain[$x]  = 170 + 10 * [Math]::Sin($x * 0.03 + 1.2) + 6 * [Math]::Sin($x * 0.08 + 0.8) + 3 * [Math]::Sin($x * 0.2)
    $mountain[$x] = 78  + 18 * [Math]::Sin($x * 0.022 + 0.6) + 11 * [Math]::Sin($x * 0.071 + 2.1) + 5 * [Math]::Sin($x * 0.16 + 0.4)
}

# Mountains with snow caps (behind terrain)
for ($x = 0; $x -lt $cols; $x++) {
    $top = [int]$mountain[$x]
    $bot = [int]$terrain[$x]
    $snowLine = -1
    if ($mountain[$x] -lt 70) { $snowLine = $top + 6 }
    for ($y = $top; $y -le $bot; $y++) {
        $b = if ($snowLine -ge 0 -and $y -lt $snowLine) { $snow } else { $stone }
        $g.FillRectangle($b, $x * $block, $y * $block, $block, $block)
    }
}

# Terrain: grass top row + dirt below, with subtle color variation
for ($x = 0; $x -lt $cols; $x++) {
    $top = [int]$terrain[$x]
    $gb = if (($x % 2) -eq 0) { $grass } else { $grassAlt }
    $g.FillRectangle($gb, $x * $block, $top * $block, $block, $block)
    for ($y = $top + 1; $y -lt $rows; $y++) {
        $db = if ((($x + $y) % 3) -eq 0) { $dirtAlt } else { $dirt }
        $g.FillRectangle($db, $x * $block, $y * $block, $block, $block)
    }
}

# Trees
foreach ($tx in @(120, 175, 235, 300, 355)) {
    $ty = [int]$terrain[$tx]
    Fill-Block $trunk     ($tx - 1) ($ty - 4) 2 4
    Fill-Block $leaf      ($tx - 2) ($ty - 8) 6 4
    Fill-Block $leafLight ($tx - 1) ($ty - 9) 4 1
}

# Blocky clouds (in front of the mountains)
$clouds = @(
    @(60, 40, 14, 4),
    @(132, 56, 11, 3),
    @(214, 34, 16, 4),
    @(290, 50, 10, 3),
    @(344, 66, 9, 2)
)
foreach ($c in $clouds) {
    Fill-Block $cloud $c[0] $c[1] $c[2] $c[3]
    Fill-Block $cloud ($c[0] + 2) ($c[1] - 1) ($c[2] - 4) 1
    Fill-Block $cloud ($c[0] + 5) ($c[1] - 2) ($c[2] - 10) 1
}

$outDir = Join-Path $PSScriptRoot "..\XO_Launcher\XO_Launcher\Assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir "hero_background.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Generated $out ($W x $H)"

$g.Dispose()
$bmp.Dispose()
