[CmdletBinding()]
param(
    [AllowEmptyCollection()]
    [string[]] $BranchNames,
    [string] $CommitSha = $env:GITHUB_SHA
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'CommitSha must be the full 40-character commit SHA.'
}
if (-not $PSBoundParameters.ContainsKey('BranchNames')) {
    # Fully qualified remote refs avoid confusing numeric branches with same-name tags.
    $BranchNames = @(git for-each-ref '--format=%(refname:strip=3)' refs/remotes/origin)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate remote branches.' }
}

$culture = [Globalization.CultureInfo]::InvariantCulture
[decimal] $highest = 0
foreach ($branch in $BranchNames) {
    if ($branch -notmatch '^\d+(?:\.\d+)?$') { continue }
    [decimal] $number = 0
    if (-not [decimal]::TryParse($branch, [Globalization.NumberStyles]::AllowDecimalPoint,
            $culture, [ref] $number)) {
        throw "Numeric branch is outside the supported range: $branch"
    }
    if ($number -gt $highest) { $highest = $number }
}

$next = ($highest + [decimal] 1).ToString('0.0###########################', $culture)
$shortSha = $CommitSha.Substring(0, 8).ToLowerInvariant()
$version = "$next-$shortSha"
[pscustomobject]@{
    Version = $version
    ArtifactName = "YogaITS-$version-win-x64"
    ArchiveName = "YogaITS-$version-win-x64.zip"
}
