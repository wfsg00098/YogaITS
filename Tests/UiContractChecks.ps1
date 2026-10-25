param([Parameter(Mandatory)][string]$PublishedDirectory)
$ErrorActionPreference = 'Stop'
$published = [IO.Path]::GetFullPath($PublishedDirectory)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('YogaITS-UiChecks-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    # Compile the same UI into an opt-in check host. Shipping assemblies contain
    # no test implementation or test dispatch entry point. All outputs are isolated.
    $root = Split-Path $PSScriptRoot
    $flavor = (Get-Content (Join-Path $published 'build-flavor.json') -Raw | ConvertFrom-Json).flavor
    $enabled = if ($flavor -eq 'liquid-glass') { 'true' } else { 'false' }
    if ($enabled -eq 'true') {
        $intermediate = Join-Path $root 'obj/liquid-glass-ui-checks/YogaITS.Tray'
        [IO.Directory]::CreateDirectory($intermediate) | Out-Null
        Copy-Item (Join-Path $root 'obj/liquid-glass/YogaITS.Tray/HdrToSdr.cso') $intermediate
    }
    dotnet publish (Join-Path $root 'YogaITS.Tray/YogaITS.Tray.csproj') -c Release -r win-x64 --self-contained false `
        "-p:EnableLiquidGlass=$enabled" -p:EnableUiChecks=true -o $temporary
    if ($LASTEXITCODE -ne 0) { throw 'UI check host build failed.' }
    if ($enabled -eq 'true') {
        $hashes = [ordered]@{}
        foreach ($file in Get-ChildItem $temporary -File -Filter '*.dll' | Sort-Object Name) {
            $hashes[$file.Name] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        }
        @{ windowsAppSdk='2.2.0'; files=$hashes } | ConvertTo-Json -Depth 4 |
            Set-Content (Join-Path $temporary 'runtime.lock.json') -Encoding utf8NoBOM
    }
    # Elevation changes apply exclusively to this disposable test host.
    $mt = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\mt.exe"
    $exe = Join-Path $temporary 'YogaITS.Tray.exe'
    $manifestPath = Join-Path $temporary 'checks.manifest'
    # Preserve the SDK-generated reg-free WinRT activation entries as well.
    & $mt -nologo "-inputresource:$exe;#1" "-out:$manifestPath"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read disposable UI check manifest.' }
    [xml]$manifest = Get-Content $manifestPath -Raw
    $manifest.SelectSingleNode('//*[local-name()="requestedExecutionLevel"]').SetAttribute('level','asInvoker')
    $manifest.Save($manifestPath)
    & $mt -nologo -manifest $manifestPath "-outputresource:$exe;#1"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare disposable UI check host.' }
    $report = Join-Path $temporary 'check.txt'
    $errors = Join-Path $temporary 'errors.txt'
    $process = Start-Process -FilePath $exe -ArgumentList '--check-ui-contract' -WorkingDirectory $temporary `
        -RedirectStandardOutput $report -RedirectStandardError $errors -PassThru
    try {
        if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw 'UI contract check timed out.' }
        $text = [IO.File]::ReadAllText($report)
        Write-Host $text
        if ($process.ExitCode -ne 0 -or -not $text.Contains('PASS: shared UI contract;')) {
            Get-Content $errors
            throw "UI contract check failed: exit=$($process.ExitCode)."
        }
    } finally { $process.Dispose() }
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
