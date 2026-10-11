namespace YogaITS.Tray.Services.Elevoc;

internal sealed record ElevocSnapshot(
    bool Uplink, bool Downlink, bool Special, bool VoiceId, bool VoiceIdRegistered,
    bool? VoiceIdAllowedInHeadset, string? MicrophoneMode, string? SpeakerMode,
    string CaptureName, string RenderName, bool InternalMicrophone = false)
{
    public static ElevocSnapshot Unavailable { get; } = new(false, false, false, false, false, null, null, null, "", "");
}

internal sealed class ElevocService : SerializedFeatureService
{
    private ElevocRpcClient? _client;
    private CoreAudioEndpointMonitor? _audio;
    private AudioEndpointInfo? _capture;
    private CancellationTokenSource? _registration;
    private long _endpointGeneration;
    public ElevocSnapshot Snapshot { get; private set; } = ElevocSnapshot.Unavailable;
    public bool IsRegistering => _registration is not null;
    public string RegistrationStatus { get; private set; } = "";

    private static bool Ok(string response) => string.Equals(ElevocJson.Status(response), "OK", StringComparison.OrdinalIgnoreCase);
    private static string Safe(Func<string> request)
    {
        try { return request(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Elevoc read failed: " + ex.Message); return ""; }
    }

    private void Read()
    {
        try
        {
            if (_audio is null)
            {
                _audio = new CoreAudioEndpointMonitor();
                _audio.DefaultEndpointChanged += EndpointChanged;
            }
            _client ??= new ElevocRpcClient(); // Constructor performs the read-only ABI self-test.
            _capture = _audio.GetDefault(AudioDataFlow.Capture);
            var render = _audio.GetDefault(AudioDataFlow.Render);
            string captureJson = _capture is null ? "" : Safe(() => _client.GetFunctionsByEndpoint(_capture.Id));
            string renderJson = render is null ? "" : Safe(() => _client.GetFunctionsByEndpoint(render.Id));
            var captureFunctions = Ok(captureJson) ? ElevocJson.SupportedFunctions(captureJson) : [];
            var renderFunctions = Ok(renderJson) ? ElevocJson.SupportedFunctions(renderJson) : [];
            string mic = Safe(_client.GetMicrophoneMode);
            string speaker = Safe(_client.GetSpeakerMode);
            string voice = Safe(_client.IsVoiceIdSet);
            string headset = Safe(_client.IsVoiceIdEnabledInHeadset);
            bool uplink = _capture is not null && captureFunctions.Contains("uplink") && Ok(mic);
            Snapshot = new(uplink,
                render is not null && renderFunctions.Contains("downlink") && Ok(speaker),
                uplink && captureFunctions.Contains("special"),
                uplink && captureFunctions.Contains("speaker_extraction") && Ok(voice),
                Ok(voice) && ElevocJson.Bool(voice, "is_voiceid_setted") == true,
                Ok(headset) ? ElevocJson.Bool(headset, "is_voiceid_enabled_in_headset") : null,
                Ok(mic) ? ElevocJson.Mode(mic) : null, Ok(speaker) ? ElevocJson.Mode(speaker) : null,
                _capture?.FriendlyName ?? "", render?.FriendlyName ?? "", _capture?.IsInternalMicrophone == true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Elevoc initialization failed: " + ex.Message);
            _client?.Dispose();
            _client = null;
            Snapshot = ElevocSnapshot.Unavailable;
        }
    }

    private void EndpointChanged(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref _endpointGeneration);
        CancelRegistration();
        // Do not SetMode on device changes: preserve the backend's preferred mode.
        _ = RefreshAsync();
    }

    public Task RefreshAsync() => RunAsync(() => { Read(); return Task.CompletedTask; });

    private static void RequireOk(string response)
    {
        if (!Ok(response))
            throw new InvalidOperationException("Elevoc 操作失败：" + (ElevocJson.Status(response) ?? "响应无效"));
    }

    private void RequireVoice()
    {
        if (!Snapshot.VoiceId || _capture is null || _client is null)
            throw new InvalidOperationException("当前麦克风没有可用的声纹能力。");
    }

    public Task SetMicrophoneAsync(string mode) => RunAsync(async () =>
    {
        Read();
        if (!Snapshot.Uplink || _client is null ||
            (mode == "shared" && !ElevocMicrophonePolicy.SupportsMultiplePeople(Snapshot.InternalMicrophone)) ||
            (mode == "special" && !ElevocMicrophonePolicy.ShowPosition(Snapshot.InternalMicrophone, Snapshot.Special, Snapshot.MicrophoneMode)) ||
            (mode == "voiceid" && (!ElevocMicrophonePolicy.ShowVoice(Snapshot.InternalMicrophone, Snapshot.VoiceId, Snapshot.MicrophoneMode) || !Snapshot.VoiceIdRegistered)))
            throw new InvalidOperationException("当前麦克风不支持该降噪模式。");
        await SetMicrophoneModeAndReadAsync(mode);
    });

    // Caller holds the service gate. Reuse this for disabling Voice ID before deletion.
    private async Task SetMicrophoneModeAndReadAsync(string mode)
    {
        try
        {
            RequireOk(_client!.SetMicrophoneMode(mode));
            // Match the final probe: allow the backend to apply the mode before verification.
            await Task.Delay(150).ConfigureAwait(false);
        }
        finally { Read(); }
        System.Diagnostics.Debug.WriteLine(
            $"Elevoc uplink verify: expected={mode}, actual={Snapshot.MicrophoneMode ?? "<unknown>"}, capture={Snapshot.CaptureName}");
        if (Snapshot.MicrophoneMode != mode || (mode == "voiceid" && !Snapshot.VoiceIdRegistered))
            throw new InvalidOperationException(
                $"模式设置已发送，但服务回读未确认生效。请求模式：{mode}；实际模式：{Snapshot.MicrophoneMode ?? "未知"}。" +
                $"当前麦克风设备：{Snapshot.CaptureName}。" +
                (mode == "voiceid" && !Snapshot.VoiceIdRegistered ? "服务未确认已注册声纹。" : ""));
    }

    public Task SetSpeakerAsync(bool enabled) => RunAsync(() =>
    {
        Read();
        if (!Snapshot.Downlink || _client is null)
            throw new InvalidOperationException("当前扬声器不支持降噪。");
        try { RequireOk(_client.SetSpeakerNoiseCancellation(enabled)); }
        finally { Read(); }
        if (Snapshot.SpeakerMode != (enabled ? "enable" : "disable"))
            throw new InvalidOperationException("扬声器降噪设置未通过回读验证。");
        return Task.CompletedTask;
    });

    public Task RemoveVoiceAsync() => RunAsync(async () =>
    {
        Read();
        RequireVoice();
        string endpoint = _capture!.Id;
        long generation = Interlocked.Read(ref _endpointGeneration);
        if (Snapshot.MicrophoneMode == "voiceid")
            await SetMicrophoneModeAndReadAsync("private");
        // Do not delete on a newly selected endpoint after the asynchronous mode change.
        if (generation != Interlocked.Read(ref _endpointGeneration) ||
            _audio!.GetDefault(AudioDataFlow.Capture)?.Id != endpoint)
            throw new OperationCanceledException("音频设备已变化，声纹删除已停止。");
        RequireVoice();
        try { RequireOk(_client!.RemoveVoiceId()); }
        finally { Read(); }
        if (Snapshot.VoiceIdRegistered)
            throw new InvalidOperationException("服务回读显示声纹仍然存在。");
        RegistrationStatus = "声纹已删除。";
    });

    public Task InspectVoiceAsync() => RunAsync(() =>
    {
        Read();
        RequireVoice();
        string response = _client!.GetSpeakerId(_capture!.Id);
        RequireOk(response);
        var voice = ElevocJson.SpeakerId(response);
        RegistrationStatus = voice.IsValid ? "声纹有效：192 维有限数值。"
            : Snapshot.MicrophoneMode == "private" ? "单人通话模式下，服务只返回声纹元信息；这不表示声纹丢失。"
            : "服务未返回完整声纹数据。";
        return Task.CompletedTask;
    });

    public Task RegisterVoiceAsync(IProgress<int>? progress = null) => RunAsync(async () =>
    {
        Read();
        RequireVoice();
        if (!Snapshot.InternalMicrophone)
            throw new InvalidOperationException("请使用内置麦克风录制声纹。");
        string endpoint = _capture!.Id;
        string? originalMode = Snapshot.MicrophoneMode;
        long generation = Interlocked.Read(ref _endpointGeneration);
        using var cancellation = new CancellationTokenSource();
        _registration = cancellation;
        CancellationToken token = cancellation.Token;
        bool success = false;
        bool written = false;
        string directory = Path.Combine(Path.GetTempPath(), "YogaITS-Voice-" + Guid.NewGuid().ToString("N"));
        string wavePath = Path.Combine(directory, "voice.wav");
        RegistrationStatus = "正在录制声纹，请连续朗读 20 秒。";
        NotifyChanged();
        try
        {
            RequireOk(_client!.SetMicrophoneMode("private"));
            await Task.Delay(150, token);
            string privateMode = _client.GetMicrophoneMode();
            RequireOk(privateMode);
            if (ElevocJson.Mode(privateMode) != "private")
                throw new InvalidOperationException("无法确认已进入单人通话模式。");
            using (var recorder = new VoiceRegistrationRecorder())
                await recorder.RecordAsync(wavePath, TimeSpan.FromSeconds(20), progress, token);

            CheckEndpoint();
            written = true;
            RequireOk(_client.SetSpeakerIdFromWaveFile(endpoint, wavePath));
            string isSet = _client.IsVoiceIdSet();
            RequireOk(isSet);
            if (ElevocJson.Bool(isSet, "is_voiceid_setted") != true)
                throw new InvalidOperationException("服务未确认声纹已注册。");
            CheckEndpoint();
            RequireOk(_client.SetMicrophoneMode("voiceid"));
            await Task.Delay(150, token);
            string active = _client.GetMicrophoneMode();
            RequireOk(active);
            if (ElevocJson.Mode(active) != "voiceid")
                throw new InvalidOperationException("服务未确认仅听机主模式生效。");
            // GetSpeakerID must follow activation; private mode gates out data[].
            string response = _client.GetSpeakerId(endpoint);
            RequireOk(response);
            var voice = ElevocJson.SpeakerId(response);
            if (!voice.IsValid || voice.Data.Length != 192)
                throw new InvalidOperationException("注册后的声纹未通过 192 维有限数值验证。");
            CheckEndpoint();
            Read();
            success = Snapshot.VoiceIdRegistered && Snapshot.MicrophoneMode == "voiceid";
            if (!success) throw new InvalidOperationException("声纹最终状态验证失败。");
            RegistrationStatus = "声纹注册成功，已启用仅听机主模式。";
        }
        catch
        {
            RegistrationStatus = written ? "注册未完整通过，服务可能已写入新声纹。" : "声纹注册已取消或失败。";
            throw;
        }
        finally
        {
            // Restore only on the same endpoint. Device-change handling remains read-only.
            if (!success && originalMode is not null && generation == Interlocked.Read(ref _endpointGeneration))
            {
                try { RequireOk(_client!.SetMicrophoneMode(originalMode)); }
                catch { RegistrationStatus += " 原模式恢复失败。"; }
            }
            _registration = null;
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch { System.Diagnostics.Debug.WriteLine("Temporary voice recording cleanup failed."); }
            Read();
        }

        void CheckEndpoint()
        {
            token.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _endpointGeneration) ||
                _audio!.GetDefault(AudioDataFlow.Capture)?.Id != endpoint)
                throw new OperationCanceledException("音频设备已变化，注册已停止。");
        }
    });

    public void CancelRegistration()
    {
        try { _registration?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    protected override void Close()
    {
        _audio?.Dispose();
        _client?.Dispose();
    }
}
