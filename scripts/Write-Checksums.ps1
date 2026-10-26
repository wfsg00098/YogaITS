#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Expected a directory.' }
$manifest = Join-Path $root 'SHA256SUMS.txt'
[string[]]$paths = @(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object {
    -not [string]::Equals($_.FullName, $manifest, [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') })
if ($paths.Count -eq 0) { throw 'Cannot create checksums for an empty directory.' }
[Array]::Sort($paths, [StringComparer]::Ordinal)
$lines = foreach ($relative in $paths) {
    if ($relative.Contains("`n") -or $relative.Contains("`r")) { throw 'Unsupported filename in checksum list.' }
    $hash = (Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[IO.File]::WriteAllText($manifest, ([string]::Join("`n", [string[]]$lines) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "SHA-256: $($paths.Count) files; $manifest"
