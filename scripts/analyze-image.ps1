Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"

$path = "d:\Users\rmb12\source\repos\XO_Launcher\XO_Launcher\1.png"
$out  = "d:\Users\rmb12\source\repos\XO_Launcher\scripts\image_map.txt"

$bmp = New-Object System.Drawing.Bitmap($path)
$w = $bmp.Width; $h = $bmp.Height

$rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$bytes = New-Object byte[] ($stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$bmp.UnlockBits($data)

$cols = 100; $rows = 40
$stepX = [double]$w / $cols
$stepY = [double]$h / $rows

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("Image: ${w}x${h}")
for ($r = 0; $r -lt $rows; $r++) {
    $line = ""
    for ($c = 0; $c -lt $cols; $c++) {
        $x = [Math]::Min($w - 1, [int](($c + 0.5) * $stepX))
        $y = [Math]::Min($h - 1, [int](($r + 0.5) * $stepY))
        $idx = $y * $stride + $x * 4
        $R = [int]$bytes[$idx + 2]; $G = [int]$bytes[$idx + 1]; $B = [int]$bytes[$idx]
        $lum = 0.299*$R + 0.587*$G + 0.114*$B

        $ch2 = ' '
        if ($R -lt 50 -and $G -lt 50 -and $B -lt 50) { $ch2 = '.' }
        elseif ($R -gt 200 -and $G -gt 200 -and $B -gt 200) { $ch2 = '#' }
        elseif ($G -gt 110 -and $G -gt ($R * 1.3) -and $G -gt ($B * 1.1) -and $R -lt 150) { $ch2 = 'G' }
        elseif ($B -gt 120 -and $B -gt ($R * 1.15) -and $G -lt 130) { $ch2 = 'B' }
        elseif ($R -gt 140 -and $G -lt 110 -and $B -lt 110) { $ch2 = 'R' }
        elseif ($R -gt 150 -and $G -gt 90 -and $B -lt 100) { $ch2 = 'O' }
        elseif ($lum -gt 100) { $ch2 = '+' }
        elseif ($lum -gt 50) { $ch2 = ':' }
        else { $ch2 = '.' }
        $line += $ch2
    }
    [void]$sb.AppendLine(("{0,3} {1}" -f [int]($r * $stepY), $line))
}
$bmp.Dispose()
[System.IO.File]::WriteAllText($out, $sb.ToString())
Write-Output "done -> $out"
