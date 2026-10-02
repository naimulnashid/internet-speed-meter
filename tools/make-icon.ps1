# Generates assets/app.ico (the executable / Alt-Tab icon) from code, so the repo needs no
# binary art checked in by hand. PNG-compressed frames, which Windows has accepted since Vista.
[CmdletBinding()]
param(
    [string]$OutputPath
)

if ([string]::IsNullOrEmpty($OutputPath)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputPath = Join-Path (Split-Path -Parent $root) 'assets\app.ico'
}

Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 32.0
    $radius = 7 * $s

    # Rounded dark tile.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $back = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 38, 40, 52),
        [System.Drawing.Color]::FromArgb(255, 22, 23, 30))
    $g.FillPath($back, $path)
    $back.Dispose()

    # Download arrow (green, left) and upload arrow (amber, right).
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 110, 231, 168))
    $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 196, 107))

    # The arrows span y 4..28 and x 2..30 of the 32 unit grid, which is most of
    # the tile. They used to sit inside y 7..25 and x 3..29, and in the
    # notification area that read as a small icon: the tile behind them is a
    # dark gradient on a dark taskbar, so nothing of it shows, and the arrows
    # alone were 14 px tall where every neighbouring icon fills a 24 px box.
    # The pair keeps its 180 degree rotational symmetry -- the upload arrow is
    # the download arrow turned about the centre -- so only these numbers, and
    # their twins in assetspp.svg, define the mark.
    $down = @(
        (New-Object System.Drawing.PointF((5.5 * $s), (4 * $s))),
        (New-Object System.Drawing.PointF((13.5 * $s), (4 * $s))),
        (New-Object System.Drawing.PointF((13.5 * $s), (17.5 * $s))),
        (New-Object System.Drawing.PointF((17 * $s), (17.5 * $s))),
        (New-Object System.Drawing.PointF((9.5 * $s), (28 * $s))),
        (New-Object System.Drawing.PointF((2 * $s), (17.5 * $s))),
        (New-Object System.Drawing.PointF((5.5 * $s), (17.5 * $s)))
    )
    $up = @(
        (New-Object System.Drawing.PointF((18.5 * $s), (28 * $s))),
        (New-Object System.Drawing.PointF((26.5 * $s), (28 * $s))),
        (New-Object System.Drawing.PointF((26.5 * $s), (14.5 * $s))),
        (New-Object System.Drawing.PointF((30 * $s), (14.5 * $s))),
        (New-Object System.Drawing.PointF((22.5 * $s), (4 * $s))),
        (New-Object System.Drawing.PointF((15 * $s), (14.5 * $s))),
        (New-Object System.Drawing.PointF((18.5 * $s), (14.5 * $s)))
    )

    $g.FillPolygon($green, [System.Drawing.PointF[]]$down)
    $g.FillPolygon($amber, [System.Drawing.PointF[]]$up)

    $green.Dispose()
    $amber.Dispose()
    $path.Dispose()
    $g.Dispose()
    return $bmp
}

$frames = @()
foreach ($size in $sizes) {
    $bmp = New-Frame $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bmp.Dispose()
}

$outDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$fs = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([Byte]$dim)          # width
    $writer.Write([Byte]$dim)          # height
    $writer.Write([Byte]0)             # palette entries
    $writer.Write([Byte]0)             # reserved
    $writer.Write([UInt16]1)           # colour planes
    $writer.Write([UInt16]32)          # bits per pixel
    $writer.Write([UInt32]$frame.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $writer.Write($frame.Bytes)
}

$writer.Flush()
$writer.Dispose()
$fs.Dispose()

Write-Host ("wrote {0} ({1} frames)" -f $OutputPath, $frames.Count)
