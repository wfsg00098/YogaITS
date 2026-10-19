namespace YogaITS.Tray.Services.Elevoc;

internal sealed record ElevocSnapshot(
    bool Uplink, bool Downlink, bool Special, bool VoiceId, bool VoiceIdRegistered,
    bool? VoiceIdAllowedInHeadset, string? MicrophoneMode, string? SpeakerMode,
    string CaptureName, string RenderName, bool InternalMicrophone = false, bool Available = true)
{
    public static ElevocSnapshot Unavailable { get; } = new(false, false, false, false, false, null, null, null, "", "");
}
