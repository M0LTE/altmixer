<#
  Draws the AltMixer icon (three mixer faders on a rounded square) at each Windows icon size and writes a
  multi-resolution .ico with PNG-compressed entries. Each size is drawn separately so small sizes stay crisp.

  pwsh build/make-icon.ps1 [-Out src/AltMixer/AltMixer.ico] [-PreviewDir somewhere]
#>
param(
    [string] $Out = "$PSScriptRoot/../src/AltMixer/AltMixer.ico",
    [string] $PreviewDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float] $x, [float] $y, [float] $w, [float] $h, [float] $r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int] $size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)

    # Background: rounded square with a subtle vertical gradient.
    $inset = [Math]::Max(0.5, $size * 0.03)
    $bg = New-RoundedRect $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) ($size * 0.22)
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF 0, $size),
        ([Drawing.Color]::FromArgb(255, 0x3B, 0x82, 0xF6)), ([Drawing.Color]::FromArgb(255, 0x1E, 0x40, 0xAF))
    $g.FillPath($grad, $bg)

    # Three faders. Knob heights read as a mix: low, high, middle.
    $levels = 0.62, 0.30, 0.47
    $top = $size * 0.20; $bottom = $size * 0.80
    $track = [Math]::Max(1.0, [Math]::Round($size * 0.055))
    $knobW = [Math]::Max(4.0, [Math]::Round($size * 0.20))
    $knobH = [Math]::Max(3.0, [Math]::Round($size * 0.12))
    $trackBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(150, 255, 255, 255))
    $knobBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    for ($i = 0; $i -lt 3; $i++) {
        $cx = [Math]::Round($size * (0.27 + 0.23 * $i))
        $g.FillRectangle($trackBrush, [float]($cx - $track / 2), [float]$top, [float]$track, [float]($bottom - $top))
        $ky = [Math]::Round($top + ($bottom - $top) * $levels[$i] - $knobH / 2)
        $knob = New-RoundedRect ([float]($cx - $knobW / 2)) ([float]$ky) ([float]$knobW) ([float]$knobH) ([float][Math]::Max(1, $knobH * 0.3))
        $g.FillPath($knobBrush, $knob)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewDir) { New-Item -ItemType Directory -Force $PreviewDir | Out-Null; $bmp.Save((Join-Path $PreviewDir "icon-$s.png")) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO: ICONDIR, then one ICONDIRENTRY per image, then the PNG data.
$ico = New-Object IO.MemoryStream
$w = [IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $ico.ToArray())
Write-Host "wrote $Out ($($ico.Length) bytes, sizes $($sizes -join ', '))"


