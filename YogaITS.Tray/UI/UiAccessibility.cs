using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace YogaITS.Tray.UI;

internal static class UiAccessibility
{
    private static readonly AccessibilitySettings Contrast = new();
    public static bool HighContrast => Contrast.HighContrast;
    public static string GroupedName(string group, string label) =>
        SettingsOptionText.GroupedName(group, label);

    public static Color SystemColor(int index)
    {
        uint value = GetSysColor(index);
        return Color.FromArgb(255, (byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    public static void SetState(Button button, string status)
    {
        string previous = AutomationProperties.GetItemStatus(button);
        if (previous == status) return;
        AutomationProperties.SetItemStatus(button, status);
        FrameworkElementAutomationPeer.FromElement(button)?.RaisePropertyChangedEvent(
            AutomationElementIdentifiers.ItemStatusProperty, previous, status);
    }

    public static void Announce(FrameworkElement element, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        FrameworkElementAutomationPeer.CreatePeerForElement(element)?.RaiseNotificationEvent(
            AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent,
            message, "YogaITS.Status");
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}
