using System;
using System.Collections.Generic;

namespace PCL.Core.Minecraft.EnvironmentSnapshot;

/// <summary>
/// 实例环境在某个时刻的文件清单快照（schema 1）。
/// 逻辑键以 <c>v/</c>（版本目录）或 <c>g/</c>（游戏根）开头，后接 <c>/</c> 分隔的相对路径。
/// </summary>
public sealed class EnvironmentSnapshot
{
    /// <summary>当前清单格式版本。</summary>
    public const int SchemaVersion = 1;

    /// <summary>清单格式版本。</summary>
    public int Schema { get; set; } = SchemaVersion;

    /// <summary>捕获时版本目录的绝对路径。</summary>
    public string InstanceFolder { get; set; } = "";

    /// <summary>捕获时游戏根目录的绝对路径。</summary>
    public string GameRoot { get; set; } = "";

    /// <summary>捕获时间（UTC）。</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>是否可作为健康基线；只有健康快照会被 <c>LoadLatestHealthy</c> 返回。</summary>
    public bool Healthy { get; set; } = true;

    /// <summary>逻辑路径到文件校验信息的映射（大小写不敏感）。</summary>
    public Dictionary<string, EnvironmentSnapshotFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>环境快照中单个文件的校验信息。</summary>
public sealed class EnvironmentSnapshotFile
{
    /// <summary>内容校验值，格式固定为 <c>sha1:&lt;40 位小写十六进制&gt;</c>。</summary>
    public string Sha1 { get; set; } = "";

    /// <summary>文件字节数。</summary>
    public long Length { get; set; }

    /// <summary>文件最后写入时间（UTC）。</summary>
    public DateTime WriteUtc { get; set; }
}

/// <summary>两份环境快照之间的一条差异。</summary>
/// <param name="Category">分类：模组、启动器版本配置、Minecraft/加载器、配置/环境资源。</param>
/// <param name="Kind">变更类型：新增、删除、修改。</param>
/// <param name="RelativePath">逻辑路径，以 <c>v/</c> 或 <c>g/</c> 开头。</param>
/// <param name="Detail">可供界面展示的补充信息，例如模组版本；没有时为 null。</param>
public sealed record EnvironmentChange(string Category, string Kind, string RelativePath, string? Detail);
