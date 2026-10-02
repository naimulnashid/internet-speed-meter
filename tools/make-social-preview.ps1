# Renders assets/social-preview.png (1280x640) for the repository's social preview card.
#
# GitHub accepts PNG/JPG/GIF only — an SVG cannot be uploaded — so the mark is read out of
# assets/app.svg and rasterised here, which keeps this card and the app icon from drifting apart.
[CmdletBinding()]
param(
    [string]$SvgPath,
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SvgPath))    { $SvgPath    = Join-Path $root 'assets\app.svg' }
if ([string]::IsNullOrEmpty($OutputPath)) { $OutputPath = Join-Path $root 'assets\social-preview.png' }

[xml]$svg = Get-Content $SvgPath -Raw

# --- read the mark out of the SVG -------------------------------------------------------------
function Expand-SvgPath([string]$d) {
    $pts = New-Object System.Collections.ArrayList
    $cx = 0.0; $cy = 0.0
    $tokens = [regex]::Matches($d, '[MHVLZ]|-?\d+(?:\.\d+)?') | ForEach-Object { $_.Value }
    $i = 0
    while ($i -lt $tokens.Count) {
        switch ($tokens[$i]) {
            'M' { $cx = [double]$tokens[$i+1]; $cy = [double]$tokens[$i+2]; [void]$pts.Add(@($cx, $cy)); $i += 3 }
            'L' { $cx = [double]$tokens[$i+1]; $cy = [double]$tokens[$i+2]; [void]$pts.Add(@($cx, $cy)); $i += 3 }
            'H' { $cx = [double]$tokens[$i+1];                             [void]$pts.Add(@($cx, $cy)); $i += 2 }
            'V' {                             $cy = [double]$tokens[$i+1]; [void]$pts.Add(@($cx, $cy)); $i += 2 }
            default { $i += 1 }
        }
    }
    return ,$pts
}

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$stops     = @($svg.svg.defs.linearGradient.stop) | ForEach-Object { $_.'stop-color' }
$tileTop    = [System.Drawing.ColorTranslator]::FromHtml($stops[0])
$tileBottom = [System.Drawing.ColorTranslator]::FromHtml($stops[1])
$tileRadius = [double]$svg.svg.rect.rx      # on the SVG's own 32 unit grid
$paths      = @($svg.svg.path)
$download   = [System.Drawing.ColorTranslator]::FromHtml($paths[0].fill)
$upload     = [System.Drawing.ColorTranslator]::FromHtml($paths[1].fill)

# --- card -------------------------------------------------------------------------------------
$W = 1280; $H = 640
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

# Backdrop: darker than the tile so the mark reads as a separate object.
$back = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $W, $H),
    ([System.Drawing.Color]::FromArgb(255, 17, 18, 24)),
    ([System.Drawing.Color]::FromArgb(255, 10, 10, 14)))
$g.FillRectangle($back, 0, 0, $W, $H)
$back.Dispose()

# The mark, drawn from the SVG's coordinates on a 300 px tile.
$tile = 300.0
$tileX = 104.0
$tileY = ($H - $tile) / 2
$k = $tile / 32.0

$tilePath = New-RoundedPath $tileX $tileY $tile $tile ($tileRadius * $k)
$tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point ([int]$tileX), ([int]$tileY)),
    (New-Object System.Drawing.Point ([int]($tileX + $tile)), ([int]($tileY + $tile))),
    $tileTop, $tileBottom)
$g.FillPath($tileBrush, $tilePath)
$tileBrush.Dispose()

$edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(38, 255, 255, 255)), 1.5
$g.DrawPath($edge, $tilePath)
$edge.Dispose(); $tilePath.Dispose()

foreach ($p in $paths) {
    $colour = [System.Drawing.ColorTranslator]::FromHtml($p.fill)
    $pts = (Expand-SvgPath $p.d) | ForEach-Object {
        New-Object System.Drawing.PointF (($tileX + $_[0] * $k), ($tileY + $_[1] * $k))
    }
    $brush = New-Object System.Drawing.SolidBrush $colour
    $g.FillPolygon($brush, [System.Drawing.PointF[]]$pts)
    $brush.Dispose()
}

# --- type -------------------------------------------------------------------------------------
function New-Font([string]$family, [int]$px, [string]$style = 'Regular') {
    try { return New-Object System.Drawing.Font($family, $px, [System.Drawing.FontStyle]::$style, [System.Drawing.GraphicsUnit]::Pixel) }
    catch { return New-Object System.Drawing.Font('Segoe UI', $px, [System.Drawing.FontStyle]::$style, [System.Drawing.GraphicsUnit]::Pixel) }
}

# Shrink until the line fits the column: nothing may run off the card, since social previews are
# scaled down to a thumbnail and any clipped word reads as a broken image.
function Fit-Font([System.Drawing.Graphics]$gfx, [string]$text, [string]$family, [int]$px, [string]$style, [double]$maxWidth) {
    for ($size = $px; $size -gt 10; $size--) {
        $f = New-Font $family $size $style
        if ($gfx.MeasureString($text, $f).Width -le $maxWidth) { return $f }
        $f.Dispose()
    }
    return (New-Font $family 10 $style)
}

$textX  = $tileX + $tile + 76
$column = $W - $textX - 72

$titleFont   = Fit-Font $g 'Internet Speed Meter' 'Segoe UI Semibold' 66 'Regular' $column
$taglineFont = New-Font 'Segoe UI' 29
$readoutFont = New-Font 'Segoe UI Semibold' 37
$footFont    = New-Font 'Segoe UI' 24

$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 242, 242, 245))
$grey  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 150, 150, 163))
$faint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 104, 104, 118))

$g.DrawString('Internet Speed Meter', $titleFont, $white, $textX, 176)

$taglineLines = @('Live download and upload speed on the', "Windows taskbar, in the clock's own font.")
$lineY = 268
foreach ($line in $taglineLines) {
    $f = Fit-Font $g $line 'Segoe UI' 29 'Regular' $column
    $g.DrawString($line, $f, $grey, $textX, $lineY)
    $f.Dispose()
    $lineY += 40
}

# A sample readout, drawn the way the app draws it.
$rowY = 384
foreach ($row in @(@{ Down = $true; Text = '12.4 MB/s' }, @{ Down = $false; Text = '1.2 MB/s' })) {
    $colour = if ($row.Down) { $download } else { $upload }
    $brush = New-Object System.Drawing.SolidBrush $colour
    $a = 22.0
    $cy = $rowY + 20
    $tri = if ($row.Down) {
        @((New-Object System.Drawing.PointF $textX, ($cy - 8)),
          (New-Object System.Drawing.PointF ($textX + $a), ($cy - 8)),
          (New-Object System.Drawing.PointF ($textX + $a / 2), ($cy + 12)))
    } else {
        @((New-Object System.Drawing.PointF $textX, ($cy + 12)),
          (New-Object System.Drawing.PointF ($textX + $a), ($cy + 12)),
          (New-Object System.Drawing.PointF ($textX + $a / 2), ($cy - 8)))
    }
    $g.FillPolygon($brush, [System.Drawing.PointF[]]$tri)
    $g.DrawString($row.Text, $readoutFont, $white, ($textX + $a + 16), $rowY)
    $brush.Dispose()
    $rowY += 54
}

$footer = 'One dependency-free .exe  ·  no SDK, no installer'
$fitFoot = Fit-Font $g $footer 'Segoe UI' 24 'Regular' $column
$g.DrawString($footer, $fitFoot, $faint, $textX, 512)
$fitFoot.Dispose()

$g.Dispose()
$bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$size = (Get-Item $OutputPath).Length
Write-Host ("wrote {0} ({1}x{2}, {3:N0} bytes)" -f $OutputPath, $W, $H, $size)
