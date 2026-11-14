using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Controls;
using PCL.Core.App;
using PCL.Core.UI;

namespace PCL.Pages.PageTools;

/// <summary>
///     陶瓦联机（Terracotta）工具页。
///     <para>原先挂在「设置 → 杂项」，3.0.1 起整体搬到工具页，逻辑原样保留。</para>
/// </summary>
public partial class PageToolsTerracotta
{
    public PageToolsTerracotta()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateTerracottaVisibility();
        Unloaded += (_, _) => _StopTerracottaStatusTimer();
    }

    private System.Windows.Threading.DispatcherTimer? _terracottaStatusTimer;
    private bool _terracottaBusy;
    private string _lastAutoInvite = "";

    /// <summary>
    /// 总开关。关闭时把详细选项整体收起，并让插件自己停止后台轮询。
    /// </summary>
    private void TerracottaEnabled_Change(object senderRaw, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;

        Config.Terracotta.Enabled = ((MyCheckBox)senderRaw).Checked == true;
        UpdateTerracottaVisibility();
    }

    private void UpdateTerracottaVisibility()
    {
        var enabled = Config.Terracotta.Enabled;
        PanTerracottaOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        LabTerracottaDisabledHint.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (!enabled)
        {
            _StopTerracottaStatusTimer();
            UpdateTerracottaStatus();
            return;
        }

        UpdateTerracottaPathHint();
        UpdateTerracottaStatus();
        _StartTerracottaStatusTimer();
    }

    private void _StartTerracottaStatusTimer()
    {
        _terracottaStatusTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _terracottaStatusTimer.Tick -= _TerracottaStatusTick;
        _terracottaStatusTimer.Tick += _TerracottaStatusTick;
        _terracottaStatusTimer.Start();
    }

    private void _StopTerracottaStatusTimer()
    {
        _terracottaStatusTimer?.Stop();
    }

    private void _TerracottaStatusTick(object? sender, EventArgs e)
    {
        UpdateTerracottaStatus();
    }

    /// <summary>
    /// 状态由插件后台线程轮询，这里只读取快照，绝不主动发请求。
    /// </summary>
    private void UpdateTerracottaStatus()
    {
        var snapshot = TerracottaAddon.Snapshot;
        var builder = new StringBuilder();

        if (!Config.Terracotta.Enabled)
        {
            builder.Append("状态：已关闭");
            LabTerracottaStatus.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
        }
        else if (!snapshot.Running)
        {
            builder.Append("状态：").Append(snapshot.Error ?? snapshot.Hint ?? "未启动");
            LabTerracottaStatus.SetResourceReference(TextBlock.ForegroundProperty,
                string.IsNullOrWhiteSpace(snapshot.Error) ? "ColorBrushGray2" : "ColorBrushRedLight");
        }
        else
        {
            builder.Append("状态：").Append(TerracottaSnapshot.Describe(snapshot));

            if (!string.IsNullOrWhiteSpace(snapshot.Room))
                builder.Append('\n').Append("邀请码：").Append(snapshot.Room);
            if (!string.IsNullOrWhiteSpace(snapshot.Address))
                builder.Append('\n').Append("连接地址：").Append(snapshot.Address);
            if (snapshot.Players.Count > 0)
                builder.Append('\n').Append("房间玩家：").Append(string.Join("、", snapshot.Players));

            var brush = snapshot.State is "host-ok" or "guest-ok"
                ? "ColorBrush1"
                : snapshot.State == "exception" ? "ColorBrushRedLight" : "ColorBrushGray2";
            LabTerracottaStatus.SetResourceReference(TextBlock.ForegroundProperty, brush);

            // 创建房间成功后自动把邀请码填进输入框；用户手动改过就不再覆盖。
            if (snapshot.State == "host-ok" && !string.IsNullOrEmpty(snapshot.Room))
            {
                if (string.IsNullOrWhiteSpace(TextTerracottaInvite.Text) ||
                    TextTerracottaInvite.Text == _lastAutoInvite)
                    TextTerracottaInvite.Text = snapshot.Room;
                _lastAutoInvite = snapshot.Room;
            }
        }

        LabTerracottaStatus.Text = builder.ToString();

        var running = snapshot.Running;
        var busy = _terracottaBusy;
        var enabled = Config.Terracotta.Enabled;
        var idle = running && snapshot.State == "waiting";

        BtnTerracottaStart.IsEnabled = enabled && !running && !busy;
        BtnTerracottaStop.IsEnabled = enabled && running && !busy;
        BtnTerracottaHost.IsEnabled = running && idle && !busy;
        BtnTerracottaReset.IsEnabled = running && snapshot.State != "waiting" && !busy;
        BtnTerracottaCopyAddress.IsEnabled = !string.IsNullOrWhiteSpace(snapshot.Address);

        TextTerracottaPath.IsEnabled = enabled && !running;
        TextTerracottaName.IsEnabled = enabled;
        BtnTerracottaBrowse.IsEnabled = enabled && !running;
        BtnTerracottaDetect.IsEnabled = enabled && !running;
    }

    /// <summary>
    /// 显示检测到的程序路径。找不到时给出明确的放置位置提示。
    /// </summary>
    private void UpdateTerracottaPathHint()
    {
        var detected = TerracottaAddon.DetectExecutable();
        if (!string.IsNullOrWhiteSpace(detected))
        {
            LabTerracottaPathHint.Text = "状态：已找到 " + detected;
            LabTerracottaPathHint.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
            return;
        }

        LabTerracottaPathHint.Text =
            "状态：未找到 terracotta.exe。请把它放到 PCL\\Terracotta\\ 下，或点击右侧「浏览」手动选择。";
        LabTerracottaPathHint.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
    }

    /// <summary>
    /// 路径与昵称共用同一个 TextChanged。路径改变时顺手刷新一次检测提示。
    /// </summary>
    private void TerracottaText_Change(object sender, TextChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;

        if (ReferenceEquals(sender, TextTerracottaPath))
        {
            Config.Terracotta.ExecutablePath = TextTerracottaPath.Text;
            UpdateTerracottaPathHint();
        }
        else if (ReferenceEquals(sender, TextTerracottaName))
        {
            Config.Terracotta.PlayerName = TextTerracottaName.Text;
        }
    }

    private void TerracottaOption_Change(object senderRaw, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;

        Config.Terracotta.AutoStart = CheckTerracottaAutoStart.Checked == true;
        Config.Terracotta.AutoFillServer = CheckTerracottaAutoFill.Checked == true;
        Config.Terracotta.StopOnExit = CheckTerracottaStopOnExit.Checked == true;
    }

    private void BtnTerracottaBrowse_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var initialDirectory = ModBase.exePath;
            var detected = TerracottaAddon.DetectExecutable();
            if (!string.IsNullOrWhiteSpace(detected))
            {
                try
                {
                    initialDirectory = Path.GetDirectoryName(detected) ?? initialDirectory;
                }
                catch (ArgumentException)
                {
                    // 路径非法时退回启动器目录。
                }
            }

            var selected = SystemDialogs.SelectFile(
                "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                "选择 terracotta.exe",
                initialDirectory);
            if (string.IsNullOrWhiteSpace(selected)) return;

            TextTerracottaPath.Text = selected;
            Config.Terracotta.ExecutablePath = selected;
            UpdateTerracottaPathHint();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Terracotta] 选择程序路径失败");
            HintService.Hint("选择程序路径失败，请手动填写。", HintType.Error, false);
        }
    }

    private void BtnTerracottaDetect_Click(object sender, MouseButtonEventArgs e)
    {
        var detected = TerracottaAddon.DetectExecutable();
        if (string.IsNullOrWhiteSpace(detected))
        {
            HintService.Hint("没有找到 terracotta.exe，请检查 PCL\\Terracotta\\ 目录。", HintType.Error, false);
            UpdateTerracottaPathHint();
            return;
        }

        TextTerracottaPath.Text = detected;
        Config.Terracotta.ExecutablePath = detected;
        UpdateTerracottaPathHint();
        HintService.Hint("已找到陶瓦联机程序。", HintType.Success, false);
    }

    private async void BtnTerracottaStart_Click(object sender, MouseButtonEventArgs e)
    {
        await RunTerracottaCommandAsync(TerracottaAddon.StartAsync);
    }

    private async void BtnTerracottaStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (TerracottaAddon.Snapshot.Running &&
            ModMain.MyMsgBox("停止陶瓦联机会断开当前房间内的所有连接。\n如果还有其他启动器正在使用陶瓦，也会一并停止。",
                "停止陶瓦联机", "停止", "取消", isWarn: true) != 1)
            return;

        await RunTerracottaCommandAsync(() => TerracottaAddon.ShutdownAsync());
    }

    private async void BtnTerracottaHost_Click(object sender, MouseButtonEventArgs e)
    {
        await RunTerracottaCommandAsync(TerracottaAddon.CreateRoomAsync);
    }

    private async void BtnTerracottaJoin_Click(object sender, MouseButtonEventArgs e)
    {
        var code = TextTerracottaInvite.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            HintService.Hint("请先填写邀请码。", HintType.Error, false);
            return;
        }

        await RunTerracottaCommandAsync(() => TerracottaAddon.JoinRoomAsync(code));
    }

    private async void BtnTerracottaReset_Click(object sender, MouseButtonEventArgs e)
    {
        await RunTerracottaCommandAsync(TerracottaAddon.ResetAsync);
    }

    private async Task RunTerracottaCommandAsync(Func<Task<TerracottaCommandResult>> command)
    {
        if (_terracottaBusy) return;

        _terracottaBusy = true;
        UpdateTerracottaStatus();
        try
        {
            var result = await command();
            HintService.Hint(result.Message, result.Success ? HintType.Success : HintType.Error, false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Terracotta] 执行联机命令失败");
            HintService.Hint("操作失败：" + ex.Message, HintType.Error, false);
        }
        finally
        {
            _terracottaBusy = false;
            UpdateTerracottaStatus();
        }
    }

    private void BtnTerracottaCopyCode_Click(object sender, MouseButtonEventArgs e)
    {
        var room = TerracottaAddon.Snapshot.Room;
        if (string.IsNullOrWhiteSpace(room))
        {
            HintService.Hint("当前还没有邀请码，请先创建房间。", HintType.Error, false);
            return;
        }

        try
        {
            Clipboard.SetText(room);
            HintService.Hint("邀请码已复制。", HintType.Success, false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Terracotta] 复制邀请码失败");
            HintService.Hint("复制失败，请手动复制。", HintType.Error, false);
        }
    }

    private void BtnTerracottaCopyAddress_Click(object sender, MouseButtonEventArgs e)
    {
        var address = TerracottaAddon.Snapshot.Address;
        if (string.IsNullOrWhiteSpace(address))
        {
            HintService.Hint("当前还没有连接地址，请先加入房间。", HintType.Error, false);
            return;
        }

        try
        {
            Clipboard.SetText(address);
            HintService.Hint("连接地址已复制，可直接粘贴到「服务器地址」中。", HintType.Success, false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Terracotta] 复制连接地址失败");
            HintService.Hint("复制失败，请手动复制。", HintType.Error, false);
        }
    }
}
