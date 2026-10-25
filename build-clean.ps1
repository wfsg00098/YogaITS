#Requires -Version 7.0
[CmdletBinding(PositionalBinding=$false)]
param(
    [Alias('liquid-glass')][switch]$LiquidGlass,
    [switch]$Checks,
    [switch]$BuildOnly,
    [string]$OutputDirectory,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$ExtraArguments
)
$ErrorActionPreference = 'Stop'
foreach ($argument in $ExtraArguments) {
    if ($argument -eq '--liquid-glass') { $LiquidGlass = $true }
    else { throw "Unknown build argument: $argument" }
}
$root = $PSScriptRoot
$projectDir = Join-Path $root 'YogaITS.Tray'
$flavor = if ($LiquidGlass) { 'liquid-glass' } else { 'native' }
$enabled = if ($LiquidGlass) { 'true' } else { 'false' }
$name = 'YogaITS.Tray'
$output = [IO.Path]::GetFullPath($(if ($OutputDirectory) { $OutputDirectory } else { Join-Path $root "dist/$flavor" }))
# Limit destructive cleanup to dedicated artifact directories in this checkout.
$allowed = @('dist','publish') | Where-Object {
    $output.StartsWith(([IO.Path]::GetFullPath((Join-Path $root $_)) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)
}
if (-not $allowed) { throw 'Output must be a dedicated subdirectory of dist/ or publish/.' }
$executable = Join-Path $output "$name.exe"
foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
    if ([string]::Equals($process.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) { throw 'Close the application in this output directory before rebuilding.' }
}
$commit = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Unable to read build commit.' }
$release = & (Join-Path $root 'scripts/Get-ReleaseVersion.ps1') -CommitSha $commit -Flavor $flavor
$intermediate = Join-Path $root "obj/$flavor/YogaITS.Tray"
[IO.Directory]::CreateDirectory($intermediate) | Out-Null
if ($LiquidGlass) {
    $locator = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $msbuild = & $locator -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'Visual Studio 2026 C++ Build Tools (v145) are required.' }
    & $msbuild (Join-Path $projectDir 'UI/Glass/Capture/Native/DesktopCaptureInterop.vcxproj') /m /nologo /v:minimal /p:Configuration=Release /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Capture bridge build failed; previous output retained.' }
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
    $fxc = Join-Path $kits 'bin/10.0.26100.0/x64/fxc.exe'
    $includes = Join-Path $kits 'Include/10.0.26100.0/um'
    $shader = Join-Path $projectDir 'UI/Glass/Rendering/Shaders/HdrToSdr.hlsl'
    $library = Join-Path $intermediate 'HdrToSdr.fxlib'
    & $fxc /nologo /WX /O3 /T lib_4_0 /D D2D_FUNCTION /D D2D_ENTRY=main /Fl $library /I $includes $shader
    if ($LASTEXITCODE -ne 0) { throw 'HDR shader library build failed.' }
    & $fxc /nologo /WX /O3 /T ps_4_0 /D D2D_FULL_SHADER /D D2D_ENTRY=main /E main /setprivate $library /Fo (Join-Path $intermediate 'HdrToSdr.cso') /I $includes $shader
    if ($LASTEXITCODE -ne 0) { throw 'HDR shader build failed.' }
    $shaderChecker = Join-Path $root 'Tests/YogaITS.ShaderChecks/Output/x64/Release/YogaITS.LiquidGlassShaderChecks.exe'
    $glassShader = Join-Path $root 'ThirdParty/LiquidGlassWinUI/Effects/Shaders/LiquidGlass.hlsl'
    & $msbuild (Join-Path $root 'Tests/YogaITS.ShaderChecks/LiquidGlassShaderChecks.vcxproj') /m /nologo /v:minimal /p:Configuration=Release /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Shader checker build failed.' }
    & $shaderChecker $glassShader --library
    if ($LASTEXITCODE -ne 0) { throw 'Native compositor shader compilation failed; previous output retained.' }
}
if (Test-Path $output) { Remove-Item -LiteralPath $output -Recurse -Force }
# Restore and publish all projects with the same flag and isolated assets. Helpers
# must recognize the correct parent executable and updater build flavor.
foreach ($project in 'YogaITS.Tray','YogaITS.Updater','YogaITS.dGPUKeepAwake','YogaITS.dGPURestart') {
    dotnet publish (Join-Path $root "$project/$project.csproj") -c Release -r win-x64 --self-contained false `
        "-p:EnableLiquidGlass=$enabled" -p:EnableUiChecks=false "-p:InformationalVersion=$($release.Version)+$($release.CommitSha)" `
        -p:IncludeSourceRevisionInInformationalVersion=false -o $output
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project ($flavor)." }
}
foreach ($project in $name,'YogaITS.Updater','YogaITS.dGPUKeepAwake','YogaITS.dGPURestart') {
    foreach ($extension in 'exe','dll','runtimeconfig.json','deps.json') {
        if (-not (Test-Path (Join-Path $output "$project.$extension"))) { throw "Missing published file: $project.$extension" }
    }
}
if (-not (Test-Path (Join-Path $output "$name.pri"))) { throw 'Application PRI is missing.' }
if (Test-Path (Join-Path $output 'coreclr.dll')) { throw '.NET must remain framework-dependent.' }
$config = Get-Content (Join-Path $output "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
if ($config.runtimeOptions.tfm -ne 'net10.0') { throw 'Expected .NET 10.' }
# Keep runtime manifests, especially CaptureIdentity/AppxManifest.xml.
Get-ChildItem $output -Recurse -File -Filter '*.pdb' | Remove-Item -Force
@{ flavor=$flavor; executable="$name.exe"; version=$release.Version; commit=$release.CommitSha;
    dotnet='10'; windowsAppSdk=if($LiquidGlass){'2.2.0-app-local'}else{'system-runtime'} } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-flavor.json') -Encoding utf8NoBOM
if ($LiquidGlass) {
    foreach ($file in 'LiquidGlassWinUI.dll','CustomEffectRuntimeNative.dll','Microsoft.UI.Xaml.dll',
        'Microsoft.WindowsAppRuntime.dll','Microsoft.Graphics.Canvas.dll','YogaITS.DesktopCapture_lg.dll') {
        if (-not (Test-Path (Join-Path $output $file))) { throw "Missing glass file: $file" }
    }
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem $output -File -Filter '*.dll' | Sort-Object Name) { $hashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    @{ windowsAppSdk='2.2.0'; files=$hashes } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $output 'runtime.lock.json') -Encoding utf8NoBOM
    & (Join-Path $projectDir 'Prepare-Identity.ps1') -OutputDirectory $output
} else {
    $deps = [IO.File]::ReadAllText((Join-Path $output "$name.deps.json"))
    if ($deps -match 'LiquidGlassWinUI|Microsoft.Graphics.Win2D|Microsoft.Graphics.Canvas|CustomEffectRuntimeNative') { throw 'Native build references a glass dependency.' }
    foreach ($file in 'LiquidGlassWinUI.dll','CustomEffectRuntimeNative.dll','Microsoft.Graphics.Canvas.dll',
        'YogaITS.DesktopCapture_lg.dll','runtime.lock.json','CaptureIdentity','Start-YogaITS.ps1') {
        if (Test-Path (Join-Path $output $file)) { throw "Glass file leaked into native build: $file" }
    }
}
& (Join-Path $root 'scripts/Copy-DistributionNotices.ps1') -OutputDirectory $output -Flavor $flavor
if ($Checks) {
    & (Join-Path $root 'Tests/ReleaseVersionChecks.ps1')
    dotnet run --project (Join-Path $root 'Tests/YogaITS.UiPolicyChecks') -c Release "-p:EnableLiquidGlass=$enabled"
    if ($LASTEXITCODE -ne 0) { throw 'UI policy checks failed.' }
    $artifactArguments = @('--icon-executable', $executable)
    if (-not $LiquidGlass) {
        $artifactArguments += @('--updater-executable', (Join-Path $output 'YogaITS.Updater.exe'), '--gpu-restart-executable', (Join-Path $output 'YogaITS.dGPURestart.exe'))
    }
    dotnet run --project (Join-Path $root 'Tests/YogaITS.PolicyChecks') -c Release -p:EnableLiquidGlass=false -- @artifactArguments
    if ($LASTEXITCODE -ne 0) { throw 'Application policy checks failed.' }
    dotnet run --project (Join-Path $root 'Tests/YogaITS.PolicyChecks') -c Release -p:EnableLiquidGlass=false -- --check-package $output
    if ($LASTEXITCODE -ne 0) { throw 'Published update package check failed.' }
    if ($LiquidGlass) {
        & (Join-Path $root 'Tests/UiContractChecks.ps1') -PublishedDirectory $output
        & $shaderChecker $glassShader
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic shader checks failed.' }
    }
}
# No sparse registration, visible window, capture or hardware operation at build time.
# -BuildOnly remains supported for the same command shape as the reference project.
$files = @(Get-ChildItem $output -Recurse -File)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host "YogaITS $($release.Version); flavor=$flavor; output=$output"
Write-Host "Files=$($files.Count); MiB=$([Math]::Round($bytes/1MB,2)). No controller or visible UI started."
