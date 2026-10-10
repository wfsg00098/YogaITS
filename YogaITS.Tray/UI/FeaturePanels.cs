using Microsoft.UI.Xaml.Controls;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Hpd;
using YogaITS.Tray.Services.Color;

namespace YogaITS.Tray.UI;

internal sealed class FeaturePanels
{
    private readonly LenovoGpuWorkingModeService _gpu;
    private readonly FeatureCard _gpuCard = new("显卡工作模式");
    private readonly HpdService _hpd;
    private readonly FeatureCard _wakeCard = new("唤醒和睡眠");
    private readonly FeatureCard _videoCard = new("播放和暂停");
    private readonly ColorGamutService _color;
    private readonly FeatureCard _colorCard = new("屏幕色域");

    public FeaturePanels(StackPanel host, LenovoGpuWorkingModeService gpu, HpdService hpd, ColorGamutService color)
    {
        _gpu = gpu;
        _hpd = hpd;
        _color = color;
        host.Children.Add(_gpuCard.View);
        host.Children.Add(_wakeCard.View);
        host.Children.Add(_videoCard.View);
        host.Children.Add(_colorCard.View);
        var refresh = new Button { Content = "刷新功能与状态" };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try { await Task.WhenAll(_gpu.RefreshAsync(), _hpd.RefreshAsync(), _color.RefreshAsync()); }
            finally { refresh.IsEnabled = true; }
        };
        host.Children.Add(refresh);
        Render();
    }

    public void Render()
    {
        var gpu = _gpu.Snapshot;
        _gpuCard.Update(gpu.Supported, body =>
        {
            var current = _gpu.Snapshot;
            _gpuCard.Options(body,
                _gpuCard.Option("集显模式", current.LogicalMode == GpuWorkingMode.IGPUOnly,
                    () => _gpu.SelectAsync(GpuWorkingMode.IGPUOnly), current.ExternalPath == 0),
                _gpuCard.Option("智能模式", current.LogicalMode == GpuWorkingMode.Auto,
                    () => _gpu.SelectAsync(GpuWorkingMode.Auto)),
                _gpuCard.Option("双显模式", current.LogicalMode == GpuWorkingMode.Default,
                    () => _gpu.SelectAsync(GpuWorkingMode.Default)));
            _gpuCard.Note(body, "智能模式：插电时双显，使用电池时自动尝试集显。外接独显显示器使用中时保留实际状态。");
            string effective = current.EffectiveMode switch { 0 => "双显", 1 => "集显", _ => "未知" };
            _gpuCard.SetStatus($"当前实际状态：{effective}。{current.Message}");
        });
        RenderHpd();
        RenderColor();
    }

    private void RenderColor()
    {
        _colorCard.Update(_color.Snapshot.Supported, body =>
        {
            var current = _color.Snapshot;
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
            _wakeCard.SetStatus("");
        });
        _videoCard.Update(snapshot.Features.AutoVideoPause, body =>
        {
            if (_hpd.Snapshot.State is not { } state) return;
            _videoCard.Options(body, _videoCard.Option("人走暂停 / 人回播放", state.VideoPause,
                () => _hpd.SetAsync(HpdSetting.VideoPause, state.VideoPause ? 0 : 1)));
            _videoCard.Note(body, "由 SmartSense 服务执行，仅支持部分播放器及版本。");
            _videoCard.SetStatus("");
        });
    }
}
