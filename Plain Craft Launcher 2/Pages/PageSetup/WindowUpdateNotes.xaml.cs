using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL.Pages.PageSetup;

/// <summary>
///     更新说明窗口：版本变化后展示本次改动，样式参考 PCL Living Fork 的更新说明。
/// </summary>
public partial class WindowUpdateNotes
{
    /// <summary>
    ///     每条的标题与说明，按版本排列。
    /// </summary>
    private static readonly (string Title, string[] Items)[] Sections =
    [
        ("界面与配色", new[]
        {
            "配色改为浅色主题，页面底色、卡片与文字层级按 PCL Living Fork 调整。",
            "启动页左侧按钮改为「版本选择」与「版本设置」，联机入口移到设置页。",
            "标题栏图标与启动图片换成新的 PCL 六边形图标。"
        }),
        ("游戏实例", new[]
        {
            "实例侧栏新增「备份」页：手动/自动备份存档世界，可恢复到备份时刻。",
            "实例侧栏新增「还原点」页：对模组、配置与加载器环境打快照，可整套回滚。",
            "实例总览新增「创建测试副本」：复制一份独立版本用于排查问题，不动原实例。",
            "实例侧栏新增「整合包更新」与「依赖检查」页。"
        }),
        ("启动流程", new[]
        {
            "启动前可检查模组依赖，缺失前置时提示并可直接打开模组管理。",
            "模组列表会标出与当前加载器不匹配的文件。",
            "按版本记忆按键、鼠标与视角设置，换版本不用重设。",
            "CurseForge 下载的地图自动归档到存档目录下的 PCLLFWorldDownload。"
        })
    ];

    private static readonly Brush HeadingBrush = _Frozen(0xED, 0xF2, 0xF8);
    private static readonly Brush AccentBrush = _Frozen(0x00, 0xB7, 0xFF);
    private static readonly Brush BodyBrush = _Frozen(0xC9, 0xD3, 0xDF);

    private static Brush _Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public WindowUpdateNotes()
    {
        InitializeComponent();
        LabVersion.Text = ModBase.versionBaseName;
        LabSubtitle.Text = "本次更新加入了 PCL Living Fork 的若干工程化功能，并调整了界面配色与图标。";
        BuildNotes();
    }

    private void BuildNotes()
    {
        foreach (var (title, items) in Sections)
        {
            PanNotes.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeadingBrush,
                Margin = new Thickness(0, 14, 0, 8)
            });

            foreach (var item in items)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var dot = new TextBlock
                {
                    Text = "•",
                    FontSize = 14,
                    Foreground = AccentBrush,
                    VerticalAlignment = VerticalAlignment.Top
                };
                var text = new TextBlock
                {
                    Text = item,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21,
                    Foreground = BodyBrush
                };
                Grid.SetColumn(text, 1);
                row.Children.Add(dot);
                row.Children.Add(text);
                PanNotes.Children.Add(row);
            }
        }
    }

    /// <summary>
    ///     有未读更新说明时弹出。调用方需在 UI 线程。
    /// </summary>
    public static void ShowIfNeeded(Window? owner = null)
    {
        try
        {
            var current = ModBase.versionBaseName;
            if (string.IsNullOrWhiteSpace(current)) return;
            if (Config.Update.LastNotesVersion == current) return;
            if (Config.Update.DismissedNotesVersion == current) return;

            var window = new WindowUpdateNotes { Owner = owner };
            window.ShowDialog();
            Config.Update.LastNotesVersion = current;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "显示更新说明失败", ModBase.LogLevel.Debug);
        }
    }

    private void BtnOk_Click(object sender, MouseButtonEventArgs e)
    {
        if (CheckDismiss.Checked == true) Config.Update.DismissedNotesVersion = ModBase.versionBaseName;
        Close();
    }

    private void BtnRelease_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            ModBase.OpenWebsite(ProjectLinks.Releases);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "打开发布页失败", ModBase.LogLevel.Debug);
        }
    }
}
