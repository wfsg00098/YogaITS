$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script = Join-Path $PSScriptRoot '..\scripts\Get-ReleaseVersion.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('YogaITS-Version-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$file = Join-Path $temporary 'VERSION'
$sha = 'ABCDEF1234567890ABCDEF1234567890ABCDEF12'
try {
    foreach ($series in @('5.0', '5.1', '5.10', '10.0', '0.1')) {
        Set-Content -LiteralPath $file -Value $series
        $result = & $script -VersionFile $file -CommitSha $sha -CommitTime ([DateTimeOffset]'2026-10-18T13:31:20+08:00')
        if ($result.Version -ne "$series-20261018053120-abcdef12") { throw 'Version or UTC conversion mismatch.' }
        if ($result.ArchiveName -ne "YogaITS-$($result.Version)-win-x64.zip") { throw 'Archive name mismatch.' }
        if ($result.CommitSha -ne $sha.ToLowerInvariant()) { throw 'Commit identity mismatch.' }
    }
    foreach ($series in @('', '5', '5.0.1', '05.0', '5.01', '-1.0', '5.0-extra')) {
        Set-Content -LiteralPath $file -Value $series
        $rejected = $false
        try { & $script -VersionFile $file -CommitSha $sha -CommitTime ([DateTimeOffset]::UtcNow) | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid VERSION accepted: $series" }
    }
    foreach ($invalid in @('', 'abcdef12', ('z' * 40))) {
        $rejected = $false
        try { & $script -VersionFile $file -CommitSha $invalid | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid SHA accepted.' }
    }
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
Write-Host 'PASS: version series, UTC commit time, archive names, and invalid inputs.'
