using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace YogaITS.Tray.Services.Color.LenovoTcon;

internal static class ProcessComSecurity
{
    internal static int MainThreadCoInitHr { get; private set; } = int.MinValue;
    internal static int CoInitializeSecurityHr { get; private set; } = int.MinValue;
    internal static bool MainThreadCoInitOwned { get; private set; }

    internal static void InitializeEarly()
    {
        MainThreadCoInitHr = WbemNative.CoInitializeEx(IntPtr.Zero, WbemNative.COINIT_APARTMENTTHREADED);
        MainThreadCoInitOwned = MainThreadCoInitHr == WbemNative.S_OK || MainThreadCoInitHr == WbemNative.S_FALSE;

        // Exact arguments recovered from current G7+ SEColorManagerPlugin64.dll 1.0.140.11181:
        // CoInitializeSecurity(NULL, -1, NULL, NULL,
        //   RPC_C_AUTHN_LEVEL_DEFAULT, RPC_C_IMP_LEVEL_IMPERSONATE,
        //   NULL, EOAC_NONE, NULL)
        CoInitializeSecurityHr = WbemNative.CoInitializeSecurity(
            IntPtr.Zero,
            -1,
            IntPtr.Zero,
            IntPtr.Zero,
            WbemNative.RPC_C_AUTHN_LEVEL_DEFAULT,
            WbemNative.RPC_C_IMP_LEVEL_IMPERSONATE,
            IntPtr.Zero,
            WbemNative.EOAC_NONE,
            IntPtr.Zero);
    }

    internal static void Shutdown()
    {
        if (MainThreadCoInitOwned)
        {
            WbemNative.CoUninitialize();
            MainThreadCoInitOwned = false;
        }
    }

    internal static void Log(Action<string> log)
    {
        log("[process COM security bootstrap]");
        log($"Main-thread CoInitializeEx(STA) : {WbemNative.Hr(MainThreadCoInitHr)}  {WbemNative.ExplainHr(MainThreadCoInitHr)}");
        log($"CoInitializeSecurity           : {WbemNative.Hr(CoInitializeSecurityHr)}  {WbemNative.ExplainHr(CoInitializeSecurityHr)}");
        log("  args: authn=DEFAULT, imp=IMPERSONATE, capabilities=EOAC_NONE (matches Lenovo DLL)");
    }
}
