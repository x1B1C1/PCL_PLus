using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.EnvironmentHistory;

namespace PCL.Pages.PageInstance;

/// <summary>
///     实例的「还原点」页：手动打点、查看历史、恢复与清理。
///     <para>打点内容由 <see cref="ModEnvironmentHistory" /> 与 Core 的引擎负责，本页只做展示与确认。</para>
/// </summary>
public partial class PageInstanceRestorePoints : IRefreshable
{
    private bool _hooked;

    public PageInstanceRestorePoints()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    void IRefreshable.Refresh()
    {
        RefreshAll();
    }

    /// <summary>供左侧栏刷新按钮与外部调用。</summary>
    public void Refresh()
    {
        RefreshAll();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        PanBack.ScrollToHome();
        if (!_hooked)
        {
            _hooked = true;
            ModEnvironmentHistory.Changed += OnChanged;
        }

        RefreshAll();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked) return;
        ModEnvironmentHistory.Changed -= OnChanged;
    }

    private void OnChanged()
    {
        ModBase.RunInUi(RefreshPoints);
    }

    private void RefreshAll()
    {
        RefreshSettings();
        RefreshPoints();
    }

    private void RefreshSettings()
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        try
        {
            CheckAutoCleanup.SetChecked(RetentionAutoCleanup(instance), false);
            TextRetention.Text = Retention(instance).ToString();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取还原点设置失败", ModBase.LogLevel.Debug);
        }
    }

    private static bool RetentionAutoCleanup(McInstance instance)
    {
        try
        {
            return Config.Instance.EnvironmentHistoryAutoCleanup[instance.PathInstance];
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取自动清理开关失败", ModBase.LogLevel.Debug);
            return false;
        }
    }

    private static int Retention(McInstance instance)
    {
        try
        {
            return Math.Clamp(Config.Instance.EnvironmentHistoryRetention[instance.PathInstance], 1, 100);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取还原点保留数量失败", ModBase.LogLevel.Debug);
            return 10;
        }
    }

    private void BtnCreate_Click(object sender, MouseButtonEventArgs e)
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        ModEnvironmentHistory.Capture(instance);
    }

    private void BtnOpenFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var folder = ModEnvironmentHistory.StoreRoot(PageInstanceLeft.McInstance);
        if (string.IsNullOrWhiteSpace(folder)) return;
        ModBase.OpenExplorer(folder);
    }

    private void BtnRefresh_Click(object sender, ModBase.RouteEventArgs e)
    {
        RefreshAll();
    }

    private void CheckAutoCleanup_Change(object sender, bool user)
    {
        if (!user) return;
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        try
        {
            Config.Instance.EnvironmentHistoryAutoCleanup[instance.PathInstance] = CheckAutoCleanup.Checked == true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "保存自动清理开关失败", ModBase.LogLevel.Debug);
        }
    }

    private void TextRetention_ValidatedTextChanged(object sender, RoutedEventArgs e)
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        if (!int.TryParse(TextRetention.Text, out var value) || value < 1 || value > 100)
        {
            TextRetention.Text = Retention(instance).ToString();
            return;
        }

        try
        {
            Config.Instance.EnvironmentHistoryRetention[instance.PathInstance] = value;
            if (RetentionAutoCleanup(instance)) ModEnvironmentHistory.PruneAutomatic(instance, value);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "保存还原点保留数量失败", ModBase.LogLevel.Debug);
        }
    }

    private void RefreshPoints()
    {
        PanEntries.Children.Clear();
        var instance = PageInstanceLeft.McInstance;
        if (instance is null)
        {
            LabEmpty.Visibility = Visibility.Visible;
            return;
        }

        IReadOnlyList<EnvironmentHistoryPoint> points = ModEnvironmentHistory.Points(instance);
        LabEmpty.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var point in points)
        {
            var item = new MyListItem
            {
                Title = point.Healthy ? $"★ {point.Label}" : point.Label,
                Info = $"{Lang.Date(point.CreatedUtc.ToLocalTime(), "g")} · " +
                       Lang.FileSize(point.Bytes) + " · " +
                       Lang.Text("Instance.RestorePoints.Files", point.FileCount.ToString()) +
                       (point.Manual ? " · " + Lang.Text("Instance.RestorePoints.Manual") : ""),
                Height = 42,
                Type = MyListItem.CheckType.Clickable
            };
            item.Click += (_, _) =>
            {
                var folder = ModEnvironmentHistory.StoreRoot(instance);
                if (!string.IsNullOrWhiteSpace(folder)) ModBase.OpenExplorer(folder);
            };

            var btnRestore = new MyIconButton
            {
                SvgIcon = "lucide/rotate-ccw",
                ToolTip = Lang.Text("Instance.RestorePoints.Restore")
            };
            btnRestore.Click += (_, _) =>
            {
                if (ModMain.MyMsgBox(Lang.Text("Instance.RestorePoints.RestoreConfirm", point.Label),
                        Lang.Text("Instance.RestorePoints.Restore"), isWarn: true) != 1) return;
                ModEnvironmentHistory.Restore(instance, point.Id);
            };

            var btnDelete = new MyIconButton
            {
                SvgIcon = "lucide/trash-2",
                ToolTip = Lang.Text("Instance.RestorePoints.Delete")
            };
            btnDelete.Click += (_, _) =>
            {
                if (ModMain.MyMsgBox(Lang.Text("Instance.RestorePoints.DeleteConfirm", point.Label),
                        Lang.Text("Instance.RestorePoints.Delete"), isWarn: true) != 1) return;
                ModEnvironmentHistory.Delete(instance, new[] { point.Id });
            };

            item.Buttons = new[] { btnRestore, btnDelete };
            PanEntries.Children.Add(item);
        }
    }
}
