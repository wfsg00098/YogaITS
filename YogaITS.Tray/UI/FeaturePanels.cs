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
    private readonly FeatureCard _colorCard = new("屏幕色域");
    private readonly ElevocService _audio;
    private readonly StackPanel _host;
    private readonly FeatureCard _micCard = new("麦克风降噪");
    private readonly FeatureCard _speakerCard = new("扬声器降噪");
    private readonly FeatureCard _voiceCard = new("声纹 / 仅听机主");
    private readonly Button _cancelVoice = new() { Content = "取消录制", IsEnabled = false };
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
        _cancelVoice.Click += (_, _) => _audio.CancelRegistration();
        _voiceCard.Footer.Children.Add(_cancelVoice);
        Render();
    }

    public void Render()
    {
        if (_closed) return;
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
            foreach (var (label, mode) in new[] { ("关闭", "off"), ("单人通话", "private"), ("多人通话", "shared") })
                buttons.Add(_micCard.Option(label, current.MicrophoneMode == mode,
                    () => _audio.SetMicrophoneAsync(mode), !_audio.IsRegistering));
            if (current.Special)
                buttons.Add(_micCard.Option("多人通话 + 人声定位", current.MicrophoneMode == "special",
                    () => _audio.SetMicrophoneAsync("special"), !_audio.IsRegistering));
            if (current.VoiceId)
                buttons.Add(_micCard.Option("单人通话 + 仅听机主", current.MicrophoneMode == "voiceid",
                    () => _audio.SetMicrophoneAsync("voiceid"), current.VoiceIdRegistered && !_audio.IsRegistering));
            _micCard.Options(body, buttons.ToArray());
            _micCard.Note(body, "降低麦克风采集的环境噪声，按通话人数选择单人或多人模式。人声定位与仅听机主仅在设备支持时显示；仅听机主需要先注册声纹。");
            _micCard.SetStatus($"当前麦克风设备：{current.CaptureName}");
        });
        _speakerCard.Update(audio.Downlink, body =>
        {
            var current = _audio.Snapshot;
            _speakerCard.Options(body,
                _speakerCard.Option("开启", current.SpeakerMode == "enable", () => _audio.SetSpeakerAsync(true), !_audio.IsRegistering),
                _speakerCard.Option("关闭", current.SpeakerMode == "disable", () => _audio.SetSpeakerAsync(false), !_audio.IsRegistering));
            _speakerCard.Note(body, "降低播放语音中的背景噪声，作用于当前默认输出设备。实际降噪效果由设备与音频服务支持情况决定。");
            _speakerCard.SetStatus($"当前扬声器设备：{current.RenderName}");
        });
        _voiceCard.Update(audio.VoiceId, body =>
        {
            var current = _audio.Snapshot;
            _voiceCard.Note(body, current.VoiceIdRegistered ? "已注册声纹" : "尚未注册声纹，录制后可以启用仅听机主。");
            _voiceCard.Options(body,
                _voiceCard.Option(current.VoiceIdRegistered ? "重新录制（20 秒）" : "录制声纹（20 秒）", false, async () =>
                {
                    if (!await ConfirmAsync("录制声纹", "将使用当前默认麦克风录制 20 秒。已有声纹会被新的注册结果替换；完成后启用仅听机主。", "开始录制")) return;
                    var progress = new Progress<int>(seconds => _voiceCard.SetStatus(seconds > 0
                        ? $"正在录音，剩余 {seconds} 秒。请连续朗读下方文字。" : "正在注册并验证声纹……"));
                    await _audio.RegisterVoiceAsync(progress);
                }, !_audio.IsRegistering),
                _voiceCard.Option("读取声纹状态", false, _audio.InspectVoiceAsync, current.VoiceIdRegistered && !_audio.IsRegistering),
                _voiceCard.Option("删除声纹", false, async () =>
                {
                    if (await ConfirmAsync("删除声纹", "确定删除当前声纹？删除后需要重新录制才能使用仅听机主。", "删除"))
                        await _audio.RemoveVoiceAsync();
                }, current.VoiceIdRegistered && !_audio.IsRegistering));
            _voiceCard.Note(body, "朗读示例：小明经常在不同环境下参加在线会议，他发现使用麦克风降噪功能可以显著减少环境噪音的干扰，从而使他的语音更加清晰。与他一同参会的人都对小明的语音质量印象深刻，这不仅提高了会议的质量，还保护了小明的个人隐私。");
            _voiceCard.SetStatus(_audio.RegistrationStatus);
        });
        _cancelVoice.IsEnabled = _audio.IsRegistering;
        _cancelVoice.Visibility = _audio.IsRegistering ? Visibility.Visible : Visibility.Collapsed;
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
                if (advanced.HighDynamicRangeSupported || advanced.HighDynamicRangeUserEnabled)
                    switches.Add(_colorCard.Option("HDR", advanced.HighDynamicRangeUserEnabled,
                        () => _color.SetAdvancedColorAsync(true, !advanced.HighDynamicRangeUserEnabled),
                        advanced.HighDynamicRangeUserEnabled || !advanced.LimitedByPolicy));
                if (advanced.WideColorSupported || advanced.WideColorUserEnabled)
                    switches.Add(_colorCard.Option("ACM 自动颜色管理", advanced.WideColorUserEnabled,
                        () => _color.SetAdvancedColorAsync(false, !advanced.WideColorUserEnabled),
                        advanced.WideColorUserEnabled || (!advanced.LimitedByPolicy && !advanced.HighDynamicRangeUserEnabled)));
                if (switches.Count > 0)
                    _colorCard.Options(body, switches.ToArray());
            }
            if (current.Modes.Count > 0)
                _colorCard.Options(body, current.Modes.Select(mode =>
                    _colorCard.Option(mode.Brightness is int brightness ? $"{mode.Name} [{brightness}]" : mode.Name,
                        current.CurrentMode == mode.Name, () => _color.ApplyAsync(mode.Name),
                        !current.Blocked && current.CurrentMode != mode.Name)).ToArray());
            _colorCard.Options(body,
                _colorCard.Option("切换色域时同时改变亮度", current.ApplyBrightness,
                    () => _color.SetBrightnessPreferenceAsync(!current.ApplyBrightness)),
                _colorCard.Option("在线恢复校色文件", false, _color.RecoverAsync));
            _colorCard.Note(body, "在线恢复会将本机序列号发送至 Lenovo 校色服务，用于下载与验证本机内屏的工厂校色文件。");
            _colorCard.SetStatus($"当前色域：{current.CurrentMode}。{current.Message}");
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
            _wakeCard.Note(body, "人离开后进入睡眠的速度");
            _wakeCard.Options(body, Enumerable.Range(0, 3).Select(value =>
                _wakeCard.Option(new[] { "快", "中", "慢" }[value], state.LeaveWaitTimeRaw == value,
                    () => _hpd.SetAsync(HpdSetting.LeaveWaitTime, value), state.LeaveEnabled)).ToArray());
            if (current.Features.CameraAssist)
                _wakeCard.Options(body, _wakeCard.Option("人员辅助判定", state.CameraAssist,
                    () => _hpd.SetAsync(HpdSetting.CameraAssist, state.CameraAssist ? 0 : 1), state.LeaveEnabled));
            _wakeCard.Note(body, "检测人员靠近时自动唤醒，离开后按所选速度自动睡眠。人员辅助判定仅在本机支持时显示；这些功能由 SmartSense 服务执行。");
            _wakeCard.SetStatus("");
        });
        _videoCard.Update(snapshot.Features.AutoVideoPause, body =>
        {
            if (_hpd.Snapshot.State is not { } state) return;
            _videoCard.Options(body, _videoCard.Option("人走暂停 / 人回播放", state.VideoPause,
                () => _hpd.SetAsync(HpdSetting.VideoPause, state.VideoPause ? 0 : 1)));
            _videoCard.Note(body, "检测到人员离开时暂停播放，返回时恢复播放。由 SmartSense 服务执行，仅支持部分播放器及版本。");
            _videoCard.SetStatus("");
        });
    }
}
