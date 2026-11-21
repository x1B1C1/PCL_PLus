// 【调试专属模块】BF 工程控制台的显示层。
// 命令执行在 BfCommandEngine（与右侧调试面板共用），这里只负责输入、输出与历史。
// 本控件在非 BF 构建里同样存在但不会被执行；真正编入的调试命令由 BF_DEBUG 控制。
using System;
using System.Threading.Tasks;
using System.Windows.Input;
#if BF_DEBUG
using PCL.Modules.Debug;
#endif

namespace PCL.Controls;

/// <summary>
/// 【调试专属模块】BF 构建专用的工程控制台。
/// <para>
/// 打开时保持静默，不自动打印欢迎与语法；输入 help 才输出帮助。
/// 与右侧调试面板共享同一个 BfCommandEngine：任意一侧执行，另一侧同步看到命令与输出。
/// </para>
/// </summary>
public partial class BfConsole
{
    private int _historyIndex;

    public BfConsole()
    {
        InitializeComponent();
#if BF_DEBUG
        TextIntro.Text = "仅 BF 构建可用，不随正式线 / 测试线发布。命令不区分大小写，支持 ↑ / ↓ 翻历史、Tab 补全；输入 help 查看完整语法。";
        TextCommand.HintText = "输入命令后回车执行（help 查看语法）";
        Loaded += OnConsoleLoaded;
        Unloaded += OnConsoleUnloaded;
#endif
    }

#if BF_DEBUG
    private void OnConsoleLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        BfCommandEngine.LineEmitted += OnEngineLine;
        BfCommandEngine.Cleared += OnEngineCleared;
        BfCommandEngine.StatusChanged += SetStatus;
        BfDebugCore.LogEmitted += OnBfDebugLog;
        _historyIndex = BfCommandEngine.History.Count;
    }

    private void OnConsoleUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        BfCommandEngine.LineEmitted -= OnEngineLine;
        BfCommandEngine.Cleared -= OnEngineCleared;
        BfCommandEngine.StatusChanged -= SetStatus;
        BfDebugCore.LogEmitted -= OnBfDebugLog;
    }

    private void OnEngineLine(string line) => WriteLine(line);

    private void OnEngineCleared() => TextOutput.Text = "";

    private void OnBfDebugLog(BfDebugLogEntry entry)
    {
        // 控制台自己产生的日志（EchoToConsole=false）只送到侧边面板，避免重复回显
        if (!entry.EchoToConsole) return;
        WriteLine(entry.Display);
    }
#endif

    /// <summary>供调试模块追加一条日志（自动补时间前缀）。</summary>
    public void AppendLog(string message)
    {
        WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void BtnRun_Click(object sender, MouseButtonEventArgs e)
    {
#if BF_DEBUG
        _ = RunCommandAsync();
#endif
    }

    private void BtnClear_Click(object sender, MouseButtonEventArgs e) => TextOutput.Text = "";

    private void TextCommand_KeyDown(object sender, KeyEventArgs e)
    {
#if BF_DEBUG
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = RunCommandAsync();
                break;
            case Key.Up:
                e.Handled = true;
                NavigateHistory(-1);
                break;
            case Key.Down:
                e.Handled = true;
                NavigateHistory(1);
                break;
            case Key.Tab:
                e.Handled = true;
                CompleteCommand();
                break;
        }
#endif
    }

#if BF_DEBUG
    private async Task RunCommandAsync()
    {
        var raw = TextCommand.Text.Trim();
        if (raw.Length == 0) return;
        TextCommand.Text = "";
        await BfCommandEngine.RunAsync(raw);
        _historyIndex = BfCommandEngine.History.Count;
    }

    private void NavigateHistory(int delta)
    {
        var history = BfCommandEngine.History;
        if (history.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + delta, 0, history.Count);
        TextCommand.Text = _historyIndex >= history.Count ? "" : history[_historyIndex];
        TextCommand.CaretIndex = TextCommand.Text.Length;
    }

    private void CompleteCommand()
    {
        var text = TextCommand.Text;
        if (text.Contains(' ')) return;
        var matches = BfCommandEngine.CommandNames
            .Where(name => name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
        {
            TextCommand.Text = matches[0] + " ";
            TextCommand.CaretIndex = TextCommand.Text.Length;
        }
        else if (matches.Length > 1)
        {
            WriteLine("补全候选：" + string.Join("  ", matches));
        }
    }
#endif

    private void SetStatus(string text) => TextStatus.Text = text;

    private void WriteLine(string text)
    {
        TextOutput.AppendText(text + Environment.NewLine);
        var over = TextOutput.Text.Length - 200_000;
        if (over > 0) TextOutput.Text = TextOutput.Text[over..];
        TextOutput.ScrollToEnd();
    }
}
