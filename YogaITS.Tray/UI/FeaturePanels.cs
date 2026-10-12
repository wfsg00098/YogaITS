using Microsoft.UI.Xaml.Controls;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Hpd;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;
using Microsoft.UI.Xaml;

namespace YogaITS.Tray.UI;

internal sealed class FeaturePanels : IDisposable
{
    private readonly LenovoGpuWorkingModeService _gpu;
    private readonly FeatureCard _gpuCard = new("显卡工作模式");
    private readonly HpdService _hpd;
    private readonly FeatureCard _wakeCard = new("唤醒和睡眠");
    private readonly FeatureCard _videoCard = new("播放和暂停");
    private readonly ColorGamutService _color;
    private readonly FeatureCard _colorCard = new("色彩管理");
    private readonly ElevocService _audio;
    private readonly StackPanel _host;
    private readonly FeatureCard _micCard = new("麦克风降噪");
    private readonly FeatureCard _speakerCard = new("扬声器降噪");
    private readonly FeatureCard _voiceCard = new("声纹管理");
    private bool _voiceStartPending;
    private const string VoiceReadingExample = "朗读示例：小明经常在不同环境下参加在线会议，他发现使用麦克风降噪功能可以显著减少环境噪音的干扰，从而使他的语音更加清晰。与他一同参会的人都对小明的语音质量印象深刻，这不仅提高了会议的质量，还保护了小明的个人隐私。";
    private bool _closed;

    public FeaturePanels(StackPanel host, LenovoGpuWorkingModeService gpu, HpdService hpd, ColorGamutService color, ElevocService audio)
    {
        _gpu = gpu;
        _hpd = hpd;
        _color = color;
        _audio = audio;
        _host = host;
        host.Children.Add(_gpuCard.View);
        host.Children.Add(_wakeCard.View);
        host.Children.Add(_videoCard.View);
        host.Children.Add(_colorCard.View);
        host.Children.Add(_speakerCard.View);
        host.Children.Add(_micCard.View);
        host.Children.Add(_voiceCard.View);
        Render();
    }

    public void Render()
    {
        if (_closed) return;
        SettingsMotion.Reposition(_host);
        var gpu = _gpu.Snapshot;
        _gpuCard.Update(gpu.Supported, body =>
        {
            var current = _gpu.Snapshot;
            _gpuCard.Options(body,
                _gpuCard.Option("集显模式", current.LogicalMode == GpuWorkingMode.IGPUOnly,
                    () => _gpu.SelectAsync(GpuWorkingMode.IGPUOnly), current.ExternalPath == 0),
                _gpuCard.Option("智能模式", current.LogicalMode == GpuWorkingMode.Auto,
                    () => _gpu.SelectAsync(GpuWorkingMode.Auto), _gpu.AutoEventsAvailable),
                _gpuCard.Option("双显模式", current.LogicalMode == GpuWorkingMode.Default,
                    () => _gpu.SelectAsync(GpuWorkingMode.Default)));
            _gpuCard.Note(body, "智能模式：插电时双显，使用电池时自动尝试集显。外接独显显示器使用中时保留实际状态。");
            string effective = current.EffectiveMode switch { 0 => "双显", 1 => "集显", _ => "未知" };
            _gpuCard.SetStatus($"当前实际状态：{effective}。{current.Message}");
        });
        RenderHpd();
        RenderColor();
        RenderAudio();
    }

    public void Dispose()
    {
        _closed = true;
        foreach (var card in new[] { _gpuCard, _wakeCard, _videoCard, _colorCard, _micCard, _speakerCard, _voiceCard })
            card.Dispose();
    }

    private void RenderAudio()
    {
        var audio = _audio.Snapshot;
        _micCard.Update(audio.Uplink, body =>
        {
            var current = _audio.Snapshot;
            var buttons = new List<Button>();
            foreach (var (label, mode) in new[] { ("关闭降噪", "environmental"), ("单人通话", "private"), ("多人通话", "shared") })
            {
                if (mode == "shared" && !ElevocMicrophonePolicy.SupportsMultiplePeople(current.InternalMicrophone))
                    continue;
                buttons.Add(_micCard.Option(label, mode switch
                    {
                        "private" => ElevocMicrophonePolicy.SinglePerson(current.MicrophoneMode),
                        "shared" => ElevocMicrophonePolicy.MultiplePeople(current.MicrophoneMode),
                        _ => current.MicrophoneMode == mode
                    },
                    () => _audio.SetMicrophoneAsync(mode), !_audio.IsRegistering));
            }
            _micCard.Options(body, buttons.ToArray());
            if (ElevocMicrophonePolicy.ShowVoice(current.InternalMicrophone, current.VoiceId, current.MicrophoneMode))
                _micCard.Options(body, _micCard.Option("仅听机主", current.MicrophoneMode == "voiceid",
                    () => _audio.SetMicrophoneAsync(current.MicrophoneMode == "voiceid" ? "private" : "voiceid"),
                    current.VoiceIdRegistered && !_audio.IsRegistering));
            if (ElevocMicrophonePolicy.ShowPosition(current.InternalMicrophone, current.Special, current.MicrophoneMode))
                _micCard.Options(body, _micCard.Option("人声定位", current.MicrophoneMode == "special",
                    () => _audio.SetMicrophoneAsync(current.MicrophoneMode == "special" ? "shared" : "special"), !_audio.IsRegistering));
            _micCard.Note(body, SettingsOptionText.MicrophoneDescription(current.MicrophoneMode));
            _micCard.SetStatus($"当前麦克风设备：{current.CaptureName}");
        });
        _speakerCard.Update(audio.Downlink, body =>
        {
            var current = _audio.Snapshot;
            _speakerCard.Options(body,
                _speakerCard.Option("仅保留人声", current.SpeakerMode == "enable",
                    () => _audio.SetSpeakerAsync(current.SpeakerMode != "enable"), !_audio.IsRegistering));
            _speakerCard.Note(body, current.SpeakerMode switch
            {
                "enable" => "电脑扬声器将仅播放人声，并过滤其他声音。",
                "disable" => "电脑扬声器将播放所有声音。",
                _ => ""
            });
            _speakerCard.SetStatus($"当前扬声器设备：{current.RenderName}");
        });
        _voiceCard.Update(audio.InternalMicrophone && audio.VoiceId, body =>
        {
            var current = _audio.Snapshot;
            _voiceCard.Note(body, current.VoiceIdRegistered ? "已注册声纹" : "尚未注册声纹，录制后可以启用仅听机主。");
            _voiceCard.Options(body,
                _voiceCard.Option(_audio.IsRegistering ? (_audio.IsRecording ? "停止录制" : "正在注册……")
                    : current.VoiceIdRegistered ? "重新录制（20 秒）" : "录制声纹（20 秒）", false, async () =>
                {
                    if (_audio.IsRecording) { _audio.StopRecording(); return; }
                    if (_audio.IsRegistering || _voiceStartPending) return;
                    _voiceStartPending = true;
                    try
                    {
                        if (!await ConfirmAsync("录制声纹", "将使用当前默认麦克风录制 20 秒。已有声纹会被新的注册结果替换；完成后启用仅听机主。", "开始录制")) return;
                        var progress = new Progress<int>(seconds =>
                        {
                            if (_audio.IsRecording && seconds > 0)
                                _voiceCard.SetStatus($"正在录音，剩余 {seconds} 秒。请连续朗读下方文字。", VoiceReadingExample);
                        });
                        await _audio.RegisterVoiceAsync(progress);
                    }
                    finally { _voiceStartPending = false; }
                }, !_audio.IsRegistering || _audio.IsRecording, keepInteractive: true),
                _voiceCard.Option("读取声纹状态", false, _audio.InspectVoiceAsync, current.VoiceIdRegistered && !_audio.IsRegistering),
                _voiceCard.Option("删除声纹", false, async () =>
                {
                    if (await ConfirmAsync("删除声纹", "确定删除当前声纹？将先关闭仅听机主，再删除声纹。删除后需要重新录制才能使用仅听机主。", "删除"))
                        await _audio.RemoveVoiceAsync();
                }, current.VoiceIdRegistered && !_audio.IsRegistering));
            _voiceCard.SetStatus(_audio.RegistrationStatus, _audio.IsRecording ? VoiceReadingExample : null);
        });
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _host.XamlRoot,
            Title = title, Content = message,
            PrimaryButtonText = primary, CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void RenderColor()
    {
        _colorCard.Update(_color.Snapshot.Supported, body =>
        {
            var current = _color.Snapshot;
            if (current.AdvancedColor is { } advanced)
            {
                var switches = new List<Button>();
                if (advanced.HighDynamicRangeSupported)
                    switches.Add(_colorCard.Option("HDR", advanced.HdrActive,
                        () => _color.SetAdvancedColorAsync(true, !advanced.HighDynamicRangeUserEnabled),
                        advanced.HighDynamicRangeUserEnabled || advanced.HighDynamicRangeSupported));
                if (advanced.WideColorSupported || advanced.WideColorUserEnabled)
                    switches.Add(_colorCard.Option("ACM 自动颜色管理", advanced.AcmActive,
                        () => _color.SetAdvancedColorAsync(false, !advanced.WideColorUserEnabled),
                        advanced.WideColorUserEnabled || (!advanced.LimitedByPolicy && !advanced.HdrActive)));
                if (switches.Count > 0)
                    _colorCard.Options(body, switches.ToArray());
            }
            bool advancedColorActive = current.AdvancedColor?.HdrActive == true ||
                current.AdvancedColor?.AcmActive == true;
            if (!advancedColorActive)
            {
                if (current.Modes.Count > 0)
                    _colorCard.Options(body, 2, true, current.Modes.Select(mode =>
                        _colorCard.Option(SettingsOptionText.ColorLabel(mode.Name, mode.Brightness),
                            current.CurrentMode == mode.Name, () => _color.ApplyAsync(mode.Name),
                            !current.Blocked && current.CurrentMode != mode.Name)).ToArray());
                _colorCard.Options(body,
                    _colorCard.Option("切换色域时同时改变亮度", current.ApplyBrightness,
                        () => _color.SetBrightnessPreferenceAsync(!current.ApplyBrightness)),
                    _colorCard.Option("在线恢复校色文件", false, async () =>
                    {
                        if (await ConfirmAsync("在线恢复校色文件",
                            "在线恢复会将本机序列号发送至 Lenovo 校色服务，用于下载与验证本机内屏的工厂校色文件。是否继续？", "开始恢复"))
                            await _color.RecoverAsync();
                    }));
            }
            _colorCard.SetStatus(SettingsOptionText.ColorStatus(current.CurrentMode, current.Message,
                current.AdvancedColor?.HdrActive == true,
                current.AdvancedColor?.AcmActive == true));
        });
    }

    private void RenderHpd()
    {
        var snapshot = _hpd.Snapshot;
        _wakeCard.Update(snapshot.Features.WakeSleepSection, body =>
        {
            var current = _hpd.Snapshot;
            if (current.State is not { } state) return;
            _wakeCard.Options(body,
                _wakeCard.Option("自动唤醒", state.ApproachEnabled,
                    () => _hpd.SetAsync(HpdSetting.AutoWake, state.ApproachEnabled ? 0 : 1)),
                _wakeCard.Option("自动睡眠", state.LeaveEnabled,
                    () => _hpd.SetAsync(HpdSetting.AutoSleep, state.LeaveEnabled ? 0 : 1)));
            if (state.LeaveEnabled)
            {
                _wakeCard.Note(body, "人离开后进入睡眠的速度");
                _wakeCard.Options(body, Enumerable.Range(0, 3).Select(value =>
                    _wakeCard.Option(new[] { "快", "中", "慢" }[value], state.LeaveWaitTimeRaw == value,
                        () => _hpd.SetAsync(HpdSetting.LeaveWaitTime, value))).ToArray());
                if (current.Features.CameraAssist)
                    _wakeCard.Options(body, _wakeCard.Option("人员辅助判定（面部识别数据）", state.CameraAssist,
                        () => _hpd.SetAsync(HpdSetting.CameraAssist, state.CameraAssist ? 0 : 1)));
            }
            _wakeCard.Note(body, string.Join("\n", new[]
            {
                state.ApproachEnabled ? "检测到人员靠近时自动唤醒电脑。" : "",
                state.LeaveEnabled ? "检测到人员离开后，按所选速度自动进入睡眠。" : ""
            }.Where(text => text.Length > 0)));
            _wakeCard.SetStatus("");
        });
        _videoCard.Update(snapshot.Features.AutoVideoPause, body =>
        {
            if (_hpd.Snapshot.State is not { } state) return;
            _videoCard.Options(body, _videoCard.Option("人走暂停 / 人回播放", state.VideoPause,
                () => _hpd.SetAsync(HpdSetting.VideoPause, state.VideoPause ? 0 : 1)));
            _videoCard.Note(body, state.VideoPause
                ? "检测到人员离开时暂停播放，返回时恢复播放。"
                : "");
            _videoCard.SetStatus("");
        });
    }
}
