param(
    [string]$SrcPng,
    [string]$OutIco,
    [string]$OutPng
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 1) build multi-size ico (all-PNG frames, same structure as v6)
$src = [System.Drawing.Image]::FromFile($SrcPng)
Write-Output ("source: " + $src.Width + "x" + $src.Height)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = New-Object System.Collections.Generic.List[object]
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames.Add(@($s, $ms.ToArray()))
    $ms.Dispose()
}
$src.Dispose()

$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = [int]$f[0]
    $dim = if ($s -ge 256) { 0 } else { [byte]$s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]($f[1].Length))
    $bw.Write([uint32]$offset)
    $offset += $f[1].Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f[1]) }
$bw.Dispose()
$fs.Dispose()
Write-Output ("ico written: " + (Get-Item $OutIco).Length + " bytes, frames=" + $frames.Count)

# 2) 256px in-app logo png (high quality bicubic)
$src2 = [System.Drawing.Image]::FromFile($SrcPng)
$bmp2 = New-Object System.Drawing.Bitmap(256, 256)
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g2.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$g2.DrawImage($src2, 0, 0, 256, 256)
$g2.Dispose()
$src2.Dispose()
$bmp2.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp2.Dispose()
Write-Output ("png written: " + (Get-Item $OutPng).Length + " bytes")
