[CmdletBinding()]
param([string] $Output)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $PSScriptRoot '..\WritingVault.Tray\Assets\WritingVault.ico'
}

function New-IconFrame([int] $Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $fill = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(222, 171, 128))
    $ink = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(39, 28, 23))
    $font = [Drawing.Font]::new('Georgia', [single]($Size * 0.58), [Drawing.FontStyle]::Regular,
        [Drawing.GraphicsUnit]::Pixel)
    $format = [Drawing.StringFormat]::new()
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([Drawing.Color]::Transparent)
        $inset = [single]($Size * 0.04)
        $diameter = [single]($Size * 0.29)
        $edge = [single]($Size - 2 * $inset)
        $shape.AddArc($inset, $inset, $diameter, $diameter, 180, 90)
        $shape.AddArc($inset + $edge - $diameter, $inset, $diameter, $diameter, 270, 90)
        $shape.AddArc($inset + $edge - $diameter, $inset + $edge - $diameter,
            $diameter, $diameter, 0, 90)
        $shape.AddArc($inset, $inset + $edge - $diameter, $diameter, $diameter, 90, 90)
        $shape.CloseFigure()
        $graphics.FillPath($fill, $shape)
        $format.Alignment = [Drawing.StringAlignment]::Center
        $format.LineAlignment = [Drawing.StringAlignment]::Center
        $graphics.DrawString('W', $font, $ink,
            [Drawing.RectangleF]::new(0, [single](-$Size * 0.015), $Size, $Size), $format)
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $format.Dispose()
        $font.Dispose()
        $ink.Dispose()
        $fill.Dispose()
        $shape.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 32, 48, 256)
$frames = @($sizes | ForEach-Object { New-IconFrame $_ })
$destination = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
$bytes = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($bytes)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = [uint32](6 + 16 * $frames.Count)
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]($size % 256))
        $writer.Write([byte]($size % 256))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write($offset)
        $offset += [uint32]$frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    [IO.File]::WriteAllBytes($destination, $bytes.ToArray())
}
finally {
    $writer.Dispose()
    $bytes.Dispose()
}
Write-Output $destination
