namespace YogaITS.Tray.Services.Elevoc;

internal static class ElevocMicrophonePolicy
{
    public static bool SinglePerson(string? mode) => mode is "private" or "voiceid";
    public static bool MultiplePeople(string? mode) => mode is "shared" or "special";
    public static bool ShowVoice(bool internalMicrophone, bool supported, string? mode) =>
        internalMicrophone && supported && SinglePerson(mode);
    public static bool ShowPosition(bool internalMicrophone, bool supported, string? mode) =>
        internalMicrophone && supported && MultiplePeople(mode);
}
