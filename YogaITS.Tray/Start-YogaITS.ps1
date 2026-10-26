param([switch]$RegisterOnly, [string[]]$ApplicationArguments = @())
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Published beside the EXE; -RegisterOnly prepares identity without launching UI.
$executable = Join-Path $PSScriptRoot 'YogaITS.Tray.exe'
$manifestPath = Join-Path $PSScriptRoot 'CaptureIdentity/AppxManifest.xml'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run the published Start-YogaITS.ps1 beside the YogaITS EXE.' }
function Register-CaptureIdentity {
    $manifest = [xml][IO.File]::ReadAllText($manifestPath)
    $identityName = [string]$manifest.Package.Identity.Name
    if ($identityName -ne 'YogaITS' -or $manifest.Package.Identity.Publisher -ne 'CN=YogaITS') { throw 'Unexpected YogaITS identity.' }
    $existing = Get-AppxPackage -Name $identityName
    $manifestDirectory = [IO.Path]::GetDirectoryName($manifestPath)
    if (-not $existing -or $existing.Version -ne [version]$manifest.Package.Identity.Version -or
        -not [string]::Equals($existing.InstallLocation.TrimEnd('\'),$manifestDirectory.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) {
        $development = Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' 'AllowDevelopmentWithoutDevLicense' -ErrorAction SilentlyContinue
        if ($development -ne 1) { throw 'The loose capture identity requires Windows Developer Mode. No system settings were changed.' }
        try {
            Add-AppxPackage -Register $manifestPath -ExternalLocation $PSScriptRoot -ErrorAction Stop
        } catch {
            # Windows cannot move an existing sparse external location in place.
            # Handle ONLY that exact failure and ONLY our loose development identity.
            if (-not $existing -or -not $existing.IsDevelopmentMode -or $_.ToString() -notmatch '0x80073D0B') { throw }
            $oldManifest = Join-Path $existing.InstallLocation 'AppxManifest.xml'
            $oldExternal = [IO.Path]::GetDirectoryName($existing.InstallLocation.TrimEnd('\'))
            if ($existing.Publisher -ne $manifest.Package.Identity.Publisher) { throw }
            $canRestore = Test-Path -LiteralPath $oldManifest
            if ($canRestore) {
                $oldIdentity = [xml][IO.File]::ReadAllText($oldManifest)
                $canRestore = Test-Path -LiteralPath (Join-Path $oldExternal ([string]$oldIdentity.Package.Applications.Application.Executable))
            }
            if ($canRestore) {
                $oldIdentity = [xml][IO.File]::ReadAllText($oldManifest)
                if ($oldIdentity.Package.Identity.Name -ne $identityName -or
                    $oldIdentity.Package.Identity.Publisher -ne $manifest.Package.Identity.Publisher) { throw }
            }
            Remove-AppxPackage -Package $existing.PackageFullName -PreserveApplicationData -ErrorAction Stop
            try {
                Add-AppxPackage -Register $manifestPath -ExternalLocation $PSScriptRoot -ErrorAction Stop
            } catch {
                $failure = $_
                if ($canRestore) {
                    try { Add-AppxPackage -Register $oldManifest -ExternalLocation $oldExternal -ErrorAction Stop }
                    catch { Write-Warning "Could not restore previous YogaITS identity: $_" }
                } else { Write-Warning 'Previous YogaITS folder no longer exists; no working old location to restore.' }
                throw $failure
            }
        }
    }
    $registered = Get-AppxPackage -Name $identityName
    if (-not $registered) { throw 'YogaITS identity registration was not confirmed.' }
    Write-Host "LG capture identity: $($registered.PackageFamilyName)"
}
if ($RegisterOnly) { Register-CaptureIdentity; return }
try { Register-CaptureIdentity } catch { Write-Warning "Identity registration unavailable; continuing with available rendering: $_" }
if (-not $RegisterOnly) {
    $options = @{ FilePath=$executable; WorkingDirectory=$PSScriptRoot; PassThru=$true }
    if ($ApplicationArguments.Count) { $options.ArgumentList=$ApplicationArguments }
    Start-Process @options | Select-Object Id,ProcessName,Path
}
