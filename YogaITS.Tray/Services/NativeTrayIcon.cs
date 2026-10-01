using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

/// <summary>
/// Minimal native Windows tray icon using Shell_NotifyIcon.
/// No third-party tray library and no polling.
/// </summary>
public sealed class NativeTrayIcon : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint CallbackMessage = WM_APP + 0x314;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NOTIFYICON_VERSION_4 = 4;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;

    private const uint WM_USER = 0x0400;
    private const uint NIN_SELECT = WM_USER + 0;
    private const uint NIN_KEYSELECT = WM_USER + 1;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;

    private const uint IconId = 1;
    private const nuint SubclassId = 0x594F4741; // "YOGA"

    private readonly nint _hwnd;
    private readonly SUBCLASSPROC _subclassProc;
    private nint _hIcon;
    private bool _added;
    private bool _version4;
    private bool _disposed;

    public event Action? MouseMove;
    public event Action? LeftClick;
    public event Action? RightClick;

    public NativeTrayIcon(nint hwnd, string iconPath, string tooltip)
    {
        if (hwnd == 0)
            throw new ArgumentException("A valid HWND is required.", nameof(hwnd));

        _hwnd = hwnd;
        _subclassProc = WindowSubclassProc;

        _hIcon = LoadImage(
            0,
            iconPath,
            IMAGE_ICON,
            0,
            0,
            LR_LOADFROMFILE | LR_DEFAULTSIZE);

        if (_hIcon == 0)
            throw new InvalidOperationException(
                $"无法加载托盘图标：{iconPath}。Win32 error={Marshal.GetLastWin32Error()}");

        if (!SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0))
            throw new InvalidOperationException(
                $"无法挂接托盘消息处理器。Win32 error={Marshal.GetLastWin32Error()}");

        NOTIFYICONDATA data = CreateData(tooltip);
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            DestroyIcon(_hIcon);
            _hIcon = 0;
            throw new InvalidOperationException(
                $"Shell_NotifyIcon(NIM_ADD) 失败。Win32 error={Marshal.GetLastWin32Error()}");
        }

        _added = true;

        // Explicitly opt into the modern callback contract recommended by Microsoft.
        // With VERSION_4:
        //   LOWORD(lParam)  = event code
        //   HIWORD(lParam)  = icon id
        //   wParam          = anchor coordinates for mouse/select/context events
        NOTIFYICONDATA versionData = CreateData(tooltip);
        versionData.uVersion = NOTIFYICON_VERSION_4;

        _version4 = Shell_NotifyIcon(
            NIM_SETVERSION,
            ref versionData);
    }

    public void SetTooltip(string text)
    {
        if (_disposed || !_added)
            return;

        NOTIFYICONDATA data = CreateData(text);
        data.uFlags = NIF_TIP;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private nint WindowSubclassProc(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData)
    {
        if (uMsg == CallbackMessage)
        {
            uint eventCode;
            bool idMatches;

            if (_version4)
            {
                ulong packed = unchecked((ulong)lParam.ToInt64());
                eventCode = (uint)(packed & 0xFFFF);
                uint iconId = (uint)((packed >> 16) & 0xFFFF);
                idMatches = iconId == IconId;
            }
            else
            {
                eventCode = unchecked((uint)lParam.ToInt64());
                idMatches =
                    unchecked((uint)wParam.ToInt64()) == IconId;
            }

            if (!idMatches)
                return 0;

            switch (eventCode)
            {
                case WM_MOUSEMOVE:
                    MouseMove?.Invoke();
                    return 0;

                case NIN_SELECT:
                case NIN_KEYSELECT:
                    LeftClick?.Invoke();
                    return 0;

                case WM_CONTEXTMENU:
                    RightClick?.Invoke();
                    return 0;

                case WM_LBUTTONUP when !_version4:
                    LeftClick?.Invoke();
                    return 0;

                case WM_RBUTTONUP when !_version4:
                    RightClick?.Invoke();
                    return 0;

                default:
                    return 0;
            }
        }

        return DefSubclassProc(
            hWnd,
            uMsg,
            wParam,
            lParam);
    }

    private NOTIFYICONDATA CreateData(string tooltip)
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            szTip = TruncateTooltip(tooltip)
        };
    }

    private static string TruncateTooltip(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // NOTIFYICONDATA.szTip is 128 WCHAR including terminator.
        return text.Length <= 127 ? text : text[..127];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_added)
        {
            NOTIFYICONDATA data = CreateData(string.Empty);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        RemoveWindowSubclass(
            _hwnd,
            _subclassProc,
            SubclassId);

        if (_hIcon != 0)
        {
            DestroyIcon(_hIcon);
            _hIcon = 0;
        }

        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SUBCLASSPROC(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(
        uint dwMessage,
        ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd,
        SUBCLASSPROC pfnSubclass,
        nuint uIdSubclass,
        nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd,
        SUBCLASSPROC pfnSubclass,
        nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam);
}
