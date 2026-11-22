using YogaITS.Tray.Services.QuickSettings;
using static YogaITS.Tray.Services.QuickSettings.QuickSettingsBridgeState;
using static YogaITS.Tray.Services.QuickSettings.QuickSettingsRecoveryAction;

internal static class QuickSettingsRecoveryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var recovery = new QuickSettingsRecovery();
        check(!recovery.ShowTray && recovery.Check(0, 1, Waiting) == None &&
            recovery.Check(4_999, 1, Waiting) == None && !recovery.ShowTray,
            "An existing bridge can complete its layout without showing a tray icon");
        recovery.ConfirmMount();
        check(recovery.Check(5_000, 1, Missing) == None && recovery.Check(6_199, 1, Missing) == None &&
            recovery.Check(6_200, 1, Missing) == Recycle && !recovery.ShowTray,
            "A missing footer is rescanned before its bridge is recycled without a tray flash");
        check(recovery.Check(7_399, 1, Missing) == None && !recovery.ShowTray &&
            recovery.Check(7_400, 1, Missing) == None && recovery.ShowTray,
            "A bridge that does not close after a recycle request falls back without another request");
        recovery.ResetConnection();
        check(recovery.ShowTray, "A new ShellHost does not hide an existing tray fallback before a mount");
        recovery.ConfirmMount();

        check(recovery.Check(10_000, 0, Missing) == Attach && !recovery.ShowTray,
            "Try a missing bridge on the first opening without showing the tray");
        check(recovery.DelayAfterAttach(10_000) == 10_450 && recovery.Check(10_449, 0, Missing) == None &&
            recovery.Check(10_450, 0, Missing) == Attach && !recovery.ShowTray,
            "A disconnected bridge receives two attempts separated by layout time");
        recovery.DelayAfterAttach(10_450);
        check(recovery.Check(10_900, 0, Missing) == None && recovery.ShowTray &&
            recovery.Check(18_899, 0, Missing) == None && recovery.Check(18_900, 0, Missing) == Attach,
            "An exhausted fast retry burst shows the tray and backs off");
        recovery.ConfirmMount();
        check(!recovery.ShowTray && recovery.Check(19_000, 2, Waiting) == None &&
            recovery.Check(24_000, 2, Waiting) == Recycle,
            "A confirmed mount removes the tray fallback and starts a fresh layout budget for a later fault");
        check(recovery.Check(24_300, 0, Missing) == Attach && !recovery.ShowTray,
            "Recycle must finish before attaching a new UI bridge");
        recovery.DelayAfterAttach(24_300);
        check(recovery.Check(24_750, 3, Ready) == None && !recovery.ShowTray,
            "A stale Ready reply still needs a mount marker and gives the replacement time to lay out");
    }
}
