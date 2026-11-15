using System;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App.Localization;

namespace PCL.Pages.PageLink;

public partial class PageLinkLeft
{
    private bool isLoad;

    public PageLinkLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += PageLinkLeft_Loaded;
    }

    private void PageLinkLeft_Loaded(object sender, RoutedEventArgs e)
    {
        if (isLoad) return;
        isLoad = true;
        ItemDirect.SetChecked(true, false, false);
    }

    /// <summary>
    ///     当前页面的编号。联机目前只有一个子页面，保留这个字段是为了和左栏的切换逻辑保持一致。
    /// </summary>
    public FormMain.PageSubType pageID = FormMain.PageSubType.Default;

    private void PageCheck(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyListItem)senderRaw;
        if (sender.Tag is not null)
            PageChange((FormMain.PageSubType)ModBase.Val(sender.Tag));
    }

    public object PageGet(FormMain.PageSubType? id = null)
    {
        ModMain.frmLinkRight ??= new PageLinkRight();
        return ModMain.frmLinkRight;
    }

    public void SwitchTo(FormMain.PageSubType id)
    {
        ItemDirect.SetChecked(true, false, true);
        PageChange(id);
    }

    public void PageChange(FormMain.PageSubType id)
    {
        if (pageID == id)
            return;
        ModAnimation.AniControlEnabled += 1;
        try
        {
            PageChangeRun((MyPageRight)PageGet(id));
            pageID = id;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"切换分页面失败（ID {(int)id}）",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Tools.Error.OperationFailed"));
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }
    }

    private static void PageChangeRun(MyPageRight target)
    {
        ModAnimation.AniStop("FrmMain PageChangeRight");
        if (target.Parent is not null)
            target.SetValue(ContentPresenter.ContentProperty, null);
        ModMain.frmMain.pageRight = target;
        ((MyPageRight)ModMain.frmMain.PanMainRight.Child).PageOnExit();
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                ((MyPageRight)ModMain.frmMain.PanMainRight.Child).PageOnForceExit();
                ModMain.frmMain.PanMainRight.Child = ModMain.frmMain.pageRight;
                ModMain.frmMain.pageRight.Opacity = 0d;
            }, 130),
            ModAnimation.AaCode(() =>
            {
                ModMain.frmMain.pageRight.Opacity = 1d;
                ModMain.frmMain.pageRight.PageOnEnter();
            }, 30, true)
        }, "PageLeft PageChange");
    }
}
