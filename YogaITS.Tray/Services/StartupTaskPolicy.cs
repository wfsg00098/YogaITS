using System.Xml;
using System.Xml.Linq;

namespace YogaITS.Tray.Services;

internal static class StartupTaskPolicy
{
    public static bool IsEnabledForExecutable(string xml, string? executable)
    {
        if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            XElement? task = XDocument.Load(reader).Root;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            if (task?.Name != ns + "Task") return false;
            string? enabled = (string?)task.Element(ns + "Settings")?.Element(ns + "Enabled");
            if (enabled is not null && !XmlConvert.ToBoolean(enabled.Trim())) return false;
            XElement[] actions = task.Element(ns + "Actions")?.Elements().ToArray() ?? [];
            if (actions.Length != 1 || actions[0].Name != ns + "Exec") return false;
            string? command = (string?)actions[0].Element(ns + "Command");
            return NormalizePath(command) is string taskPath &&
                string.Equals(taskPath, NormalizePath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or NotSupportedException or IOException or FormatException)
        {
            return false;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        if (path.StartsWith('"') && path.EndsWith('"') && path.Length >= 2)
            path = path[1..^1];
        path = Environment.ExpandEnvironmentVariables(path);
        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
    }
}
