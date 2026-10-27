using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YogaITS.Tray.UI.Accessibility;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Updates;

namespace YogaITS.Tray.UI.Shell;

internal static class AboutDialog
{
    public static ContentDialog Create(Action<Uri>? openLink = null)
    {
        var body = new StackPanel { Spacing = 8 };
        void Text(string value) => body.Children.Add(new TextBlock
        {
            Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
        });
        void Link(string label, Uri uri)
        {
            // NavigateUri launches through WinRT from this elevated process.
            // Keep it unset so only the desktop Shell opens the default browser.
            var link = new HyperlinkButton { Content = label, Padding = new Thickness(0), Tag = uri };
            ToolTipService.SetToolTip(link, uri.AbsoluteUri);
            var error = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Visibility = Visibility.Collapsed
            };
            link.Click += (_, _) =>
            {
                try
                {
                    (openLink ?? DesktopLinkLauncher.Open)(uri);
                    error.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Open About link: " + ex);
                    error.Text = "无法打开默认浏览器，请复制以下网址手动打开：\n" + uri.AbsoluteUri;
                    error.Visibility = Visibility.Visible;
                    UiAccessibility.Announce(error, error.Text);
                }
            };
            body.Children.Add(link);
            body.Children.Add(error);
        }
        Text($"版本：{UpdatePolicy.CurrentVersion}");
        Text(AppBuild.Flavor == UiBuildFlavor.LiquidGlass ? "构建：玻璃版" : "构建：普通版");
        Text("Copyright © 2026 wfsg00098 and YogaITS contributors");
        Link("原始仓库：wfsg00098/YogaITS", new Uri($"https://github.com/{UpdatePolicy.Repository}"));
        Text("许可与不背书\n原创代码采用 BSD-3-Clause；分发时须保留版权、许可条件和免责声明。未经事先书面许可，不得以作者或贡献者名义为衍生产品背书。");
        Text("硬件与系统风险\n软件按现状提供，不承诺保修、支持或兼容性。硬件和系统操作可能造成应用中断、黑屏、系统不稳定、数据丢失或硬件异常。现有校验及实机测试不构成安全保证；免责与责任限制见 LICENSE。");
        Text("第三方版本与服务\n第三方修改、重编译或打包的版本不能继承原版测试结论或暗示作者审核过。其收费、支持及质保承诺不代表原作者作出承诺。版本文字与哈希一致性检查不等于安全保证或独立发行者签名。");
        Text("非官方声明\n本项目与 Lenovo/联想、Microsoft、NVIDIA、X-Rite、Elevoc、Apple 等厂商无隶属、授权、合作或背书关系。产品、技术及公司名称仅用于说明兼容性与来源，相关商标及其他权利归各自权利人所有。");
        Text("第三方许可\n第三方组件保留各自许可。完整条款见程序目录的 LICENSE、NOTICE 与 ThirdPartyNotices。驱动、服务、固件和下载的校色文件不包含在 YogaITS 原创代码授权中。");
        Link("完整声明与第三方组件说明",
            new Uri($"https://github.com/{UpdatePolicy.Repository}/blob/main/DEVELOPMENT.md#第三方组件与许可"));
        return new ContentDialog
        {
            Title = "关于 Yoga控制中心",
            Content = new ScrollViewer
            {
                Content = body, MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            CloseButtonText = "关闭", DefaultButton = ContentDialogButton.Close
        };
    }
}
