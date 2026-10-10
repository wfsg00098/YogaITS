namespace YogaITS.Tray.Services.Hpd;

internal sealed class HpdRpcClient : IDisposable
{
    private readonly DirectHpdRpcTransport _rpc;
    private bool _disposed;

    public string TransportDescription =>
        "RPCRT4 / ncalrpc / HumanPresenceDetectionEndpoint / 848A33AC-B5A7-4253-A54F-E0AC20648468 v1.0";

    public string AbiDescription =>
        "Direct NDR32 client; vendor proc/type format; context opnum 0/1 lifecycle";

    public string LastInternalDiagnostics => _rpc.LastGetAllInternalDiagnostics;

    public int StartupVersion { get; }
    public long StartupCapability { get; }

    public HpdRpcClient()
    {
        _rpc = new DirectHpdRpcTransport();

        try
        {
            // Read-only ABI validation before any mutating operation is exposed.
            int vrc = _rpc.GetVersion(out int version);
            if (vrc != 0)
                throw new InvalidOperationException($"Direct RPC GetHPDVersion self-test failed: rc={vrc}");

            int crc = _rpc.GetCapability(out long capability);
            if (crc != 0)
                throw new InvalidOperationException($"Direct RPC GetHPDCapability self-test failed: rc={crc}");

            StartupVersion = version;
            StartupCapability = capability;
        }
        catch
        {
            _rpc.Dispose();
            throw;
        }
    }

    public int GetVersion(out int version)
    {
        EnsureReady();
        return _rpc.GetVersion(out version);
    }

    public int GetCapability(out long capability)
    {
        EnsureReady();
        return _rpc.GetCapability(out capability);
    }

    public int GetAllSettingV2(out HpdState state)
    {
        EnsureReady();
        return _rpc.GetAllSettingV2(out state);
    }

    public int SetApproachEnabled(bool enabled) => SetBool(_rpc.SetApproachEnabled, enabled);
    public int SetLeaveEnabled(bool enabled) => SetBool(_rpc.SetLeaveEnabled, enabled);
    public int SetCameraAssist(bool enabled) => SetBool(_rpc.SetCameraAssist, enabled);
    public int SetVideoPause(bool enabled) => SetBool(_rpc.SetVideoPause, enabled);

    public int SetLeaveWaitTime(int raw)
    {
        EnsureReady();
        if (raw is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(raw), "Leave wait raw 必须为 0/1/2（快/中/慢）。");
        return _rpc.SetLeaveWaitTime(raw);
    }

    private int SetBool(Func<int, int> setter, bool value)
    {
        EnsureReady();
        return setter(value ? 1 : 0);
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rpc.Dispose();
    }
}
