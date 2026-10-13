using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services.Color.LenovoTcon;

internal static class WbemNative
{
    internal const int WBEM_FLAG_RETURN_IMMEDIATELY = 0x10;
    internal const int WBEM_FLAG_FORWARD_ONLY = 0x20;
    internal const int WBEM_INFINITE = unchecked((int)0xFFFFFFFF);

    internal const uint RPC_C_AUTHN_WINNT = 10;
    internal const uint RPC_C_AUTHZ_NONE = 0;
    internal const uint RPC_C_AUTHN_LEVEL_DEFAULT = 0;
    internal const uint RPC_C_AUTHN_LEVEL_CALL = 3;
    internal const uint RPC_C_IMP_LEVEL_IMPERSONATE = 3;
    internal const uint EOAC_NONE = 0;

    internal const uint COINIT_MULTITHREADED = 0x0;
    internal const uint COINIT_APARTMENTTHREADED = 0x2;
    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    internal const int RPC_E_TOO_LATE = unchecked((int)0x80010119);
    internal const int WBEM_E_NOT_FOUND = unchecked((int)0x80041002);
    internal const int WBEM_E_ACCESS_DENIED = unchecked((int)0x80041003);

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeSecurity(
        IntPtr pSecDesc,
        int cAuthSvc,
        IntPtr asAuthSvc,
        IntPtr pReserved1,
        uint dwAuthnLevel,
        uint dwImpLevel,
        IntPtr pAuthList,
        uint dwCapabilities,
        IntPtr pReserved3);

    [DllImport("ole32.dll")]
    internal static extern int CoSetProxyBlanket(
        IntPtr pProxy,
        uint dwAuthnSvc,
        uint dwAuthzSvc,
        IntPtr pServerPrincName,
        uint dwAuthnLevel,
        uint dwImpLevel,
        IntPtr pAuthInfo,
        uint dwCapabilities);

    internal static readonly Guid CLSID_WbemLocator = new("4590F811-1D3A-11D0-891F-00AA004B2E24");

    [ComImport]
    [Guid("DC12A687-737F-11CF-884D-00AA004B2E24")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWbemLocator
    {
        [PreserveSig]
        int ConnectServer(
            [MarshalAs(UnmanagedType.BStr)] string strNetworkResource,
            [MarshalAs(UnmanagedType.BStr)] string? strUser,
            [MarshalAs(UnmanagedType.BStr)] string? strPassword,
            [MarshalAs(UnmanagedType.BStr)] string? strLocale,
            int lSecurityFlags,
            [MarshalAs(UnmanagedType.BStr)] string? strAuthority,
            IntPtr pCtx,
            [MarshalAs(UnmanagedType.Interface)] out IWbemServices ppNamespace);
    }

    [ComImport]
    [Guid("9556DC99-828C-11CF-A37E-00AA003240C7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWbemServices
    {
        [PreserveSig] int OpenNamespace([MarshalAs(UnmanagedType.BStr)] string strNamespace, int lFlags, IntPtr pCtx, out IntPtr ppWorkingNamespace, out IntPtr ppResult);
        [PreserveSig] int CancelAsyncCall(IntPtr pSink);
        [PreserveSig] int QueryObjectSink(int lFlags, out IntPtr ppResponseHandler);
        [PreserveSig] int GetObject([MarshalAs(UnmanagedType.BStr)] string strObjectPath, int lFlags, IntPtr pCtx, [MarshalAs(UnmanagedType.Interface)] out IWbemClassObject ppObject, out IntPtr ppCallResult);
        [PreserveSig] int GetObjectAsync([MarshalAs(UnmanagedType.BStr)] string strObjectPath, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);
        [PreserveSig] int PutClass(IntPtr pObject, int lFlags, IntPtr pCtx, out IntPtr ppCallResult);
        [PreserveSig] int PutClassAsync(IntPtr pObject, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);
        [PreserveSig] int DeleteClass([MarshalAs(UnmanagedType.BStr)] string strClass, int lFlags, IntPtr pCtx, out IntPtr ppCallResult);
        [PreserveSig] int DeleteClassAsync([MarshalAs(UnmanagedType.BStr)] string strClass, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);
        [PreserveSig] int CreateClassEnum([MarshalAs(UnmanagedType.BStr)] string? strSuperclass, int lFlags, IntPtr pCtx, out IntPtr ppEnum);
        [PreserveSig] int CreateClassEnumAsync([MarshalAs(UnmanagedType.BStr)] string? strSuperclass, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);
        [PreserveSig] int PutInstance(IntPtr pInst, int lFlags, IntPtr pCtx, out IntPtr ppCallResult);
        [PreserveSig] int PutInstanceAsync(IntPtr pInst, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);
        [PreserveSig] int DeleteInstance([MarshalAs(UnmanagedType.BStr)] string strObjectPath, int lFlags, IntPtr pCtx, out IntPtr ppCallResult);
        [PreserveSig] int DeleteInstanceAsync([MarshalAs(UnmanagedType.BStr)] string strObjectPath, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);

        [PreserveSig]
        int CreateInstanceEnum(
            [MarshalAs(UnmanagedType.BStr)] string strFilter,
            int lFlags,
            IntPtr pCtx,
            [MarshalAs(UnmanagedType.Interface)] out IEnumWbemClassObject ppEnum);

        [PreserveSig] int CreateInstanceEnumAsync([MarshalAs(UnmanagedType.BStr)] string strFilter, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);
        [PreserveSig] int ExecQuery([MarshalAs(UnmanagedType.BStr)] string strQueryLanguage, [MarshalAs(UnmanagedType.BStr)] string strQuery, int lFlags, IntPtr pCtx, out IntPtr ppEnum);
        [PreserveSig] int ExecQueryAsync([MarshalAs(UnmanagedType.BStr)] string strQueryLanguage, [MarshalAs(UnmanagedType.BStr)] string strQuery, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);
        [PreserveSig] int ExecNotificationQuery([MarshalAs(UnmanagedType.BStr)] string strQueryLanguage, [MarshalAs(UnmanagedType.BStr)] string strQuery, int lFlags, IntPtr pCtx, out IntPtr ppEnum);
        [PreserveSig] int ExecNotificationQueryAsync([MarshalAs(UnmanagedType.BStr)] string strQueryLanguage, [MarshalAs(UnmanagedType.BStr)] string strQuery, int lFlags, IntPtr pCtx, IntPtr pResponseHandler);

        [PreserveSig]
        int ExecMethod(
            [MarshalAs(UnmanagedType.BStr)] string strObjectPath,
            [MarshalAs(UnmanagedType.BStr)] string strMethodName,
            int lFlags,
            IntPtr pCtx,
            [MarshalAs(UnmanagedType.Interface)] IWbemClassObject pInParams,
            [MarshalAs(UnmanagedType.Interface)] out IWbemClassObject ppOutParams,
            IntPtr ppCallResult);

        [PreserveSig] int ExecMethodAsync([MarshalAs(UnmanagedType.BStr)] string strObjectPath, [MarshalAs(UnmanagedType.BStr)] string strMethodName, int lFlags, IntPtr pCtx, IntPtr pInParams, IntPtr pResponseHandler);
    }

    [ComImport]
    [Guid("DC12A681-737F-11CF-884D-00AA004B2E24")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWbemClassObject
    {
        [PreserveSig] int GetQualifierSet(out IntPtr ppQualSet);

        [PreserveSig]
        int Get(
            [MarshalAs(UnmanagedType.LPWStr)] string wszName,
            int lFlags,
            [MarshalAs(UnmanagedType.Struct)] out object pVal,
            out int pType,
            out int plFlavor);

        [PreserveSig]
        int Put(
            [MarshalAs(UnmanagedType.LPWStr)] string wszName,
            int lFlags,
            [MarshalAs(UnmanagedType.Struct)] ref object pVal,
            int Type);

        [PreserveSig] int Delete([MarshalAs(UnmanagedType.LPWStr)] string wszName);
        [PreserveSig] int GetNames([MarshalAs(UnmanagedType.LPWStr)] string? wszQualifierName, int lFlags, IntPtr pQualifierVal, out IntPtr pNames);
        [PreserveSig] int BeginEnumeration(int lEnumFlags);
        [PreserveSig] int Next(int lFlags, IntPtr strName, IntPtr pVal, IntPtr pType, IntPtr plFlavor);
        [PreserveSig] int EndEnumeration();
        [PreserveSig] int GetPropertyQualifierSet([MarshalAs(UnmanagedType.LPWStr)] string wszProperty, out IntPtr ppQualSet);
        [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IWbemClassObject ppCopy);
        [PreserveSig] int GetObjectText(int lFlags, [MarshalAs(UnmanagedType.BStr)] out string pstrObjectText);
        [PreserveSig] int SpawnDerivedClass(int lFlags, [MarshalAs(UnmanagedType.Interface)] out IWbemClassObject ppNewClass);

        [PreserveSig]
        int SpawnInstance(int lFlags, [MarshalAs(UnmanagedType.Interface)] out IWbemClassObject ppNewInstance);

        [PreserveSig] int CompareTo(int lFlags, [MarshalAs(UnmanagedType.Interface)] IWbemClassObject pCompareTo);
        [PreserveSig] int GetPropertyOrigin([MarshalAs(UnmanagedType.LPWStr)] string wszName, [MarshalAs(UnmanagedType.BStr)] out string pstrClassName);
        [PreserveSig] int InheritsFrom([MarshalAs(UnmanagedType.LPWStr)] string strAncestor);

        [PreserveSig]
        int GetMethod(
            [MarshalAs(UnmanagedType.LPWStr)] string wszName,
            int lFlags,
            [MarshalAs(UnmanagedType.Interface)] out IWbemClassObject ppInSignature,
            [MarshalAs(UnmanagedType.Interface)] out IWbemClassObject ppOutSignature);

        [PreserveSig] int PutMethod([MarshalAs(UnmanagedType.LPWStr)] string wszName, int lFlags, IntPtr pInSignature, IntPtr pOutSignature);
        [PreserveSig] int DeleteMethod([MarshalAs(UnmanagedType.LPWStr)] string wszName);
        [PreserveSig] int BeginMethodEnumeration(int lEnumFlags);
        [PreserveSig] int NextMethod(int lFlags, IntPtr pstrName, IntPtr ppInSignature, IntPtr ppOutSignature);
        [PreserveSig] int EndMethodEnumeration();
        [PreserveSig] int GetMethodQualifierSet([MarshalAs(UnmanagedType.LPWStr)] string wszMethod, out IntPtr ppQualSet);
        [PreserveSig] int GetMethodOrigin([MarshalAs(UnmanagedType.LPWStr)] string wszMethodName, [MarshalAs(UnmanagedType.BStr)] out string pstrClassName);
    }

    [ComImport]
    [Guid("027947E1-D731-11CE-A357-000000000001")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumWbemClassObject
    {
        [PreserveSig] int Reset();

        [PreserveSig]
        int Next(
            int lTimeout,
            uint uCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IWbemClassObject[] apObjects,
            out uint puReturned);

        [PreserveSig] int NextAsync(uint uCount, IntPtr pSink);
        [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumWbemClassObject ppEnum);
        [PreserveSig] int Skip(int lTimeout, uint nCount);
    }

    internal static bool Succeeded(int hr) => hr >= 0;
    internal static string Hr(int hr) => $"0x{unchecked((uint)hr):X8}";

    internal static string ExplainHr(int hr) => hr switch
    {
        S_OK => "S_OK",
        S_FALSE => "S_FALSE",
        RPC_E_CHANGED_MODE => "RPC_E_CHANGED_MODE",
        RPC_E_TOO_LATE => "RPC_E_TOO_LATE (COM security already initialized; Lenovo accepts this)",
        WBEM_E_NOT_FOUND => "WBEM_E_NOT_FOUND",
        WBEM_E_ACCESS_DENIED => "WBEM_E_ACCESS_DENIED",
        _ => hr >= 0 ? "SUCCESS" : "FAILED"
    };

    internal static int ApplyManagerProxyBlanket(object proxy)
    {
        IntPtr unk = IntPtr.Zero;
        try
        {
            unk = Marshal.GetIUnknownForObject(proxy);
            return CoSetProxyBlanket(
                unk,
                RPC_C_AUTHN_WINNT,
                RPC_C_AUTHZ_NONE,
                IntPtr.Zero,
                RPC_C_AUTHN_LEVEL_CALL,
                RPC_C_IMP_LEVEL_IMPERSONATE,
                IntPtr.Zero,
                EOAC_NONE);
        }
        finally
        {
            if (unk != IntPtr.Zero) Marshal.Release(unk);
        }
    }

    internal static void ReleaseCom(object? o)
    {
        if (o is null) return;
        try
        {
            if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
        }
        catch { }
    }
}
