#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot) 'YogaITS.Tray/Assets'
$black = [IO.File]::ReadAllBytes((Join-Path $assets 'YogaITS.Light.ico'))
$white = [IO.File]::ReadAllBytes((Join-Path $assets 'YogaITS.Dark.ico'))
$sizes = @(16,20,24,30,32,36,40,48,60,64,72,80,96,128,256)
foreach ($file in @($black,$white)) {
    if ([BitConverter]::ToUInt16($file,0) -ne 0 -or [BitConverter]::ToUInt16($file,2) -ne 1 -or
        [BitConverter]::ToUInt16($file,4) -ne $sizes.Count) { throw 'Invalid themed ICO directory.' }
}
for ($i=0; $i -lt $sizes.Count; $i++) {
    $images = @()
    $streams = @()
    try {
        foreach ($file in @($black,$white)) {
            $entry = 6 + 16 * $i
            $length = [BitConverter]::ToUInt32($file,$entry+8)
            $offset = [BitConverter]::ToUInt32($file,$entry+12)
            if ($offset -lt 6+16*$sizes.Count -or $offset+$length -gt $file.Length) { throw 'Invalid themed ICO frame.' }
            $stream = [IO.MemoryStream]::new($file,[int]$offset,[int]$length,$false)
            $streams += $stream
            $images += [Drawing.Bitmap]::new($stream)
        }
        $size = $sizes[$i]
        foreach ($image in $images) {
            if ($image.Width -ne $size -or $image.Height -ne $size -or $image.GetPixel(0,0).A -ne 0) {
                throw "Invalid size or opaque background at $size px."
            }
        }
        $visible = 0
        for ($y=0; $y -lt $size; $y++) {
            for ($x=0; $x -lt $size; $x++) {
                $b = $images[0].GetPixel($x,$y); $w = $images[1].GetPixel($x,$y)
                if ($b.A -ne $w.A) { throw "Different theme alpha masks at $size px." }
                if ($b.A -gt 0) {
                    $visible++
                    if ($b.R -ne 0 -or $b.G -ne 0 -or $b.B -ne 0 -or
                        $w.R -ne 255 -or $w.G -ne 255 -or $w.B -ne 255) { throw "Non-monochrome ink at $size px." }
                }
            }
        }
        if ($visible -eq 0) { throw "Empty icon at $size px." }
    } finally {
        foreach ($image in $images) { $image.Dispose() }
        foreach ($stream in $streams) { $stream.Dispose() }
    }
}
Write-Host 'PASS: 30 transparent monochrome tray frames, identical theme alpha masks, exact black/white ink and complete 16-256 px coverage.'
