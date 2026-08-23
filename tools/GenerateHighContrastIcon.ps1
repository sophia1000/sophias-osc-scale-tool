Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
$pngPath = Join-Path $assets 'vrc-height-osc-icon.png'
$icoPath = Join-Path $assets 'vrc-height-osc-icon.ico'

function New-IconBitmap([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = $size / 256.0
        $outer = [System.Drawing.RectangleF]::new(10 * $scale, 10 * $scale, 236 * $scale, 236 * $scale)
        $inner = [System.Drawing.RectangleF]::new(26 * $scale, 26 * $scale, 204 * $scale, 204 * $scale)

        $cyan = [System.Drawing.Color]::FromArgb(255, 0, 224, 255)
        $blue = [System.Drawing.Color]::FromArgb(255, 17, 74, 149)
        $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

        $graphics.FillEllipse([System.Drawing.SolidBrush]::new($cyan), $outer)
        $graphics.FillEllipse([System.Drawing.SolidBrush]::new($blue), $inner)

        $rulerPen = [System.Drawing.Pen]::new($white, 13 * $scale)
        $rulerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $rulerPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($rulerPen, 74 * $scale, 60 * $scale, 74 * $scale, 196 * $scale)

        foreach ($tick in 76, 108, 140, 172) {
            $length = if ($tick % 64 -eq 12) { 25 } else { 16 }
            $graphics.DrawLine($rulerPen, 74 * $scale, $tick * $scale, (74 + $length) * $scale, $tick * $scale)
        }

        $arrowPen = [System.Drawing.Pen]::new($white, 21 * $scale)
        $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arrowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawLine($arrowPen, 159 * $scale, 176 * $scale, 159 * $scale, 88 * $scale)
        $graphics.DrawLine($arrowPen, 159 * $scale, 72 * $scale, 128 * $scale, 103 * $scale)
        $graphics.DrawLine($arrowPen, 159 * $scale, 72 * $scale, 190 * $scale, 103 * $scale)
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

$iconSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = [System.Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $iconSizes) {
        $bitmap = New-IconBitmap $size
        try {
            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $pngs.Add($stream.ToArray())
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    [System.IO.File]::WriteAllBytes($pngPath, $pngs[$pngs.Count - 1])

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$iconSizes.Count)

        $offset = 6 + (16 * $iconSizes.Count)
        for ($index = 0; $index -lt $iconSizes.Count; $index++) {
            $size = $iconSizes[$index]
            $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$pngs[$index].Length)
            $writer.Write([UInt32]$offset)
            $offset += $pngs[$index].Length
        }

        foreach ($png in $pngs) {
            $writer.Write($png)
        }

        [System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}
finally {
    $pngs.Clear()
}
