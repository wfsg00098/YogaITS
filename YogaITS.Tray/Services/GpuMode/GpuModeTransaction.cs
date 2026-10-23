namespace YogaITS.Tray.Services;

// One attempt: guard -> suspend our context -> SET once -> verify -> reconcile.
internal sealed class GpuModeTransaction(IGpuModeBackend backend, Func<Task> suspendKeepAwake,
    Func<int?, Task> completeKeepAwake, Func<Task>? settle = null, Action<string>? observe = null)
{
    public async Task<GpuModeOperationResult> ExecuteAsync(int target, long capability,
        string keepAwakeContext, int? requiredPower = null, CancellationToken cancellationToken = default)
    {
        if (target is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(target));
        var trace = new List<string>();
        var display = new DgpuDisplayQueryResult(DgpuDisplayState.Unknown);
        bool invoked = false;
        int? effective = null;
        void Log(string message)
        {
            trace.Add(message);
            GpuModeDiagnostics.Write(message);
            observe?.Invoke(string.Join(Environment.NewLine, trace));
        }
        GpuModeOperationResult Finish(GpuModeOperationOutcome outcome, GpuModeOperationFailure? failure = null,
            GpuModeOperationFailure? auxiliary = null)
        {
            Log($"outcome={outcome}; stage={failure?.Stage ?? auxiliary?.Stage}; readback={effective}; setInvoked={invoked}");
            if (failure is not null) Log(failure.ToString());
            if (auxiliary is not null) Log(auxiliary.ToString());
            return new(target, outcome, effective, invoked, display, failure, auxiliary, string.Join(Environment.NewLine, trace));
        }
        async Task<GpuModeOperationFailure?> CompleteAsync(int? actual, bool coreSuccess)
        {
            Log("stage=KeepAwakeRestore; backend=YogaITS.KeepAwake");
            try { await completeKeepAwake(actual).ConfigureAwait(false); Log($"keepAwake complete={actual?.ToString() ?? "unknown"}"); return null; }
            catch (Exception ex)
            {
                return GpuModeOperationFailure.From(GpuModeOperationStage.KeepAwakeRestore, "YogaITS.KeepAwake",
                    coreSuccess ? "显卡模式已切换成功，但“保持独显唤醒”恢复失败。" : "“保持独显唤醒”恢复失败。", ex);
            }
        }
        bool CanceledOrPowerChanged() => cancellationToken.IsCancellationRequested ||
            requiredPower is { } expected && backend.ReadPowerSource() != expected;
        Log($"target={target}; capability={capability}; Keep Awake: {keepAwakeContext}");
        try
        {
            if (CanceledOrPowerChanged()) return Finish(GpuModeOperationOutcome.Blocked,
                new(GpuModeOperationStage.RuntimeStateRead, "Power/Retry", "供电条件已改变或请求已取消，未执行显卡切换。"));
            Log("stage=RuntimeStateRead; backend=LENOVO_GAMEZONE_DATA.GetIGPUModeStatus");
            effective = backend.ReadMode();
            if (effective is not (0 or 1)) throw new InvalidDataException("显卡实际状态未知。");
            Log("runtimeState=" + effective);
        }
        catch (Exception ex) { return Finish(GpuModeOperationOutcome.Failed,
            GpuModeOperationFailure.From(GpuModeOperationStage.RuntimeStateRead, "LENOVO_GAMEZONE_DATA.GetIGPUModeStatus",
                "无法读取当前显卡状态，未执行切换。", ex)); }
        if (effective == target)
        {
            var warning = await CompleteAsync(effective, true);
            return Finish(warning is null ? GpuModeOperationOutcome.Success : GpuModeOperationOutcome.SuccessWithWarning,
                auxiliary: warning);
        }
        if (target == 1)
        {
            Log("stage=DisplayGuard; backend=NVAPI");
            try { display = backend.QueryDisplays(); }
            catch (Exception ex) { display = new(DgpuDisplayState.Unknown, Detail: ex.ToString(), NativeCode: ex.HResult); }
            Log($"display={display.State}; backend={display.Backend}; code={display.NativeCode}; detail={display.Detail}");
            if (display.State != DgpuDisplayState.Disconnected)
                return Finish(GpuModeOperationOutcome.Blocked, new(GpuModeOperationStage.DisplayGuard, display.Backend,
                    display.State == DgpuDisplayState.Connected
                        ? "无法进入集显模式：独立显卡当前正在驱动外接显示器。"
                        : "无法检测独显显示状态，暂不允许关闭独立显卡。", display.Detail, display.NativeCode));
            Log("stage=KeepAwakeSuspend; backend=YogaITS.KeepAwake");
            try { await suspendKeepAwake().ConfigureAwait(false); Log("keepAwake suspend=success"); }
            catch (Exception ex) { return Finish(GpuModeOperationOutcome.Failed,
                GpuModeOperationFailure.From(GpuModeOperationStage.KeepAwakeSuspend, "YogaITS.KeepAwake",
                    "无法暂停“保持独显唤醒”，未执行集显模式切换。", ex)); }
        }
        int before = effective.Value;
        try
        {
            if (CanceledOrPowerChanged())
            {
                var restore = target == 1 ? await CompleteAsync(before, false) : null;
                return Finish(GpuModeOperationOutcome.Blocked,
                    new(GpuModeOperationStage.ModeSet, "Power/Retry", "供电条件已改变或请求已取消，未执行显卡切换。"), restore);
            }
            Log("stage=ModeSet; backend=LENOVO_GAMEZONE_DATA.SetIGPUModeStatus");
            backend.SetMode(target);
            invoked = true;
            Log($"SetIGPUModeStatus({target})=invoked");
        }
        catch (Exception ex)
        {
            var restore = target == 1 ? await CompleteAsync(before, false) : null;
            return Finish(GpuModeOperationOutcome.Failed,
                GpuModeOperationFailure.From(GpuModeOperationStage.ModeSet, "LENOVO_GAMEZONE_DATA.SetIGPUModeStatus",
                    "联想显卡模式设置接口调用失败。", ex), restore);
        }
        // After SET, cancellation must not skip readback or restore blindly.
        try
        {
            Log("stage=ModeReadback; backend=LENOVO_GAMEZONE_DATA.GetIGPUModeStatus");
            await (settle?.Invoke() ?? Task.Delay(1500)).ConfigureAwait(false);
            effective = backend.ReadMode();
            if (effective is not (0 or 1)) throw new InvalidDataException("切换后返回未知显卡状态。");
            Log("GetIGPUModeStatus=" + effective);
        }
        catch (Exception ex)
        {
            effective = null;
            var suppression = await CompleteAsync(null, false);
            return Finish(GpuModeOperationOutcome.Indeterminate,
                GpuModeOperationFailure.From(GpuModeOperationStage.ModeReadback, "LENOVO_GAMEZONE_DATA.GetIGPUModeStatus",
                    "显卡模式请求已发送，但无法读取切换后的显卡状态。", ex), suppression);
        }
        bool success = effective == target;
        var completion = await CompleteAsync(effective, success);
        if (!success)
            return Finish(GpuModeOperationOutcome.Failed,
                new(GpuModeOperationStage.EffectiveStateVerify, "LENOVO_GAMEZONE_DATA.GetIGPUModeStatus",
                    "显卡模式请求已发送，但实际显卡状态未切换到目标模式。",
                    $"Requested={target}; Readback={effective}; Display={display.State}; SET=Invoked without exception"), completion);
        return Finish(completion is null ? GpuModeOperationOutcome.Success : GpuModeOperationOutcome.SuccessWithWarning,
            auxiliary: completion);
    }
}
