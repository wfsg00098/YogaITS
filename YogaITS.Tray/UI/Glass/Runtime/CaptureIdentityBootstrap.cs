using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace YogaITS.Tray.UI.Glass.Runtime;

internal static class CaptureIdentityBootstrap
{
    private const string RestartMarker = "YOGAITS_APP_LG_IDENTITY_RESTARTED";
    private const string RepairPlan = "YOGAITS_APP_LG_IDENTITY_PLAN";
    private const int InsufficientBuffer = 122;
    public static string Status { get; private set; } = "尚未检查";

    public static bool ContinueStartup(string[] args)
    {
        string directory = AppContext.BaseDirectory;
        bool restarted = CaptureIdentityFlow.SameDirectory(Environment.GetEnvironmentVariable(RestartMarker), directory);
        Environment.SetEnvironmentVariable(RestartMarker, null);
        try
        {
            bool matching = HasMatchingIdentity(directory);
            Status = matching ? "身份与当前目录一致" : restarted ? "身份修复未完成，继续运行" : "正在准备修复当前目录的身份";
            return CaptureIdentityFlow.ContinueStartup(matching, restarted,
                () => Prepare(directory), () => Handoff(directory, args), Failed);
        }
        catch (Exception ex) { Failed(ex); return true; }
    }

    private static bool HasMatchingIdentity(string directory)
    {
        uint length = 0;
        if (GetCurrentPackageFullName(ref length, null) != InsufficientBuffer) return false;
        var name = new StringBuilder(checked((int)length));
        if (GetCurrentPackageFullName(ref length, name) != 0 ||
            !name.ToString().StartsWith("YogaITS_", StringComparison.Ordinal)) return false;
        length = 0;
        if (GetCurrentPackagePath2(5, ref length, null) != InsufficientBuffer) return false;
        var external = new StringBuilder(checked((int)length));
        return GetCurrentPackagePath2(5, ref length, external) == 0 &&
            CaptureIdentityFlow.SameDirectory(external.ToString(), directory);
    }

    private static bool Prepare(string directory)
    {
        foreach (string relative in new[] { "Start-YogaITS.ps1", @"CaptureIdentity\AppxManifest.xml" })
        {
            string path = Path.Combine(directory, relative);
            if (!File.Exists(path)) throw new FileNotFoundException("Capture identity registration file is missing.", path);
        }
        return true;
    }

    private static void Handoff(string directory, string[] args)
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"),
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(RepairScript)) }) start.ArgumentList.Add(argument);
        start.Environment[RepairPlan] = JsonSerializer.Serialize(new
        {
            Directory = directory, Executable = Environment.ProcessPath,
            Arguments = CaptureIdentityFlow.CommandLine(args), ParentId = current.Id,
            ParentStartTicks = current.StartTime.ToUniversalTime().Ticks
        });
        CaptureDiagnostics.Write("Identity repair handed off; exiting before package deployment.");
        // Deployment can terminate a process holding the old identity. The
        // independent helper waits for us to exit, then owns registration AND
        // reopening the UI, including failure/timeout cases.
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Cannot start identity repair helper.");
    }

    private static void Failed(Exception ex)
    {
        Status = "身份准备失败，继续运行并保留捕获边框";
        CaptureDiagnostics.Write("Capture identity bootstrap: " + ex);
    }

    private const string RepairScript = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$plan = $env:YOGAITS_APP_LG_IDENTITY_PLAN | ConvertFrom-Json
[Environment]::SetEnvironmentVariable('YOGAITS_APP_LG_IDENTITY_PLAN',$null)
function Record([string]$message) { [Diagnostics.Trace]::WriteLine($message) }
try {
    $parent = Get-Process -Id $plan.ParentId -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $plan.ParentStartTicks) {
        if (-not $parent.WaitForExit(15000)) { throw 'Startup parent has not exited; registration skipped.' }
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $start.WorkingDirectory = $plan.Directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Windows paths cannot contain quotes; no script evaluation or shell expansion.
    $path = Join-Path $plan.Directory 'Start-YogaITS.ps1'
    $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $path + '" -RegisterOnly'
    $registration = [Diagnostics.Process]::Start($start)
    $output = $registration.StandardOutput.ReadToEndAsync()
    $errors = $registration.StandardError.ReadToEndAsync()
    try {
        if (-not $registration.WaitForExit(45000)) {
            try { $registration.Kill() } catch { }
            throw 'Registration timed out; opening UI without another identity retry.'
        }
        Record ($output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult())
        if ($registration.ExitCode -ne 0) { throw ('Registration failed: ' + $registration.ExitCode) }
        Record 'Registration completed after parent exit.'
    } finally { $registration.Dispose() }
} catch { Record $_.ToString() }
finally {
    # Even denial/missing Developer Mode/timeout must reopen the normal UI.
    [Environment]::SetEnvironmentVariable('YOGAITS_APP_LG_IDENTITY_RESTARTED',$plan.Directory)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $plan.Executable
    $start.Arguments = $plan.Arguments
    $start.WorkingDirectory = $plan.Directory
    $start.UseShellExecute = $false
    try {
        $replacement = [Diagnostics.Process]::Start($start)
        Record ('Reopened UI PID=' + $replacement.Id)
        $replacement.Dispose()
    } catch { Record ('Cannot reopen UI: ' + $_.ToString()) }
}
""";

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);
    // On the tested OS this is exported by KernelBase, not Kernel32.
    [DllImport("kernelbase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackagePath2(int type, ref uint length, StringBuilder? path);
}
