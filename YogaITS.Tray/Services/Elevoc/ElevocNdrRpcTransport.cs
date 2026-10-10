using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace YogaITS.Tray.Services.Elevoc;

/// <summary>
/// Vendor-DLL-free Elevoc local RPC transport.
///
/// The NDR32 procedure/type metadata below was verified byte-for-byte against
/// three different ElevocUAPO.dll builds (Xiaoxin 1.0.1.765, ThinkBook
/// 1.0.1.1222 and Yoga 1.0.1.1623).  No code or metadata is loaded from the
/// vendor APO at runtime.
///
/// Wire contract:
///   protocol sequence : ncalrpc
///   endpoint          : ElevocRpcEndpoint
///   interface UUID    : 6F761170-1054-4339-A573-063DD08C7E16
///   interface version : 4.0
///   transfer syntax   : DCE NDR 2.0
///   opnum             : 0
///   argument          : [in,out,string,size_is(0x5000)] wchar_t *buffer
/// </summary>
internal sealed class ElevocNdrRpcTransport : IDisposable
{
    internal const int RpcBufferChars = 0x5000;
    internal const int RpcBufferBytes = RpcBufferChars * sizeof(char);

    private const uint RpcCallTimeoutOption = 12; // RPC_C_OPT_CALL_TIMEOUT
    private const nuint RpcCallTimeoutMs = 1000;

    internal static readonly Guid InterfaceUuid =
        new("6F761170-1054-4339-A573-063DD08C7E16");

    private static readonly Guid Ndr32TransferSyntax =
        new("8A885D04-1CEB-11C9-9FE8-08002B104860");

    // Exact NDR32 type format found once in each of the three analyzed DLLs.
    //
    // Offset 2:
    //   0x25 = FC_C_WSTRING
    //   0x44 = FC_STRING_SIZED
    //   0x40 = FC_CONSTANT_CONFORMANCE
    //   constant = 0x5000 WCHAR
    private static readonly byte[] TypeFormat =
    [
        0x00, 0x00,
        0x25, 0x44, 0x40, 0x00, 0x00, 0x50,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    // Exact NDR32 procedure format found once in each analyzed DLL.
    //
    // FC_BIND_PRIMITIVE, opnum 0, x64 stack size 8, one parameter.
    // Parameter flags 0x001b = MustSize | MustFree | In | Out.
    // Type offset 2 selects the sized conformant WCHAR string above.
    private static readonly byte[] ProcFormat =
    [
        0x32, 0x48,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00,
        0x08, 0x00,
        0x00, 0x00,
        0x00, 0x00,
        0x43, 0x01,
        0x0a, 0x07,
        0x01, 0x00,
        0x01, 0x00,
        0x00, 0x00,
        0x00, 0x00,
        0x1b, 0x00,
        0x00, 0x00,
        0x02, 0x00,
        0x00, 0x00,
        0x00, 0x00,
        0x00, 0x00
    ];

    private nint _binding;
    private nint _bindingSlot;
    private nint _rpcClientInterface;
    private nint _typeFormat;
    private nint _procFormat;
    private nint _stubDesc;

    private readonly MidlAllocateDelegate _allocateDelegate;
    private readonly MidlFreeDelegate _freeDelegate;
    private bool _disposed;

    public ElevocNdrRpcTransport()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Elevoc RPC 仅支持 Windows。");

        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("本 Probe 的直接 NDR client 仅实现 Windows x64 ABI。");

        _allocateDelegate = MidlAllocate;
        _freeDelegate = MidlFree;

        try
        {
            ValidateManagedLayouts();
            InitializeBinding();
            InitializeNdr32Stub();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Invoke(nint buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer == 0)
            throw new ArgumentNullException(nameof(buffer));

        if (_binding == 0 || _stubDesc == 0 || _procFormat == 0)
            throw new InvalidOperationException("Elevoc direct RPC transport 尚未初始化。");

        // Preserve the original v8 preflight behavior.
        uint listenStatus = RpcMgmtIsServerListening(_binding);
        if (listenStatus != 0)
        {
            throw new InvalidOperationException(
                $"Elevoc RPC 服务当前不可用。RpcMgmtIsServerListening = {listenStatus}.\n" +
                "请确认 Elevoc Control Service (ElevocService) 正在运行。");
        }

        try
        {
            // The vendor uses NdrClientCall3 because its interface advertises
            // NDR32 + NDR64.  We intentionally advertise only the NDR32 syntax
            // that is byte-for-byte identical across all three analyzed builds,
            // so the standard single-syntax NdrClientCall2 path is sufficient.
            _ = NdrClientCall2(_stubDesc, _procFormat, buffer);
        }
        catch (SEHException ex)
        {
            throw new InvalidOperationException(
                "Elevoc direct RPC/NDR32 调用触发原生异常。\n" +
                "当前 proc/type format 已按三份 UAPO 的 NDR32 metadata 原样重建；" +
                "若仍失败，请保留完整启动日志。\n\n" + ex.Message,
                ex);
        }
    }

    public static bool LooksLikeValidElevocResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (doc.RootElement.TryGetProperty("status", out _))
                return true;

            if (doc.RootElement.TryGetProperty("Header", out JsonElement header) &&
                header.ValueKind == JsonValueKind.Object)
            {
                return header.TryGetProperty("status", out _) ||
                       header.TryGetProperty("method", out _) ||
                       header.TryGetProperty("elevoc_rpc_protocol", out _);
            }

            if (doc.RootElement.TryGetProperty("header", out header) &&
                header.ValueKind == JsonValueKind.Object)
            {
                return header.TryGetProperty("status", out _) ||
                       header.TryGetProperty("method", out _) ||
                       header.TryGetProperty("elevoc_rpc_protocol", out _);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void InitializeBinding()
    {
        uint status = RpcStringBindingComposeW(
            ObjUuid: null,
            ProtSeq: "ncalrpc",
            NetworkAddr: null,
            Endpoint: "ElevocRpcEndpoint",
            Options: null,
            out nint stringBinding);

        if (status != 0)
            throw new Win32Exception((int)status, $"RpcStringBindingComposeW 失败: {status}");

        try
        {
            status = RpcBindingFromStringBindingW(stringBinding, out _binding);
            if (status != 0 || _binding == 0)
                throw new Win32Exception((int)status, $"RpcBindingFromStringBindingW 失败: {status}");
        }
        finally
        {
            if (stringBinding != 0)
                _ = RpcStringFreeW(ref stringBinding);
        }

        // Yoga 1.0.1.1623 uses 1000 ms; older analyzed builds omit it.  This is
        // transport policy rather than wire ABI, and prevents a hung UI.
        _ = RpcBindingSetOption(_binding, RpcCallTimeoutOption, RpcCallTimeoutMs);
    }

    private void InitializeNdr32Stub()
    {
        _bindingSlot = Marshal.AllocHGlobal(nint.Size);
        Marshal.WriteIntPtr(_bindingSlot, _binding);

        _typeFormat = AllocAndCopy(TypeFormat);
        _procFormat = AllocAndCopy(ProcFormat);

        var clientInterface = new RpcClientInterface
        {
            Length = (uint)Marshal.SizeOf<RpcClientInterface>(),
            InterfaceId = new RpcSyntaxIdentifier
            {
                SyntaxGuid = InterfaceUuid,
                SyntaxVersion = new RpcVersion { MajorVersion = 4, MinorVersion = 0 }
            },
            TransferSyntax = new RpcSyntaxIdentifier
            {
                SyntaxGuid = Ndr32TransferSyntax,
                SyntaxVersion = new RpcVersion { MajorVersion = 2, MinorVersion = 0 }
            },
            DispatchTable = 0,
            RpcProtseqEndpointCount = 0,
            RpcProtseqEndpoint = 0,
            Reserved = 0,
            InterpreterInfo = 0,
            Flags = 0 // single NDR32 transfer syntax
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

            // These two values are also identical in all three vendor
            // MIDL_STUB_DESC structures.
            Version = 0x0006_0001,
            PMallocFreeStruct = 0,
            MidlVersion = unchecked((int)0x0801_026e),

            CommFaultOffsets = 0,
            AUserMarshalQuadruple = 0,
            NotifyRoutineTable = 0,

            // This client intentionally advertises only classic NDR32, so do
            // not carry the vendor's multi-syntax 0x02000000 flag.
            MFlags = 0x1,
            CsRoutineTables = 0,
            ProxyServerInfo = 0,
            PExprInfo = 0
        };

        _stubDesc = Marshal.AllocHGlobal(Marshal.SizeOf<MidlStubDesc>());
        Marshal.StructureToPtr(stubDesc, _stubDesc, fDeleteOld: false);
    }

    private static void ValidateManagedLayouts()
    {
        static int OffsetOf<T>(string field) where T : struct =>
            checked((int)Marshal.OffsetOf<T>(field));

        if (Marshal.SizeOf<RpcClientInterface>() != 0x60 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.InterfaceId)) != 0x04 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.TransferSyntax)) != 0x18 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.DispatchTable)) != 0x30 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.RpcProtseqEndpointCount)) != 0x38 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.RpcProtseqEndpoint)) != 0x40 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.InterpreterInfo)) != 0x50 ||
            OffsetOf<RpcClientInterface>(nameof(RpcClientInterface.Flags)) != 0x58)
        {
            throw new PlatformNotSupportedException(
                "RPC_CLIENT_INTERFACE 的托管布局与 Windows x64 ABI 不一致。");
        }

        if (Marshal.SizeOf<MidlStubDesc>() != 0x98)
        {
            throw new PlatformNotSupportedException(
                "MIDL_STUB_DESC 的托管布局与 Windows x64 ABI 不一致。");
        }
    }

    private static nint AllocAndCopy(byte[] bytes)
    {
        nint ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
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
            // Never allow a managed exception to cross the unmanaged MIDL
            // allocator callback boundary. NDR treats NULL as allocation failure.
            return 0;
        }
    }

    private static void MidlFree(nint memory)
    {
        if (memory != 0)
            Marshal.FreeHGlobal(memory);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        FreeHGlobal(ref _stubDesc);
        FreeHGlobal(ref _procFormat);
        FreeHGlobal(ref _typeFormat);
        FreeHGlobal(ref _rpcClientInterface);
        FreeHGlobal(ref _bindingSlot);

        if (_binding != 0)
        {
            nint binding = _binding;
            _ = RpcBindingFree(ref binding);
            _binding = 0;
        }
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

    [DllImport("Rpcrt4.dll", EntryPoint = "NdrClientCall2", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NdrClientCall2(
        nint pStubDescriptor,
        nint pFormat,
        nint inOutBuffer);

    [DllImport("Rpcrt4.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RpcStringBindingComposeW(
        string? ObjUuid,
        string? ProtSeq,
        string? NetworkAddr,
        string? Endpoint,
        string? Options,
        out nint StringBinding);

    [DllImport("Rpcrt4.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RpcBindingFromStringBindingW(
        nint StringBinding,
        out nint Binding);

    [DllImport("Rpcrt4.dll", ExactSpelling = true)]
    private static extern uint RpcBindingSetOption(
        nint Binding,
        uint Option,
        nuint OptionValue);

    [DllImport("Rpcrt4.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RpcStringFreeW(ref nint StringBinding);

    [DllImport("Rpcrt4.dll", ExactSpelling = true)]
    private static extern uint RpcBindingFree(ref nint Binding);

    [DllImport("Rpcrt4.dll", ExactSpelling = true)]
    private static extern uint RpcMgmtIsServerListening(nint Binding);
}
