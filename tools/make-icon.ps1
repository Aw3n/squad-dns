Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'src\SquadDns\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc(($X + $W - $d), $Y, $d, $d, 270, 90)
    $path.AddArc(($X + $W - $d), ($Y + $H - $d), $d, $d, 0, 90)
    $path.AddArc($X, ($Y + $H - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Get-ScaledPoints {
    param([double[]]$Normalized, [int]$Size, [double]$OffsetX, [double]$OffsetY, [double]$Scale)
    $count = $Normalized.Length / 2
    $points = New-Object System.Drawing.PointF[] $count
    for ($i = 0; $i -lt $count; $i++) {
        $px = [single](($OffsetX + $Normalized[($i * 2)] * $Scale) * $Size)
        $py = [single](($OffsetY + $Normalized[($i * 2 + 1)] * $Scale) * $Size)
        $points[$i] = New-Object System.Drawing.PointF($px, $py)
    }
    return $points
}

$shieldNorm = @(
    0.16, 0.12,
    0.84, 0.12,
    0.84, 0.50,
    0.68, 0.72,
    0.50, 0.88,
    0.32, 0.72,
    0.16, 0.50
)

$boltNorm = @(
    0.575, 0.235,
    0.375, 0.545,
    0.487, 0.545,
    0.435, 0.775,
    0.640, 0.455,
    0.525, 0.455
)

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [Math]::Round($Size * 0.03))
    $boxW = $Size - (2 * $pad)

    $bgPath = New-RoundedPath $pad $pad $boxW $boxW ([single]($Size * 0.22))

    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Rectangle($pad, $pad, $boxW, $boxW)),
        [System.Drawing.Color]::FromArgb(255, 3, 13, 14),
        [System.Drawing.Color]::FromArgb(255, 8, 40, 30),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($bgBrush, $bgPath)

    if ($Size -ge 48) {
        $gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(38, 34, 255, 156)), ([single]($Size * 0.012))
        for ($c = 1; $c -lt 6; $c++) {
            $x = [single]($pad + ($boxW * $c / 6))
            $g.DrawLine($gridPen, $x, [single]($pad + $boxW * 0.08), $x, [single]($pad + $boxW * 0.92))
        }
        $gridPen.Dispose()
    }

    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(190, 34, 255, 156)), ([single]([Math]::Max(1, $Size * 0.035)))
    $g.DrawPath($borderPen, $bgPath)
    $borderPen.Dispose()

    $insetX = [double]($pad / $Size)
    $insetWidth = [double]($boxW / $Size)

    $shieldPts = Get-ScaledPoints $shieldNorm $Size $insetX $insetX $insetWidth
    $shieldPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shieldPath.AddPolygon($shieldPts)

    $shieldBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)),
        [System.Drawing.Color]::FromArgb(96, 34, 255, 156),
        [System.Drawing.Color]::FromArgb(30, 56, 225, 255),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($shieldBrush, $shieldPath)

    $shieldPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 34, 255, 156)), ([single]([Math]::Max(1, $Size * 0.045)))
    $shieldPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($shieldPen, $shieldPath)
    $shieldPen.Dispose()
    $shieldBrush.Dispose()

    if ($Size -ge 32) {
        $glowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 228, 255, 246)), ([single]($Size * 0.10))
        $glowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $boltPtsGlow = Get-ScaledPoints $boltNorm $Size $insetX $insetX $insetWidth
        $boltGlow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $boltGlow.AddPolygon($boltPtsGlow)
        $g.DrawPath($glowPen, $boltGlow)
        $glowPen.Dispose()
        $boltGlow.Dispose()
    }

    $boltPts = Get-ScaledPoints $boltNorm $Size $insetX $insetX $insetWidth
    $boltPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $boltPath.AddPolygon($boltPts)
    $boltBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 232, 255, 248))
    $g.FillPath($boltBrush, $boltPath)
    $boltBrush.Dispose()
    $boltPath.Dispose()

    $shieldPath.Dispose()
    $bgPath.Dispose()
    $bgBrush.Dispose()
    $g.Dispose()

    return $bmp
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $Bitmap.Dispose()
    return , $bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = New-Object System.Collections.Generic.List[object]

foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $png = Get-PngBytes $bmp
    if ($size -eq 256) {
        $pngPath = Join-Path $outDir 'icon.png'
        [System.IO.File]::WriteAllBytes($pngPath, $png)
        Write-Host ("png  -> {0} ({1} bytes)" -f $pngPath, $png.Length)
    }
    $images.Add([pscustomobject]@{ Size = $size; Bytes = $png })
}

$icoPath = Join-Path $outDir 'icon.ico'
$fs = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $dim = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$image.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $bw.Write($image.Bytes)
}

$bw.Flush()
$bw.Close()
$fs.Close()

Write-Host ("ico  -> {0} ({1} bytes, {2} sizes)" -f $icoPath, ([System.IO.File]::ReadAllBytes($icoPath)).Length, $images.Count)
