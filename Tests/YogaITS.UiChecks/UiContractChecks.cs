using YogaITS.Tray.UI.Controls;
using YogaITS.Tray.UI.Shell;
#if LIQUID_GLASS
using YogaITS.Tray.UI.Glass.Controls;
using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Layout;
using YogaITS.Tray.UI.Glass.Windowing;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.AlwaysOnUsb;
using YogaITS.Tray.Services.KeyboardBacklight;
using YogaITS.Tray.Services.Hpd;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;
using YogaITS.Tray.UI.Features;
using YogaITS.Tray.UI.Layout;
using Microsoft.UI.Xaml.Media;

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
        CheckNaturalHostMeasurement();
        CheckPresentationLifecycle();
        CheckDisabledTiles();
        var cards = TrayMenuWindow.CreateTileGrid([
            TrayMenuWindow.CreateOptionTile("单人", true, false, TrayMenuWindow.OptionTileHeightDip),
            TrayMenuWindow.CreateOptionTile("关闭", false, false, TrayMenuWindow.OptionTileHeightDip)], 2, TrayMenuWindow.OptionTileHeightDip);
        using var view = new OptionGroupPresentation(bar.View, cards, () => false, bar.CancelPreview);
        view.Refresh();
        Ensure(cards.Visibility == Visibility.Visible, "Native presentation must remain available.");
        Ensure(cards.ChildrenTransitions is null || cards.ChildrenTransitions.Count == 0,
            "Native card grids must resize without reposition easing.");
        var assembly = typeof(ChoiceBar).Assembly;
#if LIQUID_GLASS
        Ensure(bar.View is GlassOptionBar, "Opt-in build must contain the real glass renderer.");
        Ensure(assembly.GetType("YogaITS.Tray.UI.Glass.Controls.UiOptionsPanel") is not null, "Glass UI options must be compiled.");
        CheckMotionMaterialSwitch();
#else
        Ensure(bar.View is null, "Native build must not instantiate a hidden glass tree.");
        foreach (string name in new[] { "Controls.GlassOptionBar", "Windowing.LiquidGlassWindow", "Controls.UiOptionsPanel",
            "Capture.DesktopCaptureSource", "Animation.GlassMotionLayer", "Animation.AccelerationLayoutMotion" })
            Ensure(assembly.GetType("YogaITS.Tray.UI.Glass." + name) is null, "Native assembly contains " + name);
        foreach (var reference in assembly.GetReferencedAssemblies())
            Ensure(!reference.Name!.Contains("LiquidGlass", StringComparison.OrdinalIgnoreCase) &&
                !reference.Name.StartsWith("Microsoft.Graphics.Canvas", StringComparison.OrdinalIgnoreCase),
                "Native assembly references optional graphics dependencies.");
#endif
        Console.WriteLine("UI contract: hardware adapter");
        await CheckHardwareAdapterAsync();
        Console.WriteLine("UI contract: window declarations");
        CheckWindowDeclarations();
        await CheckAlwaysOnUsbPanelAsync();
        await CheckAboutLinksAsync();
        Console.WriteLine("PASS: shared UI contract; confirmed readback, disable, dynamic remove/add/reorder, host lifecycle, native fallback, real hardware adapter, compile-time renderer isolation. No UI or capture started.");
    }

#if LIQUID_GLASS
    private static void CheckMotionMaterialSwitch()
    {
        bool glass = true;
        TabFrame published = default;
        using var motion = new AccelerationTabMotion((frame, _) => published = frame, () => glass, layout: true, elasticLayout: true);
        motion.SetTarget(new(0, 0, 100, 48), false);
        motion.SetPressed(false, true);
        motion.SetTarget(new(70, 20, 160, 48), true);
        Ensure(!motion.Physics.IsSettled, "Glass resize should retain physical movement.");
        glass = false;
        var native = new TabFrame(90, 35, 180, 48);
        motion.SetTarget(native, true);
        Ensure(motion.Physics.IsSettled && published == native,
            "Native mode must snap even if a queued resize still requests physical animation.");
        glass = true;
        motion.SetPressed(false, true);
        motion.SetTarget(new(120, 35, 210, 48), true);
        Ensure(!motion.Physics.IsSettled, "Switching back to glass must enable its original physics.");
        glass = false;
        motion.SetPressed(false, true);
        Ensure(motion.Physics.IsSettled && published == motion.Physics.Target,
            "A late input callback must discard glass momentum in native mode.");
    }

    private static void CheckResizeRegions(TrayMenuWindow full, TrayMenuWindow mini)
    {
        var root = (Grid)full.Content;
        using var regions = new GlassResizeRegions(full, root);
        foreach (double width in new[] { 560.0, 700.0, 480.0 })
        {
            regions.Update(true);
            root.Measure(new Windows.Foundation.Size(width, 850));
            root.Arrange(new Windows.Foundation.Rect(0, 0, width, 850));
            var bounds = regions.Children.OfType<FrameworkElement>().Select(grip =>
                grip.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, grip.ActualWidth, grip.ActualHeight))).ToArray();
            Ensure(regions.Visibility == Visibility.Visible && bounds.Length == 8,
                "A resizable glass window must expose four WinUI edges and four corners.");
            foreach (var point in new[] { new Windows.Foundation.Point(1, 425), new(width - 1, 425), new(width / 2, 1),
                new(width / 2, 849), new(1, 1), new(width - 1, 1), new(1, 849), new(width - 1, 849) })
                Ensure(bounds.Count(rect => rect.Contains(point)) == 1,
                    "Every visible edge and corner must have exactly one WinUI input owner after resizing.");
            Ensure(!bounds.Any(rect => rect.Contains(new Windows.Foundation.Point(width / 2, 425))),
                "Resize input must not cover the settings content.");
        }
        regions.Update(false);
        Ensure(regions.Visibility == Visibility.Collapsed, "Native mode must release custom WinUI resize input.");
        using var miniRegions = new GlassResizeRegions(mini, (Grid)mini.Content);
        miniRegions.Update(true);
        Ensure(miniRegions.Visibility == Visibility.Collapsed, "The fixed-size mini window must never expose resize grips.");
    }

#endif

    private static void CheckPresentationLifecycle()
    {
        bool glass = false;
        int canceled = 0;
        var root = new StackPanel();
        var deferred = new StackPanel { Visibility = Visibility.Collapsed };
        root.Children.Add(deferred);
        var groups = Enumerable.Range(0, 8).Select(index => new OptionGroupPresentation(
            new Border { Height = 48 }, new Button { Content = index.ToString() }, () => glass, () => canceled++)).ToArray();
        try
        {
            foreach (var group in groups) deferred.Children.Add(group);
            Ensure(groups.All(group => !group.IsLoaded && group.Children[0].Visibility == Visibility.Visible),
                "Cold-start declarations begin as native cards before material restoration and Loaded.");
            glass = true;
            OptionGroupPresentation.RefreshTree(root);
            Ensure(groups.All(group => group.Children[0].Visibility == Visibility.Collapsed &&
                group.Children[1].Visibility == Visibility.Visible),
                "The first material-ready refresh must update every unloaded/collapsed group to tabs.");
            OptionGroupPresentation.RefreshTree(root);
            Ensure(canceled == 0, "Idempotent glass refresh must preserve pending gestures and selection.");
            glass = false;
            OptionGroupPresentation.RefreshTree(root);
            Ensure(groups.All(group => group.Children[0].Visibility == Visibility.Visible) && canceled == groups.Length,
                "Native fallback must update all groups together and cancel each stale preview once.");
            using var late = new OptionGroupPresentation(new Border { Height = 48 }, new Button(), () => glass);
            using var host = new AnimatedOptionHost(() => false, _ => { });
            root.Children.Add(host);
            glass = true;
            host.SetChild(late);
            Ensure(late.Children[0].Visibility == Visibility.Collapsed,
                "A dynamically replaced presentation must use the current material before its first Loaded event.");
            OptionGroupPresentation.RefreshTree(root);
            Ensure(groups.All(group => group.Children[0].Visibility == Visibility.Collapsed),
                "Late feature discovery must use the same presentation refresh as startup sections.");
        }
        finally { foreach (var group in groups) group.Dispose(); }
    }

    private static void CheckDisabledTiles()
    {
        foreach (bool action in new[] { false, true })
        {
            var button = TrayMenuWindow.CreateOptionTile("禁用选项", true, action, TrayMenuWindow.OptionTileHeightDip);
            var label = WarmUiTree.Elements(button).OfType<TextBlock>().Single();
            button.IsEnabled = false;
            TrayMenuWindow.RefreshOptionTile(button, true, pending: true);
            Ensure(label.TextDecorations == Windows.UI.Text.TextDecorations.Strikethrough &&
                label.Foreground is SolidColorBrush disabled && disabled.Color == OptionLabelAppearance.DisabledColor(button.ActualTheme == ElementTheme.Dark),
                "Disabled action and selection cards must be gray and struck through, including selected/pending cards before Loaded.");
            TrayMenuWindow.SetOptionText(button, "更新后的禁用选项");
            TrayMenuWindow.RefreshOptionTile(button, false);
            Ensure(label.TextDecorations == Windows.UI.Text.TextDecorations.Strikethrough,
                "Refreshing a disabled card label must preserve its disabled decoration.");
            button.IsEnabled = true;
            TrayMenuWindow.RefreshOptionTile(button, false);
            Ensure(label.TextDecorations == Windows.UI.Text.TextDecorations.None,
                "Reenabling a card must immediately remove its strike-through.");
        }
        using var card = new FeatureCard("共享按钮");
        bool available = true;
        void Render() => card.Update(true, body => card.Options(body, card.Option("仅听机主", false,
            () => Task.CompletedTask, available)));
        Render();
        var retained = Tree(card.View).OfType<Button>().Single();
        available = false;
        Render();
        Ensure(ReferenceEquals(retained, Tree(card.View).OfType<Button>().Single()) &&
            WarmUiTree.Elements(retained).OfType<TextBlock>().Single().TextDecorations == Windows.UI.Text.TextDecorations.Strikethrough,
            "Retained feature buttons must apply availability before refreshing appearance, even while unloaded.");
        available = true;
        Render();
        Ensure(WarmUiTree.Elements(retained).OfType<TextBlock>().Single().TextDecorations == Windows.UI.Text.TextDecorations.None,
            "Retained feature buttons must restore normal labels after capability recovers.");
#if LIQUID_GLASS
        var tab = new RadioButton();
        var caption = new TextBlock();
        GlassOptionBar.SetLabelAvailability(tab, caption, false);
        Ensure(caption.TextDecorations == Windows.UI.Text.TextDecorations.Strikethrough,
            "Tab and card disabled labels share the same decoration.");
        GlassOptionBar.SetLabelAvailability(tab, caption, true);
        Ensure(caption.TextDecorations == Windows.UI.Text.TextDecorations.None, "Reenabled tabs restore their labels too.");
#endif
    }

    private static void CheckNaturalHostMeasurement()
    {
        var rows = new StackPanel();
        using var host = new AnimatedOptionHost(() => false, _ => { });
        host.SetChild(rows);
        rows.Children.Add(new Border { Height = 64 });
        host.Measure(new Windows.Foundation.Size(120, double.PositiveInfinity));
        Ensure(host.DesiredSize.Height >= 64, "Content added before first display must contribute its natural height.");

        rows.Children.Clear();
        var text = new TextBlock
        {
            Text = string.Concat(Enumerable.Repeat("窗口中的功能说明需要完整显示。", 8)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        };
        rows.Children.Add(text);
        double previous = 0;
        foreach (double width in new[] { 240.0, 100.0, 300.0 })
        {
            host.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            Ensure(text.DesiredSize.Width <= width + 0.5, "Wrapped content must use the measured width before activation.");
            Ensure(host.DesiredSize.Height >= text.DesiredSize.Height - 0.5, "The host must reserve the complete wrapped content height.");
            if (width == 100) Ensure(host.DesiredSize.Height > previous, "Narrowing the menu must increase the reserved text height.");
            previous = host.DesiredSize.Height;
        }
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

    private static TrayMenuWindow CreateDeclarationWindow(bool full) => new(0, "contract", new(true, true, true, 8192, 0x20001),
            new(ItsMode.Intelligent, 0, "智能模式"), new(true, BatteryChargeMode.Normal, "常规模式"),
            new(true, false), new(true, false, "contract"), true, false,
            _ => throw new InvalidOperationException("No hardware writes in declaration checks"),
            (_, _) => throw new InvalidOperationException("No hardware writes in declaration checks"),
            _ => throw new InvalidOperationException("No hardware writes in declaration checks"),
            () => throw new InvalidOperationException("No GPU actions in declaration checks"),
            () => throw new InvalidOperationException("No GPU actions in declaration checks"),
            _ => throw new InvalidOperationException("No task writes in declaration checks"),
            (_, _) => throw new InvalidOperationException("No dialogs in declaration checks"), () => { }, fullWindow: full);
    private static void CheckWindowDeclarations()
    {
        var full = CreateDeclarationWindow(true);
        var mini = CreateDeclarationWindow(false);
        try
        {
            Ensure(!full.AppWindow.IsVisible && !mini.AppWindow.IsVisible,
                "Constructing settings windows must not display an intermediate window.");
            Ensure(full.AppWindow.Size.Width < full.AppWindow.Size.Height,
                "The control center must already have portrait bounds before content is shown.");
#if LIQUID_GLASS
            CheckResizeRegions(full, mini);
#endif
            foreach (double width in new[] { 244.0, 228.0, 196.0 })
            {
                int height = mini.MeasureMiniContentHeight(width);
                var root = (FrameworkElement)mini.Content;
                root.Arrange(new Windows.Foundation.Rect(0, 0, width, height));
                var buttons = Tree(root).OfType<Button>().Where(button => button.ActualWidth > 0 && button.ActualHeight > 0).ToArray();
                Ensure(buttons.Length == 11, "Every performance, charge, GPU and exit button must be laid out in the mini window.");
                foreach (var button in buttons)
                {
                    var bounds = button.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    Ensure(bounds.Top >= -0.5 && bounds.Bottom <= height + 0.5,
                        "The measured mini window height must include all function buttons and the exit button.");
                }
            }
            string[] fullText = TextIn((DependencyObject)full.Content).ToArray();
            string[] miniText = TextIn((DependencyObject)mini.Content).ToArray();
            Ensure(full.Title == "Yoga控制中心" && mini.Title == "Yoga控制中心", "Both windows must retain application branding.");
            Ensure(fullText.Contains("性能选项") && fullText.Contains("充电设置"), "Full window core sections missing.");
            Ensure(miniText.Contains("性能选项") && miniText.Contains("充电选项"), "Mini window core sections missing.");
            Ensure(!miniText.Any(text => text.Contains("保持独显唤醒")), "Mini window must omit keep-awake.");
            Ensure(fullText.SkipWhile(text => text != "杂项").Where(text => !string.IsNullOrEmpty(text))
                .Take(4).SequenceEqual(new[] { "杂项", "开机自启", "关于", "退出" }),
                "Full-window About must sit between startup and exit in the existing miscellaneous section.");
            Ensure(!miniText.Contains("关于"), "The mini window must keep its existing layout without About.");
            var about = AboutDialog.Create();
            Ensure(about.Title as string == "关于 Yoga控制中心" && about.CloseButtonText == "关闭",
                "Both builds use the same native modal About dialog.");
            var aboutText = TextIn(about).ToArray();
            Ensure(aboutText.Any(text => text.Contains("BSD-3-Clause")) &&
                aboutText.Any(text => text.Contains("硬件与系统风险")) &&
                aboutText.Any(text => text.Contains("非官方声明")), "About must include license, risk, and affiliation notices.");
            Ensure(Tree(about).OfType<HyperlinkButton>().Any(link =>
                link.Tag is Uri uri && uri.AbsoluteUri == "https://github.com/wfsg00098/YogaITS"), "About identifies the original repository.");
#if LIQUID_GLASS
            Ensure(fullText.Any(text => text.Contains("UI选项")), "Glass build must expose the provided material options.");
#else
            Ensure(!fullText.Any(text => text.Contains("UI选项")), "Native build must omit glass options.");
#endif
        }
        finally { mini.Close(); full.Close(); }
    }

    private static async Task CheckAlwaysOnUsbPanelAsync()
    {
        uint raw = 0x80;
        bool fail = false;
        await using var usb = new AlwaysOnUsbService(new(() => new AlwaysOnUsbUiDevice(() =>
            fail ? throw new IOException("temporary read failure") : raw)));
        await using var keyboard = new KeyboardBacklightService();
        await using var gpu = new LenovoGpuWorkingModeService(new LenovoGpuModeBackend(), GpuWorkingMode.Default, _ => { });
        await using var hpd = new HpdService();
        await using var color = new ColorGamutService();
        await using var audio = new ElevocService();
        var window = CreateDeclarationWindow(true);
        try
        {
            using var panels = new FeaturePanels(window, gpu, hpd, color, audio, keyboard, usb);
            var groups = window.AdditionalSections.Children;
            var keyboardView = (FrameworkElement)groups[groups.Count - 2];
            var usbView = (FrameworkElement)groups[groups.Count - 1];
            Ensure(TextIn(keyboardView).Contains("键盘背光") && TextIn(usbView).Contains("Always-On USB"),
                "Always-On USB must immediately follow keyboard backlight in additional sections.");
            Ensure(usbView.Visibility == Visibility.Collapsed, "Unsupported USB must start hidden without writing a probe.");
            var sectionTitles = TextIn((DependencyObject)window.Content).ToArray();
            Ensure(Array.IndexOf(sectionTitles, "Always-On USB") < Array.IndexOf(sectionTitles, "开盖开机"),
                "Always-On USB must precede lid-open startup.");
            await usb.RefreshAsync();
            panels.Render();
            Ensure(usbView.Visibility == Visibility.Visible, "Successful USB GET must dynamically reveal the section.");
            var text = TextIn(usbView).ToArray();
            Ensure(new[] { "关闭", "仅睡眠时开启", "始终开启" }.All(text.Contains), "USB must expose all three BIOS modes in order.");
            foreach (var button in Tree(usbView).OfType<Button>())
                if (AutomationProperties.GetName(button).Contains("仅睡眠时开启"))
                    Ensure(AutomationProperties.GetName(button).Contains("Always-On USB"), "USB narrator labels identify the feature group.");
            fail = true;
            await usb.RefreshAsync();
            panels.Render();
            Ensure(usbView.Visibility == Visibility.Visible && Tree(usbView).OfType<Button>().All(button => !button.IsEnabled),
                "Known USB capability remains visible but disabled during a transient read failure.");
            fail = false;
            raw = 0x8080;
            await usb.RefreshAsync();
            panels.Render();
            Ensure(usb.Snapshot.Mode == AlwaysOnUsbMode.Always && Tree(usbView).OfType<Button>().Any(button => button.IsEnabled),
                "A subsequent real readback must restore USB controls without reopening the window.");
        }
        finally { window.Close(); }
    }

    private sealed class AlwaysOnUsbUiDevice(Func<uint> read) : IAlwaysOnUsbDevice
    {
        public uint Read() => read();
        public void Write(uint command) => throw new InvalidOperationException("No hardware writes in USB declarations");
        public void Dispose() { }
    }

    private static async Task CheckAboutLinksAsync()
    {
        bool fail = false;
        int launches = 0;
        var clicked = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var about = AboutDialog.Create(uri =>
        {
            launches++;
            clicked.TrySetResult(uri);
            if (fail) throw new InvalidOperationException("simulated desktop Shell failure");
        });
        var links = Tree(about).OfType<HyperlinkButton>().ToArray();
        Ensure(links.Length == 2 && links.All(link => link.NavigateUri is null),
            "Both About links must bypass automatic elevated WinRT navigation.");
        async Task ClickAsync(HyperlinkButton link)
        {
            clicked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var peer = new Microsoft.UI.Xaml.Automation.Peers.HyperlinkButtonAutomationPeer(link);
            var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(
                Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
            invoke.Invoke();
            Uri uri = await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(uri == (Uri)link.Tag && uri.Scheme == "https" && uri.Host == "github.com",
                "Keyboard/automation activation must send the complete intended HTTPS URI to the launcher.");
        }
        foreach (var link in links) await ClickAsync(link);
        Ensure(launches == 2 && Uri.UnescapeDataString(((Uri)links[1].Tag).Fragment) == "#第三方组件与许可",
            "Each click launches once and preserves the Chinese documentation anchor.");
        fail = true;
        await ClickAsync(links[1]);
        var error = Tree(about).OfType<TextBlock>().Single(text => text.Text.StartsWith("无法打开默认浏览器"));
        Ensure(error.Visibility == Visibility.Visible && error.IsTextSelectionEnabled &&
            error.Text.Contains(((Uri)links[1].Tag).AbsoluteUri), "Launch failure must keep a selectable URL in the About dialog.");
        fail = false;
        await ClickAsync(links[1]);
        Ensure(error.Visibility == Visibility.Collapsed && launches == 4, "A successful retry clears the previous error.");
    }

    private static IEnumerable<string> TextIn(DependencyObject node)
        => Tree(node).OfType<TextBlock>().Select(text => text.Text);

    private static IEnumerable<DependencyObject> Tree(DependencyObject node)
    {
        yield return node;
        IEnumerable<DependencyObject> children = node switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            Border { Child: { } child } => [child],
            ContentControl { Content: DependencyObject content } => [content],
            _ => []
        };
        foreach (var child in children)
            foreach (var value in Tree(child)) yield return value;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
