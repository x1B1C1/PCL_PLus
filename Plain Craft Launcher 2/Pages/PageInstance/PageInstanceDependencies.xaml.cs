using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Mod;

namespace PCL.Pages.PageInstance;

/// <summary>
///     依赖检查页：列出「缺前置的文件」与「能解决问题但被禁用的模组」。
///     <para>只读，不下载、不改文件；真正的依赖自动补齐需要联网元数据，暂未接入。</para>
/// </summary>
public partial class PageInstanceDependencies : IRefreshable
{
    public PageInstanceDependencies()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        PanMissing.Children.Clear();
        PanDisabled.Children.Clear();
        LabMissingEmpty.Visibility = Visibility.Visible;
        LabDisabledEmpty.Visibility = Visibility.Visible;
        LabStatus.Text = "";

        var instance = ModInstanceList.McMcInstanceSelected;
        if (instance is null)
        {
            LabStatus.Text = Lang.Text("Instance.Dependencies.NoInstance");
            return;
        }

        var profile = ModLoaderProfiles.Of(instance);
        var modsFolder = Path.Combine(instance.PathIndie, "mods");
        if (!Directory.Exists(modsFolder))
        {
            LabStatus.Text = Lang.Text("Instance.Dependencies.NoMods");
            return;
        }

        ModDependencyReport report;
        try
        {
            report = ModDependencyGraph.Scan(modsFolder, profile);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Dependency] 依赖检查失败");
            LabStatus.Text = Lang.Text("Instance.Dependencies.Failed", ex.Message);
            return;
        }

        LabStatus.Text = report.IsHealthy
            ? Lang.Text("Instance.Dependencies.AllGood")
            : Lang.Text("Instance.Dependencies.IssueCount", report.Issues.Count);

        if (report.Issues.Count > 0)
        {
            LabMissingEmpty.Visibility = Visibility.Collapsed;
            var grouped = report.Issues
                .GroupBy(issue => issue.FilePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(200);
            foreach (var group in grouped)
            {
                var missing = string.Join(", ", group.Select(issue => issue.MissingId).Distinct());
                PanMissing.Children.Add(BuildRow(
                    Path.GetFileName(group.Key),
                    Lang.Text("Instance.Dependencies.MissingOf", missing)));
            }
        }

        if (report.DisabledProviders.Count > 0)
        {
            LabDisabledEmpty.Visibility = Visibility.Collapsed;
            foreach (var pair in report.DisabledProviders
                         .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                         .Take(200))
            {
                PanDisabled.Children.Add(BuildRow(
                    Path.GetFileName(pair.Value),
                    Lang.Text("Instance.Dependencies.Provides", pair.Key)));
            }
        }
    }

    private static FrameworkElement BuildRow(string title, string info)
    {
        var panel = new StackPanel { Margin = new Thickness(5, 6, 5, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = info,
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 11.5,
            Opacity = 0.7
        });
        return panel;
    }

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        ModLoaderProfiles.ClearCache();
        Refresh();
    }
}
