namespace YogaITS.Tray.Services.Hpd;

/// <summary>
/// Final feature exposure policy for the YogaITS integration scope.
///
/// It preserves Lenovo's capability semantics, but all inputs come from the
/// SmartSense HPD RPC backend. No Lenovo PC Manager / SensorFusion registry
/// keys are required.
/// </summary>
internal sealed record HpdFeatureProfile(
    bool WakeSleepSection,
    bool AutoWake,
    bool AutoSleep,
    bool LeaveWaitTime,
    bool CameraAssist,
    bool AutoVideoPause)
{
    private const long CameraAssistBit = 0x0040;

    public static HpdFeatureProfile Create(long capability, HpdState state, int getAllRc)
    {
        if (getAllRc != 0)
            return new(false, false, false, false, false, false);

        // Lenovo's F4001 / Smart Aware overall ability is represented by the
        // first GetHPDAllSettingV2 output (called unAwareAbility in Lenovo logs).
        bool aware = state.UnknownField0 != 0;

        // For the final YogaITS scope we expose the core Wake/Sleep controls
        // only when Lenovo's overall Smart Aware ability is present.
        bool autoWake = aware;
        bool autoSleep = aware;
        bool leaveWait = aware;

        // Camera Assist is part of Lenovo's normal (non sensor_type==4) detail
        // layout. The backend capability bit is also required here so YogaITS
        // never exposes the switch on a backend that does not advertise it.
        bool cameraAssist =
            aware &&
            state.SensorType != 4 &&
            (capability & CameraAssistBit) != 0;

        // Lenovo PC Manager exposes "播放和暂停" as feature F4009.
        // Native logic: sensor type 3/4 explicitly enables F4009; otherwise it
        // falls back to the overall Smart Aware ability. We use the RPC
        // sensor_type instead of Lenovo's registry mirror.
        bool autoVideoPause =
            state.SensorType is 3 or 4 ||
            aware;

        return new(
            WakeSleepSection: aware,
            AutoWake: autoWake,
            AutoSleep: autoSleep,
            LeaveWaitTime: leaveWait,
            CameraAssist: cameraAssist,
            AutoVideoPause: autoVideoPause);
    }

    public string Summary =>
        $"wakeSleep={WakeSleepSection}, wake={AutoWake}, sleep={AutoSleep}, " +
        $"leaveWait={LeaveWaitTime}, cameraAssist={CameraAssist}, autoVideoPause={AutoVideoPause}";
}
