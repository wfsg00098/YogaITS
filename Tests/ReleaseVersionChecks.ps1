$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$versionScript = Join-Path $PSScriptRoot '..\scripts\Get-ReleaseVersion.ps1'
$commitSha = 'ABCDEF1234567890ABCDEF1234567890ABCDEF12'
$cases = @(
    @{ Branches = @('main', '1.0', '2.0'); Expected = '3.0-abcdef12' },
    @{ Branches = @('main', '2.0', '1.0'); Expected = '3.0-abcdef12' },
    @{ Branches = @('main', '2.0', '10.0'); Expected = '11.0-abcdef12' },
    @{ Branches = @('main'); Expected = '1.0-abcdef12' },
    @{ Branches = @(); Expected = '1.0-abcdef12' },
    @{ Branches = @('main', 'HEAD', 'ui/review', 'backup/20.0'); Expected = '1.0-abcdef12' },
    @{ Branches = @('1.0', '2.5'); Expected = '3.5-abcdef12' },
    @{ Branches = @('1', '2'); Expected = '3.0-abcdef12' },
    @{ Branches = @('2.0', 'v20.0', '3.0-abcdef12', '2.0.1'); Expected = '3.0-abcdef12' },
    @{ Branches = @('2.0', '2,5', '-10', 'feature/30'); Expected = '3.0-abcdef12' }
)
foreach ($case in $cases) {
    $result = & $versionScript -BranchNames $case.Branches -CommitSha $commitSha
    if ($result.Version -ne $case.Expected) {
        throw "Unexpected release version: $($result.Version), expected $($case.Expected)"
    }
    if ($result.ArchiveName -ne "YogaITS-$($case.Expected)-win-x64.zip") {
        throw 'Unexpected archive name.'
    }
}
foreach ($invalidSha in @('', 'abcdef12', ('z' * 40))) {
    $rejected = $false
    try { & $versionScript -BranchNames @('2.0') -CommitSha $invalidSha | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid commit SHA was accepted.' }
}
Write-Host 'PASS: 10 version scenarios, archive names, and 3 invalid SHA checks.'
