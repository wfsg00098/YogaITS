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

    private const uint IconId = 1;
    private const nuint SubclassId = 0x594F4741; // "YOGA"

    private readonly nint _hwnd;
    private readonly SUBCLASSPROC _subclassProc;
    private readonly uint _taskbarCreatedMessage;
    private nint _hIcon;
    private ApplicationIcon? _icon;
    private string _tooltip;
    private bool _added;
    private bool _version4;
    private bool _disposed;

    public event Action? MouseMove;
    public event Action? LeftClick;
    public event Action? RightClick;

    public NativeTrayIcon(nint hwnd, string tooltip)
    {
        if (hwnd == 0)
            throw new ArgumentException("A valid HWND is required.", nameof(hwnd));

        _hwnd = hwnd;
        _subclassProc = WindowSubclassProc;
        _tooltip = TruncateTooltip(tooltip);

        // Explorer destroys the notification area when it restarts. Windows
        // broadcasts this registered message after the taskbar is recreated;
        // tray applications must add their icon again in response.
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        if (_taskbarCreatedMessage == 0)
            throw new InvalidOperationException(
                $"无法注册 TaskbarCreated 消息。Win32 error={Marshal.GetLastWin32Error()}");

        _icon = ApplicationIcon.Load(small: true);
        _hIcon = _icon.DangerousGetHandle();

        if (!SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0))
        {
            _icon.Dispose();
            throw new InvalidOperationException(
                $"无法挂接托盘消息处理器。Win32 error={Marshal.GetLastWin32Error()}");
        }

        if (!AddIconToShell())
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            _icon.Dispose();
            _hIcon = 0;
            throw new InvalidOperationException(
                $"Shell_NotifyIcon(NIM_ADD) 失败。Win32 error={Marshal.GetLastWin32Error()}");
        }
    }

    public void SetTooltip(string text)
    {
        if (_disposed)
            return;

        string tooltip = TruncateTooltip(text);
        // Repeated NIM_MODIFY calls on mouse movement can interrupt the shell's
        // hover timing. The fixed tooltip needs no update when text is unchanged.
        if (tooltip == _tooltip) return;

        if (!_added)
        {
            _tooltip = tooltip;
            return;
        }

        NOTIFYICONDATA data = CreateData(tooltip);
        // Version 4 suppresses standard tooltips unless SHOWTIP is retained.
        data.uFlags = NIF_TIP | NIF_SHOWTIP;
        if (Shell_NotifyIcon(NIM_MODIFY, ref data)) _tooltip = tooltip;
    }

    private nint WindowSubclassProc(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData)
    {
        if (uMsg == _taskbarCreatedMessage)
        {
            // The Explorer instance that owned the old notification area is gone,
            // so our previous registration no longer exists even though this
            // process and HWND are still alive. Recreate the icon and opt back
            // into NOTIFYICON_VERSION_4. No polling is required.
            _ = AddIconToShell();
            return 0;
        }

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

    private bool AddIconToShell()
    {
        if (_disposed || _hIcon == 0)
            return false;

        NOTIFYICONDATA data = CreateData(_tooltip);
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            _added = false;
            _version4 = false;
            return false;
        }

        _added = true;

        // NIM_SETVERSION must be sent after every successful NIM_ADD, including
        // after Explorer restarts, because the version setting is not persisted.
        NOTIFYICONDATA versionData = CreateData(_tooltip);
        versionData.uVersion = NOTIFYICON_VERSION_4;
        _version4 = Shell_NotifyIcon(NIM_SETVERSION, ref versionData);

        return true;
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
            _icon?.Dispose();
            _icon = null;
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
    private static extern uint RegisterWindowMessageW(string lpString);

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
