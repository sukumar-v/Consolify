<#
.SYNOPSIS
    Cuts every logo asset out of the master: Loungepad\Loungepad.ico, loungepad-icon.png and the
    README's header, assets\loungepad-logo.jpg.

.DESCRIPTION
    The master is assets\raw\Loungepad_logo.jpg, the full lockup at 2752x1536: the badge on the
    left, the wordmark on the right, on a dark background. The badge is cropped out, its corners
    are made transparent with a rounded mask, and it is written at every size Windows asks an
    icon for.

    The crop and the corner were measured off the master, not guessed: the ring's outer edge runs
    x 357.3-1013.4 and y 438.5-1095.6, and all four corners cross the diagonal where a circle of
    radius 148.5 does (to within 2 px; it flattens a little near the straight edges, like a
    squircle). The mask sits 2 px inside that edge, so no background survives as a dark fringe on
    a light taskbar. A new master with the badge somewhere else needs these numbers measured again.

    20, 24 and 40 px are there for the tray and small icons at 125-150% scaling; without them
    Windows scales the 16 or the 32 and the outline goes soft.

.EXAMPLE
    .\tools\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$master = Join-Path $repo 'assets\raw\Loungepad_logo.jpg'

# The badge, in the master's pixels.
$left = 357.3; $top = 438.5; $right = 1013.4; $bottom = 1095.6
$radius = 148.5
$inset = 2.0

$side = [Math]::Max($right - $left, $bottom - $top)
$cx = ($left + $right) / 2; $cy = ($top + $bottom) / 2
$crop = New-Object System.Drawing.RectangleF(($cx - $side / 2), ($cy - $side / 2), $side, $side)

function New-RoundedPath([double]$size, [double]$r, [double]$pad) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = $pad; $y = $pad; $w = $size - 2 * $pad; $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# One large render, masked with antialiasing, that every size is scaled down from. Premultiplied,
# so the scaling does not drag the transparent corners' black into the edge.
$big = 1024
$src = [System.Drawing.Image]::FromFile($master)
$scaled = New-Object System.Drawing.Bitmap($big, $big, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
$g = [System.Drawing.Graphics]::FromImage($scaled)
$g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
$g.DrawImage($src, (New-Object System.Drawing.RectangleF(0, 0, $big, $big)), $crop, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose(); $src.Dispose()

$k = $big / $side
$masked = New-Object System.Drawing.Bitmap($big, $big, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
$g = [System.Drawing.Graphics]::FromImage($masked)
$g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
$brush = New-Object System.Drawing.TextureBrush($scaled)
$path = New-RoundedPath $big ($radius * $k) ($inset * $k)
$g.FillPath($brush, $path)
$path.Dispose(); $brush.Dispose(); $g.Dispose(); $scaled.Dispose()

function Get-Png([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($masked, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# The PNG master beside the repo root, as before.
[System.IO.File]::WriteAllBytes((Join-Path $repo 'loungepad-icon.png'), (Get-Png 512))

# An .ico of PNG frames: header, one 16-byte entry per frame, then the frames.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { , (Get-Png $s) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $repo 'Loungepad\Loungepad.ico'), $ico.ToArray())
$w.Dispose(); $masked.Dispose()

# The README's header: the whole lockup, cut close round the badge and the wordmark.
$lockup = New-Object System.Drawing.Rectangle(234, 330, 2284, 875)
$lw = 1200; $lh = [int][Math]::Round($lw * $lockup.Height / $lockup.Width)
$src = [System.Drawing.Image]::FromFile($master)
$header = New-Object System.Drawing.Bitmap($lw, $lh)
$g = [System.Drawing.Graphics]::FromImage($header)
$g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
$g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $lw, $lh)), $lockup, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose(); $src.Dispose()
$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$quality = New-Object System.Drawing.Imaging.EncoderParameters(1)
$quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]90)
$header.Save((Join-Path $repo 'assets\loungepad-logo.jpg'), $jpeg, $quality)
$header.Dispose()

Write-Host "Wrote Loungepad\Loungepad.ico ($($sizes -join ', ') px), loungepad-icon.png and assets\loungepad-logo.jpg"
