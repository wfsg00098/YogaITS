using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace YogaITS.Tray;

// The core WinUI runtime is app-local. Glass extensions are optional: failure
// hides the switch and leaves the native UI available, without starting capture.
internal static class BundledRuntime
{
    private static readonly HashSet<string> GlassFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "LiquidGlassWinUI.dll", "CustomEffectRuntimeNative.dll", "YogaITS.DesktopCapture_lg.dll",
        "Microsoft.Graphics.Canvas.dll", "Microsoft.Graphics.Canvas.Interop.dll"
    };
    private static Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private static string? _glassError;
    private static nint _runtime;
    private static bool _modulesLocal;
    public static bool ModulesLocal => _modulesLocal;
    public static bool GlassReady => _glassError is null && _modulesLocal;
    public static void CheckLoadedModules() => _modulesLocal = ModulesAreLocal();

    public static void Initialize()
    {
        // Keep the Windows loader's normal search policy for WinUI's own window
        // creation. Pin SDK activation through the local manifest/base directory,
        // absolute runtime loading, hashes and loaded-module provenance instead.
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "runtime.lock.json")));
        if (manifest.RootElement.GetProperty("windowsAppSdk").GetString() != "2.2.0")
            throw new InvalidDataException("YogaITS requires its bundled Windows App SDK 2.2.0.");
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateObject())
            _hashes.Add(entry.Name, entry.Value.GetString()!);
        foreach (var file in _hashes.Keys)
        {
            try { Verify(file); }
            catch (Exception ex) when (GlassFiles.Contains(file)) { _glassError ??= ex.Message; }
        }
        NativeLibrary.SetDllImportResolver(typeof(BundledRuntime).Assembly, ResolvePrivate);
        AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
        {
            if (args.LoadedAssembly.GetName().Name == "LiquidGlassWinUI")
                NativeLibrary.SetDllImportResolver(args.LoadedAssembly, ResolvePrivate);
        };
        _runtime = LoadPrivate("Microsoft.WindowsAppRuntime.dll");
        var ensure = Marshal.GetDelegateForFunctionPointer<EnsureLoaded>(NativeLibrary.GetExport(_runtime, "WindowsAppRuntime_EnsureIsLoaded"));
        Marshal.ThrowExceptionForHR(ensure());
        CheckLoadedModules();
        if (!_modulesLocal) throw new InvalidOperationException("A Windows App SDK module was loaded outside this deployment.");
    }

    private static void Verify(string name)
    {
        if (Path.GetFileName(name) != name || !_hashes.TryGetValue(name, out string? expected))
            throw new InvalidDataException("Unlisted bundled runtime file: " + name);
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, name));
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bundled runtime hash mismatch: " + name);
    }

    private static nint ResolvePrivate(string name, Assembly assembly, DllImportSearchPath? path) =>
        name is "CustomEffectRuntimeNative.dll" or "YogaITS.DesktopCapture_lg.dll" ? LoadPrivate(name) : 0;

    private static nint LoadPrivate(string name)
    {
        Verify(name);
        nint handle = LoadLibraryExW(Path.Combine(AppContext.BaseDirectory, name), 0, 0x100 | 0x800);
        if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), name);
        return handle; // Deliberately retained for the process lifetime.
    }

    private static bool ModulesAreLocal()
    {
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
            if (_hashes.ContainsKey(module.ModuleName) && !string.Equals(Path.GetDirectoryName(module.FileName)?.TrimEnd('\\'),
                    AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("External module: " + module.ModuleName + " => " + module.FileName);
                return false;
            }
        return true;
    }

    public static void WriteReport()
    {
        Console.WriteLine("Framework=" + RuntimeInformation.FrameworkDescription);
        Console.WriteLine("WindowsAppSDK=2.2.0; app-local hashes=" + _hashes.Count);
        Console.WriteLine("Native runtime=" + Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.dll"));
        Console.WriteLine("Modules app-local=" + ModulesAreLocal());
        Console.WriteLine("Glass extensions=" + (GlassReady ? "available" : "unavailable: " + _glassError));
        Console.WriteLine("Headless runtime check; capture not created; border permission not requested.");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnsureLoaded();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string file, nint reserved, uint flags);
}
