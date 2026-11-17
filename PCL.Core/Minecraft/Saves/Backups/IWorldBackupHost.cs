using System;
using System.Collections.Generic;

namespace PCL.Core.Minecraft.Saves.Backups;

/// <summary>
/// 一个仍可能在运行、且可能正在使用某个存档目录的游戏进程。
/// </summary>
/// <param name="Pid">进程 ID。</param>
/// <param name="StartedUtc">进程启动时间（UTC），用于识别 PID 复用；未知时传 <see cref="DateTime.MinValue"/>。</param>
/// <param name="GameFolder">该进程使用的游戏根目录；未知时传 null，表示任意游戏目录都视为占用。</param>
public readonly record struct WorldBackupWatcher(int Pid, DateTime StartedUtc, string? GameFolder);

/// <summary>
/// 存档备份引擎与宿主（启动器）之间的契约。
/// 引擎本身不读取配置、不触碰界面、不直接写日志，所有环境交互都通过本接口注入。
/// 实现方需保证成员可从后台线程调用；<see cref="NotifyChanged"/> 由实现方自行切回 UI 线程。
/// </summary>
public interface IWorldBackupHost
{
    /// <summary>备份根目录（绝对路径），通常为 <c>&lt;共享数据&gt;\world-backups</c>。</summary>
    string BackupRoot { get; }

    /// <summary>读取存档显示名称；无法读取时返回 null，引擎会回退到文件夹名。</summary>
    string? WorldLabel(string worldFolder);

    /// <summary>把字节数格式化为可供界面展示的字符串。</summary>
    string FormatSize(long bytes);

    /// <summary>记录一条警告。引擎在清理失败、跳过损坏记录等可恢复场景调用。</summary>
    void Warn(Exception exception, string message);

    /// <summary>向用户展示一条提示；<paramref name="error"/> 为 true 时表示失败提示。</summary>
    void Hint(string message, bool error);

    /// <summary>把回调切到 UI 线程执行。</summary>
    void Dispatch(Action action);

    /// <summary>通知界面备份列表已变化。实现方应自行切换到 UI 线程。</summary>
    void NotifyChanged();

    /// <summary>在文件管理器中打开指定目录。</summary>
    void OpenFolder(string path);

    /// <summary>返回当前可能仍在运行的游戏进程快照；没有时返回空列表。</summary>
    IReadOnlyList<WorldBackupWatcher> Watchers();
}
