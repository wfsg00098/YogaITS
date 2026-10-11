namespace YogaITS.Tray.Services.Color;

internal sealed record ColorMode(string Name, int? Brightness);
internal sealed record ColorGamutSnapshot(
    bool Supported, bool Blocked, bool FactoryValid, bool ApplyBrightness,
    string CurrentMode, IReadOnlyList<ColorMode> Modes, string Message,
    NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2? AdvancedColor = null)
{
    public static ColorGamutSnapshot Unavailable { get; } = new(false, true, false, true, "", [], "");
}

internal sealed class ColorGamutService : SerializedFeatureService
{
    private MachineIdentity? _machine;
    private DisplayInfo? _display;
    private FactoryValidationResult? _factory;
    private bool _applyBrightness = UserSettingsStore.Get("GamutApplyBrightness", true);
    public ColorGamutSnapshot Snapshot { get; private set; } = ColorGamutSnapshot.Unavailable;

    private void Read()
    {
        try
        {
            _machine = MachineIdentityService.Read();
            _display = DisplayService.FindInternal(DisplayService.EnumerateActive());
            if (_display is null || string.IsNullOrWhiteSpace(_display.WcsDeviceId) ||
                !LcdBrightnessService.Query().Ok)
            {
                Snapshot = ColorGamutSnapshot.Unavailable;
                return;
            }
            _factory = FactoryRecoveryService.ValidateCurrent(_machine, _display);
            var modes = _factory.Ok ? _factory.Profiles.Select(profile =>
            {
                var brightness = IccMetaBrightness.Read(profile.Path);
                return new ColorMode(profile.Mode, brightness.Ok ? brightness.Brightness : null);
            }).ToArray() : [];
            string message = _display.IsBlocked
                ? _display.ColorBlockReason
                : !_factory.Ok ? "本机校色文件缺失或未通过验证，请在线恢复校色文件。" : "";
            Snapshot = new(true, _display.IsBlocked, _factory.Ok, _applyBrightness,
                ProfileService.ModeFromCurrent(ProfileService.GetStandardDefault(_display)), modes, message, _display.AdvancedColor);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Color capability read failed: " + ex.GetType().Name);
            Snapshot = ColorGamutSnapshot.Unavailable;
        }
    }

    public Task RefreshAsync() => RunAsync(() => { Read(); return Task.CompletedTask; });

    public Task SetAdvancedColorAsync(bool hdr, bool enabled) => RunAsync(async () =>
    {
        Read();
        if (_display is null || _display.AdvancedColor is not { } state)
            throw new InvalidOperationException("当前无法读取内屏高级颜色能力，请刷新后重试。");
        bool supported = hdr ? state.HighDynamicRangeSupported : state.WideColorSupported;
        // HDR writes change the preference only; Windows decides whether it can
        // become active (including the user's battery policy). Never change policy.
        if (enabled && (!supported || !hdr && state.LimitedByPolicy))
            throw new InvalidOperationException("系统当前不允许开启该高级颜色功能。");
        if (!hdr && enabled && state.HdrActive)
            throw new InvalidOperationException("请先关闭 HDR，再开启 ACM。");
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
        UserSettingsStore.Set("GamutApplyBrightness", enabled);
        _applyBrightness = enabled;
        Read();
        return Task.CompletedTask;
    });

    public Task RecoverAsync() => RunAsync(async () =>
    {
        Read();
        if (!Snapshot.Supported || _machine is null || _display is null)
            throw new InvalidOperationException("无法确定本机和内屏身份，不能恢复校色文件。");
        var machine = _machine;
        var result = await FactoryRecoveryService.RecoverAsync(machine, _display).ConfigureAwait(false);
        Read();
        if (!result.Ok)
            throw new InvalidOperationException(result.Message.Replace(machine.SerialNumber, "<masked>", StringComparison.OrdinalIgnoreCase));
    });

    public Task ApplyAsync(string mode) => RunAsync(() =>
    {
        Read();
        if (!Snapshot.Supported || Snapshot.Blocked || _machine is null || _display is null || _factory?.Ok != true)
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
