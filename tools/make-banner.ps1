# Builds docs/banner.png - the wide hero image used at the top of the README.
# Everything is composed locally from the render samples and the bundled font;
# no third-party artwork is embedded.
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$W = 1280
$H = 640

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
    $m = 10
    $minX = [Math]::Max(0, $minX - $m); $minY = [Math]::Max(0, $minY - $m)
    $maxX = [Math]::Min($bmp.Width - 1, $maxX + $m); $maxY = [Math]::Min($bmp.Height - 1, $maxY + $m)
    return [System.Drawing.Rectangle]::new($minX, $minY, $maxX - $minX + 1, $maxY - $minY + 1)
}

function Load-Cropped([string]$path) {
    $src = [System.Drawing.Bitmap]::FromFile($path)
    $r = Get-ContentBounds $src
    $crop = $src.Clone($r, $src.PixelFormat)
    $src.Dispose()
    return $crop
}

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$bmp = [System.Drawing.Bitmap]::new($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

# backdrop
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    [System.Drawing.Rectangle]::new(0, 0, $W, $H),
    [System.Drawing.Color]::FromArgb(255, 7, 9, 12),
    [System.Drawing.Color]::FromArgb(255, 12, 20, 16), 35.0)
$g.FillRectangle($bg, 0, 0, $W, $H)
$bg.Dispose()

# album-green ambience
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(-220, 300, 900, 700)
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(70, 46, 160, 78)
$pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 46, 160, 78))
$g.FillPath($pgb, $glowPath)
$pgb.Dispose(); $glowPath.Dispose()

$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(620, -260, 800, 620)
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(46, 244, 217, 138)
$pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 244, 217, 138))
$g.FillPath($pgb, $glowPath)
$pgb.Dispose(); $glowPath.Dispose()

# font
$pfc = New-Object System.Drawing.Text.PrivateFontCollection
$pfc.AddFontFile((Join-Path $root 'Assets\Fonts\PlusJakartaSans-Bold.ttf'))
$pfc.AddFontFile((Join-Path $root 'Assets\Fonts\PlusJakartaSans-SemiBold.ttf'))
$pfc.AddFontFile((Join-Path $root 'Assets\Fonts\PlusJakartaSans-Medium.ttf'))
$family = $pfc.Families[0]
$fTitle = New-Object System.Drawing.Font($family, 66, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fTag = New-Object System.Drawing.Font($family, 24, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fBody = New-Object System.Drawing.Font($family, 17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fPill = New-Object System.Drawing.Font($family, 12, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)

$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 248, 249, 251))
$muted = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 174, 184, 191))
$faint = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 126, 137, 146))

# logo
$logo = [System.Drawing.Bitmap]::FromFile((Join-Path $root 'docs\logo.png'))
$g.DrawImage($logo, 80, 92, 78, 78)
$logo.Dispose()

$g.DrawString('Float Island', $fTitle, $white, 176, 82)
$g.DrawString('A dynamic island for Spotify on Windows 11', $fTag, $muted, 180, 166)
$g.DrawString('Synced lyrics, album tint, audio-reactive visuals - and zero polling.', $fBody, $faint, 182, 204)

# feature pills
$pills = @('WINDOWS 11', '.NET 10', 'MIT LICENSE', 'SPOTIFY')
$px = 182.0
$py = 250.0
foreach ($text in $pills) {
    $sz = $g.MeasureString($text, $fPill)
    $pw = $sz.Width + 26
    $ph = 28.0
    $rp = New-RoundedRect $px $py $pw $ph 14
    $pb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 22, 30, 25))
    $g.FillPath($pb, $rp)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 55, 78, 62), 1)
    $g.DrawPath($pen, $rp)
    $tb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 196, 208, 200))
    $g.DrawString($text, $fPill, $tb, $px + 13, $py + 7)
    $pb.Dispose(); $pen.Dispose(); $tb.Dispose(); $rp.Dispose()
    $px += $pw + 10
}

# screenshot montage
$collapsed = Load-Cropped (Join-Path $root 'docs\collapsed.png')
$music = Load-Cropped (Join-Path $root 'docs\music.png')

$cw = 470.0
$ch = $collapsed.Height * ($cw / $collapsed.Width)
$g.DrawImage($collapsed, 600, 238, $cw, $ch)
$collapsed.Dispose()

$mw = 640.0
$mh = $music.Height * ($mw / $music.Width)
$g.DrawImage($music, 600, 300, $mw, $mh)
$music.Dispose()

# hairline at the bottom edge
$linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 120, 190, 145), 1)
$g.DrawLine($linePen, 0, $H - 1, $W, $H - 1)
$linePen.Dispose()

$g.Dispose()
$bmp.Save((Join-Path $root 'docs\banner.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "wrote docs\banner.png ($W x $H)"
$bmp.Dispose()
