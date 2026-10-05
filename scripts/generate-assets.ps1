# Generates placeholder MSIX package assets for XO Launcher.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\generate-assets.ps1
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "..\XO_Launcher\Assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function New-CreeperIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    $g.Clear([System.Drawing.Color]::FromArgb(255, 63, 185, 80))   # green background

    $black = [System.Drawing.Brushes]::Black
    $dark  = [System.Drawing.Color]::FromArgb(255, 13, 17, 23)
    $darkBrush = New-Object System.Drawing.SolidBrush($dark)

    # pixel grid: 3 columns x 3 rows, margin 1/8 of size
    $margin = [int]($size / 8.0)
    $cell = [int](($size - 2 * $margin) / 3.0)
    $half = [int]($cell / 2.0)

    # eyes (top-left and top-right cells)
    $g.FillRectangle($darkBrush, $margin, $margin, $cell, $half)
    $g.FillRectangle($darkBrush, $margin + 2 * $cell, $margin, $cell, $half)
    # mouth (center-bottom cell)
    $g.FillRectangle($darkBrush, $margin + $cell, $margin + 2 * $cell, $cell, $half)

    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, [string]$name) {
    $path = Join-Path $outDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Generated $path"
    $bmp.Dispose()
}

# 1:1 icons
foreach ($size in 44, 71, 150, 50) {
    $name = if ($size -eq 50) { "StoreLogo.png" } elseif ($size -eq 71) { "Square71x71Logo.png" } elseif ($size -eq 44) { "Square44x44Logo.png" } else { "Square150x150Logo.png" }
    Save-Png (New-CreeperIcon $size) $name
}

# wide 310x150
$wide = New-Object System.Drawing.Bitmap(310, 150)
$g = [System.Drawing.Graphics]::FromImage($wide)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
$g.Clear([System.Drawing.Color]::FromArgb(255, 22, 27, 34))
$brush = [System.Drawing.Brushes]::Green
$g.FillRectangle($brush, 18, 18, 114, 114)
$g.Dispose()
Save-Png $wide "Wide310x150Logo.png"

# splash 620x300
$splash = New-Object System.Drawing.Bitmap(620, 300)
$g = [System.Drawing.Graphics]::FromImage($splash)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
$g.Clear([System.Drawing.Color]::FromArgb(255, 13, 17, 23))
$g.FillRectangle($brush, 260, 90, 100, 100)
$g.Dispose()
Save-Png $splash "SplashScreen.png"

Write-Host "Done."
