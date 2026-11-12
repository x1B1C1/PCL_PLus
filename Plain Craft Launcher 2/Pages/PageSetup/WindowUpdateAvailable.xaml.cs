using System.Windows;
using System.Windows.Input;

namespace PCL.Pages.PageSetup;

/// <summary>
///     发现新版本时的提示窗口：黑底白字、蓝色按钮，与更新说明窗口同一套样式。
///     <para>
///         只有远端版本比当前版本新时才会弹出；用户点「立即更新」返回 <c>true</c>，
///         由调用方决定跳去设置页还是直接开始下载。
///     </para>
/// </summary>
public partial class WindowUpdateAvailable
{
    public WindowUpdateAvailable(string currentVersion, string latestVersion, string? changelog)
    {
        InitializeComponent();
        LabLatest.Text = latestVersion;
        LabCurrent.Text = $"当前版本 {currentVersion}，可以更新到上面的版本。";
        PanChangelog.Text = _FormatChangelog(changelog);
    }

    private static string _FormatChangelog(string? changelog)
    {
        var text = (changelog ?? string.Empty).Trim();
        if (text.Length == 0) return "这次更新没有提供说明，可以直接更新。";
        // 更新日志可能很长，窗口里只展示前 6000 字，剩下的去发布页看
        if (text.Length > 6000) text = text[..6000] + "\n……";
        return text;
    }

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e) => DialogResult = true;

    private void BtnLater_Click(object sender, MouseButtonEventArgs e) => DialogResult = false;
}
