using YogaITS.Tray.Services.Color.LenovoTcon;

namespace YogaITS.Tray.Services.Color;

internal sealed record ColorMode(string Name, int? Brightness);
internal sealed record ColorGamutSnapshot(
    bool Supported, bool Blocked, bool FactoryValid, bool ApplyBrightness,
    string CurrentMode, IReadOnlyList<ColorMode> Modes, string Message,
    NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2? AdvancedColor = null,
    ColorGamutBackend Backend = ColorGamutBackend.Icc, bool Available = true, string DisplayIdentity = "")
{
    public static ColorGamutSnapshot Unavailable { get; } = new(false, true, false, true, "", [], "", Backend: ColorGamutBackend.None);
}

internal sealed class ColorGamutService : SerializedFeatureService
{
    protected override TimeSpan ReadTimeout => TimeSpan.FromSeconds(90); // Preserve the probe's bounded TCON waits.
    protected override TimeSpan WriteTimeout => TimeSpan.FromSeconds(210); // Includes the three-minute online recovery.
    private readonly CancellationTokenSource _shutdown = new();
    public void CancelPendingOperations() => _shutdown.Cancel();
    private MachineIdentity? _machine;
    private DisplayInfo? _display;
    private FactoryValidationResult? _factory;
    private FactoryValidationResult? _cachedFactory;
    private string _factoryFingerprint = "";
    private IReadOnlyList<ColorMode> _cachedModes = [];
    private string? _lastPanel;
    private bool _applyBrightness = UserSettingsStore.Get("GamutApplyBrightness", true);
    public ColorGamutSnapshot Snapshot { get; private set; } = ColorGamutSnapshot.Unavailable;

    private void Read(bool fullValidation = false)
    {
        try
        {
            _factory = null;
            _display = DisplayService.FindInternal(DisplayService.EnumerateActive());
            if (_display is null)
            {
                _lastPanel = null;
                Snapshot = ColorGamutSnapshot.Unavailable;
                return;
            }
            var tcon = TconGamutBackend.Probe();
            if (tcon.Supported)
            {
                Snapshot = new(true, _display.IsBlocked, false, false, tcon.CurrentMode, tcon.Modes,
                    _display.IsBlocked ? _display.ColorBlockReason : "", _display.AdvancedColor, ColorGamutBackend.Tcon,
                    DisplayIdentity: _display.DevicePath);
                _lastPanel = _display.HardwareId;
                return;
            }
            if (!tcon.Available && Snapshot.Backend == ColorGamutBackend.Tcon && _lastPanel == _display.HardwareId)
                throw new InvalidOperationException("TCON 暂时无法读取，保留能力但不允许写入。");
            bool iccEligible = !string.IsNullOrWhiteSpace(_display.WcsDeviceId) && LcdBrightnessService.Query().Ok;
            if (!iccEligible && Snapshot.Backend == ColorGamutBackend.Icc && _lastPanel == _display.HardwareId)
                throw new InvalidOperationException("ICC 控制通道暂不可读，保留能力但不允许写入。");
            if (iccEligible)
            {
                _machine ??= MachineIdentityService.Read();
                string fingerprint = _display.HardwareId + "|" + string.Join("|",
                    Directory.Exists(FactoryRecoveryService.FactoryRoot)
                        ? Directory.EnumerateFiles(FactoryRecoveryService.FactoryRoot, "*.icm", SearchOption.AllDirectories)
                            .Order(StringComparer.OrdinalIgnoreCase).Select(path =>
                            {
                                var file = new FileInfo(path);
                                return $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
                            }) : []);
                if (fullValidation || _cachedFactory is null || fingerprint != _factoryFingerprint)
                {
                    _cachedFactory = FactoryRecoveryService.ValidateCurrent(_machine, _display);
                    _factoryFingerprint = fingerprint;
                    _cachedModes = _cachedFactory.Ok ? _cachedFactory.Profiles.Select(profile =>
                    {
                        var brightness = IccMetaBrightness.Read(profile.Path);
                        return new ColorMode(profile.Mode, brightness.Ok ? brightness.Brightness : null);
                    }).ToArray() : [];
                }
                _factory = _cachedFactory;
            }
            var backend = ColorBackendPolicy.Select(false, _factory?.Ok == true, tcon.Absent, iccEligible);
            if (backend == ColorGamutBackend.None)
            {
                var advanced = _display.AdvancedColor;
                bool supported = advanced is { } state && (state.HighDynamicRangeSupported ||
                    state.WideColorSupported || state.WideColorUserEnabled);
                Snapshot = new(supported, true, false, false, "未知", [], "当前没有可用的色域控制能力。",
                    advanced, ColorGamutBackend.None, DisplayIdentity: _display.DevicePath);
                return;
            }
            var modes = _factory!.Ok ? _cachedModes : [];
            string message = _display.IsBlocked
                ? _display.ColorBlockReason
                : !_factory.Ok ? "本机校色文件缺失或未通过验证，请在线恢复校色文件。" : "";
            Snapshot = new(true, _display.IsBlocked, _factory.Ok, _applyBrightness,
                ProfileService.ModeFromCurrent(ProfileService.GetStandardDefault(_display)), modes, message, _display.AdvancedColor,
                DisplayIdentity: _display.DevicePath);
            _lastPanel = _display.HardwareId;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Color capability read failed: " + ex.GetType().Name);
            Snapshot = Snapshot with { Available = false, Message = "暂时无法读取色彩状态，请稍后重试。" };
        }
    }

    public Task RefreshAsync() => RefreshAsyncCore(() => { Read(); return Task.CompletedTask; });

    public Task SetAdvancedColorAsync(bool hdr, bool enabled) => RunAsync(async () =>
    {
        Read();
        if (!Snapshot.Available || _display is null || _display.AdvancedColor is not { } state)
            throw new InvalidOperationException("当前无法读取内屏高级颜色能力，请刷新后重试。");
        bool supported = hdr ? state.HighDynamicRangeSupported : state.WideColorSupported;
        // Both switches request preferences only. HDR/power policy affects the
        // effective mode, not whether the user may submit an ACM preference.
        if (enabled && !supported)
            throw new InvalidOperationException("系统当前不允许开启该高级颜色功能。");
        var request = DisplayService.CreateColorStateRequest(_display, hdr, enabled);
        bool verified = false;
        try
        {
            int rc = NativeMethods.DisplayConfigSetColorState(ref request);
            if (rc != 0)
                throw new InvalidOperationException($"{(hdr ? "HDR" : "ACM")} 设置失败：{new System.ComponentModel.Win32Exception(rc).Message}（{rc}）。");
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                var current = DisplayService.FindInternal(DisplayService.EnumerateActive());
                if (current?.AdvancedColor is { } actual && current.HardwareId == _display.HardwareId &&
                    (hdr ? actual.HighDynamicRangeUserEnabled : actual.WideColorUserEnabled) == enabled)
                { verified = true; break; }
            }
        }
        finally { Read(); }
        if (!verified)
            throw new InvalidOperationException("设置已发送，但系统尚未确认启用偏好，请刷新查看。");
        // Refresh capabilities only. Turning off HDR/ACM never reapplies an old ICC.
    });

    public Task SetBrightnessPreferenceAsync(bool enabled) => RunAsync(() =>
    {
        Read();
        if (!Snapshot.Available || Snapshot.Backend != ColorGamutBackend.Icc)
            throw new InvalidOperationException("当前色域控制不支持 ICC 亮度联动。");
        UserSettingsStore.Set("GamutApplyBrightness", enabled);
        _applyBrightness = enabled;
        Read();
        return Task.CompletedTask;
    });

    public Task RecoverAsync() => RunAsync(async () =>
    {
        Read();
        if (!Snapshot.Available || !Snapshot.Supported || Snapshot.Backend != ColorGamutBackend.Icc || _machine is null || _display is null)
            throw new InvalidOperationException("无法确定本机和内屏身份，不能恢复校色文件。");
        var machine = _machine;
        var result = await FactoryRecoveryService.RecoverAsync(machine, _display, cancellationToken: _shutdown.Token).ConfigureAwait(false);
        Read(fullValidation: true);
        if (!result.Ok)
            throw new InvalidOperationException(result.Message.Replace(machine.SerialNumber, "<masked>", StringComparison.OrdinalIgnoreCase));
    });

    public Task ApplyAsync(string mode) => RunAsync(() =>
    {
        Read(fullValidation: true);
        if (!Snapshot.Available) throw new InvalidOperationException("色彩状态暂不可读，未发送切换命令。");
        if (Snapshot.Backend == ColorGamutBackend.Tcon)
        {
            if (Snapshot.Blocked || !Snapshot.Modes.Any(item => item.Name == mode))
                throw new InvalidOperationException("当前系统状态或内屏能力不允许切换该 TCON 色域。");
            try { TconGamutBackend.Apply(mode); }
            finally { Read(); }
            return Task.CompletedTask;
        }
        if (!Snapshot.Supported || Snapshot.Backend != ColorGamutBackend.Icc || Snapshot.Blocked ||
            _machine is null || _display is null || _factory?.Ok != true)
            throw new InvalidOperationException("色域切换不可用。请检查 HDR / ACM 和本机校色文件。");
        var profile = _factory.Profiles.SingleOrDefault(p => p.Mode == mode)
            ?? throw new InvalidOperationException("该色域没有经过本机校色文件验证。");
        GamutApplyResult result;
        try { result = GamutApplyService.Apply(_display, profile, _machine, _applyBrightness); }
        finally { Read(); }
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        return Task.CompletedTask;
    });
}
