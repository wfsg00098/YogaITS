#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('native','liquid-glass')][string]$Flavor = 'native'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$catalog = Get-Content (Join-Path $root 'ThirdParty/Licenses/components.json') -Raw | ConvertFrom-Json
$components = @($catalog.components | Where-Object { $Flavor -in $_.flavors })

# Check actual published assets, not every restore dependency: optional SDK
# workloads and build-only packages must not be reported as shipped software.
$known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($component in $components) { [void]$known.Add("$($component.id)/$($component.version)") }
$publishedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
Get-ChildItem $output -File -Recurse | ForEach-Object { [void]$publishedNames.Add($_.Name) }
foreach ($manifest in Get-ChildItem $output -File -Filter 'YogaITS.*.deps.json') {
    $deps = Get-Content $manifest.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach ($target in $deps.targets.Values) {
        foreach ($entry in $target.GetEnumerator()) {
            if ($entry.Key -match '^YogaITS\.(Tray|Updater|dGPUKeepAwake|dGPURestart)/') { continue }
            $shipped = $false
            foreach ($kind in 'runtime','native','resources','runtimeTargets') {
                if (-not $entry.Value.ContainsKey($kind)) { continue }
                foreach ($asset in $entry.Value[$kind].Keys) {
                    if ($publishedNames.Contains([IO.Path]::GetFileName($asset))) { $shipped = $true }
                }
            }
            if ($shipped -and -not $known.Contains($entry.Key)) {
                throw "Published dependency has no reviewed license record: $($entry.Key). Update ThirdParty/Licenses/components.json."
            }
        }
    }
}

$destination = Join-Path $output 'ThirdPartyNotices'
[IO.Directory]::CreateDirectory($destination) | Out-Null
foreach ($name in 'LICENSE','NOTICE') { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $output $name) -Force }
Copy-Item -LiteralPath (Join-Path $root 'YogaITS.Tray/ThirdPartyNotices.txt') -Destination $destination -Force
$documents = [ordered]@{}
foreach ($component in $components) {
    foreach ($document in $component.documents) {
        $source = Join-Path $root $document.source
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $document.sha256) {
            throw "Reviewed license document changed: $($document.source). Review and update its checksum."
        }
        $name = [IO.Path]::GetFileName($source)
        if ($document.source -eq 'ThirdParty/LiquidGlassWinUI/LICENSE') { $name = 'LiquidGlassWinUI-LICENSE.txt' }
        if ($document.source -eq 'ThirdParty/LiquidGlassTabBars/LICENSE.txt') { $name = 'LiquidGlassTabBars-LICENSE.txt' }
        if ($documents.Contains($name) -and $documents[$name] -ne $document.sha256) { throw "Conflicting license filename: $name" }
        $documents[$name] = $document.sha256
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name) -Force
    }
}
@{ schemaVersion=1; flavor=$Flavor; reviewedOn=$catalog.reviewedOn;
    components=$components; documents=$documents } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'components.json') -Encoding utf8NoBOM
Write-Host "License coverage: $($components.Count) reviewed component/version records; $($documents.Count) unmodified documents ($Flavor)."
