using System;
using System.Collections.Generic;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Saves.Backups;

namespace PCL.Pages.PageInstance;

/// <summary>
///     实例的存档备份页。列表、设置与「备份全部」按钮都集中在这里，
///     存档页只负责存档本身，不再混排备份内容。
/// </summary>
public partial class PageInstanceSavesBackup : IRefreshable
{
    private bool _hooked;

    public PageInstanceSavesBackup()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    void IRefreshable.Refresh()
    {
        RefreshAll();
    }

    /// <summary>
    ///     供左侧栏的刷新按钮与外部代码直接调用。
    /// </summary>
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
            CheckBackupAuto.Change += CheckBackupAuto_Change;
            TextBackupRetention.ValidatedTextChanged += TextBackupRetention_ValidatedTextChanged;
            BtnBackupAll.Click += BtnBackupAll_Click;
            ModWorldBackups.Changed += OnBackupsChanged;
        }

        RefreshAll();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked) return;
        ModWorldBackups.Changed -= OnBackupsChanged;
    }

    private void RefreshAll()
    {
        RefreshSettings();
        RefreshPoints();
    }

    /// <summary>
    ///     把当前实例的自动备份设置显示到控件上。
    /// </summary>
    private void RefreshSettings()
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        try
        {
            CheckBackupAuto.SetChecked(ModWorldBackups.AutoEnabled(instance), false);
            TextBackupRetention.Text = ModWorldBackups.Retention(instance).ToString();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取存档备份设置失败", ModBase.LogLevel.Debug);
        }
    }

    private void OnBackupsChanged()
    {
        ModBase.RunInUi(RefreshPoints);
    }

    private void CheckBackupAuto_Change(object sender, bool user)
    {
        if (!user) return;
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        try
        {
            Config.Instance.WorldBackupAutoEnabled[instance.PathInstance] = CheckBackupAuto.Checked == true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "保存自动备份开关失败", ModBase.LogLevel.Debug);
        }
    }

    private void TextBackupRetention_ValidatedTextChanged(object sender, RoutedEventArgs e)
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        if (!int.TryParse(TextBackupRetention.Text, out var value) || value < 1 || value > 50)
        {
            TextBackupRetention.Text = ModWorldBackups.Retention(instance).ToString();
            return;
        }

        try
        {
            Config.Instance.WorldBackupRetention[instance.PathInstance] = value;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "保存备份保留份数失败", ModBase.LogLevel.Debug);
        }
    }

    private void BtnBackupAll_Click(object sender, ModBase.RouteEventArgs e)
    {
        ModWorldBackups.Start(PageInstanceLeft.McInstance);
    }

    /// <summary>
    ///     重建备份点列表。任何一项失败都只记录日志，不影响页面本身。
    /// </summary>
    private void RefreshPoints()
    {
        PanBackups.Children.Clear();
        var instance = PageInstanceLeft.McInstance;
        if (instance is null)
        {
            LabBackupsEmpty.Visibility = Visibility.Visible;
            return;
        }

        IReadOnlyList<WorldBackupPoint> points;
        try
        {
            points = ModWorldBackups.Points(instance);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取存档备份列表失败", ModBase.LogLevel.Debug);
            points = Array.Empty<WorldBackupPoint>();
        }

        LabBackupsEmpty.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var point in points)
        {
            var item = new MyListItem
            {
                Title = string.IsNullOrWhiteSpace(point.World) ? point.Label : $"{point.Label}（{point.World}）",
                Info = $"{Lang.Date(point.CreatedUtc.ToLocalTime(), "g")} · {Lang.FileSize(point.ArchiveBytes)} · " +
                       Lang.Text("Instance.Saves.Backup.Files", point.FileCount.ToString()),
                Height = 42,
                Type = MyListItem.CheckType.Clickable
            };
            item.Click += (_, _) => ModBase.OpenExplorer(point.Folder);

            var btnRestore = new MyIconButton
            {
                SvgIcon = "lucide/rotate-ccw",
                ToolTip = Lang.Text("Instance.Saves.Backup.Restore")
            };
            btnRestore.Click += (_, _) =>
            {
                if (ModMain.MyMsgBox(Lang.Text("Instance.Saves.Backup.RestoreConfirm", point.World, point.Label),
                        Lang.Text("Instance.Saves.Backup.Restore"), isWarn: true) != 1) return;
                ModWorldBackups.Start(instance, restoreId: point.Id);
            };
            var btnOpen = new MyIconButton
            {
                SvgIcon = "lucide/folder-open",
                ToolTip = Lang.Text("Instance.Saves.Backup.OpenFolder")
            };
            btnOpen.Click += (_, _) => ModBase.OpenExplorer(point.Folder);
            var btnDelete = new MyIconButton
            {
                SvgIcon = "lucide/trash-2",
                ToolTip = Lang.Text("Instance.Saves.Backup.Delete")
            };
            btnDelete.Click += (_, _) =>
            {
                if (ModMain.MyMsgBox(Lang.Text("Instance.Saves.Backup.DeleteConfirm", "1"),
                        Lang.Text("Instance.Saves.Backup.Delete"), isWarn: true) != 1) return;
                ModWorldBackups.Start(instance, deleteIds: new[] { point.Id });
            };

            item.Buttons = new[] { btnRestore, btnOpen, btnDelete };
            PanBackups.Children.Add(item);
        }
    }
}
