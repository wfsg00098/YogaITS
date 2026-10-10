using System.Runtime.InteropServices;
using System.Text.Json;

namespace YogaITS.Tray.Services.Elevoc;

/// <summary>
/// Vendor-DLL-free Elevoc RPC client.
///
/// Runtime dependencies:
///   Windows RPCRT4
///   ncalrpc:ElevocRpcEndpoint
///
/// Intentionally NOT dependent on:
///   ElevocUAPO.dll / file version / size / SHA256 / RVA.
/// </summary>
internal sealed class ElevocRpcClient : IDisposable
{
    private const int RpcBufferChars = ElevocNdrRpcTransport.RpcBufferChars;

    private ElevocNdrRpcTransport? _transport;
    private bool _disposed;

    public string TransportDescription =>
        "RPCRT4 / ncalrpc / ElevocRpcEndpoint / 6F761170-1054-4339-A573-063DD08C7E16 v4.0";

    public string AbiVariant { get; private set; } = "not initialized";
    public string InitializationProbeResponse { get; private set; } = string.Empty;
    public string InitializationProbeDiagnostics { get; private set; } = string.Empty;

    public ElevocRpcClient()
    {
        ElevocNdrRpcTransport? transport = null;
        try
        {
            transport = new ElevocNdrRpcTransport();

            string response = SendJsonWithBufferCore(
                transport,
                "{\"request\":\"GetMode\",\"link\":\"app_uplink\"}",
                RpcBufferChars);

            if (!ElevocNdrRpcTransport.LooksLikeValidElevocResponse(response))
            {
                throw new InvalidOperationException(
                    "Direct RPC 已返回，但启动只读自检没有得到可识别的 Elevoc JSON：\n" +
                    Truncate(response, 320));
            }

            _transport = transport;
            transport = null;

            AbiVariant =
                "NDR32 opnum 0 / [in,out,string,size_is(0x5000)] WCHAR buffer";
            InitializationProbeResponse = response;
            InitializationProbeDiagnostics =
                "proc/type format 已与 Yoga 1.0.1.1623、ThinkBook 1.0.1.1222、" +
                "小新 1.0.1.765 三份 UAPO 逐字节核对一致";
        }
        catch
        {
            transport?.Dispose();
            throw;
        }
    }

    // ---------------- Mode control ----------------

    public string SetMicrophoneMode(string mode)
    {
        string normalized = mode?.Trim().ToLowerInvariant()
            ?? throw new ArgumentNullException(nameof(mode));

        if (normalized is not ("off" or "private" or "shared" or "special" or "voiceid"))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的 Elevoc uplink mode。");

        return SendObject(new
        {
            request = "SetMode",
            link = "uplink",
            mode = normalized
        });
    }

    public string GetMicrophoneMode() =>
        SendJson("{\"request\":\"GetMode\",\"link\":\"app_uplink\"}");

    public string SetSpeakerNoiseCancellation(bool enabled) =>
        SendObject(new
        {
            request = "SetMode",
            link = "downlink",
            mode = enabled ? "enable" : "disable"
        });

    public string GetSpeakerMode() =>
        SendJson("{\"request\":\"GetMode\",\"link\":\"app_downlink\"}");

    // ---------------- Capability ----------------

    public string GetFunctionsByEndpoint(string endpointId) =>
        SendObject(new
        {
            request = "GetFunctionsByEndpoint",
            endpoint = RequireEndpoint(endpointId)
        });

    public string GetHeadsetStatus() =>
        SendJson("{\"request\":\"GetHeadSetStatus\"}");

    // ---------------- Voice ID ----------------

    public string IsVoiceIdSet() =>
        SendJson("{\"request\":\"IsVoiceidSetted\"}");

    public string IsVoiceIdEnabledInHeadset() =>
        SendJson("{\"request\":\"IsVoiceidEnabledInHeadset\"}");

    public string GetSpeakerId(string endpointId) =>
        SendObject(new
        {
            request = "GetSpeakerID",
            endpoint = RequireEndpoint(endpointId)
        });

    public string SetSpeakerIdFromWaveFile(string endpointId, string wavePath)
    {
        if (string.IsNullOrWhiteSpace(wavePath))
            throw new ArgumentException("WAV 路径不能为空。", nameof(wavePath));

        return SendObject(new
        {
            request = "SetSpeakerID",
            endpoint = RequireEndpoint(endpointId),
            file = wavePath
        });
    }

    public string RemoveVoiceId() =>
        SendJson("{\"request\":\"RemoveSpeakerID\"}");

    // ---------------- Raw transport ----------------

    public string SendJson(string json) => SendJsonWithBuffer(json, RpcBufferChars);

    private string SendObject<T>(T value) =>
        SendJson(JsonSerializer.Serialize(value));

    private static string RequireEndpoint(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            throw new ArgumentException("Endpoint ID 不能为空。", nameof(endpointId));
        return endpointId;
    }

    private string SendJsonWithBuffer(string json, int bufferChars)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ElevocNdrRpcTransport transport = _transport
            ?? throw new InvalidOperationException("Elevoc RPC 尚未初始化。");
        return SendJsonWithBufferCore(transport, json, bufferChars);
    }

    private static string SendJsonWithBufferCore(
        ElevocNdrRpcTransport transport,
        string json,
        int bufferChars)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON 不能为空。", nameof(json));
        if (bufferChars < 2)
            throw new ArgumentOutOfRangeException(nameof(bufferChars));
        if (json.Length >= bufferChars)
        {
            throw new ArgumentOutOfRangeException(
                nameof(json),
                $"RPC 请求必须少于 {bufferChars} 个 UTF-16 字符。");
        }

        int bytes = checked(bufferChars * sizeof(char));
        nint buffer = Marshal.AllocHGlobal(bytes);

        try
        {
            byte[] zero = new byte[bytes];
            Marshal.Copy(zero, 0, buffer, bytes);

            char[] chars = json.ToCharArray();
            Marshal.Copy(chars, 0, buffer, chars.Length);
            Marshal.WriteInt16(buffer, chars.Length * sizeof(char), 0);

            transport.Invoke(buffer);

            int length = 0;
            for (; length < bufferChars; length++)
            {
                if (Marshal.ReadInt16(buffer, length * sizeof(char)) == 0)
                    break;
            }

            if (length == bufferChars)
            {
                throw new InvalidOperationException(
                    $"Elevoc RPC 返回达到 {bufferChars} wchar_t 且没有 NUL 终止符；已拒绝越界读取。");
            }

            return Marshal.PtrToStringUni(buffer, length) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max
            ? value
            : value[..max] + "…";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _transport?.Dispose();
        _transport = null;
    }
}
