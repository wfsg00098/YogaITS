#Requires -Version 7.0
param([Parameter(Mandatory)][string]$PublishedDirectory)
$ErrorActionPreference = 'Stop'
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$identity = Join-Path $published 'CaptureIdentity'
$dump = Join-Path ([IO.Path]::GetTempPath()) ('YogaITS-Icons-' + [guid]::NewGuid().ToString('N') + '.xml')
try {
    $makepri = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\makepri.exe"
    & $makepri dump /if (Join-Path $identity 'resources.pri') /of $dump /o > $null
    if ($LASTEXITCODE -ne 0) { throw 'Shell icon resource index is unreadable.' }
    [xml]$resources = [IO.File]::ReadAllText($dump)
    $logo = $resources.SelectSingleNode('//NamedResource[@uri="ms-resource://YogaITS/Files/CaptureIdentity/Square44x44Logo.png"]')
    if (-not $logo) { throw 'Shell icon resource URI does not match the manifest path.' }
    [xml]$manifest = [IO.File]::ReadAllText((Join-Path $identity 'AppxManifest.xml'))
    if ([version]$manifest.Package.Identity.Version -le [version]'1.0.0.0') { throw 'Shell resource revision was not advanced.' }
    Add-Type -AssemblyName System.Drawing
    foreach ($size in 16,20,24,30,32,36,40,48,60,64,72,80,96,256) {
        $baseName = "Square44x44Logo.targetsize-$size.png"
        $path = Join-Path $identity $baseName
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $bitmap = [Drawing.Bitmap]::new($path)
        try {
            if ($bitmap.Width -ne $size -or $bitmap.Height -ne $size) { throw "Incorrect icon size: $baseName" }
            if ($bitmap.GetPixel(0,0).A -ne 0) { throw "Opaque background in $baseName" }
            $visible = $false
            for ($y=0; $y -lt $size -and -not $visible; $y++) {
                for ($x=0; $x -lt $size; $x++) {
                    if ($bitmap.GetPixel($x,$y).A -gt 0) { $visible=$true; break }
                }
            }
            if (-not $visible) { throw "Empty icon: $baseName" }
        } finally { $bitmap.Dispose() }
        foreach ($form in '','unplated','lightunplated') {
            $suffix = if ($form) { "_altform-$form" } else { '' }
            $name = "Square44x44Logo.targetsize-$size$suffix.png"
            $candidate = @($logo.Candidate | Where-Object { $_.Value -eq "CaptureIdentity\$name" })
            if ($candidate.Count -ne 1 -or $candidate[0].qualifiers -notmatch "TargetSize-$size(?:,|$)" -or
                ($form -and $candidate[0].qualifiers -notmatch "AlternateForm-$form(?:,|$)")) {
                throw "Unindexed taskbar icon variant: $name"
            }
            if ((Get-FileHash -LiteralPath (Join-Path $identity $name) -Algorithm SHA256).Hash -ne $hash) {
                throw "Taskbar theme variant changed the transparent artwork: $name"
            }
        }
    }
    Write-Host 'PASS: 42 transparent taskbar icon variants, exact resource URIs/qualifiers, and identity resource revision. No registration or UI started.'
} finally { if (Test-Path -LiteralPath $dump) { Remove-Item -LiteralPath $dump -Force } }
