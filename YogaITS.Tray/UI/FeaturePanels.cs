using Microsoft.UI.Xaml.Controls;
using YogaITS.Tray.Services;

namespace YogaITS.Tray.UI;

internal sealed class FeaturePanels
{
    private readonly LenovoGpuWorkingModeService _gpu;
    private readonly FeatureCard _gpuCard = new("显卡工作模式");

    public FeaturePanels(StackPanel host, LenovoGpuWorkingModeService gpu)
    {
        _gpu = gpu;
        host.Children.Add(_gpuCard);
        var refresh = new Button { Content = "刷新功能与状态" };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try { await _gpu.RefreshAsync(); }
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
    }
}
