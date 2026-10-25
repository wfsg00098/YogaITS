using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class PublishedAssemblyChecks
{
    public static int Run(string executable)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Published assembly: " + message);
            checks++;
        }
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "build-flavor.json")));
        bool glass = marker.RootElement.GetProperty("flavor").GetString() == "liquid-glass";
        using var input = File.OpenRead(Path.ChangeExtension(executable, ".dll"));
        using var pe = new PEReader(input);
        var metadata = pe.GetMetadataReader();
        var namespaces = metadata.TypeDefinitions.Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Namespace)).ToArray();
        Check(!namespaces.Any(name => name.StartsWith("YogaITS.Tray.Tests", StringComparison.Ordinal)),
            "Test implementations are absent from delivered assemblies.");
        Check(!namespaces.Any(name => name.StartsWith("YogaITS.Tray.Demos", StringComparison.Ordinal)),
            "Legacy demonstration components are absent from delivered assemblies.");
        Check(namespaces.Any(name => name.StartsWith("YogaITS.Tray.UI.Glass.", StringComparison.Ordinal)) == glass,
            "Glass components exist only in the glass distribution.");
        Check(!metadata.MethodDefinitions.Any(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "CheckUiContract"),
            "The application contains no UI test dispatch implementation.");
        var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
        Check(glass || !references.Any(name => name == "LiquidGlassWinUI" || name.StartsWith("Microsoft.Graphics.Canvas", StringComparison.Ordinal)),
            "Native builds have no optional glass/Win2D assembly references.");
        Check(File.ReadAllText(Path.Combine(directory, "LICENSE")).Contains("BSD 3-Clause License", StringComparison.Ordinal),
            "The project license accompanies every distribution.");
        Check(File.ReadAllText(Path.Combine(directory, "NOTICE")).Contains("wfsg00098", StringComparison.Ordinal),
            "The project attribution accompanies every distribution.");
        foreach (string name in new[] { "YogaITS.Tray", "YogaITS.Updater", "YogaITS.dGPUKeepAwake", "YogaITS.dGPURestart" })
            Check(System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(directory, name + ".exe"))
                    .LegalCopyright?.Contains("wfsg00098", StringComparison.Ordinal) == true,
                "Every YogaITS executable displays the project copyright in its version information.");
        string noticeDirectory = Path.Combine(directory, "ThirdPartyNotices");
        using var notices = JsonDocument.Parse(File.ReadAllText(Path.Combine(noticeDirectory, "components.json")));
        Check(notices.RootElement.GetProperty("flavor").GetString() == (glass ? "liquid-glass" : "native"),
            "License inventory matches the distribution flavor.");
        foreach (var document in notices.RootElement.GetProperty("documents").EnumerateObject())
        {
            Check(Path.GetFileName(document.Name) == document.Name, "License documents have flat filenames.");
            using var content = File.OpenRead(Path.Combine(noticeDirectory, document.Name));
            Check(Convert.ToHexString(SHA256.HashData(content)).Equals(document.Value.GetString(), StringComparison.OrdinalIgnoreCase),
                "Every retained upstream license/notice survives publishing unchanged.");
        }
        return checks;
    }
}
