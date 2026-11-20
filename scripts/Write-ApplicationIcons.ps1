#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot) 'YogaITS.Tray/Assets'
$sizes = @(16,20,24,30,32,36,40,48,60,64,72,80,96,128,256)

function Draw-MonochromeIcon([Drawing.Graphics]$graphics, [int]$size, [Drawing.Color]$color) {
    # Draw from a centered 24-unit grid. At 16 px the ring
    # is still wider than one pixel and its closed outline has no seams.
    $graphics.ScaleTransform($size / 24.0, $size / 24.0)
    $ring = [Drawing.Pen]::new($color, 1.75)
    $letter = [Drawing.Pen]::new($color, 2.55)
    try {
        $letter.StartCap = $letter.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $letter.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawEllipse($ring, [single]2.4, [single]2.4, [single]19.2, [single]19.2)
        $graphics.DrawLines($letter, [Drawing.PointF[]]@(
            [Drawing.PointF]::new(7.45, 8.35),
            [Drawing.PointF]::new(12, 12.65),
            [Drawing.PointF]::new(16.55, 8.35)))
        $graphics.DrawLine($letter, [single]12, [single]12.5, [single]12, [single]17.05)
    } finally { $letter.Dispose(); $ring.Dispose() }
}

$source = [Drawing.Bitmap]::new(1536,1536,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sourceGraphics = [Drawing.Graphics]::FromImage($source)
try {
    $sourceGraphics.Clear([Drawing.Color]::Transparent)
    $sourceGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    Draw-MonochromeIcon $sourceGraphics $source.Width ([Drawing.Color]::Black)
} finally { $sourceGraphics.Dispose() }

try {
    foreach ($variant in @(@('YogaITS.Light.ico',0), @('YogaITS.Dark.ico',255))) {
        $frames = [Collections.Generic.List[byte[]]]::new()
        foreach ($size in $sizes) {
            $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $png = [IO.MemoryStream]::new()
            try {
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.Clear([Drawing.Color]::Transparent)
                $ink = [int]$variant[1]
                $graphics.DrawImage($source,0,0,$size,$size)
                # Keep the ink monochrome even at partially transparent edges.
                for ($y=0; $y -lt $size; $y++) {
                    for ($x=0; $x -lt $size; $x++) {
                        $alpha = $bitmap.GetPixel($x,$y).A
                        $bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb($alpha,$ink,$ink,$ink))
                    }
                }
                $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
                $frame = $png.ToArray()
                $frames.Add($frame)
                if ($size -eq 256 -and $ink -eq 0) {
                    [IO.File]::WriteAllBytes((Join-Path $assets 'YogaITS.Monochrome.png'), $frame)
                }
            } finally { $png.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
        }
        $stream = [IO.File]::Create((Join-Path $assets $variant[0]))
        $writer = [IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
            [uint32]$offset = 6 + 16 * $frames.Count
            for ($i=0; $i -lt $frames.Count; $i++) {
                $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
                $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
                $writer.Write([byte]0); $writer.Write([byte]0)
                $writer.Write([uint16]1); $writer.Write([uint16]32)
                $writer.Write([uint32]$frames[$i].Length); $writer.Write($offset)
                $offset += $frames[$i].Length
            }
            foreach ($frame in $frames) { $writer.Write($frame) }
        } finally { $writer.Dispose(); $stream.Dispose() }
        Write-Host "Generated $($variant[0]): $($sizes -join ', ') px."
    }
} finally { $source.Dispose() }
