using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.App.Tools;
using PCL.Core.IO;
using PCL.Core.IO.Net;
using PCL.Core.UI;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Secret;
using PCL.Core.Utils.Validate;
using PCL.Modules.Network;
using PCL.Modules.Network.Loaders;
using PCL.Core.App.Localization;
using System.Globalization;

namespace PCL.Pages.PageTools;

public partial class PageToolsTest
{
    public PageToolsTest()
    {
        InitializeComponent();
        Loaded += (_, _) => MeLoaded();
        #if DEBUG
        BtnCrash.Visibility = Visibility.Visible;
        #endif
    }

    private void MeLoaded()
    {
        BtnDownloadStart.IsEnabled = false;

        TextDownloadFolder.Text = States.Tool.DownloadFolder;
        TextDownloadFolder.Validate();

        if (!string.IsNullOrEmpty(TextDownloadFolder.ValidateResult) || string.IsNullOrEmpty(TextDownloadFolder.Text))
            TextDownloadFolder.Text = ModBase.exePath + @"PCL\MyDownload\";

        TextDownloadFolder.Validate();
        TextDownloadName.Validate();
        TextDownloadUrl.Validate();
        StartButtonRefresh();
    }

    private void StartButtonRefresh()
    {
        BtnDownloadStart.IsEnabled = string.IsNullOrEmpty(TextDownloadFolder.ValidateResult) &&
                                     string.IsNullOrEmpty(TextDownloadUrl.ValidateResult) &&
                                     string.IsNullOrEmpty(TextDownloadName.ValidateResult);

        BtnDownloadOpen.IsEnabled = string.IsNullOrEmpty(TextDownloadFolder.ValidateResult);
    }

    private void SaveCacheDownloadFolder(object sender, RoutedEventArgs e)
    {
        States.Tool.DownloadFolder = TextDownloadFolder.Text;
        TextDownloadName.Validate();
    }

    private static void DownloadState(ModLoader.LoaderCombo<int> loader)
    {
        try
        {
            switch (loader.State)
            {
                case ModBase.LoadState.Finished:
                {
                    HintService.Hint(Lang.Text("Tools.Test.CustomDownload.Finished", loader.name), HintType.Success);
                    Console.Beep();
                    break;
                }
                case ModBase.LoadState.Failed:
                {
                    ModBase.Log(
                        loader.Error,
                        $"{loader.name}失败",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
                    Console.Beep();
                    break;
                }
                case ModBase.LoadState.Aborted:
                {
                    HintService.Hint(Lang.Text("Tools.Test.CustomDownload.Aborted", loader.name));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
        }
    }

    public static void StartCustomDownload(string url, string fileName, string folder = null, string userAgent = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = SystemDialogs.SelectSaveFile(Lang.Text("Tools.Test.CustomDownload.SelectLocation"), fileName);
                if (!folder.Contains(@"\")) return;
                if (folder.EndsWith(fileName)) folder = folder[..^fileName.Length];
            }

            folder = folder.Replace("/", @"\").TrimEnd(new[] { '\\' }) + @"\";
            try
            {
                Directory.CreateDirectory(folder);
                ModBase.CheckPermissionWithException(folder);
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    $"访问文件夹失败（{folder}）",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
                return;
            }

            ModBase.Log("[Download] 自定义下载文件名：" + fileName);
            ModBase.Log("[Download] 自定义下载文件目标：" + folder);
            var uuid = ModBase.GetUuid();
            ModLoader.LoaderBase loaderdownload;
            if (new HttpValidator().Validate(url).IsValid)
                loaderdownload = new LoaderDownload(Lang.Text("Tools.Test.CustomDownload.LoaderName", fileName),
                    new List<DownloadFile> { new(new[] { url }, folder + fileName, null, true, userAgent) });
            else // UNC 路径
                loaderdownload = new LoaderDownloadUnc(Lang.Text("Tools.Test.CustomDownload.LoaderName", fileName),
                    new Tuple<string, string>(url, folder + fileName));
            var loaderCombo = new ModLoader.LoaderCombo<int>(Lang.Text("Tools.Test.CustomDownload.LoaderTitle", uuid), new[] { loaderdownload })
                { OnStateChanged = a => DownloadState((ModLoader.LoaderCombo<int>)a) };
            loaderCombo.Start();
            ModLoader.LoaderTaskbarAdd(loaderCombo);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始自定义下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
        }
    }

    public static void Jrrp()
    {
        var random = new Random(GenerateDailySeed());
        var luckValue = random.Next(0, 101);
        var rating = GetRating(luckValue);
        var title = Lang.Text("Tools.Test.Luck.MsgboxTitle", Lang.Date(DateTime.Now, "d"));

        if (luckValue >= 60)
            ModMain.MyMsgBox(Lang.Text("Tools.Test.Luck.MessageGood", luckValue, rating), title);
        else
            ModMain.MyMsgBox(Lang.Text("Tools.Test.Luck.MessageBad", luckValue, rating), title, isWarn: luckValue <= 30);
    }

    public static void RubbishClear()
    {
        ModBase.RunInUi(() =>
        {
            if (ModMain.frmToolsTest is not null && ModMain.frmToolsTest.BtnClear is not null)
                ModMain.frmToolsTest.BtnClear.IsEnabled = false;
        });
        // 只有当没有运行中的Minecraft游戏且启动器不在加载状态时才能清理

        // 清理的文件数量
        // 所有 Minecraft 文件夹


        // 寻找所有 Minecraft 文件夹

        // 删除 Minecraft 的缓存
        // 删除日志和崩溃报告并计数

        // 删除 Natives 文件

        // 删除 PCL 的缓存

        ModBase.RunInNewThread(() =>
        {
            try
            {
                if (!ModWatcher.hasRunningMinecraft && ModLaunch.mcLaunchLoader.State != ModBase.LoadState.Loading)
                {
                    if (ModNet.HasDownloadingTask())
                    {
                        HintService.Hint(Lang.Text("Tools.Test.Clean.WaitForDownload"));
                        return;
                    }

                    if (!ModFolder.mcFolderList.Any()) ModFolder.mcFolderListLoader.Start();
                    if (States.Hint.CleanJunkFile <= 2)
                    {
                        if (ModMain.MyMsgBox(
                                Lang.Text("Tools.Test.Clean.ConfirmMessage"),
                                Lang.Text("Tools.Test.Clean.ConfirmTitle"),
                                Lang.Text("Common.Action.Confirm"),
                                Lang.Text("Common.Action.Cancel")
                            ) == 2) return;
                        States.Hint.CleanJunkFile += 1;
                    }

                    var num = 0;
                    var cleanMcFolderList = new List<DirectoryInfo>();
                    if (!ModFolder.mcFolderList.Any()) ModFolder.mcFolderListLoader.WaitForExit();
                    foreach (var mcFolder in ModFolder.mcFolderList)
                    {
                        cleanMcFolderList.Add(new DirectoryInfo(mcFolder.Location));
                        var dirInfo = new DirectoryInfo(mcFolder.Location + "versions");
                        if (dirInfo.Exists)
                            foreach (var item in dirInfo.EnumerateDirectories())
                                cleanMcFolderList.Add(item);
                    }

                    foreach (var dirInfo in cleanMcFolderList)
                    {
                        num += ModBase.DeleteDirectory(
                            dirInfo.FullName + (dirInfo.FullName.EndsWith(@"\") ? "" : @"\") + @"crash-reports\", true);
                        num += ModBase.DeleteDirectory(
                            dirInfo.FullName + (dirInfo.FullName.EndsWith(@"\") ? "" : @"\") + @"logs\", true);
                        foreach (var fileInfo in dirInfo.EnumerateFiles("*"))
                            if (fileInfo.Name.StartsWith("hs_err_pid") || fileInfo.Name.EndsWith(".log") ||
                                fileInfo.Name == "WailaErrorOutput.txt")
                            {
                                fileInfo.Delete();
                                num += 1;
                            }

                        foreach (var dirInfo2 in dirInfo.EnumerateDirectories())
                            if ((dirInfo2.Name ?? "") == (dirInfo2.Name + "-natives" ?? "") ||
                                dirInfo2.Name == "natives-windows-x86_64")
                                num += ModBase.DeleteDirectory(dirInfo2.FullName, true);
                    }

                    num += ModBase.DeleteDirectory(ModBase.pathTemp, true);
                    num += ModBase.DeleteDirectory(Path.Combine(SystemPaths.DriveLetter, "ProgramData", "PCL"), true);
                    if (num != 0)
                    {
                        ModMain.MyMsgBox(Lang.Text("Tools.Test.Clean.ClearedMessage", num),
                            Lang.Text("Tools.Test.Clean.Cleared"), Lang.Text("Common.Action.Confirm"), "", "", false, true, true);
                        Process.Start(new ProcessStartInfo(Basics.ExecutablePath));
                        FormMain.EndProgramForce();
                    }
                    else
                    {
                        HintService.Hint(Lang.Text("Tools.Test.Clean.NoFiles"));
                    }
                }
                else
                {
                    HintService.Hint(Lang.Text("Tools.Test.Clean.CloseGameFirst"));
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "清理垃圾失败",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Tools.Test.Error.OperationFailed"));
            }
            finally
            {
                ModBase.RunInUiWait(() =>
                {
                    if (ModMain.frmToolsTest is not null && ModMain.frmToolsTest.BtnClear is not null)
                        ModMain.frmToolsTest.BtnClear.IsEnabled = true;
                });
            }
        }, "Rubbish Clear");
    }

    public static string GetRandomCave()
    {
        return Lang.Text("Tools.Test.CeNotice");
    }

    public static string GetRandomHint()
    {
        return Lang.Text("Tools.Test.CeNotice");
    }

    public static string GetRandomPresetHint()
    {
        return Lang.Text("Tools.Test.CeNotice");
    }

    private void TextDownloadUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(TextDownloadName.Text) || string.IsNullOrEmpty(TextDownloadUrl.Text)) return;
            TextDownloadName.Text = ModBase.GetFileNameFromPath(WebUtility.UrlDecode(TextDownloadUrl.Text));
            StartButtonRefresh();
        }
        catch
        {
        }
    }

    private void MyTextButton_Click(object sender, EventArgs e)
    {
        var text = SystemDialogs.SelectFolder();
        if (!string.IsNullOrEmpty(text)) TextDownloadFolder.Text = text;
    }

    private void BtnDownloadOpen_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var text = TextDownloadFolder.Text;
            Directory.CreateDirectory(text);
            Basics.OpenPath(text);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "打开下载文件夹失败");
        }
    }

    private void BtnDownloadStart_Click(object sender, MouseButtonEventArgs e)
    {
        StartCustomDownload(TextDownloadUrl.Text, TextDownloadName.Text, TextDownloadFolder.Text);
        TextDownloadUrl.Text = "";
        TextDownloadUrl.Validate();
        TextDownloadUrl.ForceShowAsSuccess();
        TextDownloadName.Text = "";
        TextDownloadName.Validate();
        TextDownloadName.ForceShowAsSuccess();
        StartButtonRefresh();
    }

    private void TextDownloadUrl_ValidateChanged(object sender, RoutedEventArgs e)
    {
        StartButtonRefresh();
    }

    private void TextDownloadFolder_ValidateChanged(object sender, EventArgs e)
    {
        StartButtonRefresh();
    }

    private void TextDownloadName_ValidateChanged(object sender, EventArgs e)
    {
        StartButtonRefresh();
    }

    private void BtnClear_Click(object sender, MouseButtonEventArgs e)
    {
        RubbishClear();
    }

    // 下载正版玩家皮肤
    // 今日人品
    private void BtnLuck_Click(object sender, MouseButtonEventArgs e)
    {
        Jrrp();
    }

    public static int GenerateDailySeed()
    {
        var datePart = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        return DJB2Hash(datePart + Identify.LauncherId);
    }

    private static int DJB2Hash(string str)
    {
        var hash = 5381L;
        var prime = 33L;
        foreach (var c in str)
        {
            long charValue = c;
            hash = (hash * prime + charValue) % 0x100000000L;
        }

        return (int)(hash & 0x7FFFFFFFL);
    }

    public static string GetRating(int luckValue)
    {
        var key = luckValue switch
        {
            100 => "Tools.Test.Luck.Rating100",
            >= 95 => "Tools.Test.Luck.Rating95",
            >= 90 => "Tools.Test.Luck.Rating90",
            >= 60 => "Tools.Test.Luck.Rating60",
            >= 40 => "Tools.Test.Luck.Rating40",
            >= 30 => "Tools.Test.Luck.Rating30",
            >= 10 => "Tools.Test.Luck.Rating10",
            _ => "Tools.Test.Luck.Rating0"
        };

        return Lang.Text(key);
    }

    private void BtnCreateShortcut_Click(object sender, MouseButtonEventArgs e)
    {
        var shortcutName = Lang.Text("Tools.Test.Shortcut.FileName", ".lnk");
        var desktopName = Lang.Text("Tools.Test.Shortcut.Desktop");
        var startName = Lang.Text("Tools.Test.Shortcut.StartMenu");
        var desktop = Paths.GetSpecialPath(Environment.SpecialFolder.Desktop, shortcutName);
        var start = Paths.GetSpecialPath(Environment.SpecialFolder.StartMenu, @"Programs\" + shortcutName);
        var choice =
            ModMain.MyMsgBox(
                Lang.Text("Tools.Test.Shortcut.ConfirmMessage", desktopName, desktop, startName, start),
                Lang.Text("Tools.Test.Shortcut.SelectLocation"), Lang.Text("Common.Action.Cancel"), desktopName, startName);
        if (choice == 1)
            return;
        var shortcutPath = choice == 2 ? desktop : start;
        var locationName = choice == 2 ? desktopName : startName;
        Files.CreateShortcut(shortcutPath, Basics.ExecutablePath);
        HintService.Hint(Lang.Text("Tools.Test.Shortcut.Created", locationName), HintType.Success);
    }

    // 启动计数显示
    private void BtnLaunchCount_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.MyMsgBox(Lang.Text("Tools.Test.LaunchCount.Message", States.System.LaunchCount), Lang.Text("Tools.Test.LaunchCount.Title"));
    }

    /// <summary>
    ///     跳到左栏的「导入设置」。走左栏的切换逻辑，勾选状态和动画才跟得上，
    ///     否则会出现「页面换了但左栏还亮着实用工具」。
    /// </summary>
    private void BtnImport_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.frmToolsLeft?.SwitchTo(FormMain.PageSubType.ToolsImport);
    }

    private void BtnCrash_Click(object sender, MouseButtonEventArgs e)
    {
        throw new Exception(Lang.Text("Tools.Test.Crash.ManualCrash"));
    }

    private void TextDownloadFolder_OnValidatedTextChanged(object sender, RoutedEventArgs e)
    {
        SaveCacheDownloadFolder(sender, e);
        TextDownloadName_ValidateChanged(sender, e);
    }
}
