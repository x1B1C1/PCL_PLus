using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Plugins;
#if BF_DEBUG
using PCL.Modules.Debug;
#endif

namespace PCL.Pages.PagePlugins;

public sealed class PagePluginsMarket : MyPageRight
{
    private readonly TextBlock _status;
    private readonly StackPanel _content;
    private bool _installationInProgress;

    public PagePluginsMarket()
    {
        var root = PluginPageSupport.CreateRoot(Lang.Text("Plugin.Market.Title"), out _status, out _content);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        actions.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Market.Refresh"), async (_, _) => await RefreshAsync()));
#if BF_DEBUG
        // 【调试专属模块】BF 构建里真实安装入口锁死，只保留 UI 与模拟流程
        if (BuildLine.IsBf)
        {
            var localInstall = PluginPageSupport.Button(Lang.Text("Plugin.Install.Local"),
                (_, _) => { }, true);
            localInstall.IsEnabled = false;
            localInstall.ToolTip = "【调试专属模块】BF 构建已锁定真实安装，请使用「模拟安装模块」";
            actions.Children.Add(localInstall);
            actions.Children.Add(PluginPageSupport.Button("模拟安装模块",
                async (_, _) => await SimulateInstallAsync()));
            actions.Children.Add(PluginPageSupport.Button("模拟卸载模块",
                async (_, _) => await SimulateUninstallAsync()));
        }
        else
        {
            actions.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Install.Local"), async (_, _) => await InstallLocalAsync(), true));
        }
#else
        actions.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Install.Local"), async (_, _) => await InstallLocalAsync(), true));
#endif
        root.Children.Insert(1, PluginPageSupport.CreateCard(new TextBlock
        {
            Text = Lang.Text("Plugin.Market.Description"), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Opacity = 0.9
        }));
        root.Children.Insert(2, actions);
        var scroll = new MyScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        };
        Child = scroll;
        PanScroll = scroll;
        Loaded += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_installationInProgress) return;
        _content.Children.Clear();
        var manager = PluginManagerService.Manager;
        if (manager is null)
        {
            _status.Text = Lang.Text("Plugin.Common.NoManager");
            return;
        }
        _status.Text = "";
        try
        {
            var index = await manager.GetMarketAsync(new Uri(ProjectLinks.PluginCatalog));
            var candidates = manager.GetMarketUpdateCandidates(index.Plugins);
            if (index.Plugins.Count == 0)
            {
                _status.Text = Lang.Text("Plugin.Market.Empty");
                return;
            }
            foreach (var candidate in candidates)
            {
                var entry = candidate.Entry;
                var stack = new StackPanel();
                var icon = new Image { Width = 42, Height = 42,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                if (!string.IsNullOrWhiteSpace(entry.Icon))
                {
                    var header = new StackPanel { Orientation = Orientation.Horizontal };
                    header.Children.Add(icon);
                    header.Children.Add(PluginPageSupport.Text($"{GetDisplayName(entry)}  ·  {entry.Author}  ·  {entry.Version}", 12));
                    stack.Children.Add(header);
                }
                else
                stack.Children.Add(PluginPageSupport.Text($"{GetDisplayName(entry)}  ·  {entry.Author}  ·  {entry.Version}", 12));
                stack.Children.Add(PluginPageSupport.Text(GetDescription(entry)));
                var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                switch (candidate.Kind)
                {
                    case PluginMarketUpdateKind.Current:
                        row.Children.Add(PluginPageSupport.Text(Lang.Text("Plugin.Market.Current"), 12));
                        break;
                    case PluginMarketUpdateKind.UpdateAvailable:
                        row.Children.Add(CreateInstallButton(entry, Lang.Text("Plugin.Market.Update")));
                        break;
                    case PluginMarketUpdateKind.DowngradeAvailable:
                        row.Children.Add(CreateInstallButton(entry, Lang.Text("Plugin.Market.Downgrade")));
                        break;
                    default:
                        row.Children.Add(CreateInstallButton(entry, Lang.Text("Plugin.Market.Install")));
                        break;
                }
                stack.Children.Add(row);
                Grid? websiteFooter = null;
                if (Uri.TryCreate(entry.Website, UriKind.Absolute, out var website) && website.Scheme == Uri.UriSchemeHttps)
                {
                    var footer = new Grid
                    {
                        Margin = new Thickness(0, 2, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    footer.ColumnDefinitions.Add(new ColumnDefinition());
                    footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var websiteButton = PluginPageSupport.Button(Lang.Text("Download.Version.OpenWebsite"),
                        (_, _) => ModBase.OpenWebsite(website.AbsoluteUri));
                    websiteButton.Margin = new Thickness(0);
                    websiteButton.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(websiteButton, 1);
                    footer.Children.Add(websiteButton);
                    stack.Children.Add(footer);
                    websiteFooter = footer;
                }
                var card = PluginPageSupport.CreateCard(stack, GetDisplayName(entry));
                card.HorizontalAlignment = HorizontalAlignment.Stretch;
                if (websiteFooter is not null)
                {
                    void ResizeWebsiteFooter() => websiteFooter.Width = Math.Max(0, card.ActualWidth - 40);
                    card.Loaded += (_, _) => ResizeWebsiteFooter();
                    card.SizeChanged += (_, _) => ResizeWebsiteFooter();
                }
                _content.Children.Add(card);
                if (!string.IsNullOrWhiteSpace(entry.Icon))
                    _ = PluginPageSupport.LoadRemoteIconIntoAsync(entry.Icon, icon);
            }
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _status.Text = Lang.Text("Plugin.Market.LoadFailed", ex.Message);
        }
    }

    private async Task InstallLocalAsync()
    {
        if (_installationInProgress) return;
        var dialog = new OpenFileDialog
        {
            Filter = $"PCL plugin package (*{PCL.Plugin.SDK.PluginPackage.Extension})|*{PCL.Plugin.SDK.PluginPackage.Extension}",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        var manager = PluginManagerService.Manager;
        if (manager is null) { PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Common.NoManager"), PCL.Plugin.SDK.PluginNotificationKind.Warning); return; }
        _installationInProgress = true;
        _status.Text = Lang.Text("Plugin.Install.Working");
        try
        {
            var check = await Task.Run(() => manager.InstallAsync(dialog.FileName,
                PluginPageSupport.ConfirmInstallationAsync));
            var note = check.IsSigned ? Lang.Text("Plugin.Install.SignedNote") + " " : "";
            PluginPageSupport.PromptRestartForPluginChanges(note + Lang.Text("Plugin.Install.Done"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Install.Failed", ex.Message), PCL.Plugin.SDK.PluginNotificationKind.Error);
        }
        finally
        {
            _installationInProgress = false;
            _status.Text = "";
        }
    }

    private MyButton CreateInstallButton(PluginMarketEntry entry, string label)
    {
#if BF_DEBUG
        // 【调试专属模块】BF 构建里市场条目也禁止真实安装，按钮只触发模拟流程
        if (BuildLine.IsBf)
        {
            MyButton simulate = null!;
            simulate = PluginPageSupport.Button("模拟安装", async (_, _) =>
            {
                simulate.IsEnabled = false;
                try { await SimulateInstallAsync(entry); }
                finally { simulate.IsEnabled = true; }
            }, true);
            return simulate;
        }
#endif
        MyButton button = null!;
        button = PluginPageSupport.Button(label, async (_, _) => await InstallMarketAsync(entry, button), true);
        return button;
    }

#if BF_DEBUG
    /// <summary>【调试专属模块】模拟安装：不调用任何真实模块接口，不写磁盘。</summary>
    private static async Task SimulateInstallAsync(PluginMarketEntry? entry = null)
    {
        await BfDebugCore.SimulateInstallAsync(new BfDebugModule
        {
            Id = entry?.Id ?? "plugin-market",
            Name = entry is null ? "插件市场（模拟）" : GetDisplayName(entry),
            Version = entry?.Version ?? "—",
            Description = "【调试专属模块】仅模拟安装流程，不创建、修改或删除任何文件"
        });
    }

    /// <summary>【调试专属模块】模拟卸载：不调用任何真实模块接口，不删文件。</summary>
    private static async Task SimulateUninstallAsync()
    {
        await BfDebugCore.SimulateUninstallAsync(new BfDebugModule
        {
            Id = "plugin-market",
            Name = "插件市场（模拟）",
            Version = "—",
            Description = "【调试专属模块】仅模拟卸载流程，不删除任何文件"
        });
    }
#endif

    private static string GetDisplayName(PluginMarketEntry entry) =>
        entry.LocalizedNames.TryGetValue(LocalizationService.CurrentLanguage.Code, out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized : entry.Name;

    private static string GetDescription(PluginMarketEntry entry) =>
        entry.LocalizedDescriptions.TryGetValue(LocalizationService.CurrentLanguage.Code, out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized : entry.Description;

    private async Task InstallMarketAsync(PluginMarketEntry entry, MyButton button)
    {
        if (_installationInProgress) return;
        var manager = PluginManagerService.Manager;
        if (manager is null) { PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Common.NoManager"), PCL.Plugin.SDK.PluginNotificationKind.Warning); return; }
        _installationInProgress = true;
        button.IsEnabled = false;
        _status.Text = Lang.Text("Plugin.Install.Working");
        try
        {
            var check = await Task.Run(() => manager.InstallFromMarketAsync(entry,
                PluginPageSupport.ConfirmInstallationAsync));
            button.Text = Lang.Text("Plugin.Market.PendingRestart");
            PluginPageSupport.PromptRestartForPluginChanges(
                (check.IsSigned ? Lang.Text("Plugin.Install.SignedNote") + " " : "") + Lang.Text("Plugin.Install.Done"));
        }
        catch (OperationCanceledException) { button.IsEnabled = true; }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Install.Failed", ex.Message), PCL.Plugin.SDK.PluginNotificationKind.Error);
        }
        finally
        {
            _installationInProgress = false;
            _status.Text = "";
        }
    }
}
