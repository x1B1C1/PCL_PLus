using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Saves.Backups;

/// <summary>
/// 备份中单个文件的校验信息。
/// </summary>
public sealed class WorldBackupFile
{
    /// <summary>文件字节数。</summary>
    public long Length { get; set; }

    /// <summary>文件内容的 Base64 编码 SHA-256（32 字节）。</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>文件最后写入时间（UTC）。</summary>
    public DateTime WriteUtc { get; set; }
}

/// <summary>
/// 存档备份清单（<c>manifest.json</c>，schema 1）。
/// 记录一次备份的元数据、文件哈希与空目录列表。
/// </summary>
public sealed class WorldBackupManifest
{
    /// <summary>清单格式版本，当前固定为 1。</summary>
    public int Schema { get; set; } = 1;

    /// <summary>备份编号，32 位小写十六进制。</summary>
    public string Id { get; set; } = "";

    /// <summary>游戏根目录（版本隔离后的 .minecraft 根）的绝对路径。</summary>
    public string GameFolder { get; set; } = "";

    /// <summary>存档文件夹名（不含路径）。</summary>
    public string World { get; set; } = "";

    /// <summary>存档显示名称。</summary>
    public string Label { get; set; } = "";

    /// <summary>创建时间（UTC）。</summary>
    public DateTime Created { get; set; }

    /// <summary>备份原因，用于界面展示。</summary>
    public string Reason { get; set; } = "";

    /// <summary>是否为手动备份；手动备份不参与保留份数裁剪。</summary>
    public bool Manual { get; set; }

    /// <summary>原始文件总字节数。</summary>
    public long Bytes { get; set; }

    /// <summary>压缩包字节数。</summary>
    public long ArchiveBytes { get; set; }

    /// <summary>相对路径（<c>/</c> 分隔）到文件校验信息的映射。</summary>
    public Dictionary<string, WorldBackupFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>空目录的相对路径列表（<c>/</c> 分隔，不含根）。</summary>
    public List<string> Directories { get; set; } = [];
}

/// <summary>
/// 恢复事务日志（<c>transactions/&lt;tx&gt;.json</c>）。
/// 用于恢复中断后回滚到恢复前状态。
/// </summary>
public sealed class WorldBackupJournal
{
    /// <summary>游戏根目录的绝对路径。</summary>
    public string GameFolder { get; set; } = "";

    /// <summary>存档文件夹名。</summary>
    public string World { get; set; } = "";

    /// <summary>正在恢复的备份编号。</summary>
    public string Point { get; set; } = "";

    /// <summary>恢复前自动备份的编号；恢复前世界不存在时为 null。</summary>
    public string? Undo { get; set; }

    /// <summary>恢复前世界是否已存在。</summary>
    public bool Existed { get; set; }

    /// <summary>事务是否已提交（新世界已就位）。</summary>
    public bool Complete { get; set; }

    /// <summary>发起恢复的进程 ID。</summary>
    public int ProcessId { get; set; }

    /// <summary>发起恢复的进程启动时间（UTC）。</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>事务创建时间（UTC）。</summary>
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// 一份已校验的存档备份的只读视图，供界面展示。
/// </summary>
public sealed record WorldBackupPoint
{
    /// <summary>备份编号（32 位小写十六进制）。</summary>
    public required string Id { get; init; }

    /// <summary>存档文件夹名。</summary>
    public required string World { get; init; }

    /// <summary>存档显示名称。</summary>
    public required string Label { get; init; }

    /// <summary>备份原因。</summary>
    public required string Reason { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>是否为手动备份。</summary>
    public bool Manual { get; init; }

    /// <summary>原始文件总字节数。</summary>
    public long Bytes { get; init; }

    /// <summary>压缩包字节数。</summary>
    public long ArchiveBytes { get; init; }

    /// <summary>文件数量。</summary>
    public int FileCount { get; init; }

    /// <summary>文件相对路径到校验信息的映射。</summary>
    public IReadOnlyDictionary<string, WorldBackupFile> Files { get; init; } =
        new Dictionary<string, WorldBackupFile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>空目录相对路径列表。</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];

    /// <summary>清单是否通过结构校验且压缩包存在。</summary>
    public bool Healthy { get; init; }

    /// <summary>备份目录的绝对路径。</summary>
    public required string Folder { get; init; }

    /// <summary>供界面展示的一行摘要。</summary>
    public required string Description { get; init; }
}

/// <summary>
/// 清单与事务日志共用的 JSON 序列化设置。
/// </summary>
internal static class WorldBackupJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
