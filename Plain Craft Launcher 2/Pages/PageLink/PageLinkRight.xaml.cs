using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.Ipv6Direct;

namespace PCL.Pages.PageLink;

public partial class PageLinkRight
{
    public PageLinkRight()
    {
        InitializeComponent();
        Loaded += (_, _) => InitLinkSection();
        Unloaded += (_, _) =>
        {
            _ = StopHostAsync();
            _ = StopGuestAsync();
        };
    }


    // ============ 联机（IPv6 直连） ============

    private Ipv6HostSession? _hostSession;
    private Ipv6GuestSession? _guestSession;
    private IReadOnlyList<Ipv6Candidate> _ipv6Candidates = Array.Empty<Ipv6Candidate>();

    /// <summary>
    ///     打开页面时回填开关与上次的邀请码。
    /// </summary>
    private void InitLinkSection()
    {
        CheckLinkEnabled.SetChecked(Config.Link.DirectLinkEnabled, false);
        PanLinkBody.Visibility = Config.Link.DirectLinkEnabled ? Visibility.Visible : Visibility.Collapsed;
        TextGuestInvite.Text = Config.Link.DirectLinkLastInvite ?? "";
        if (Config.Link.DirectLinkEnabled) RefreshIpv6Status();
    }

    private void LinkEnabled_Change(object senderRaw, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        var enabled = ((MyCheckBox)senderRaw).Checked == true;
        Config.Link.DirectLinkEnabled = enabled;
        PanLinkBody.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled)
        {
            RefreshIpv6Status();
        }
        else
        {
            _ = StopHostAsync();
            _ = StopGuestAsync();
        }
    }

    private void BtnIpv6Refresh_Click(object sender, MouseButtonEventArgs e) => RefreshIpv6Status();

    /// <summary>
    ///     重新枚举本机公网 IPv6 地址，没有就把原因写到状态行。
    /// </summary>
    private void RefreshIpv6Status()
    {
        _ipv6Candidates = Ipv6AddressDetector.GetPublicIpv6Addresses();
        LabIpv6Status.Text = _ipv6Candidates.Count == 0
            ? Lang.Text("Link.Env.NoIpv6Desc")
            : $"{Lang.Text("Link.Sidebar.Ipv6Ready")} · {_ipv6Candidates[0].Address}";
    }

    private async void BtnLinkHost_Click(object sender, MouseButtonEventArgs e)
    {
        if (_hostSession is not null)
        {
            await StopHostAsync();
            return;
        }

        RefreshIpv6Status();
        if (_ipv6Candidates.Count == 0)
        {
            HintService.Hint(Lang.Text("Link.Env.NoIpv6Title"), HintType.Error);
            return;
        }

        if (!ushort.TryParse(TextHostPort.Text.Trim(), out var mcPort) || mcPort == 0)
        {
            HintService.Hint(Lang.Text("Link.Host.PortLabel") + " 1 - 65535", HintType.Error);
            return;
        }

        try
        {
            _hostSession = Ipv6HostSession.Start(_ipv6Candidates[0].Address, mcPort, false);
            _hostSession.StateChanged += () => Dispatcher.BeginInvoke(UpdateHostPeersUi);
            TextHostInvite.Text = _hostSession.InviteCode;
            PanHostResult.Visibility = Visibility.Visible;
            BtnLinkHost.Text = Lang.Text("Link.Host.StopBtn");
            UpdateHostPeersUi();
            TrySetClipboard(_hostSession.InviteCode);
            HintService.Hint(Lang.Text("Link.Host.InviteHint"), HintType.Success);
        }
        catch (Exception ex)
        {
            HintService.Hint(ex.Message, HintType.Error);
        }
    }

    private async Task StopHostAsync()
    {
        if (_hostSession is null) return;
        var session = _hostSession;
        _hostSession = null;
        await session.DisposeAsync();
        PanHostResult.Visibility = Visibility.Collapsed;
        TextHostInvite.Text = "";
        TextHostAckInput.Text = "";
        LabHostPeers.Text = Lang.Text("Link.Host.NoPeers");
        BtnLinkHost.Text = Lang.Text("Link.Host.StartBtn");
        BtnLinkHost.ColorType = MyButton.ColorState.Highlight;
    }

    private void UpdateHostPeersUi()
    {
        if (_hostSession is null) return;
        var peers = _hostSession.GetPeers();
        if (peers.Count == 0)
        {
            LabHostPeers.Text = Lang.Text("Link.Host.NoPeers");
            LabLinkStatus.Text = Lang.Text("Link.Sidebar.HostWaiting", _hostSession.McPort);
            return;
        }

        LabHostPeers.Text = string.Join(Environment.NewLine,
            peers.Select(p => $"{p.Endpoint.Address}  ·  {p.State}  ·  {p.RttMs} ms"));
        LabLinkStatus.Text = Lang.Text("Link.Sidebar.HostPeersCount", peers.Count, _hostSession.McPort);
    }

    private void BtnHostCopy_Click(object sender, MouseButtonEventArgs e)
    {
        if (_hostSession is null) return;
        TrySetClipboard(_hostSession.InviteCode);
        HintService.Hint(Lang.Text("Link.Host.CopyAgain"));
    }

    private void BtnHostAccept_Click(object sender, MouseButtonEventArgs e)
    {
        if (_hostSession is null) return;
        var ack = TextHostAckInput.Text.Trim();
        if (string.IsNullOrEmpty(ack))
        {
            try
            {
                if (Clipboard.ContainsText()) ack = Clipboard.GetText().Trim();
            }
            catch
            {
                // 剪贴板被别的进程占着，忽略。
            }
        }

        if (string.IsNullOrEmpty(ack))
        {
            HintService.Hint(Lang.Text("Link.Host.AckPlaceholder"), HintType.Error);
            return;
        }

        if (_hostSession.TryAcceptGuestAck(ack, out var message))
        {
            HintService.Hint(message, HintType.Success);
            TextHostAckInput.Text = "";
            UpdateHostPeersUi();
        }
        else
        {
            HintService.Hint(message, HintType.Error);
        }
    }

    private async void BtnLinkJoin_Click(object sender, MouseButtonEventArgs e)
    {
        if (_guestSession is not null)
        {
            await StopGuestAsync();
            return;
        }

        RefreshIpv6Status();
        if (_ipv6Candidates.Count == 0)
        {
            HintService.Hint(Lang.Text("Link.Env.NoIpv6Title"), HintType.Error);
            return;
        }

        var raw = TextGuestInvite.Text.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            try
            {
                if (Clipboard.ContainsText()) raw = Clipboard.GetText().Trim();
            }
            catch
            {
                // 同上，读不到就让用户手填。
            }
        }

        if (!InviteCodeCodec.TryDecode(raw, out var invite, out var error))
        {
            HintService.Hint(error, HintType.Error);
            return;
        }

        try
        {
            _guestSession = Ipv6GuestSession.Start(_ipv6Candidates[0].Address, invite);
            _guestSession.StateChanged += () => Dispatcher.BeginInvoke(UpdateGuestUi);
            Config.Link.DirectLinkLastInvite = raw;
            TextGuestInvite.Text = raw;
            PanGuestResult.Visibility = Visibility.Visible;
            TextGuestAck.Text = _guestSession.AckCode;
            TextGuestAddr.Text = $"127.0.0.1:{_guestSession.LocalProxyPort}";
            UpdateGuestUi();
            TrySetClipboard(_guestSession.AckCode);
            HintService.Hint(Lang.Text("Link.Guest.Connecting"));
        }
        catch (Exception ex)
        {
            HintService.Hint(ex.Message, HintType.Error);
        }
    }

    private void UpdateGuestUi()
    {
        if (_guestSession is null) return;
        if (_guestSession.IsConnected)
        {
            LabGuestStatus.Text = Lang.Text("Link.Guest.ConnectedStatus", _guestSession.RttMs);
            LabLinkStatus.Text = Lang.Text("Link.Sidebar.GuestConnected");
        }
        else
        {
            LabGuestStatus.Text = Lang.Text("Link.Guest.Connecting");
            LabLinkStatus.Text = Lang.Text("Link.Sidebar.GuestConnecting");
        }
    }

    private async Task StopGuestAsync()
    {
        if (_guestSession is null) return;
        var session = _guestSession;
        _guestSession = null;
        await session.DisposeAsync();
        PanGuestResult.Visibility = Visibility.Collapsed;
        TextGuestAck.Text = "";
        TextGuestAddr.Text = "";
        LabGuestStatus.Text = "";
        LabLinkStatus.Text = "";
    }

    private void BtnGuestAckCopy_Click(object sender, MouseButtonEventArgs e)
    {
        if (_guestSession is null) return;
        TrySetClipboard(_guestSession.AckCode);
        HintService.Hint(Lang.Text("Link.Guest.CopyAck"));
    }

    private void BtnGuestAddrCopy_Click(object sender, MouseButtonEventArgs e)
    {
        if (_guestSession is null) return;
        TrySetClipboard($"127.0.0.1:{_guestSession.LocalProxyPort}");
        HintService.Hint(Lang.Text("Link.Guest.CopyAddr"));
    }

    private async void BtnLinkLeave_Click(object sender, MouseButtonEventArgs e)
    {
        await StopGuestAsync();
        LabLinkStatus.Text = "";
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // 剪贴板被占用时静默失败，邀请码仍可在界面上手动复制。
        }
    }
}
