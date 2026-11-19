#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot) 'YogaITS.Tray/Assets'
$source = [Drawing.Bitmap]::new((Join-Path $assets 'YogaITS.Monochrome.png'))
$sizes = @(16,20,24,30,32,36,40,48,60,64,72,80,96,128,256)
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
                $graphics.DrawImage($source,0,0,$size,$size)
                # One alpha mask supplies both themes; keep the ink exactly
                # monochrome even at partially transparent antialiased edges.
                $ink = [int]$variant[1]
                for ($y=0; $y -lt $size; $y++) {
                    for ($x=0; $x -lt $size; $x++) {
                        $alpha = $bitmap.GetPixel($x,$y).A
                        $bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb($alpha,$ink,$ink,$ink))
                    }
                }
                $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
                $frame = $png.ToArray()
                $frames.Add($frame)
                if ($size -eq 48) {
                    $theme = if ($ink -eq 0) { 'Light' } else { 'Dark' }
                    $panel = Join-Path (Split-Path $assets) "Services/QuickSettings/Native/Icon$theme.png"
                    [IO.File]::WriteAllBytes($panel, $frame)
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
