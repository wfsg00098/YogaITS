using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
        return checks;
    }
}
