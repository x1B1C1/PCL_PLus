using System;

namespace PCL.Core.Minecraft.Instance;

/// <summary>
/// 测试副本的可选复制范围与安全开关。
/// </summary>
public sealed class InstanceCloneOptions
{
    /// <summary>是否同时复制游戏根下的 <c>saves</c> 存档目录。默认不复制。</summary>
    public bool CopySaves { get; set; }

    /// <summary>是否同时复制游戏根下的 <c>screenshots</c> 截图目录。默认不复制。</summary>
    public bool CopyScreenshots { get; set; }

    /// <summary>是否同时复制 <c>servers.dat</c> 与 <c>servers.dat_old</c>。默认不复制。</summary>
    public bool CopyServers { get; set; }

    /// <summary>
    /// 遇到目录链接时是否跳过。为 <see langword="false"/>（默认）时直接停止并报错，
    /// 避免把链接目标或链接结构带进副本。
    /// </summary>
    public bool SkipLinks { get; set; }

    /// <summary>本次复制允许收录的最大文件数。</summary>
    public int MaxFiles { get; set; } = 200_000;

    /// <summary>本次复制允许收录的最大字节数（默认 512 GiB）。</summary>
    public long MaxBytes { get; set; } = 512L * 1024 * 1024 * 1024;

    /// <summary>接收跳过链接等非致命提示的回调；为 null 时提示只写入结果。</summary>
    public Action<string>? Warn { get; set; }
}
