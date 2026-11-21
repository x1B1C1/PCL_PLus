// 【调试专属模块】BF 调试页：模拟模块安装 / 卸载、调试设置与调试日志。
// 仅在 -p:BfDebug=true 的 BF 本地构建里编入；正式线 / 测试线通过编译宏完全排除。
#if BF_DEBUG
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Modules.Debug;

namespace PCL.Pages.PageTools;

/// <summary>
/// 【调试专属模块】BF 调试页。
/// <para>
/// 模块安装 / 卸载全部是模拟流程：只打印进度与校验日志，不调用任何真实模块接口，
/// 也不会创建、修改或删除磁盘文件。
/// </para>
/// </summary>
public sealed class PageToolsBfDebug : MyPageRight
{
    private readonly BfConsole _console = new();
    private readonly List<BfDebugModule> _modules = new(BfDebugCore.CreateDefaultModules());

    public PageToolsBfDebug()
    {
        var root = new StackPanel { Margin = new Thickness(25, 10, 25, 10) };
        root.Children.Add(BuildModuleCard());
        root.Children.Add(BuildSettingsCard());
        root.Children.Add(BuildConsoleCard());

        var scroll = new MyScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        };
        Child = scroll;
        PanScroll = scroll;
        BfDebugCore.Log("已打开 BF 调试页（模块操作均为模拟）", BfDebugLogLevel.Info);
    }

    private MyCard BuildModuleCard()
    {
        var card = new MyCard { Title = "调试模块（模拟安装 / 卸载）", Margin = new Thickness(0, 15, 0, 15) };
        var panel = new StackPanel { Margin = new Thickness(25, 40, 25, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = "【调试专属模块】以下模块只做 UI 与流程模拟，真实安装 / 卸载接口已锁定，不会改动本地任何文件。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 15)
        });

        foreach (var module in _modules) panel.Children.Add(BuildModuleRow(module));
        card.Children.Add(panel);
        return card;
    }

    private Grid BuildModuleRow(BfDebugModule module)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        var name = new TextBlock
        {
            Text = $"{module.Name}  {module.Version}",
            FontWeight = FontWeights.SemiBold
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        var desc = new TextBlock
        {
            Text = module.Description,
            Opacity = 0.65,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        var status = new TextBlock
        {
            Text = module.Installed ? "状态：已安装（模拟）" : "状态：未安装",
            Opacity = 0.65,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0)
        };
        texts.Children.Add(name);
        texts.Children.Add(desc);
        texts.Children.Add(status);
        Grid.SetColumn(texts, 0);
        row.Children.Add(texts);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(15, 0, 0, 0)
        };
        var install = new MyButton { Text = "模拟安装", MinWidth = 105, Margin = new Thickness(0, 0, 8, 0) };
        var uninstall = new MyButton { Text = "模拟卸载", MinWidth = 105, ColorType = MyButton.ColorState.Red };
        install.Click += async (_, _) =>
        {
            await BfDebugCore.SimulateInstallAsync(module).ConfigureAwait(true);
            status.Text = "状态：已安装（模拟）";
        };
        uninstall.Click += async (_, _) =>
        {
            await BfDebugCore.SimulateUninstallAsync(module).ConfigureAwait(true);
            status.Text = "状态：未安装";
        };
        buttons.Children.Add(install);
        buttons.Children.Add(uninstall);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);

        return row;
    }

    private MyCard BuildSettingsCard()
    {
        var card = new MyCard { Title = "调试设置", Margin = new Thickness(0, 0, 0, 15) };
        var panel = new StackPanel { Margin = new Thickness(25, 40, 25, 20) };

        var logPanelSwitch = new MyCheckBox
        {
            Text = "启用右侧调试日志面板（默认开启，占界面宽度 40%）",
            Checked = BfDebugPanel.IsVisible,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var syncing = false;
        logPanelSwitch.Change += (sender, _) =>
        {
            if (syncing) return;
            var on = ((MyCheckBox)sender).Checked == true;
            BfDebugPanel.SetVisible(on);
            BfDebugCore.Log(on ? "已启用右侧调试日志面板" : "已关闭右侧调试日志面板", BfDebugLogLevel.Info);
        };
        void SyncFromPanel(bool on)
        {
            syncing = true;
            logPanelSwitch.Checked = on;
            syncing = false;
        }

        BfDebugPanel.VisibilityChanged += SyncFromPanel;
        Unloaded += (_, _) => BfDebugPanel.VisibilityChanged -= SyncFromPanel;
        panel.Children.Add(logPanelSwitch);

        for (var i = 2; i <= 4; i++)
        {
            var placeholder = new MyCheckBox
            {
                Text = $"调试设置项 {i}（预留，后续接入）",
                IsEnabled = false,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(placeholder);
        }

        var hwidNote = new TextBlock
        {
            Text = "Hwid Bind：把当前设备 HWID 写入本机白名单（%APPDATA%\\PCL_Plus\\bf-whitelist.txt），已存在则不重复写入。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 8)
        };
        var hwidBind = new MyButton { Text = "Hwid Bind" };
        hwidBind.Click += (_, _) =>
        {
            var result = BfDebugCore.BindHwid();
            BfDebugCore.Log("Hwid Bind：" + result,
                result.StartsWith("绑定失败", StringComparison.Ordinal)
                    ? BfDebugLogLevel.Warn
                    : BfDebugLogLevel.Success);
        };
        panel.Children.Add(hwidNote);
        panel.Children.Add(hwidBind);

        card.Children.Add(panel);
        return card;
    }

    private MyCard BuildConsoleCard()
    {
        var card = new MyCard { Title = "BF 工程控制台（调试日志同步）", Margin = new Thickness(0, 0, 0, 15) };
        _console.Margin = new Thickness(25, 40, 25, 20);
        card.Children.Add(_console);
        return card;
    }
}
#endif
