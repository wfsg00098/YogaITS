$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../scripts/Write-Checksums.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('YogaITS-Checksums-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $temporary '子目录')) | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $temporary 'example.zip'), 'abc', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes((Join-Path $temporary '子目录/背景 image.png'), [byte[]]@())
    & $script -Directory $temporary
    $manifest = Join-Path $temporary 'SHA256SUMS.txt'
    $expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  example.zip`n" +
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  子目录/背景 image.png`n"
    if ([IO.File]::ReadAllText($manifest) -cne $expected) { throw 'Checksums, paths, ordering, or line endings differ.' }
    $before = (Get-FileHash -LiteralPath $manifest).Hash
    & $script -Directory $temporary
    if ((Get-FileHash -LiteralPath $manifest).Hash -ne $before) { throw 'Repeated generation included the manifest itself.' }
    [IO.File]::WriteAllText((Join-Path $temporary 'example.zip'), 'changed')
    & $script -Directory $temporary
    if ((Get-FileHash -LiteralPath $manifest).Hash -eq $before) { throw 'Changed source bytes did not change the checksum list.' }
    $empty = Join-Path $temporary 'empty'
    [IO.Directory]::CreateDirectory($empty) | Out-Null
    $rejected = $false
    try { & $script -Directory $empty } catch { $rejected = $true }
    if (-not $rejected -or (Test-Path (Join-Path $empty 'SHA256SUMS.txt'))) { throw 'Empty input was published.' }
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
Write-Host 'PASS: SHA-256 known vectors, Unicode/space paths, stable regeneration, changed bytes, and empty input rejection.'
