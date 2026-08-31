param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\PttDictation.App\Assets\PttDictation.ico"),
    [string]$PreviewPath = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing.Common

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [single]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $stream = [System.IO.MemoryStream]::new()

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $margin = [single][Math]::Max(1, $Size * 0.055)
        $keyBounds = [System.Drawing.RectangleF]::new(
            $margin,
            $margin,
            $Size - ($margin * 2),
            $Size - ($margin * 2))
        $keyPath = New-RoundedRectanglePath $keyBounds ([single]($Size * 0.19))
        $surfaceBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 27, 31, 39))
        $borderPen = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(255, 45, 212, 191),
            [single][Math]::Max(1, $Size * 0.045))

        $bodyBounds = [System.Drawing.RectangleF]::new(
            [single]($Size * 0.37),
            [single]($Size * 0.19),
            [single]($Size * 0.26),
            [single]($Size * 0.39))
        $bodyPath = New-RoundedRectanglePath $bodyBounds ([single]($bodyBounds.Width / 2))
        $microphoneBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 238, 242, 247))
        $microphonePen = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(255, 238, 242, 247),
            [single][Math]::Max(1.25, $Size * 0.065))
        $microphonePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $microphonePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $statusBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 171, 64))

        try {
            $graphics.FillPath($surfaceBrush, $keyPath)
            $graphics.DrawPath($borderPen, $keyPath)
            $graphics.FillPath($microphoneBrush, $bodyPath)
            $graphics.DrawArc(
                $microphonePen,
                [single]($Size * 0.28),
                [single]($Size * 0.40),
                [single]($Size * 0.44),
                [single]($Size * 0.34),
                0,
                180)
            $graphics.DrawLine(
                $microphonePen,
                [single]($Size * 0.50),
                [single]($Size * 0.73),
                [single]($Size * 0.50),
                [single]($Size * 0.82))
            $graphics.DrawLine(
                $microphonePen,
                [single]($Size * 0.38),
                [single]($Size * 0.82),
                [single]($Size * 0.62),
                [single]($Size * 0.82))

            $dotSize = [single][Math]::Max(1.5, $Size * 0.075)
            $graphics.FillEllipse(
                $statusBrush,
                [single](($Size - $dotSize) / 2),
                [single]($Size * 0.30),
                $dotSize,
                $dotSize)

            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return [byte[]]$stream.ToArray()
        }
        finally {
            $statusBrush.Dispose()
            $microphonePen.Dispose()
            $microphoneBrush.Dispose()
            $bodyPath.Dispose()
            $borderPen.Dispose()
            $surfaceBrush.Dispose()
            $keyPath.Dispose()
        }
    }
    finally {
        $stream.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Png = [byte[]](New-IconPng $size)
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
$file = [System.IO.File]::Open($resolvedOutput, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($file)

try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { [byte]0 } else { [byte]$image.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Png.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Png.Length
    }

    foreach ($image in $images) {
        $writer.Write($image.Png)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

if (-not [string]::IsNullOrWhiteSpace($PreviewPath)) {
    $resolvedPreview = [System.IO.Path]::GetFullPath($PreviewPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedPreview)) | Out-Null
    [System.IO.File]::WriteAllBytes($resolvedPreview, [byte[]](New-IconPng 256))
}

Write-Output $resolvedOutput
