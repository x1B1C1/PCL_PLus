using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Minecraft.PackUpdate;

/// <summary>整合包更新中单个文件相对于本地实例的变化类型。</summary>
public enum PackUpdateKind
{
    /// <summary>本地不存在，需要从整合包写入。</summary>
    Added,

    /// <summary>本地存在但内容与整合包不同，需要覆盖。</summary>
    Updated,

    /// <summary>整合包已不再包含该文件，需要从实例中移除。</summary>
    Removed,

    /// <summary>保留本地文件，不做任何改动。</summary>
    Preserved
}

/// <summary>更新预览中的一条文件差异。</summary>
/// <param name="RelativePath">相对游戏根目录的正斜杠路径。</param>
/// <param name="Kind">变化类型。</param>
/// <param name="Reason">面向用户的中文原因说明。</param>
/// <param name="ExistingSha1">本地文件当前的 sha1 小写十六进制值；本地不存在或无需计算时为 null。</param>
/// <param name="IncomingSha1">整合包中文件的 sha1 小写十六进制值；清单未提供哈希时为 null。</param>
/// <param name="PersonalModified">本地文件可能被用户改过；为 true 时应用阶段不会自动删除它。</param>
public sealed record PackUpdateEntry(
    string RelativePath,
    PackUpdateKind Kind,
    string Reason,
    string? ExistingSha1,
    string? IncomingSha1,
    bool PersonalModified);

/// <summary>
/// 一次更新预览的完整结果。
/// <see cref="Entries"/> 已按 <see cref="PackUpdateKind"/>、再按路径排序；
/// 内容一致的「保留」项也会以 <see cref="PackUpdateKind.Preserved"/> 出现，
/// 界面可以用 <see cref="Added"/> 等计数或按 Kind 过滤把它们放进各自的表。
/// </summary>
/// <param name="ManifestName">整合包名称。</param>
/// <param name="VersionId">整合包版本号；清单未提供时为 null。</param>
/// <param name="Entries">全部差异项。</param>
public sealed record PackUpdatePlan(
    string ManifestName,
    string? VersionId,
    IReadOnlyList<PackUpdateEntry> Entries)
{
    /// <summary>需要新增的文件数。</summary>
    public int Added => CountOf(PackUpdateKind.Added);

    /// <summary>需要覆盖的文件数。</summary>
    public int Updated => CountOf(PackUpdateKind.Updated);

    /// <summary>需要移除的文件数。</summary>
    public int Removed => CountOf(PackUpdateKind.Removed);

    /// <summary>需要保留（含内容一致）的文件数。</summary>
    public int Preserved => CountOf(PackUpdateKind.Preserved);

    /// <summary>预览中没有任何条目。</summary>
    public bool IsEmpty => Entries.Count == 0;

    private int CountOf(PackUpdateKind kind) => Entries.Count(entry => entry.Kind == kind);
}

/// <summary>一次更新落地的统计结果。</summary>
/// <param name="Written">成功写入（新增或覆盖）的文件数。</param>
/// <param name="Deleted">成功移除的文件数。</param>
/// <param name="Preserved">按计划原样保留的文件数。</param>
/// <param name="Skipped">因内容缺失、个人修改或路径保护而未处理的文件数。</param>
/// <param name="RolledBack">是否因为异常执行了回滚。</param>
public sealed record PackUpdateApplyResult(
    int Written,
    int Deleted,
    int Preserved,
    int Skipped,
    bool RolledBack);
