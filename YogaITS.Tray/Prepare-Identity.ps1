param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$identity = Join-Path $OutputDirectory 'CaptureIdentity'
[IO.Directory]::CreateDirectory($identity) | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CaptureIdentity/AppxManifest.xml') -Destination (Join-Path $identity 'AppxManifest.xml') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-YogaITS.ps1') -Destination (Join-Path $OutputDirectory 'Start-YogaITS.ps1') -Force
# Package privacy UI and the shell use the same ICO artwork as the application.
# Target-size and unplated variants stop Windows adding a taskbar icon plate.
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $PSScriptRoot 'Assets/YogaITS.ico'
function Write-IconAsset([string]$name, [int]$size) {
    $icon = [Drawing.Icon]::new($iconPath, $size, $size)
    $image = $icon.ToBitmap()
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($image,0,0,$size,$size)
        if ($bitmap.GetPixel(0,0).A -ne 0) { throw "Icon asset lost its transparent background: $name" }
        $bitmap.Save((Join-Path $identity $name),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose(); $image.Dispose(); $icon.Dispose() }
}
foreach ($entry in @(@('StoreLogo.png',50), @('Square150x150Logo.png',150), @('Square44x44Logo.png',44))) {
    Write-IconAsset $entry[0] ([int]$entry[1])
}
foreach ($size in 16,20,24,30,32,36,40,48,60,64,72,80,96,256) {
    $base = "Square44x44Logo.targetsize-$size"
    Write-IconAsset "$base.png" $size
    foreach ($form in 'unplated','lightunplated') {
        Copy-Item -LiteralPath (Join-Path $identity "$base.png") -Destination (Join-Path $identity "${base}_altform-$form.png") -Force
    }
}

# Keep the shell index at the sparse package root. It indexes external-relative
# CaptureIdentity paths only, leaving the application's YogaITS.Tray.pri intact.
$tools = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64"
$intermediate = Join-Path $PSScriptRoot '../obj/liquid-glass/YogaITS.Tray'
[IO.Directory]::CreateDirectory($intermediate) | Out-Null
$assetRoot = Join-Path $intermediate 'identity-assets'
if (Test-Path -LiteralPath $assetRoot) { Remove-Item -LiteralPath $assetRoot -Recurse -Force }
$assetFolder = Join-Path $assetRoot 'CaptureIdentity'
[IO.Directory]::CreateDirectory($assetFolder) | Out-Null
Get-ChildItem -LiteralPath $identity -File -Filter '*.png' | Copy-Item -Destination $assetFolder
$config = Join-Path $intermediate 'identity-priconfig.xml'
& (Join-Path $tools 'makepri.exe') createconfig /cf $config /dq lang-zh-CN /o
if ($LASTEXITCODE -ne 0) { throw 'Cannot create the shell icon resource configuration.' }
[xml]$priConfig = Get-Content -LiteralPath $config -Raw
if ($priConfig.resources.packaging) { [void]$priConfig.resources.RemoveChild($priConfig.resources.packaging) }
$index = $priConfig.resources.index
foreach ($indexer in @($index.'indexer-config')) {
    if ($indexer.type -ne 'folder') { [void]$index.RemoveChild($indexer) }
}
$priConfig.Save($config)
& (Join-Path $tools 'makepri.exe') new /pr $assetRoot /cf $config /in YogaITS /of (Join-Path $identity 'resources.pri') /o
if ($LASTEXITCODE -ne 0) { throw 'Cannot index taskbar icon variants.' }
# SDK schema validation only. /nv skips semantic file-reference validation for
# sparse external content. Nothing is registered, installed, signed or launched.
$makeappx = Join-Path $tools 'makeappx.exe'
$validation = Join-Path $PSScriptRoot '../obj/liquid-glass/YogaITS.Tray/CaptureIdentity_lg.msix'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($validation)) | Out-Null
& $makeappx pack /o /nv /d $identity /p $validation
if ($LASTEXITCODE -ne 0) { throw 'Capture identity manifest validation failed.' }
