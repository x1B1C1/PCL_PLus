using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL.Modules.Updates;

public static class AnnouncementService
{
    public static void Load()
    {
        // 0 = 全部显示，1 = 仅重要，2 = 完全关闭
        var solution = States.System.AnnounceSolution;
        if (solution > 1) return;

        var showedAnnounced = States.Hint.ShowedAnnouncements
            .Split("|".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var showAnnounce = UpdateManager.remoteServer.GetAnnouncementList().Content
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            // 选了「仅重要」的人只该看到 important: true 的那几条
            .Where(x => solution != 1 || x.Important)
            .Where(x => !showedAnnounced.Contains(x.Id))
            .ToList();

        ModBase.Log("[System] 需要展示的公告数量：" + showAnnounce.Count);

        ModBase.RunInNewThread(() =>
        {
            foreach (var item in showAnnounce)
            {
                var first = _ResolveAction(item.Btn1);
                var second = _ResolveAction(item.Btn2);
                // 主按钮一直显示。第一条动作为空（或不被允许）时降级成「关闭」，
                // 免得留一个点下去没反应的按钮。
                ModMain.MyMsgBox(item.Detail, item.Title,
                    first is null ? Lang.Text("Common.Action.Close") : first.Text,
                    second?.Text ?? "",
                    first is null ? "" : Lang.Text("Common.Action.Close"),
                    button1Action: first is null ? null : () => _Invoke(first),
                    button2Action: second is null ? null : () => _Invoke(second));
            }
        });

        showedAnnounced.AddRange(showAnnounce.Select(x => x.Id));
        showedAnnounced = showedAnnounced.Distinct().ToList();
        States.Hint.ShowedAnnouncements = showedAnnounced.Join("|");
    }

    /// <summary>
    ///     判断一条公告按钮能不能用。文字为空、事件解析不出来、或者被安全策略拦下，都当作没有按钮。
    /// </summary>
    private static AnnouncementBtnInfoModel? _ResolveAction(AnnouncementBtnInfoModel? button)
    {
        if (button is null || string.IsNullOrWhiteSpace(button.Text)) return null;
        if (!EventTypeMapper.TryParse(button.Command, out var type)) return null;
        return _IsAllowed(type, button.CommandParameter) ? button : null;
    }

    /// <summary>
    ///     公告按钮允许做什么。
    ///     <para>
    ///         默认只放行「打开 https 网页」：公告由远端下发，如果按钮能触发执行命令、启动游戏、
    ///         改设置之类的事件，等于把整台机器的操作权交给下发方（Lite 那边把这个叫 door）。
    ///         在设置里打开 <c>SystemUpdateAnnouncementLocalActions</c> 后才放行全部事件，仅限 BF 试验线。
    ///     </para>
    /// </summary>
    private static bool _IsAllowed(EventType type, string? parameter)
    {
        if (Config.Update.AnnouncementLocalActions) return true;
        return type == EventType.OpenUrl
               && Uri.TryCreate(parameter, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && uri.Host.Length > 0;
    }

    private static void _Invoke(AnnouncementBtnInfoModel button)
    {
        if (!EventTypeMapper.TryParse(button.Command, out var type)) return;
        if (!_IsAllowed(type, button.CommandParameter)) return;
        CustomEvent.Raise(type, button.CommandParameter);
    }
}
