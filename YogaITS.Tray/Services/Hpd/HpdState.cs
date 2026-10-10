namespace YogaITS.Tray.Services.Hpd;

internal sealed record HpdState(
    int UnknownField0,
    int SensorType,
    int ApproachEnabledRaw,
    int ApproachDistanceRaw,
    int ApproachAutoAdjustRaw,
    int LeaveEnabledRaw,
    int LeaveWaitTimeRaw,
    int CameraAssistRaw,
    int LeaveDistanceRaw,
    int LeaveAutoAdjustRaw,
    int VideoPauseRaw,
    int FacialRegisteredRaw,
    int YogaModeRaw)
{
    public bool ApproachEnabled => ApproachEnabledRaw != 0;
    public bool LeaveEnabled => LeaveEnabledRaw != 0;
    public bool CameraAssist => CameraAssistRaw != 0;
    public bool VideoPause => VideoPauseRaw != 0;
    public bool FacialRegistered => FacialRegisteredRaw != 0;

    public string LeaveWaitText => LeaveWaitTimeRaw switch
    {
        0 => "快 (raw=0)",
        1 => "中 (raw=1)",
        2 => "慢 (raw=2)",
        _ => $"未知 (raw={LeaveWaitTimeRaw})"
    };
}
