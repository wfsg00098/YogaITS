param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$identity = Join-Path $OutputDirectory 'CaptureIdentity'
[IO.Directory]::CreateDirectory($identity) | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CaptureIdentity/AppxManifest.xml') -Destination (Join-Path $identity 'AppxManifest.xml') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-YogaITS.ps1') -Destination (Join-Path $OutputDirectory 'Start-YogaITS.ps1') -Force
# Package privacy UI uses the same embedded YogaITS artwork as the application.
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Icon]::new((Join-Path $PSScriptRoot 'Assets/YogaITS.ico'), 256, 256)
$image = $icon.ToBitmap()
try {
    foreach ($entry in @(@('StoreLogo.png',50), @('Square150x150Logo.png',150), @('Square44x44Logo.png',44))) {
        $size = [int]$entry[1]
        $bitmap = [Drawing.Bitmap]::new($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($image,0,0,$size,$size)
            $bitmap.Save((Join-Path $identity $entry[0]),[Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $image.Dispose(); $icon.Dispose() }
# SDK schema validation only. /nv skips semantic file-reference validation for
# sparse external content. Nothing is registered, installed, signed or launched.
$makeappx = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
$validation = Join-Path $PSScriptRoot '../obj/liquid-glass/YogaITS.Tray/CaptureIdentity_lg.msix'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($validation)) | Out-Null
& $makeappx pack /o /nv /d $identity /p $validation
if ($LASTEXITCODE -ne 0) { throw 'Capture identity manifest validation failed.' }
