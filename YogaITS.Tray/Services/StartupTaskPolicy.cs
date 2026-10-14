using System.Xml;
using System.Xml.Linq;

namespace YogaITS.Tray.Services;

internal static class StartupTaskPolicy
{
    public static string CreateDefinition(string taskName, string executable, string userSid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo", new XElement(ns + "URI", @"\" + taskName)),
                new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger",
                    new XElement(ns + "Enabled", true),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "UserId", userSid))),
                new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", userSid),
                    new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", false),
                    new XElement(ns + "StopIfGoingOnBatteries", false),
                    new XElement(ns + "RunOnlyIfIdle", false),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", false),
                    new XElement(ns + "IdleSettings", new XElement(ns + "StopOnIdleEnd", false)),
                    new XElement(ns + "AllowStartOnDemand", true),
                    new XElement(ns + "Enabled", true),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S")),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec", new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", "--startup"),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable)))))).ToString();
    }

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
