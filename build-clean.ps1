$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "YogaITS.Tray\YogaITS.Tray.csproj"
$projectDir = Split-Path $project
$dist = Join-Path $root "dist"

Write-Host "Cleaning previous build artifacts..." -ForegroundColor Cyan
Remove-Item (Join-Path $projectDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $projectDir "obj") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Restoring..." -ForegroundColor Cyan
dotnet restore $project
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing framework-dependent Release build..." -ForegroundColor Cyan
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $dist `
    --no-restore

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Unpackaged WinUI 3 needs the application's PRI in the publish directory.
$projectPri = Join-Path $dist "YogaITS.Tray.pri"
if (-not (Test-Path $projectPri)) {
    throw "Publish failed validation: YogaITS.Tray.pri is missing from dist."
}

# The app does not ship debugging symbols or XML documentation.
Get-ChildItem $dist -Recurse -File -Include *.pdb,*.xml |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "Removing intermediate bin/obj..." -ForegroundColor Cyan
Remove-Item (Join-Path $projectDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $projectDir "obj") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done. Final framework-dependent output:" -ForegroundColor Green
$files = Get-ChildItem $dist -File -Recurse
$files | Select-Object FullName, Length

$total = ($files | Measure-Object Length -Sum).Sum
Write-Host ""
Write-Host ("Files: {0}" -f $files.Count) -ForegroundColor Green
Write-Host ("Total: {0:N2} MB" -f ($total / 1MB)) -ForegroundColor Green
