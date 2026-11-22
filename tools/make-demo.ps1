# Composes docs/collapsed.png + music.png + lyrics.png into docs/demo.png (a static
# "filmstrip" for the README; a real GIF needs a screen recorder).
#
# Each render sample is transparent around the island, so crop to the visible
# content first. That keeps the filmstrip tight instead of showing the empty
# margins of the fixed 900x300 render surface.
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$names = 'collapsed', 'music', 'lyrics'
$W = 640
$pad = 16

function Get-ContentBounds([System.Drawing.Bitmap]$bmp) {
    $minX = $bmp.Width; $minY = $bmp.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $bmp.Height; $y += 2) {
        for ($x = 0; $x -lt $bmp.Width; $x += 2) {
            if ($bmp.GetPixel($x, $y).A -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { return [System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height) }

    $m = 8
    $minX = [Math]::Max(0, $minX - $m); $minY = [Math]::Max(0, $minY - $m)
    $maxX = [Math]::Min($bmp.Width - 1, $maxX + $m); $maxY = [Math]::Min($bmp.Height - 1, $maxY + $m)
    return [System.Drawing.Rectangle]::new($minX, $minY, $maxX - $minX + 1, $maxY - $minY + 1)
}

$ims = @()
foreach ($n in $names) {
    $src = [System.Drawing.Bitmap]::FromFile((Join-Path $root "docs\$n.png"))
    $r = Get-ContentBounds $src
    $crop = $src.Clone($r, $src.PixelFormat)
    $src.Dispose()
    $crop.Save((Join-Path $root "docs\$n-crop.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $ims += $crop
}

$hs = @()
foreach ($im in $ims) { $hs += [int]($im.Height * ($W / [double]$im.Width)) }
$sumH = ($hs | Measure-Object -Sum).Sum

$bw = [int]($W + $pad * 2)
$bh = [int]($pad + $sumH + $pad * $ims.Count)
$out = [System.Drawing.Bitmap]::new($bw, $bh)
$g = [System.Drawing.Graphics]::FromImage($out)
$g.Clear([System.Drawing.Color]::FromArgb(255, 9, 10, 13))
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

$y = $pad
for ($i = 0; $i -lt $ims.Count; $i++) {
    $g.DrawImage($ims[$i], $pad, $y, $W, $hs[$i])
    $y += $hs[$i] + $pad
    $ims[$i].Dispose()
}
$g.Dispose()
$out.Save((Join-Path $root 'docs\demo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "wrote docs\demo.png ($bw x $bh)"
$out.Dispose()

# Tight crops for the README's screenshot table.
foreach ($n in 'menu', 'volume', 'tint_green', 'tint_red', 'tint_white') {
    $src = [System.Drawing.Bitmap]::FromFile((Join-Path $root "docs\$n.png"))
    $r = Get-ContentBounds $src
    $crop = $src.Clone($r, $src.PixelFormat)
    $src.Dispose()
    $crop.Save((Join-Path $root "docs\$n-crop.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
}
