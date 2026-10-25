param([Parameter(Mandatory)][string]$PublishedDirectory)
$ErrorActionPreference = 'Stop'
$published = [IO.Path]::GetFullPath($PublishedDirectory)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('YogaITS-UiChecks-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    # Change elevation ONLY on a disposable copy. Never run the controller or
    # change the manifest, permissions, identity or hashes of the delivered app.
    Copy-Item -Path (Join-Path $published '*') -Destination $temporary -Recurse
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
