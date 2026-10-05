# PowerShell script to generate XO Launcher icons from 2.png
# Generates .ico file for EXE and all required PNG sizes for packaging

Add-Type -AssemblyName System.Drawing

function Write-Log {
    param([string]$Message)
    Write-Host "[$([DateTime]::Now.ToString('HH:mm:ss'))] $Message" -ForegroundColor Cyan
}

function Get-SquareCenterCrop {
    param([System.Drawing.Image]$Image)
    $size = [Math]::Min($Image.Width, $Image.Height)
    $x = [Math]::Floor(($Image.Width - $size) / 2)
    $y = [Math]::Floor(($Image.Height - $size) / 2)
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($Image, 0, 0, [System.Drawing.Rectangle]::new($x, $y, $size, $size), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return $bmp
}

function Resize-Image {
    param([System.Drawing.Image]$Image, [int]$Width, [int]$Height)
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($Image, 0, 0, $Width, $Height)
    $g.Dispose()
    return $bmp
}

function Resize-Square {
    param([System.Drawing.Image]$Image, [int]$Size)
    return Resize-Image -Image $Image -Width $Size -Height $Size
}

function New-WideTile {
    param([System.Drawing.Image]$Image, [int]$Width, [int]$Height, [string]$BgColor)
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # Fill background
    $brush = New-Object System.Drawing.SolidBrush(([System.Drawing.Color]::FromArgb(0xFF, 0x7C, 0xBD, 0x4B)))
    $g.FillRectangle($brush, 0, 0, $Width, $Height)
    $brush.Dispose()

    # Draw centered square icon
    $iconSize = [Math]::Min($Width, $Height) - 20
    $iconSize = [Math]::Max($iconSize, 64)  # Minimum size
    $iconX = [Math]::Floor(($Width - $iconSize) / 2)
    $iconY = [Math]::Floor(($Height - $iconSize) / 2)
    $g.DrawImage($Image, $iconX, $iconY, $iconSize, $iconSize)
    $g.Dispose()
    return $bmp
}

function Save-Png {
    param([System.Drawing.Image]$Image, [string]$Path)
    $dir = Split-Path $Path -Parent
    if ($dir -and !(Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $Image.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Log "  Saved: $Path"
}

# ============================================================
# LOAD SOURCE IMAGE
# ============================================================
$sourcePath = "2.png"
if (!(Test-Path $sourcePath)) {
    Write-Error "Source file not found: $sourcePath"
    exit 1
}

Write-Log "Loading source image: $sourcePath"
$sourceImg = [System.Drawing.Image]::FromFile($sourcePath)
Write-Log "Source dimensions: $($sourceImg.Width) x $($sourceImg.Height)"
Write-Log "Pixel format: $($sourceImg.PixelFormat)"

# ============================================================
# 1. GENERATE ICO FILE (multi-size PNG inside ICO)
# ============================================================
Write-Log "=== 1. Generating ICO file ==="
$icoSizes = @(16, 24, 32, 48, 64, 96, 128, 256)

$icoPngData = @()
$icoDataSize = 0
$icoHeaderSize = 6 + ($icoSizes.Count * 16)

foreach ($size in $icoSizes) {
    $square = Get-SquareCenterCrop -Image $sourceImg
    $resized = Resize-Square -Image $square -Size $size
    $square.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngData = $ms.ToArray()
    $ms.Dispose()
    $resized.Dispose()

    $icoPngData += @{
        Size = $size
        Data = $pngData
        Offset = $icoHeaderSize + $icoDataSize
    }
    $icoDataSize += $pngData.Length
}

# Write ICO file
$icoPath = "Assets\XO_Launcher.ico"
$fs = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($fs)

# ICO header
$writer.Write([UInt16]0)                # Reserved
$writer.Write([UInt16]1)                # Type: 1 = ICO
$writer.Write([UInt16]$icoSizes.Count)  # Number of images

# Directory entries
$currentOffset = $icoHeaderSize
foreach ($entry in $icoPngData) {
    $w = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
    $h = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
    $writer.Write([Byte]$w)           # Width (0 = 256)
    $writer.Write([Byte]$h)           # Height (0 = 256)
    $writer.Write([Byte]0)            # Color palette
    $writer.Write([Byte]0)            # Reserved
    $writer.Write([UInt16]1)          # Color planes
    $writer.Write([UInt16]32)         # Bits per pixel
    $writer.Write([UInt32]$entry.Data.Length)  # Image size
    $writer.Write([UInt32]$currentOffset)      # Offset
    $currentOffset += $entry.Data.Length
}

# Image data
foreach ($entry in $icoPngData) {
    $writer.Write($entry.Data)
}

$writer.Close()
$fs.Close()
Write-Log "  Created ICO: $icoPath ($($icoSizes.Count) sizes)" -ForegroundColor Green

# ============================================================
# 2. GENERATE PACKAGE ICONS (XO_Launcher (Package)\Images\)
# ============================================================
Write-Log "=== 2. Generating package icons ==="

$squareImg = Get-SquareCenterCrop -Image $sourceImg

$packageIcons = @(
    @{ Name = "StoreLogo.png"; Width = 50; Height = 50; Square = $true }
    @{ Name = "Square44x44Logo.scale-200.png"; Width = 88; Height = 88; Square = $true }
    @{ Name = "Square44x44Logo.targetsize-24_altform-unplated.png"; Width = 24; Height = 24; Square = $true }
    @{ Name = "Square150x150Logo.scale-200.png"; Width = 300; Height = 300; Square = $true }
    @{ Name = "Wide310x150Logo.scale-200.png"; Width = 620; Height = 300; Square = $false }
    @{ Name = "SplashScreen.scale-200.png"; Width = 1240; Height = 600; Square = $false }
    @{ Name = "LockScreenLogo.scale-200.png"; Width = 48; Height = 48; Square = $true }
)

$packageImagesDir = "XO_Launcher (Package)\Images"
if (!(Test-Path $packageImagesDir)) {
    New-Item -ItemType Directory -Path $packageImagesDir -Force | Out-Null
}

foreach ($icon in $packageIcons) {
    if ($icon.Square) {
        $resized = Resize-Square -Image $squareImg -Size $icon.Width
    } else {
        $resized = New-WideTile -Image $squareImg -Width $icon.Width -Height $icon.Height
    }
    $path = Join-Path $packageImagesDir $icon.Name
    Save-Png -Image $resized -Path $path
    $resized.Dispose()
}

$squareImg.Dispose()

# ============================================================
# 3. GENERATE ROOT ASSETS ICONS
# ============================================================
Write-Log "=== 3. Generating root Assets icons ==="
$squareImg2 = Get-SquareCenterCrop -Image $sourceImg

$rootAssets = @(
    @{ Name = "StoreLogo.png"; Width = 50; Height = 50; Square = $true }
    @{ Name = "Square44x44Logo.png"; Width = 44; Height = 44; Square = $true }
    @{ Name = "Square71x71Logo.png"; Width = 71; Height = 71; Square = $true }
    @{ Name = "Square150x150Logo.png"; Width = 150; Height = 150; Square = $true }
    @{ Name = "Wide310x150Logo.png"; Width = 310; Height = 150; Square = $false }
    @{ Name = "SplashScreen.png"; Width = 620; Height = 300; Square = $false }
)

$rootAssetsDir = "Assets"
if (!(Test-Path $rootAssetsDir)) {
    New-Item -ItemType Directory -Path $rootAssetsDir -Force | Out-Null
}

foreach ($icon in $rootAssets) {
    if ($icon.Square) {
        $resized = Resize-Square -Image $squareImg2 -Size $icon.Width
    } else {
        $resized = New-WideTile -Image $squareImg2 -Width $icon.Width -Height $icon.Height
    }
    $path = Join-Path $rootAssetsDir $icon.Name
    Save-Png -Image $resized -Path $path
    $resized.Dispose()
}

$squareImg2.Dispose()
$sourceImg.Dispose()

Write-Log "=== All icons generated successfully! ===" -ForegroundColor Green