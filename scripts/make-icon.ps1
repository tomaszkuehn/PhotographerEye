Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size)
{
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $s = $size / 256.0

    $rounded = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 48 * $s
    $rounded.AddArc(2, 2 + $r, $r * 2, $r * 2, 180, 90)
    $rounded.AddArc($size - 2 - $r * 2, 2 + $r, $r * 2, $r * 2, 270, 90)
    $rounded.AddArc($size - 2 - $r * 2, $size - 2 - $r * 2, $r * 2, $r * 2, 0, 90)
    $rounded.AddArc(2, $size - 2 - $r * 2, $r * 2, $r * 2, 90, 90)
    $rounded.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point(0, $size)),
        [System.Drawing.Color]::FromArgb(255, 52, 54, 62),
        [System.Drawing.Color]::FromArgb(255, 30, 31, 36))
    $g.FillPath($brush, $rounded)

    $border = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 90, 92, 104), [Math]::Max(2, 6 * $s))
    $g.DrawPath($border, $rounded)

    $sunBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 205, 110))
    $sunR = 30 * $s
    $g.FillEllipse($sunBrush, 150 * $s - $sunR, 62 * $s - $sunR, $sunR * 2, $sunR * 2)

    $mountain = New-Object System.Drawing.Drawing2D.GraphicsPath
    $mountain.AddPolygon(@(
        (New-Object System.Drawing.PointF([float](36 * $s), [float](186 * $s))),
        (New-Object System.Drawing.PointF([float](102 * $s), [float](96 * $s))),
        (New-Object System.Drawing.PointF([float](142 * $s), [float](150 * $s))),
        (New-Object System.Drawing.PointF([float](168 * $s), [float](116 * $s))),
        (New-Object System.Drawing.PointF([float](224 * $s), [float](186 * $s)))
    ))
    $mountain.CloseFigure()
    $mBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 118, 168, 255))
    $g.FillPath($mBrush, $mountain)

    $snow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $snow.AddPolygon(@(
        (New-Object System.Drawing.PointF([float](102 * $s), [float](96 * $s))),
        (New-Object System.Drawing.PointF([float](124 * $s), [float](127 * $s))),
        (New-Object System.Drawing.PointF([float](114 * $s), [float](134 * $s))),
        (New-Object System.Drawing.PointF([float](102 * $s), [float](124 * $s))),
        (New-Object System.Drawing.PointF([float](90 * $s), [float](136 * $s))),
        (New-Object System.Drawing.PointF([float](80 * $s), [float](127 * $s)))
    ))
    $g.FillPath($sunBrush, $snow)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$blobs = @()
$dataStreams = @()

foreach ($size in $sizes)
{
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blob = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
    $blobs += , $blob
}

$icoDir = Join-Path $PSScriptRoot "..\Assets"
New-Item -ItemType Directory -Force -Path $icoDir | Out-Null
$icoPath = Join-Path $icoDir "icon.ico"

$outStream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($outStream)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++)
{
    $size = $sizes[$i]
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$blobs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $blobs[$i].Length
}

foreach ($blob in $blobs)
{
    $bw.Write($blob)
}

$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $outStream.ToArray())
$bw.Dispose()
$outStream.Dispose()
Write-Host "OK $icoPath"