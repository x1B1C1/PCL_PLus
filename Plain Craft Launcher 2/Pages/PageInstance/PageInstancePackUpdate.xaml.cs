using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.PackUpdate;

namespace PCL.Pages.PageInstance;

/// <summary>
///     整合包更新页：选一个整合包压缩包，预览差异，确认后事务式应用。
///     <para>P0 只支持本地压缩包（.mrpack / CurseForge zip），在线下载走下载页原有的流程。</para>
/// </summary>
public partial class PageInstancePackUpdate : IRefreshable
{
    private PackPackage? _package;
    private PackUpdatePlan? _plan;

    public PageInstancePackUpdate()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        _package = null;
        _plan = null;
        LabPicked.Text = "";
        LabSummary.Text = "";
        PanEntries.Children.Clear();
        LabPreviewHint.Visibility = Visibility.Visible;
        BtnApply.IsEnabled = false;
    }

    private void BtnPick_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Lang.Text("Instance.PackUpdate.Pick"),
            Filter = Lang.Text("Instance.PackUpdate.Filter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _package = PackPackageReader.Read(dialog.FileName);
            LabPicked.Text = Path.GetFileName(dialog.FileName);
            BuildPreview();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[PackUpdate] 读取整合包失败");
            ModMain.MyMsgBox(Lang.Text("Instance.PackUpdate.ReadFailed", ex.Message),
                Lang.Text("Instance.PackUpdate.Title"), isWarn: true);
        }
    }

    private void BuildPreview()
    {
        PanEntries.Children.Clear();
        var instance = ModInstanceList.McMcInstanceSelected;
        if (_package is null || instance is null)
        {
            BtnApply.IsEnabled = false;
            return;
        }

        try
        {
            _plan = PackUpdatePlanner.Plan(_package, instance.PathIndie);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[PackUpdate] 生成更新预览失败");
            LabPreviewHint.Text = Lang.Text("Instance.PackUpdate.PlanFailed", ex.Message);
            LabPreviewHint.Visibility = Visibility.Visible;
            BtnApply.IsEnabled = false;
            return;
        }

        LabPreviewHint.Visibility = Visibility.Collapsed;
        LabSummary.Text = Lang.Text("Instance.PackUpdate.Summary",
            _plan.Added, _plan.Updated, _plan.Removed, _plan.Preserved);

        // 预览只列会真正改动文件系统的三类，保留项不占地方
        var changes = _plan.Entries
            .Where(entry => entry.Kind != PackUpdateKind.Preserved)
            .Take(200)
            .ToList();
        foreach (var entry in changes)
        {
            var item = new MyListItem
            {
                Height = 38,
                Type = MyListItem.CheckType.None,
                IsScaleAnimationEnabled = false
            };
            var label = new TextBlock
            {
                Text = entry.RelativePath,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            item.Children.Add(label);
            PanEntries.Children.Add(item);
        }

        if (_plan.Entries.Count > changes.Count)
        {
            PanEntries.Children.Add(new TextBlock
            {
                Text = Lang.Text("Instance.PackUpdate.More", _plan.Entries.Count - changes.Count),
                Margin = new Thickness(5, 6, 0, 0),
                FontSize = 12,
                Opacity = 0.7
            });
        }

        BtnApply.IsEnabled = !_plan.IsEmpty;
    }

    private void BtnApply_Click(object sender, MouseButtonEventArgs e)
    {
        var instance = ModInstanceList.McMcInstanceSelected;
        if (_package is null || _plan is null || instance is null) return;
        if (ModMain.MyMsgBox(Lang.Text("Instance.PackUpdate.ApplyConfirm",
                _plan.Added, _plan.Updated, _plan.Removed),
                Lang.Text("Instance.PackUpdate.Title"), isWarn: true) != 1) return;

        var package = _package;
        var plan = _plan;
        var gameRoot = instance.PathIndie;
        var workRoot = Path.Combine(instance.PathInstance, "PCL");
        var loader = new ModLoader.LoaderTask<string, string>("Loader PackUpdate", task =>
        {
            var result = PackUpdateApplier.Apply(package, plan, gameRoot, workRoot,
                task.AbortedToken, progress => task.Progress = progress);
            task.output = "applied";
            ModBase.RunInUi(() =>
            {
                HintService.Hint(Lang.Text("Instance.PackUpdate.Applied",
                    result.Written, result.Deleted, result.Preserved), HintType.Success);
                Refresh();
            });
        });
        var combo = new ModLoader.LoaderCombo<string>("更新整合包", new List<ModLoader.LoaderBase> { loader });
        ModLoader.LoaderTaskbarAdd(combo);
        combo.Start();
    }
}
