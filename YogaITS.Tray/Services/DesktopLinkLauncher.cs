using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

internal static class DesktopLinkLauncher
{
    public static void Open(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Expected an absolute HTTPS address.", nameof(uri));

        // Use the existing desktop's Shell.Application, not one created in our
        // elevated/sparse-package context. Explorer owns browser association and
        // the interactive user's normal launch context.
        var owned = new List<object>();
        object Own(object? value)
        {
            if (value is null) throw new InvalidOperationException("桌面 Shell 当前不可用。");
            owned.Add(value);
            return value;
        }
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
            dynamic windows = Own(Activator.CreateInstance(type));
            object location = 0;
            object locationRoot = null!;
            int handle = 0;
            dynamic desktop = Own(windows.FindWindowSW(ref location, ref locationRoot, 8, ref handle, 1));
            dynamic view = Own(desktop.Document);
            dynamic shell = Own(view.Application);
            shell.ShellExecute(uri.AbsoluteUri, "", "", "open", 1);
        }
        finally
        {
            foreach (object value in owned.AsEnumerable().Reverse())
                if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }
}
