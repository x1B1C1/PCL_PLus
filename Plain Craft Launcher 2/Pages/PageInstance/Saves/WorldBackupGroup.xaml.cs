using System;
using System.Collections.Generic;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Saves.Backups;

namespace PCL.Pages.PageInstance.Saves;

/// <summary>
/// 单个存档世界的备份卡片：列出该世界的备份点，并提供备份、恢复、删除入口。
/// </summary>
public partial class WorldBackupGroup
{
    private McInstance? _owner;
    private string _world = "";

    /// <summary>
    /// 创建备份前回调（用于先把设置卡里的保留份数写回配置）。
    /// </summary>
    internal Func<bool>? BeforeCreate;

    public WorldBackupGroup()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 绑定到某个存档世界并刷新备份列表。
    /// </summary>
    internal void Bind(McInstance owner, string worldFolder, IReadOnlyList<WorldBackupPoint> points)
    {
        _owner = owner;
        _world = worldFolder;
        Title = Lang.Text("Instance.Saves.Backup.Count", points.Count.ToString());
        PanPoints.Children.Clear();
        LabEmpty.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnCreate.IsEnabled = points.Count >= 0;
        foreach (var point in points)
        {
            PanPoints.Children.Add(BuildItem(point));
        }
    }

    private MyListItem BuildItem(WorldBackupPoint point)
    {
        var item = new MyListItem
        {
            Title = point.Label,
            Info = $"{point.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {point.Reason}",
            Type = MyListItem.CheckType.Clickable
        };

        var btnRestore = new MyIconButton
        {
            SvgIcon = "lucide/rotate-ccw",
            ToolTip = Lang.Text("Instance.Saves.Backup.Restore")
        };
        btnRestore.Click += (_, _) => Restore(point);

        var btnOpen = new MyIconButton
        {
            SvgIcon = "lucide/folder-open",
            ToolTip = Lang.Text("Instance.Saves.Backup.OpenFolder")
        };
        btnOpen.Click += (_, _) =>
        {
            if (_owner is not null) ModWorldBackups.OpenFolder(_owner);
        };

        var btnDelete = new MyIconButton
        {
            SvgIcon = "lucide/trash-2",
            ToolTip = Lang.Text("Instance.Saves.Backup.Delete")
        };
        btnDelete.Click += (_, _) => Delete(point);

        item.Buttons = new[] { btnRestore, btnOpen, btnDelete };
        return item;
    }

    private void Restore(WorldBackupPoint point)
    {
        if (_owner is null) return;
        var answer = ModMain.MyMsgBox(
            Lang.Text("Instance.Saves.Backup.RestoreConfirm", point.Label,
                point.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            Lang.Text("Instance.Saves.Backup.Restore"),
            Lang.Text("Common.Action.Confirm"),
            Lang.Text("Common.Action.Cancel"));
        if (answer != 1) return;
        ModWorldBackups.Start(_owner, restoreId: point.Id);
    }

    private void Delete(WorldBackupPoint point)
    {
        if (_owner is null) return;
        var answer = ModMain.MyMsgBox(
            Lang.Text("Instance.Saves.Backup.DeleteConfirm", "1"),
            Lang.Text("Instance.Saves.Backup.Delete"),
            Lang.Text("Common.Action.Confirm"),
            Lang.Text("Common.Action.Cancel"));
        if (answer != 1) return;
        ModWorldBackups.Start(_owner, deleteIds: new[] { point.Id });
    }

    private void BtnCreate_Click(object sender, ModBase.RouteEventArgs e)
    {
        if (_owner is null) return;
        if (BeforeCreate is not null && !BeforeCreate()) return;
        ModWorldBackups.Start(_owner, worldIdentifier: _world);
    }
}
