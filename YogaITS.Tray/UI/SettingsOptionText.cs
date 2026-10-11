namespace YogaITS.Tray.UI;

// Display text stays separate from RPC/profile identifiers.
internal static class SettingsOptionText
{
    public static string ColorLabel(string mode, int? brightness = null)
    {
        string label = mode switch
        {
            "Default" => "出厂默认（D65）",
            "Native" => "鲜艳（Native）",
            "sRGB" => "网页办公（sRGB-D65）",
            "DisplayP3" => "摄影/数码影院（P3-D65）",
            "DCIP3" => "原生数码影院（DCI-P3）",
            "AdobeRGB" => "摄影印刷（Adobe-RGB-D65）",
            "REC709" => "SDTV视频（REC709）",
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
