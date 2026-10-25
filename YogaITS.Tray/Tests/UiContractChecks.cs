using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using YogaITS.Tray.UI;
using YogaITS.Tray.Services;

namespace YogaITS.Tray.Tests;

// Runs inside the real WinUI dispatcher, never activates or captures a window.
internal static class UiContractChecks
{
    public static async Task RunAsync()
    {
        Console.WriteLine("UI contract: choice control");
        int requests = 0;
        using var bar = new ChoiceBar(null!, options: ["关闭", "单人", "多人"], columns: 3);
        bar.SelectIndex(2);
        bar.SelectionRequested += _ => requests++;
        bar.SetOptionEnabled(0, false);
        bar.RequestSelection(0);
        Ensure(requests == 0 && bar.SelectedIndex == 2, "Disabled entries must not request or change state.");
        bool rejected = false;
        try { bar.UpdateOptions(["关闭", "单人"], 2); }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected && bar.OptionCount == 3, "Removing a confirmed entry must be rejected atomically.");
        bar.RequestSelection(1);
        Ensure(requests == 1 && bar.IsPending && bar.SelectedIndex == 2, "Request must wait for readback.");
        bar.SelectIndex(1);
        bar.UpdateOptions(["关闭", "单人"], 2);
        Ensure(bar.OptionCount == 2 && bar.SelectedIndex == 1 && !bar.IsPending, "Confirm before removal.");
        for (int i = 0; i < 32; i++)
        {
            bar.UpdateOptions(["关闭", "单人", "多人"], 3);
            bar.UpdateOptions(["关闭", "单人"], 2);
        }
        bar.UpdateOptions(["单人", "关闭"], 2);
        Ensure(bar.SelectedIndex == 0, "Reorder must preserve identity rather than index.");
        bar.RequestSelection(1);
        Ensure(!bar.IsPending && requests == 1, "Disabled identity must survive reorder.");
        using var host = new AnimatedOptionHost(() => false, _ => { });
        host.SetChild(new TextBlock { Text = "dependency region" });
        host.SetExpanded(false);
        Ensure(!host.IsHitTestVisible && host.Visibility == Visibility.Collapsed, "Collapsed host must stop input.");
        host.SetExpanded(true);
        Ensure(host.IsHitTestVisible && host.Visibility == Visibility.Visible, "Host must support reversal.");
        var cards = TrayMenuWindow.CreateTileGrid([
            TrayMenuWindow.CreateOptionTile("单人", true, false, TrayMenuWindow.OptionTileHeightDip),
            TrayMenuWindow.CreateOptionTile("关闭", false, false, TrayMenuWindow.OptionTileHeightDip)], 2, TrayMenuWindow.OptionTileHeightDip);
        using var view = new OptionGroupPresentation(bar.View, cards, () => false, bar.CancelPreview);
        view.Refresh();
        Ensure(cards.Visibility == Visibility.Visible, "Native presentation must remain available.");
        var assembly = typeof(ChoiceBar).Assembly;
#if LIQUID_GLASS
        Ensure(bar.View is GlassOptionBar, "Opt-in build must contain the real glass renderer.");
        Ensure(assembly.GetType("YogaITS.Tray.UI.UiOptionsPanel") is not null, "Glass UI options must be compiled.");
#else
        Ensure(bar.View is null, "Native build must not instantiate a hidden glass tree.");
        foreach (string name in new[] { "GlassOptionBar", "LiquidGlassWindow", "UiOptionsPanel",
            "DesktopCaptureSource", "GlassMotionLayer", "AccelerationLayoutMotion" })
            Ensure(assembly.GetType("YogaITS.Tray.UI." + name) is null, "Native assembly contains " + name);
        foreach (var reference in assembly.GetReferencedAssemblies())
            Ensure(!reference.Name!.Contains("LiquidGlass", StringComparison.OrdinalIgnoreCase) &&
                !reference.Name.StartsWith("Microsoft.Graphics.Canvas", StringComparison.OrdinalIgnoreCase),
                "Native assembly references optional graphics dependencies.");
#endif
        Console.WriteLine("UI contract: hardware adapter");
        await CheckHardwareAdapterAsync();
        Console.WriteLine("UI contract: window declarations");
        CheckWindowDeclarations();
        Console.WriteLine("PASS: shared UI contract; confirmed readback, disable, dynamic remove/add/reorder, host lifecycle, native fallback, real hardware adapter, compile-time renderer isolation. No UI or capture started.");
    }

    private static async Task CheckHardwareAdapterAsync()
    {
        // A basic hidden Window supplies renderer identity. No MainWindow,
        // service, capture session, sparse registration or hardware is created.
        var window = new Window { Content = new Grid() };
        try
        {
            string? actual = null;
            int writes = 0, refreshes = 0, errors = 0;
            TaskCompletionSource? completion = null;
            HardwareChoice[] Options() => [
                new("off", "关闭", async () => { writes++; if (completion is not null) await completion.Task; }),
                new("on", "开启", async () => { writes++; if (completion is not null) await completion.Task; }),
                new("unsupported", "不可用", () => { writes++; return Task.CompletedTask; }, false)];
            HardwareChoiceGroup? group = null;
            void Refresh()
            {
                refreshes++;
                group!.Update(Options(), actual, true, 3, domain: "device-a");
            }
            using var owned = group = new(window, "真实回读契约", Refresh, _ => errors++);
            Refresh();
            Ensure(group.ConfirmedId is null && writes == 0, "Unknown readback must not select or write a fallback.");
            actual = "off";
            Refresh();
            await group.RequestAsync("unsupported");
            Ensure(writes == 0 && group.ConfirmedId == "off", "Disabled capability must not issue a SET.");
            completion = new();
            Task request = group.RequestAsync("on");
            Ensure(writes == 1 && group.PendingId == "on" && group.ConfirmedId == "off", "Pending must preserve real selection.");
            Refresh();
            Ensure(group.PendingId == "on", "Unrelated refresh must preserve pending state.");
            await group.RequestAsync("on");
            Ensure(writes == 1, "A pending request must not duplicate hardware writes.");
            completion.SetResult();
            await request;
            Ensure(group.PendingId is null && group.ConfirmedId == "off", "Unconfirmed SET must keep the old selection.");
            completion = new();
            request = group.RequestAsync("on");
            actual = "on";
            completion.SetResult();
            await request;
            Ensure(group.ConfirmedId == "on", "Only the confirmed service snapshot may change selection.");
            group.Update([new("on", "新名称", () => Task.CompletedTask), new("off", "关闭", () => Task.CompletedTask)],
                "on", true, 2, domain: "device-a");
            Ensure(group.ConfirmedId == "on", "Rename/reorder must preserve hardware identity.");
            actual = "off";
            Refresh();
            completion = new();
            request = group.RequestAsync("on");
            group.Update(Options(), "off", true, 2, domain: "device-b");
            int before = refreshes;
            completion.SetResult();
            await request;
            Ensure(refreshes == before && group.ConfirmedId == "off" && group.PendingId is null,
                "Old endpoint completion must not overwrite the new domain.");
            group.Update([new("fail", "失败", () => throw new InvalidOperationException("fixture"))], null, true, 1, domain: "device-a");
            actual = null;
            await group.RequestAsync("fail");
            Ensure(errors == 1 && group.ConfirmedId is null && group.PendingId is null, "Failed writes must return to confirmed state.");
            completion = new();
            actual = "off";
            Refresh();
            request = group.RequestAsync("on");
            group.Dispose();
            before = refreshes;
            completion.SetResult();
            await request;
            Ensure(refreshes == before, "Closed windows must ignore late completions.");
            using var measured = new HardwareChoiceGroup(window, "色彩管理", () => { }, _ => { }, fitToContent: true);
            var modes = Enumerable.Range(0, 25).Select(index => new HardwareChoice("raw_" + index,
                index == 0 ? "暗室医学影像模拟 (DICOM Dim SIM)" : "Future_Mode_" + index, () => Task.CompletedTask)).ToArray();
            measured.Update(modes, "raw_24", true, 2);
            foreach (double width in new[] { 300.0, 500, 250, 700 })
            {
                measured.View.Width = width;
                measured.View.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
                measured.View.Arrange(new Windows.Foundation.Rect(0, 0, width, measured.View.DesiredSize.Height));
                Ensure(measured.ConfirmedId == "raw_24", "Resizing adaptive rows must preserve the selected raw profile ID.");
            }
            measured.Update([new("first", "same label", () => Task.CompletedTask),
                new("second", "same label", () => Task.CompletedTask)], "second", true, 2);
            Ensure(measured.ConfirmedId == "second", "Equal display labels cannot hide or merge different raw modes.");
        }
        finally { window.Close(); }
    }

    private static void CheckWindowDeclarations()
    {
        TrayMenuWindow Create(bool full) => new(0, "contract", new(true, true, true, 8192, 0x20001),
            new(ItsMode.Intelligent, 0, "智能模式"), new(true, BatteryChargeMode.Normal, "常规模式"),
            new(true, false), new(true, false, "contract"), true, false,
            _ => throw new InvalidOperationException("No hardware writes in declaration checks"),
            (_, _) => throw new InvalidOperationException("No hardware writes in declaration checks"),
            _ => throw new InvalidOperationException("No hardware writes in declaration checks"),
            () => throw new InvalidOperationException("No GPU actions in declaration checks"),
            () => throw new InvalidOperationException("No GPU actions in declaration checks"),
            _ => throw new InvalidOperationException("No task writes in declaration checks"),
            (_, _) => throw new InvalidOperationException("No dialogs in declaration checks"), () => { }, fullWindow: full);
        var full = Create(true);
        var mini = Create(false);
        try
        {
            string[] fullText = TextIn((DependencyObject)full.Content).ToArray();
            string[] miniText = TextIn((DependencyObject)mini.Content).ToArray();
            Ensure(full.Title == "Yoga控制中心" && mini.Title == "Yoga控制中心", "Both windows must retain application branding.");
            Ensure(fullText.Contains("性能选项") && fullText.Contains("充电设置"), "Full window core sections missing.");
            Ensure(miniText.Contains("性能选项") && miniText.Contains("充电选项"), "Mini window core sections missing.");
            Ensure(!miniText.Any(text => text.Contains("保持独显唤醒")), "Mini window must omit keep-awake.");
#if LIQUID_GLASS
            Ensure(fullText.Any(text => text.Contains("UI选项")), "Glass build must expose the provided material options.");
#else
            Ensure(!fullText.Any(text => text.Contains("UI选项")), "Native build must omit glass options.");
#endif
        }
        finally { mini.Close(); full.Close(); }
    }

    private static IEnumerable<string> TextIn(DependencyObject node)
    {
        if (node is TextBlock text) yield return text.Text;
        IEnumerable<DependencyObject> children = node switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            Border { Child: { } child } => [child],
            ContentControl { Content: DependencyObject content } => [content],
            _ => []
        };
        foreach (var child in children)
            foreach (var value in TextIn(child)) yield return value;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
