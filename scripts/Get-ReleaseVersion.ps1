[CmdletBinding()]
param(
    [string] $VersionFile = (Join-Path $PSScriptRoot '..\VERSION'),
    [string] $CommitSha = $env:GITHUB_SHA,
    [Nullable[DateTimeOffset]] $CommitTime,
    [ValidateSet('native', 'liquid-glass')][string] $Flavor = 'native'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'CommitSha must be the full 40-character commit SHA.'
}
$series = (Get-Content -LiteralPath $VersionFile -Raw).Trim()
if ($series -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'VERSION must contain x.y, with no leading zeros.' }
if ($null -eq $CommitTime) {
    $timestamp = git -C (Join-Path $PSScriptRoot '..') show -s --format=%cI $CommitSha
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read commit time.' }
    $CommitTime = [DateTimeOffset]::Parse($timestamp, [Globalization.CultureInfo]::InvariantCulture)
}
$time = $CommitTime.UtcDateTime.ToString('yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture)
$shortSha = $CommitSha.Substring(0, 8).ToLowerInvariant()
$version = "$series-$time-$shortSha"
$flavorSuffix = if ($Flavor -eq 'liquid-glass') { '-lg' } else { '' }
[pscustomobject]@{
    Version = $version
    CommitSha = $CommitSha.ToLowerInvariant()
    Flavor = $Flavor
    ArtifactName = "YogaITS-$version$flavorSuffix-win-x64"
    ArchiveName = "YogaITS-$version$flavorSuffix-win-x64.zip"
}
