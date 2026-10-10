namespace YogaITS.Tray.Services.Color;

internal sealed record ColorMode(string Name, int? Brightness);
internal sealed record ColorGamutSnapshot(
    bool Supported, bool Blocked, bool FactoryValid, bool ApplyBrightness,
    string CurrentMode, IReadOnlyList<ColorMode> Modes, string Message)
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
                ? "HDR / ACM 已开启或高级颜色状态不可读取。请关闭 HDR / ACM 后刷新；不会自动恢复之前的色域。"
                : !_factory.Ok ? "本机校色文件缺失或未通过验证，请在线恢复校色文件。" : "";
            Snapshot = new(true, _display.IsBlocked, _factory.Ok, _applyBrightness,
                ProfileService.ModeFromCurrent(ProfileService.GetStandardDefault(_display)), modes, message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Color capability read failed: " + ex.GetType().Name);
            Snapshot = ColorGamutSnapshot.Unavailable;
        }
    }

    public Task RefreshAsync() => RunAsync(() => { Read(); return Task.CompletedTask; });

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
        var result = await FactoryRecoveryService.RecoverAsync(_machine, _display).ConfigureAwait(false);
        Read();
        if (!result.Ok)
            throw new InvalidOperationException(result.Message.Replace(_machine.SerialNumber, "<masked>", StringComparison.OrdinalIgnoreCase));
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
