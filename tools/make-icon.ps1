# Generates search/app.ico - the File Search Manager icon.
# Design: white magnifying glass over a "file list" on a blue rounded square.
# Rerun after design changes: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$dest = Join-Path (Split-Path $PSScriptRoot -Parent) 'search\app.ico'

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # All coordinates are designed in a 256x256 space and scaled down
    $s = $size / 256.0
    function SF([double]$v) { return [single]($v * $s) }

    # Rounded-square background with a vertical blue gradient
    $margin = SF 10; $side = SF 236; $radius = SF 52; $d = 2 * $radius
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($margin, $margin, $d, $d, 180, 90)
    $path.AddArc($margin + $side - $d, $margin, $d, $d, 270, 90)
    $path.AddArc($margin + $side - $d, $margin + $side - $d, $d, $d, 0, 90)
    $path.AddArc($margin, $margin + $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $top = [System.Drawing.Color]::FromArgb(255, 47, 128, 237)
    $bottom = [System.Drawing.Color]::FromArgb(255, 17, 53, 122)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, [single]$size)), $top, $bottom)
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()

    # File list inside the lens area: three shortening lines
    $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(215, 255, 255, 255), (SF 13))
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($linePen, (SF 78), (SF 84), (SF 138), (SF 84))
    $g.DrawLine($linePen, (SF 78), (SF 110), (SF 146), (SF 110))
    $g.DrawLine($linePen, (SF 78), (SF 136), (SF 126), (SF 136))
    $linePen.Dispose()

    # Magnifying glass: lens circle + handle
    $lensPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, (SF 20))
    $cx = 110.0; $cy = 110.0; $r = 64.0
    $g.DrawEllipse($lensPen, (SF ($cx - $r)), (SF ($cy - $r)), (SF (2 * $r)), (SF (2 * $r)))
    $lensPen.Dispose()

    $handlePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, (SF 28))
    $handlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($handlePen, (SF 158), (SF 158), (SF 212), (SF 212))
    $handlePen.Dispose()

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# Pack all sizes into one .ico (PNG-compressed entries)
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($sz in $sizes) { $images.Add((New-IconPng $sz)) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0)              # reserved
$w.Write([uint16]1)              # type: icon
$w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    if ($sz -ge 256) { $dim = 0 } else { $dim = $sz }   # 0 means 256
    $w.Write([byte]$dim)         # width
    $w.Write([byte]$dim)         # height
    $w.Write([byte]0)            # palette colors
    $w.Write([byte]0)            # reserved
    $w.Write([uint16]1)          # color planes
    $w.Write([uint16]32)         # bits per pixel
    $w.Write([uint32]$images[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()

[System.IO.File]::WriteAllBytes($dest, $out.ToArray())
Write-Host "Wrote $dest ($($out.Length) bytes, sizes: $($sizes -join ', '))"
