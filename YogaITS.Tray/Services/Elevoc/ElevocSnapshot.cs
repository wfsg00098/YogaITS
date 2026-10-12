namespace YogaITS.Tray.Services.Elevoc;

internal sealed record ElevocSnapshot(
    bool Uplink, bool Downlink, bool Special, bool VoiceId, bool VoiceIdRegistered,
    bool? VoiceIdAllowedInHeadset, string? MicrophoneMode, string? SpeakerMode,
    string CaptureName, string RenderName, bool InternalMicrophone = false)
{
    public static ElevocSnapshot Unavailable { get; } = new(false, false, false, false, false, null, null, null, "", "");

    // An older full read must not undo an independently confirmed downlink operation.
    public ElevocSnapshot MergeSpeaker(ElevocSnapshot latest, bool speakerChanged) => speakerChanged
        ? this with { Downlink = latest.Downlink, SpeakerMode = latest.SpeakerMode, RenderName = latest.RenderName }
        : this;
}
