# Composes docs/collapsed.png + music.png + lyrics.png into docs/demo.png (a static
# "filmstrip" for the README; a real GIF needs a screen recorder).
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$names = 'collapsed', 'music', 'lyrics'
$W = 640
$pad = 14

$ims = @()
foreach ($n in $names) { $ims += [System.Drawing.Bitmap]::FromFile((Join-Path $root "docs\$n.png")) }

$hs = @()
foreach ($im in $ims) { $hs += [int]($im.Height * ($W / [double]$im.Width)) }
$sumH = ($hs | Measure-Object -Sum).Sum

$bw = [int]($W + $pad * 2)
$bh = [int]($pad + $sumH + $pad * $ims.Count)
$out = [System.Drawing.Bitmap]::new($bw, $bh)
$g = [System.Drawing.Graphics]::FromImage($out)
$g.Clear([System.Drawing.Color]::FromArgb(255, 10, 11, 14))
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

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
