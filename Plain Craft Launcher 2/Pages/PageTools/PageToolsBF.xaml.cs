using System;
using System.Runtime.InteropServices;
using PCL.Core.App;
#if BF_DEBUG
using PCL.Modules.Debug;
#endif

namespace PCL.Pages.PageTools;

/// <summary>
/// BF 构建专用的工程控制台页面。
/// <para>
/// 只在 BF 线（metadata.json suffix = <c>bf</c>）的左栏出现，正式线 / 测试线既看不到入口，
/// 也不会执行到这里；控制台本体见 <see cref="PCL.Controls.BfConsole" />，它同时被「工具」页顶部的
/// BF 卡片复用。它随 BF 的本地构建分发，不进正式发布包。
/// </para>
/// </summary>
public partial class PageToolsBF
{
    public PageToolsBF()
    {
        InitializeComponent();
        var version = Basics.Metadata.Version;
        LabBuildInfo.Text = string.Join(Environment.NewLine, new[]
        {
            $"版本      {version.BaseName}",
            $"Base      {version.BaseVersion}",
            $"后缀      {(string.IsNullOrWhiteSpace(version.Suffix) ? "(无)" : version.Suffix)}",
            $"代号      {version.Code}",
            $"上游      {version.UpstreamVersion}",
            $"构建线    BF（仅本地，不发布）",
            $"提交      {version.CommitDigest}",
            $"运行时    {Environment.Version} / {RuntimeInformation.ProcessArchitecture}",
            $"数据目录  {Paths.Data}",
            $"日志目录  {ModBase.exePath}PCL\\Log\\"
        });

#if BF_DEBUG
        // 【调试专属模块】第 1 项：右侧调试日志面板开关（与面板上的关闭按钮双向同步）
        CheckDebugPanel.Checked = BfDebugPanel.IsVisible;
        CheckDebugPanel.Change += (_, on) =>
        {
            if (!_syncingPanelSwitch) BfDebugPanel.SetVisible(on);
        };
        BfDebugPanel.VisibilityChanged += SyncPanelSwitch;
        SyncPanelSwitch(BfDebugPanel.IsVisible);
#endif
    }

#if BF_DEBUG
    private bool _syncingPanelSwitch;

    private void SyncPanelSwitch(bool visible)
    {
        if (_syncingPanelSwitch) return;
        _syncingPanelSwitch = true;
        try
        {
            CheckDebugPanel.Checked = visible;
        }
        finally
        {
            _syncingPanelSwitch = false;
        }
    }
#endif
}
