namespace YogaITS.Tray.Services.QuickSettings;

// Values are part of the message protocol in Native/Panel.cpp.
internal enum QuickSettingsBridgeState { Missing = 1, Waiting = 2, Ready = 3 }
internal enum QuickSettingsRecoveryAction { None, Attach, Recycle }

internal sealed class QuickSettingsRecovery
{
    private const int MissingGrace = 1_200;
    private const int LayoutGrace = 5_000;
    private const int FastRetry = 450;
    private const int Backoff = 8_000;
    private long? _missingSince;
    private long _nextAttach;
    private long _nextRecycle;
    private long _recycleDeadline;
    private nint _recycledBridge;
    private int _attachAttempts;

    public bool ShowTray { get; private set; }

    public void ResetConnection()
    {
        _missingSince = null;
        _nextAttach = _nextRecycle = _recycleDeadline = 0;
        _recycledBridge = 0;
        _attachAttempts = 0;
    }

    public void ConfirmMount()
    {
        ResetConnection();
        ShowTray = false;
    }

    public long DelayAfterAttach(long now) => _nextAttach = now + (_attachAttempts > 2 ? Backoff : FastRetry);

    public QuickSettingsRecoveryAction Check(long now, nint bridge, QuickSettingsBridgeState state)
    {
        _missingSince ??= now;
        if (bridge != 0)
        {
            if (bridge == _recycledBridge)
            {
                if (now >= _recycleDeadline) ShowTray = true;
                return QuickSettingsRecoveryAction.None;
            }

            int grace = state == QuickSettingsBridgeState.Missing ? MissingGrace : LayoutGrace;
            if (now - _missingSince < grace || now < _nextRecycle) return QuickSettingsRecoveryAction.None;

            // A replacement bridge has also exhausted its layout window.
            if (_recycledBridge != 0 || _attachAttempts != 0) ShowTray = true;
            _recycledBridge = bridge;
            _recycleDeadline = now + MissingGrace;
            _nextRecycle = now + Backoff;
            return QuickSettingsRecoveryAction.Recycle;
        }

        if (now < _nextAttach) return QuickSettingsRecoveryAction.None;
        if (_attachAttempts == 2)
        {
            _attachAttempts++;
            ShowTray = true;
            _nextAttach = now + Backoff;
            return QuickSettingsRecoveryAction.None;
        }
        if (_attachAttempts < 2) _attachAttempts++;
        return QuickSettingsRecoveryAction.Attach;
    }
}
