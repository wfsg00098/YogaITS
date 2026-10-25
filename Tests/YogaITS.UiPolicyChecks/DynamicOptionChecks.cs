using YogaITS.Tray.UI.Controls;

internal static class DynamicOptionChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool valid, string message)
        {
            checks++;
            if (!valid) throw new InvalidOperationException("Dynamic options: " + message);
        }
        string[] three = ["关闭降噪", "单人通话", "多人通话"];
        string[] two = ["关闭降噪", "单人通话"];
        bool Rejected(int selected)
        {
            try { OptionSetPolicy.RemapConfirmed(three, selected, two); return false; }
            catch (InvalidOperationException) { return true; }
        }
        Check(OptionSetPolicy.RemapConfirmed(two, 0, three) == 0 && OptionSetPolicy.RemapConfirmed(two, 1, three) == 1,
            "Adding Multiple People preserves either existing mode");
        Check(OptionSetPolicy.RemapConfirmed(three, 0, two) == 0 && OptionSetPolicy.RemapConfirmed(three, 1, two) == 1,
            "Removing an unselected option preserves the actual selection");
        var state = new OptionSelectionState(3);
        state.Confirm(2);
        Check(Rejected(state.SelectedIndex), "The selected option cannot disappear before a successful fallback");
        Check(state.Request(1) && state.PendingIndex == 1 && Rejected(state.SelectedIndex),
            "Sending a fallback request is insufficient: wait for confirmed readback");
        state.Confirm(2); // Failed readback.
        Check(Rejected(state.SelectedIndex), "A failed fallback retains the old option set");
        state.Request(1);
        state.Confirm(1);
        Check(OptionSetPolicy.RemapConfirmed(three, state.SelectedIndex, two) == 1,
            "Confirmed Single Person permits removing Multiple People");
        string[] reordered = ["多人通话", "关闭降噪", "单人通话"];
        Check(OptionSetPolicy.RemapConfirmed(three, 2, reordered) == 0 && OptionSetPolicy.RemapConfirmed(three, 1, reordered) == 2,
            "Stable identity, not old position, preserves the mode during reordering");
        bool ambiguousRejected = false;
        try { OptionSetPolicy.RemapConfirmed(two, 1, ["单人通话", "单人通话"]); }
        catch (ArgumentException) { ambiguousRejected = true; }
        Check(ambiguousRejected, "Ambiguous identities are rejected before replacing working controls");
        return checks;
    }
}
