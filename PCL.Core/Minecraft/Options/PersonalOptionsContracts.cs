using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Options;

/// <summary>
/// 个人游戏习惯（个人设置）同步的类别，可多选。
/// </summary>
public enum PersonalOptionsCategory
{
    /// <summary>按键绑定，即 <c>options.txt</c> 中的 <c>key_key.*</c>。</summary>
    Keys,

    /// <summary>鼠标灵敏度、滚轮、反转等鼠标设置。</summary>
    Mouse,

    /// <summary>视场角、视角摇晃等视角设置。</summary>
    View,

    /// <summary>字幕、自动跳跃、高对比度等辅助功能。</summary>
    Accessibility,

    /// <summary>声音类别音量，即 <c>soundCategory_*</c>。</summary>
    Audio
}

/// <summary>
/// 一次个人设置操作的执行结果状态。
/// </summary>
public enum PersonalOptionsStatus
{
    /// <summary>未执行任何写入（文件不存在、未勾选类别、没有档案等）。</summary>
    Skipped,

    /// <summary>已读取合并，但结果与原文件一致，因此没有写回。</summary>
    Unchanged,

    /// <summary>已把合并结果写回 <c>options.txt</c>。</summary>
    Applied
}

/// <summary>
/// 参与个人设置同步的实例信息与版本判定。
/// <see cref="PersonalOptionsFlow"/> 依赖这些信息决定键名、绑定值与视场角格式。
/// </summary>
public sealed class PersonalOptionsTarget
{
    /// <summary>实例（版本）目录的完整路径，写入会话文件后用于恢复。</summary>
    public string InstancePath { get; init; } = "";

    /// <summary>游戏根目录，即 <c>options.txt</c> 所在目录；留空时回退到 <see cref="InstancePath"/>。</summary>
    public string GameFolder { get; init; } = "";

    /// <summary>
    /// 版本信息是否有效。为 <see langword="false"/> 时一律不同步按键绑定，
    /// 因为无法确定目标版本使用的是字符串绑定还是旧版数字码。
    /// </summary>
    public bool VersionValid { get; init; } = true;

    /// <summary>原版主版本号（例如 1.20.1 为 20）。为 0 表示未知，按旧版处理。</summary>
    public int VanillaMajor { get; init; }

    /// <summary>
    /// 视场角是否使用角度制。为 <see langword="null"/> 时按
    /// 「版本有效且主版本号不小于 19」推断，与参考实现一致。
    /// </summary>
    public bool? UseFovDegrees { get; init; }

    /// <summary>实际使用的游戏目录。</summary>
    public string ResolvedGameFolder =>
        string.IsNullOrWhiteSpace(GameFolder) ? InstancePath : GameFolder;
}

/// <summary>
/// 一次读取 <c>options.txt</c> 得到的只读快照，携带写回时用于冲突检测的哈希。
/// </summary>
public sealed class PersonalOptionsSnapshot
{
    /// <summary>游戏根目录。</summary>
    public string GameFolder { get; init; } = "";

    /// <summary>按 UTF-8 解码并去掉 BOM 后的原始文本。</summary>
    public string Text { get; init; } = "";

    /// <summary>原始字节的 SHA-256（小写十六进制），写回前用于比较文件是否被外部修改。</summary>
    public string Hash { get; init; } = "";

    /// <summary>原始字节长度。</summary>
    public long Length { get; init; }

    /// <summary>原文件是否带有 UTF-8 BOM，写回时保持一致。</summary>
    public bool HasBom { get; init; }

    /// <summary>解析后的键值，键已按 <see cref="PersonalOptionsRules.CanonicalKey"/> 归一。</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>判断快照中是否存在指定键（会先归一键名）。</summary>
    /// <param name="key">要查询的键。</param>
    /// <returns>存在返回 <see langword="true"/>。</returns>
    public bool HasKey(string key) => Values.ContainsKey(PersonalOptionsRules.CanonicalKey(key));
}

/// <summary>
/// 一次游戏启动期间的个人设置会话，用于退出后只回收玩家真正改过的键。
/// </summary>
public sealed class PersonalOptionsSession
{
    /// <summary>会话标识，同时是 <c>options-sessions/{Id}.json</c> 的文件名。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>实例（版本）目录。</summary>
    public string VersionPath { get; set; } = "";

    /// <summary>游戏根目录。</summary>
    public string GameFolder { get; set; } = "";

    /// <summary>游戏进程 ID；尚未启动时为 0。</summary>
    public int Pid { get; set; }

    /// <summary>游戏进程启动时间（UTC）；尚未启动时为 <see cref="DateTimeOffset.MinValue"/>。</summary>
    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>写入 <c>options.txt</c> 之后、游戏启动之前的选中键取值。</summary>
    public Dictionary<string, string> Baseline { get; set; } = new(StringComparer.Ordinal);

    /// <summary>本次会话同步的键集合（已按类别与版本有效性过滤）。</summary>
    public HashSet<string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>是否已经写出会话文件。仅内存使用，不写入 JSON。</summary>
    [JsonIgnore]
    public bool Tracked { get; set; }

    /// <summary>创建一个仅替换 <see cref="Id"/> 的副本，便于恢复场景重建会话对象。</summary>
    /// <param name="id">新的会话标识。</param>
    /// <returns>副本。</returns>
    public PersonalOptionsSession WithId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new PersonalOptionsSession
        {
            Id = id,
            VersionPath = VersionPath,
            GameFolder = GameFolder,
            Pid = Pid,
            StartedUtc = StartedUtc,
            Baseline = new Dictionary<string, string>(Baseline, StringComparer.Ordinal),
            Keys = new HashSet<string>(Keys, StringComparer.Ordinal),
            Tracked = Tracked
        };
    }
}

/// <summary>
/// 个人设置操作的返回值：状态、提示文本、快照与待跟踪会话。
/// </summary>
public sealed class PersonalOptionsResult
{
    /// <summary>执行状态。</summary>
    public PersonalOptionsStatus Status { get; init; }

    /// <summary>面向用户的中文提示。</summary>
    public string Message { get; init; } = "";

    /// <summary>是否真的写回了 <c>options.txt</c>。</summary>
    public bool Written { get; init; }

    /// <summary>读取到的快照；未读取文件时为 <see langword="null"/>。</summary>
    public PersonalOptionsSnapshot? Snapshot { get; init; }

    /// <summary>准备完成、等待 <see cref="PersonalOptionsFlow.Track"/> 的会话；跳过时为 <see langword="null"/>。</summary>
    public PersonalOptionsSession? Session { get; init; }

    /// <summary>本次实际合并进 <c>options.txt</c> 的键。</summary>
    public IReadOnlyCollection<string> AppliedKeys { get; init; } = Array.Empty<string>();

    /// <summary>是否属于「没有做任何写入」的结果。</summary>
    public bool Skipped => Status == PersonalOptionsStatus.Skipped;

    /// <summary>构造一个跳过结果。</summary>
    /// <param name="message">面向用户的中文说明。</param>
    /// <param name="snapshot">已读取的快照，可为 <see langword="null"/>。</param>
    /// <returns>状态为 <see cref="PersonalOptionsStatus.Skipped"/> 的结果。</returns>
    public static PersonalOptionsResult Skip(string message, PersonalOptionsSnapshot? snapshot = null) =>
        new()
        {
            Status = PersonalOptionsStatus.Skipped,
            Message = message,
            Snapshot = snapshot
        };
}
