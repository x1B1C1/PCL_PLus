using System;
using System.Collections.Generic;

namespace PCL.Core.Minecraft.Instance;

/// <summary>测试副本的一次进度快照。</summary>
/// <param name="Progress">总进度，范围 0 到 1。</param>
/// <param name="Stage">当前阶段的中文名称。</param>
/// <param name="FilesCopied">已经复制的文件数。</param>
/// <param name="BytesCopied">已经复制的字节数。</param>
public sealed record InstanceCloneProgress(double Progress, string Stage, int FilesCopied, long BytesCopied);

/// <summary>测试副本完成后的结果。</summary>
public sealed class InstanceCloneResult
{
    /// <summary>新版本目录的绝对路径（不带结尾分隔符）。</summary>
    public string DestinationFolder { get; set; } = "";

    /// <summary>实际复制的文件数。</summary>
    public int Files { get; set; }

    /// <summary>实际复制的字节数。</summary>
    public long Bytes { get; set; }

    /// <summary>复制过程中产生的非致命提示，例如被跳过的链接目录。</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// 测试副本的来源记录（<c>PCL/CloneOrigin.json</c>，schema 1）。
/// 只记录路径与名称，不包含账号、日志或存档内容。
/// </summary>
/// <param name="SourceVersionFolder">来源版本目录的绝对路径。</param>
/// <param name="SourceGameRoot">来源实例当时使用的游戏根目录绝对路径。</param>
/// <param name="SourceName">来源版本目录名。</param>
/// <param name="CloneName">副本版本目录名。</param>
/// <param name="CreatedUtc">记录创建时间（UTC）。</param>
public sealed record InstanceCloneOrigin(
    string SourceVersionFolder,
    string SourceGameRoot,
    string SourceName,
    string CloneName,
    DateTime CreatedUtc);
