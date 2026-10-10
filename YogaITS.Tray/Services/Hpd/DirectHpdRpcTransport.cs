using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services.Hpd;

/// <summary>
/// Direct Windows RPC client for Lenovo SmartSense HPD.
///
/// This transport does NOT load SmartSenseRpcClient.dll. The NDR32 procedure
/// and type format strings below were recovered byte-for-byte from the vendor
/// client used by Lenovo PC Manager 5.1.190.5202.
///
/// Transport : ncalrpc
/// Endpoint  : HumanPresenceDetectionEndpoint
/// Interface : 848A33AC-B5A7-4253-A54F-E0AC20648468 v1.0
/// Syntax    : DCE NDR 2.0
/// </summary>
internal sealed class DirectHpdRpcTransport : IDisposable
{
    private static readonly byte[] TypeFormat = Convert.FromHexString(
        "00001104020030A400001104020030E5000030450000110C085C110C0B5C" +
        "11140200122002001B010200295478000100055B1108255C110002001B000100" +
        "280010000000015B11140200122002001B000100285420000000015B1108085C");

    // Exact NDR32 procedure chunks for the operations used by this probe.
    private static readonly byte[] Proc0CreateContext = Convert.FromHexString(
        "00480000000000001800320000000000480044020A010000000000000000100108000600700010000B00");

    private static readonly byte[] Proc1CloseContext = Convert.FromHexString(
        "0048000000000100100030E4000000003800480044020A010000000000000000180100000E00700008000B00");

    private static readonly byte[] Proc4GetVersion = Convert.FromHexString(
        "0048000000000400180030440000000024002C0044030A010000000000000000080000001200502108000800700010000B00");

    private static readonly byte[] Proc5GetCapability = Convert.FromHexString(
        "004800000000050018003044000000002400340044030A010000000000000000080000001200502108000B00700010000B00");

    private static readonly byte[] Proc6GetAllSettingV2 = Convert.FromHexString(
        "004800000000060090003044000000002400B40145120A030100000000000000080000001200" +
        "502108000800502110000800502118000800502120000800502128000800502130000800" +
        "502138000800502140000800502148000800502150000800502158000800502160000800" +
        "502168000800502170000800502178000800132080001E00700088000B00");

    private static readonly byte[] Proc8SetApproachEnabled = Convert.FromHexString(
        "004800000000080018003044000000002C00100044030A010000000000000000080000001200480008000800700010000B00");

    private static readonly byte[] Proc11SetLeaveEnabled = Convert.FromHexString(
        "0048000000000B0018003044000000002C00100044030A010000000000000000080000001200480008000800700010000B00");

    private static readonly byte[] Proc12SetLeaveWait = Convert.FromHexString(
        "0048000000000C0018003044000000002C00100044030A010000000000000000080000001200480008000800700010000B00");

    private static readonly byte[] Proc13SetCameraAssist = Convert.FromHexString(
        "0048000000000D0018003044000000002C00100044030A010000000000000000080000001200480008000800700010000B00");

    private static readonly byte[] Proc16SetVideoPause = Convert.FromHexString(
        "004800000000100018003044000000002C00100044030A010000000000000000080000001200480008000800700010000B00");

    private nint _binding;
    private nint _bindingSlot;
    private nint _contextSlot;
    private nint _rpcClientInterface;
    private nint _typeFormat;
    private nint _stubDesc;

    private nint _proc0;
    private nint _proc1;
    private nint _proc4;
    private nint _proc5;
    private nint _proc6;
    private nint _proc8;
    private nint _proc11;
    private nint _proc12;
    private nint _proc13;
    private nint _proc16;

    private readonly MidlAllocateDelegate _allocateDelegate;
    private readonly MidlFreeDelegate _freeDelegate;
    private bool _disposed;

    public nint ContextHandle => _contextSlot == 0 ? 0 : Marshal.ReadIntPtr(_contextSlot);

    public string LastGetAllInternalDiagnostics { get; private set; } = string.Empty;

    public DirectHpdRpcTransport()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("HPD Direct RPC 仅支持 Windows。");
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("HPD Direct RPC Probe 仅实现 Windows x64 ABI。");

        _allocateDelegate = MidlAllocate;
        _freeDelegate = MidlFree;

        try
        {
            ValidateManagedLayouts();
            CreateBinding();
            BuildNdrMetadata();
            CreateRpcContext();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int GetVersion(out int version)
    {
        EnsureReady();
        nint output = AllocZero(4);
        try
        {
            int rc = Result32(NdrClientCall2_GetInt(_stubDesc, _proc4, ContextHandle, output));
            version = Marshal.ReadInt32(output);
            return rc;
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    public int GetCapability(out long capability)
    {
        EnsureReady();
        nint output = AllocZero(8);
        try
        {
            int rc = Result32(NdrClientCall2_GetLong(_stubDesc, _proc5, ContextHandle, output));
            capability = Marshal.ReadInt64(output);
            return rc;
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    public int GetAllSettingV2(out HpdState state)
    {
        EnsureReady();

        // RPC opnum 6 has 15 [out] LONG simple refs followed by one complex
        // [out] value. Lenovo's public GetHPDAllSettingV2 exposes the first 12
        // LONGs directly and maps LONG #13 to YogaMode. LONG #14/#15 and the
        // complex result are internal implementation details.
        nint[] slots = new nint[15];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = AllocZero(4);

        nint complexSlot = AllocZero(nint.Size);

        try
        {
            int rc = Result32(NdrClientCall2_GetAll(
                _stubDesc,
                _proc6,
                ContextHandle,
                slots[0], slots[1], slots[2], slots[3], slots[4],
                slots[5], slots[6], slots[7], slots[8], slots[9],
                slots[10], slots[11], slots[12], slots[13], slots[14],
                complexSlot));

            int[] v = new int[slots.Length];
            for (int i = 0; i < slots.Length; i++)
                v[i] = Marshal.ReadInt32(slots[i]);

            nint complex = Marshal.ReadIntPtr(complexSlot);

            state = new HpdState(
                v[0],  // field0
                v[1],  // sensor_type
                v[2],  // approach_enabled
                v[3],  // approach_distance
                v[4],  // approach_autoadjust
                v[5],  // leave_enable
                v[6],  // leave_time
                v[7],  // camera_assist
                v[8],  // leave_distance
                v[9],  // leave_autoadjust
                v[10], // video_pause
                v[11], // facial_registered
                v[12]  // yogamode (vendor wrapper maps internal LONG #13 here)
            );

            LastGetAllInternalDiagnostics =
                $"internal13={v[13]}, internal14={v[14]}, complex={(complex == 0 ? "NULL" : $"0x{complex:X}")}";

            // The vendor wrapper frees this exact complex output after opnum 6.
            // Because this direct client supplies the MIDL allocator, the memory
            // belongs to our allocator and can be released with FreeHGlobal.
            if (complex != 0)
            {
                Marshal.FreeHGlobal(complex);
                Marshal.WriteIntPtr(complexSlot, 0);
            }

            return rc;
        }
        finally
        {
            foreach (nint slot in slots)
            {
                if (slot != 0)
                    Marshal.FreeHGlobal(slot);
            }
            Marshal.FreeHGlobal(complexSlot);
        }
    }

    public int SetApproachEnabled(int value) => CallSet(_proc8, value);
    public int SetLeaveEnabled(int value) => CallSet(_proc11, value);
    public int SetLeaveWaitTime(int value) => CallSet(_proc12, value);
    public int SetCameraAssist(int value) => CallSet(_proc13, value);
    public int SetVideoPause(int value) => CallSet(_proc16, value);

    private int CallSet(nint proc, int value)
    {
        EnsureReady();
        return Result32(NdrClientCall2_SetInt(_stubDesc, proc, ContextHandle, value));
    }

    private void CreateBinding()
    {
        uint status = RpcStringBindingComposeW(
            ObjUuid: null,
            ProtSeq: "ncalrpc",
            NetworkAddr: null,
            Endpoint: "HumanPresenceDetectionEndpoint",
            Options: null,
            out nint stringBinding);

        if (status != 0)
            throw new Win32Exception((int)status, $"RpcStringBindingComposeW failed: {status}");

        try
        {
            status = RpcBindingFromStringBindingW(stringBinding, out _binding);
            if (status != 0 || _binding == 0)
                throw new Win32Exception((int)status, $"RpcBindingFromStringBindingW failed: {status}");
        }
        finally
        {
            if (stringBinding != 0)
                _ = RpcStringFreeW(ref stringBinding);
        }
    }

    private void BuildNdrMetadata()
    {
        _bindingSlot = AllocZero(nint.Size);
        Marshal.WriteIntPtr(_bindingSlot, _binding);

        _contextSlot = AllocZero(nint.Size);

        _typeFormat = AllocAndCopy(TypeFormat);
        _proc0 = AllocAndCopy(Proc0CreateContext);
        _proc1 = AllocAndCopy(Proc1CloseContext);
        _proc4 = AllocAndCopy(Proc4GetVersion);
        _proc5 = AllocAndCopy(Proc5GetCapability);
        _proc6 = AllocAndCopy(Proc6GetAllSettingV2);
        _proc8 = AllocAndCopy(Proc8SetApproachEnabled);
        _proc11 = AllocAndCopy(Proc11SetLeaveEnabled);
        _proc12 = AllocAndCopy(Proc12SetLeaveWait);
        _proc13 = AllocAndCopy(Proc13SetCameraAssist);
        _proc16 = AllocAndCopy(Proc16SetVideoPause);

        var clientInterface = new RpcClientInterface
        {
            Length = (uint)Marshal.SizeOf<RpcClientInterface>(),
            InterfaceId = new RpcSyntaxIdentifier
            {
                SyntaxGuid = new Guid("848A33AC-B5A7-4253-A54F-E0AC20648468"),
                SyntaxVersion = new RpcVersion { MajorVersion = 1, MinorVersion = 0 }
            },
            TransferSyntax = new RpcSyntaxIdentifier
            {
                SyntaxGuid = new Guid("8A885D04-1CEB-11C9-9FE8-08002B104860"),
                SyntaxVersion = new RpcVersion { MajorVersion = 2, MinorVersion = 0 }
            },
            DispatchTable = 0,
            RpcProtseqEndpointCount = 0,
            RpcProtseqEndpoint = 0,
            Reserved = 0,
            InterpreterInfo = 0,
            Flags = 0
        };

        _rpcClientInterface = Marshal.AllocHGlobal(Marshal.SizeOf<RpcClientInterface>());
        Marshal.StructureToPtr(clientInterface, _rpcClientInterface, fDeleteOld: false);

        var stubDesc = new MidlStubDesc
        {
            RpcInterfaceInformation = _rpcClientInterface,
            PfnAllocate = Marshal.GetFunctionPointerForDelegate(_allocateDelegate),
            PfnFree = Marshal.GetFunctionPointerForDelegate(_freeDelegate),
            ImplicitHandleInfo = _bindingSlot,
            ApfnNdrRundownRoutines = 0,
            AGenericBindingRoutinePairs = 0,
            ApfnExprEval = 0,
            AXmitQuintuple = 0,
            PFormatTypes = _typeFormat,
            FCheckBounds = 1,
            Version = 0x0006_0001,
            PMallocFreeStruct = 0,
            MidlVersion = unchecked((int)0x0801_0272),
            CommFaultOffsets = 0,
            AUserMarshalQuadruple = 0,
            NotifyRoutineTable = 0,
            MFlags = 0x0200_0001,
            CsRoutineTables = 0,
            ProxyServerInfo = 0,
            PExprInfo = 0
        };

        _stubDesc = Marshal.AllocHGlobal(Marshal.SizeOf<MidlStubDesc>());
        Marshal.StructureToPtr(stubDesc, _stubDesc, fDeleteOld: false);
    }

    private void CreateRpcContext()
    {
        int rc;
        try
        {
            rc = Result32(NdrClientCall2_CreateContext(_stubDesc, _proc0, _binding, _contextSlot));
        }
        catch (SEHException ex)
        {
            throw new InvalidOperationException(
                "HPD RPC context creation caused an RPC/NDR native exception.", ex);
        }

        nint context = ContextHandle;
        if (rc != 0 || context == 0)
        {
            throw new InvalidOperationException(
                $"HPD RPC context creation failed: rc={rc}, context=0x{context:X}");
        }
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_binding == 0 || _stubDesc == 0 || ContextHandle == 0)
            throw new InvalidOperationException("HPD Direct RPC transport 尚未初始化。 ");
    }

    private static int Result32(nint value) => unchecked((int)value.ToInt64());

    private static nint AllocZero(int bytes)
    {
        nint p = Marshal.AllocHGlobal(bytes);
        byte[] zero = new byte[bytes];
        Marshal.Copy(zero, 0, p, bytes);
        return p;
    }

    private static nint AllocAndCopy(byte[] bytes)
    {
        nint p = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    private static nint MidlAllocate(nuint bytes)
    {
        if (bytes == 0)
            bytes = 1;

        try
        {
            return Marshal.AllocHGlobal(checked((nint)bytes));
        }
        catch
        {
            return 0;
        }
    }

    private static void MidlFree(nint memory)
    {
        if (memory != 0)
            Marshal.FreeHGlobal(memory);
    }

    private static void ValidateManagedLayouts()
    {
        static int OffsetOf<T>(string field) where T : struct =>
            checked((int)Marshal.OffsetOf<T>(field));

        if (Marshal.SizeOf<RpcClientInterface>() != 0x60 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.InterfaceId)) != 0x04 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.TransferSyntax)) != 0x18 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.DispatchTable)) != 0x30 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.InterpreterInfo)) != 0x50 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.Flags)) != 0x58)
        {
            throw new PlatformNotSupportedException(
                "RPC_CLIENT_INTERFACE managed layout does not match Windows x64 ABI.");
        }

        if (Marshal.SizeOf<MidlStubDesc>() != 0x98)
        {
            throw new PlatformNotSupportedException(
                $"MIDL_STUB_DESC size mismatch: 0x{Marshal.SizeOf<MidlStubDesc>():X}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Vendor lifecycle: opnum 1 receives a pointer to the context handle,
        // then RpcBindingFree releases the ncalrpc binding.
        if (_stubDesc != 0 && _proc1 != 0 && _contextSlot != 0 && ContextHandle != 0)
        {
            try
            {
                _ = NdrClientCall2_CloseContext(_stubDesc, _proc1, _contextSlot);
            }
            catch
            {
                // Best effort during shutdown.
            }
        }

        if (_contextSlot != 0)
            Marshal.WriteIntPtr(_contextSlot, 0);

        if (_binding != 0)
        {
            nint binding = _binding;
            _ = RpcBindingFree(ref binding);
            _binding = 0;
        }

        FreeHGlobal(ref _stubDesc);
        FreeHGlobal(ref _rpcClientInterface);
        FreeHGlobal(ref _typeFormat);
        FreeHGlobal(ref _proc0);
        FreeHGlobal(ref _proc1);
        FreeHGlobal(ref _proc4);
        FreeHGlobal(ref _proc5);
        FreeHGlobal(ref _proc6);
        FreeHGlobal(ref _proc8);
        FreeHGlobal(ref _proc11);
        FreeHGlobal(ref _proc12);
        FreeHGlobal(ref _proc13);
        FreeHGlobal(ref _proc16);
        FreeHGlobal(ref _contextSlot);
        FreeHGlobal(ref _bindingSlot);
    }

    private static void FreeHGlobal(ref nint ptr)
    {
        if (ptr == 0)
            return;
        Marshal.FreeHGlobal(ptr);
        ptr = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RpcVersion
    {
        public ushort MajorVersion;
        public ushort MinorVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RpcSyntaxIdentifier
    {
        public Guid SyntaxGuid;
        public RpcVersion SyntaxVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RpcClientInterface
    {
        public uint Length;
        public RpcSyntaxIdentifier InterfaceId;
        public RpcSyntaxIdentifier TransferSyntax;
        public nint DispatchTable;
        public uint RpcProtseqEndpointCount;
        public nint RpcProtseqEndpoint;
        public nuint Reserved;
        public nint InterpreterInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MidlStubDesc
    {
        public nint RpcInterfaceInformation;
        public nint PfnAllocate;
        public nint PfnFree;
        public nint ImplicitHandleInfo;
        public nint ApfnNdrRundownRoutines;
        public nint AGenericBindingRoutinePairs;
        public nint ApfnExprEval;
        public nint AXmitQuintuple;
        public nint PFormatTypes;
        public int FCheckBounds;
        public uint Version;
        public nint PMallocFreeStruct;
        public int MidlVersion;
        public nint CommFaultOffsets;
        public nint AUserMarshalQuadruple;
        public nint NotifyRoutineTable;
        public nuint MFlags;
        public nint CsRoutineTables;
        public nint ProxyServerInfo;
        public nint PExprInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint MidlAllocateDelegate(nuint bytes);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void MidlFreeDelegate(nint memory);

    [DllImport("Rpcrt4.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RpcStringBindingComposeW(
        string? ObjUuid,
        string? ProtSeq,
        string? NetworkAddr,
        string? Endpoint,
        string? Options,
        out nint StringBinding);

    [DllImport("Rpcrt4.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RpcBindingFromStringBindingW(nint StringBinding, out nint Binding);

    [DllImport("Rpcrt4.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RpcStringFreeW(ref nint StringBinding);

    [DllImport("Rpcrt4.dll", ExactSpelling = true)]
    private static extern uint RpcBindingFree(ref nint Binding);

    [DllImport("Rpcrt4.dll", EntryPoint = "NdrClientCall2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NdrClientCall2_CreateContext(
        nint pStubDesc,
        nint pFormat,
        nint binding,
        nint contextOut);

    [DllImport("Rpcrt4.dll", EntryPoint = "NdrClientCall2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NdrClientCall2_CloseContext(
        nint pStubDesc,
        nint pFormat,
        nint contextInOut);

    [DllImport("Rpcrt4.dll", EntryPoint = "NdrClientCall2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NdrClientCall2_GetInt(
        nint pStubDesc,
        nint pFormat,
        nint context,
        nint output);

    [DllImport("Rpcrt4.dll", EntryPoint = "NdrClientCall2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NdrClientCall2_GetLong(
        nint pStubDesc,
        nint pFormat,
        nint context,
        nint output);

    [DllImport("Rpcrt4.dll", EntryPoint = "NdrClientCall2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NdrClientCall2_SetInt(
        nint pStubDesc,
        nint pFormat,
        nint context,
        int value);

    [DllImport("Rpcrt4.dll", EntryPoint = "NdrClientCall2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NdrClientCall2_GetAll(
        nint pStubDesc,
        nint pFormat,
        nint context,
        nint out01,
        nint out02,
        nint out03,
        nint out04,
        nint out05,
        nint out06,
        nint out07,
        nint out08,
        nint out09,
        nint out10,
        nint out11,
        nint out12,
        nint out13,
        nint out14,
        nint out15,
        nint complexOut);
}
