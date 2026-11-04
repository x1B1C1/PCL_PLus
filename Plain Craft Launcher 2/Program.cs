using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.Utils.OS;

namespace PCL;

internal static class Program
{
    /// <summary>
    /// Program startup point
    /// </summary>
    [STAThread]
    public static void Main()
    {
        // 启动期任何未处理异常都必须让用户看见，否则双击之后「什么都没发生」，
        // 连日志在哪都不知道。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception, "未处理异常");
        try
        {
            if (Basics.CommandLineArguments.Contains("--console")) KernelInterop.AllocateConsole();
#if DEBUG
            if (Basics.CommandLineArguments.Contains("--debug"))
            {
                Console.WriteLine("Waiting for debugger...");
                while (!Debugger.IsAttached) Thread.Sleep(50);
            }
#endif
            Console.WriteLine("Welcome to PCL_Plus!");
            // 网络授权弹窗：必须早于任何联网操作注册
            PCL.Modules.Network.Consent.NetworkConsentDialog.Register();
            // Preloading tasks
            ApplicationService.Loading = static () =>
            {
                var app = new Application();
                app.InitializeComponent();
                return app;
            };
            MainWindowService.Loading = static () =>
            {
                // 首次启动写入默认网络白名单（必须在任何联网操作之前，且此时配置系统已就绪）
                PCL.Core.IO.Net.Consent.NetworkConsentService.SeedDefaults();
                var form = new FormMain();
                return form;
            };
            // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+
            _ = Tablet.TabletDevices;
            // Start lifecycle
            Lifecycle.OnInitialize();
        }
        catch (Exception ex)
        {
            ReportFatal(ex, "启动失败");
        }
    }

    /// <summary>
    /// 把致命异常写到 exe 旁边的日志目录，并弹窗告知用户。<br/>
    /// 提权启动（UAC）后如果这里再崩，用户至少能看到原因而不是一片安静。
    /// </summary>
    private static void ReportFatal(Exception? ex, string title)
    {
        var text = ex?.ToString() ?? "未知错误";
        Console.WriteLine($"[{title}] {text}");

        string? logPath = null;
        try
        {
            var dir = Path.Combine(Basics.ExecutableDirectory, "PCL", "Log");
            Directory.CreateDirectory(dir);
            logPath = Path.Combine(dir, $"crash-{DateTime.Now:yyyy-M-d-HHmmss}.log");
            File.WriteAllText(logPath, $"{title}{Environment.NewLine}{Environment.NewLine}{text}");
        }
        catch
        {
            // 目录不可写时不要因为写日志再崩一次
        }

        try
        {
            var hint = logPath is null ? "" : $"\n\n诊断信息已保存到：\n{logPath}";
            MessageBox.Show(
                $"PCL_Plus 无法启动：{title}\n\n{ex?.Message}\n\n{FirstLines(text, 12)}{hint}",
                "PCL_Plus 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 忽略：连弹窗都失败时只能靠控制台与日志
        }
    }

    private static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n');
        return lines.Length <= count ? text : string.Join('\n', lines.Take(count)) + "\n…";
    }
}
