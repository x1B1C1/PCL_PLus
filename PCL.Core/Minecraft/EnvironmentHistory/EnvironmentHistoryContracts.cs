using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.EnvironmentHistory;

/// <summary>
/// 一份环境还原点的只读摘要，供界面展示。内容寻址快照的文件本体保存在存储目录的 <c>objects</c> 中，
/// 这里只暴露界面需要的统计信息。
/// </summary>
/// <param name="Id">还原点编号（32 位小写十六进制）。</param>
/// <param name="Label">创建还原点时使用的名称。</param>
/// <param name="CreatedUtc">创建时间（UTC）。</param>
/// <param name="Manual">是否为手动还原点；自动还原点参与保留策略裁剪。</param>
/// <param name="Healthy">是否被标记为「正常运行过」。</param>
/// <param name="FileCount">清单中的文件数量。</param>
/// <param name="Bytes">清单中文件的总字节数。</param>
/// <param name="Note">预留备注字段；当前引擎不写入（始终为 null）。</param>
public sealed record EnvironmentHistoryPoint(
    string Id,
    string Label,
    DateTime CreatedUtc,
    bool Manual,
    bool Healthy,
    int FileCount,
    long Bytes,
    string? Note = null);

/// <summary>
/// 还原点清单中单个文件的校验信息。文件本体按 <see cref="Hash"/> 存放在 <c>objects</c> 中。
/// </summary>
internal sealed class EnvironmentHistoryFile
{
    /// <summary>内容校验值，格式固定为 <c>sha1:&lt;40 位小写十六进制&gt;</c>。</summary>
    public string Hash { get; set; } = "";

    /// <summary>文件字节数。</summary>
    public long Length { get; set; }

    /// <summary>文件最后写入时间（UTC），用于两遍扫描的一致性与恢复后的时间戳还原。</summary>
    public DateTime WriteUtc { get; set; }
}

/// <summary>
/// 还原点清单（<c>points/&lt;id&gt;.json</c>，schema 1，coverageVersion 2）。
/// 清单只记录「相对路径 → 内容校验值」，因此同一份内容天然去重，还原点本身很小。
/// </summary>
internal sealed class EnvironmentHistoryManifest
{
    /// <summary>清单格式版本，当前固定为 1。</summary>
    public int Schema { get; set; } = EnvironmentHistoryRules.SchemaVersion;

    /// <summary>还原点编号，32 位小写十六进制。</summary>
    public string Id { get; set; } = "";

    /// <summary>覆盖范围版本；不同版本收录的文件范围不同。</summary>
    public int CoverageVersion { get; set; } = EnvironmentHistoryRules.CurrentCoverageVersion;

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>创建时使用的名称。</summary>
    public string Label { get; set; } = "";

    /// <summary>是否为手动还原点；手动还原点不会被保留策略裁剪。</summary>
    public bool Manual { get; set; }

    /// <summary>是否被标记为「正常运行过」。只有 <c>MarkHealthy</c> 会改写此字段。</summary>
    public bool Healthy { get; set; }

    /// <summary>创建还原点时的实例名称（通常是版本目录名），仅用于展示。</summary>
    public string InstanceName { get; set; } = "";

    /// <summary>创建还原点时的游戏根目录绝对路径，用于识别版本隔离设置是否变化。</summary>
    public string GameRoot { get; set; } = "";

    /// <summary>相对游戏根目录的路径（<c>/</c> 分隔）到文件校验信息的映射。</summary>
    public Dictionary<string, EnvironmentHistoryFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>创建时存在的目录相对路径（<c>/</c> 分隔），恢复时用于重建空目录。</summary>
    public List<string> Directories { get; set; } = [];
}

/// <summary>恢复事务中单个文件的处理动作。</summary>
internal enum EnvironmentHistoryAction
{
    /// <summary>把清单中的内容写到目标路径（必要时覆盖）。</summary>
    Write,

    /// <summary>删除目标路径上「清单里没有」的文件。</summary>
    Delete
}

/// <summary>
/// 恢复事务中的一个文件操作。<see cref="Relative"/>、<see cref="Action"/>、<see cref="OldHash"/>
/// 会写入事务日志；<see cref="NewHash"/> 与 <see cref="NewWriteUtc"/> 只在内存中传递。
/// </summary>
internal sealed class EnvironmentHistoryOperation
{
    /// <summary>相对游戏根目录的路径（<c>/</c> 分隔）。</summary>
    public string Relative { get; set; } = "";

    /// <summary>该操作的最终效果。</summary>
    public EnvironmentHistoryAction Action { get; set; }

    /// <summary>操作前该路径的内容校验值；原本不存在时为 null。回滚完全依赖此字段。</summary>
    public string? OldHash { get; set; }

    /// <summary>写入的目标内容校验值；<see cref="EnvironmentHistoryAction.Delete"/> 时为 null。</summary>
    [JsonIgnore]
    public string? NewHash { get; set; }

    /// <summary>写入后应设置的文件时间（UTC）。</summary>
    [JsonIgnore]
    public DateTime NewWriteUtc { get; set; }
}

/// <summary>
/// 恢复事务日志（<c>PCL/EnvironmentBackups/&lt;guid&gt;/transaction.json</c>，schema 1）。
/// 日志先于任何文件改动写入，成功后把 <see cref="Complete"/> 置为 true 并删除事务目录；
/// 失败或中断时按 <see cref="Operations"/> 的逆序把 <see cref="EnvironmentHistoryOperation.OldHash"/> 写回。
/// </summary>
internal sealed class EnvironmentHistoryTransaction
{
    /// <summary>日志格式版本，当前固定为 1。</summary>
    public int Schema { get; set; } = 1;

    /// <summary>事务是否已全部应用成功。</summary>
    public bool Complete { get; set; }

    /// <summary>事务创建时间（UTC）。</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>事务作用的目标游戏根目录，恢复中断后用于确认现场是否匹配。</summary>
    public string GameRoot { get; set; } = "";

    /// <summary>按应用顺序记录的操作；回滚时逆序处理。</summary>
    public List<EnvironmentHistoryOperation> Operations { get; set; } = [];
}

/// <summary>
/// 清单与事务日志共用的 JSON 序列化设置。全部使用 <c>System.Text.Json</c>（本仓库没有 Newtonsoft）。
/// </summary>
internal static class EnvironmentHistoryJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
