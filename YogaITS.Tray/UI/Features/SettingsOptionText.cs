namespace YogaITS.Tray.UI.Features;

// Display text stays separate from RPC/profile identifiers.
internal static class SettingsOptionText
{
    public static int[] KeyboardBacklightStates(int levels) => levels switch
    {
        1 => [0, 1],
        2 => [0, 1, 2],
        3 => [0, 3, 1, 2],
        _ => []
    };

    public static string KeyboardBacklightLabel(int levels, int state) => state switch
    {
        0 => "关闭",
        1 => levels == 1 ? "开启" : "低亮度",
        2 => "高亮度",
        3 => "自动",
        _ => "未知"
    };

    public static string VoiceRegistrationStatus(string status, bool recording, int remainingSeconds) =>
        recording && remainingSeconds > 0
            ? $"正在录音，剩余 {remainingSeconds} 秒。请连续朗读下方文字。"
            : status;

    public static string FlipToStartDescription(bool operated) =>
        "开启后，当您打开笔记本盖子时，系统将自动开机。" +
        (operated ? "（设置改变后，重启电脑方可生效）" : "");

    public static string GroupedName(string group, string label) =>
        $"{group}中的{label.Replace("\r", "").Replace("\n", "")}";

    public static string ColorStatus(string mode, string message, bool hdr, bool acm) => hdr || acm
        ? "当前颜色配置：不适用。HDR或者ACM已开启。"
        : $"当前颜色配置：{ColorLabel(mode)}。{message}";

    public static string ColorLabel(string mode, int? brightness = null)
    {
        string label = mode.ToUpperInvariant() switch
        {
            "TCONP3" => "P3",
            "TCONSRGB" => "sRGB",
            "TCONADOBERGB" => "Adobe RGB",
            "DEFAULT" => "默认 (D65)",
            "NATIVE" => "鲜艳 (Native)",
            "SRGB" => "网页办公 (sRGB-D65)",
            "DISPLAYP3" => "摄影/数码影院 (P3-D65)",
            "DCIP3" => "原生数码影院 (P3-DCI)",
            "ADOBERGB" => "摄影印刷 (Adobe RGB)",
            "REC709" => "HDTV视频 (REC709)",
            "DICOM_DIM" => "暗室医学影像模拟 (DICOM Dim SIM)",
            "DICOM_OFFICE" => "办公室医学影像模拟 (DICOM Office SIM)",
            _ => mode
        };
        return brightness is int value ? $"{label}[{value}]" : label;
    }

    public static string MicrophoneDescription(string? mode) => mode switch
    {
        "environmental" => "电脑将拾取环境中所有的声音。",
        "private" => "拾取正对电脑的人声，消除环境噪音和其他角度的声音。",
        "voiceid" => "电脑将通过学习您的声音，优先捕获您的人声，消除环境噪音和其他人的声音。",
        "shared" => "捕获电脑前较大范围的人声，并消除环境杂音。",
        "special" => "捕获电脑前较大范围的人声，并还原声音的空间位置信息。",
        _ => ""
    };

    public static string ChargeDescription(string? mode, bool night)
    {
        if (night)
            return "根据每天的充电情况，优化设备在夜间的充电速度和容量。在夜间将设备充至80%电量，次日早上再根据您的日常使用情况，提前将设备充至100%电量。使用该模式需插入电源。";
        string description = mode switch
        {
            "Conservation" => "电量维持在75%-80%，适合长期使用，可延长电池寿命。",
            "Normal" => "连接电源后的常规充电模式。",
            "RapidCharge" => "使用比常规模式更快的速度将电池充满。",
            _ => ""
        };
        return description;
    }
}
