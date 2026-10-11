namespace YogaITS.Tray.Services.Elevoc;

internal static class ElevocMicrophonePolicy
{
    public static bool SinglePerson(string? mode) => mode is "private" or "voiceid";
    public static bool MultiplePeople(string? mode) => mode is "shared" or "special";
    // Uplink capability alone does not imply shared-mode support on a headset.
    public static bool SupportsMultiplePeople(bool internalMicrophone) => internalMicrophone;
    public static bool ShowVoice(bool internalMicrophone, bool supported, string? mode) =>
        internalMicrophone && supported && SinglePerson(mode);
    public static bool ShowPosition(bool internalMicrophone, bool supported, string? mode) =>
        SupportsMultiplePeople(internalMicrophone) && supported && MultiplePeople(mode);
}
