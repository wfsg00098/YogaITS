namespace YogaITS.Tray.Services.Hpd;

internal enum HpdSetting { AutoWake, AutoSleep, LeaveWaitTime, CameraAssist, VideoPause }

internal sealed record HpdSnapshot(HpdState? State, HpdFeatureProfile Features)
{
    public static HpdSnapshot Unavailable { get; } = new(null, new(false, false, false, false, false, false));
}

internal sealed class HpdService : SerializedFeatureService
{
    private HpdRpcClient? _client;
    public HpdSnapshot Snapshot { get; private set; } = HpdSnapshot.Unavailable;

    private void Read()
    {
        try
        {
            _client ??= new HpdRpcClient();
            int capabilityRc = _client.GetCapability(out long capability);
            int rc = _client.GetAllSettingV2(out var state);
            Snapshot = new(state, HpdFeatureProfile.Create(capabilityRc == 0 ? capability : 0, state, rc));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("HPD read failed: " + ex.Message);
            _client?.Dispose();
            _client = null;
            Snapshot = HpdSnapshot.Unavailable;
        }
    }

    public Task RefreshAsync() => RunAsync(() => { Read(); return Task.CompletedTask; });

    public Task SetAsync(HpdSetting setting, int value) => RunAsync(() =>
    {
        Read();
        var features = Snapshot.Features;
        bool supported = setting switch
        {
            HpdSetting.AutoWake => features.AutoWake,
            HpdSetting.AutoSleep => features.AutoSleep,
            HpdSetting.LeaveWaitTime => features.LeaveWaitTime,
            HpdSetting.CameraAssist => features.CameraAssist,
            HpdSetting.VideoPause => features.AutoVideoPause,
            _ => false
        };
        if (!supported || _client is null)
            throw new InvalidOperationException("该 HPD 功能当前不可用。");
        try
        {
            int rc = setting switch
            {
                HpdSetting.AutoWake => _client.SetApproachEnabled(value != 0),
                HpdSetting.AutoSleep => _client.SetLeaveEnabled(value != 0),
                HpdSetting.LeaveWaitTime => _client.SetLeaveWaitTime(value),
                HpdSetting.CameraAssist => _client.SetCameraAssist(value != 0),
                HpdSetting.VideoPause => _client.SetVideoPause(value != 0),
                _ => -1
            };
            if (rc != 0) throw new InvalidOperationException($"HPD 设置失败：rc={rc}。");
        }
        finally { Read(); }
        var state = Snapshot.State;
        int? actual = setting switch
        {
            HpdSetting.AutoWake => state?.ApproachEnabledRaw,
            HpdSetting.AutoSleep => state?.LeaveEnabledRaw,
            HpdSetting.LeaveWaitTime => state?.LeaveWaitTimeRaw,
            HpdSetting.CameraAssist => state?.CameraAssistRaw,
            HpdSetting.VideoPause => state?.VideoPauseRaw,
            _ => null
        };
        if (actual != value)
            throw new InvalidOperationException("HPD 命令已发送，回读状态未达到目标。");
        return Task.CompletedTask;
    });

    protected override void Close() => _client?.Dispose();
}
