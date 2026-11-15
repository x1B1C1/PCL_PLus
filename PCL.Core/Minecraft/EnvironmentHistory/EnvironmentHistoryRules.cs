using System;
using System.Collections.Generic;
using System.IO;

namespace PCL.Core.Minecraft.EnvironmentHistory;

/// <summary>
/// 环境还原点的固定规则：覆盖范围（coverageVersion 2）、排除项、数量上限与自动标签。
/// 这里是「哪些文件算环境」的唯一定义处，捕获与恢复共用同一套判断，避免两边跑偏。
/// </summary>
public static class EnvironmentHistoryRules
{
    /// <summary>清单格式版本。</summary>
    public const int SchemaVersion = 1;

    /// <summary>当前覆盖范围版本。版本目录顶层 + 游戏根环境目录 + 根目录散文件，见本类型的各个方法。</summary>
    public const int CurrentCoverageVersion = 2;

    /// <summary>单个还原点允许收录的最大文件数。</summary>
    public const int MaxFiles = 200_000;

    /// <summary>单个还原点允许收录的最大字节数（8 GiB）。</summary>
    public const long MaxBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>清单 JSON 允许的最大字节数（32 MB）。</summary>
    public const long MaxManifestBytes = 32L * 1024 * 1024;

    /// <summary>恢复前自动保存所使用的固定名称。</summary>
    public const string UndoLabel = "恢复前自动保存";

    /// <summary>版本目录下需要额外收录的固定文件（相对版本目录，<c>/</c> 分隔）。</summary>
    /// <remarks>
    /// 刻意<b>不</b>收录 <c>PCL/config.v1.yml</c>：它记录启动次数等信息，回滚会让启动计数倒退。
    /// </remarks>
    public static readonly IReadOnlyList<string> VersionFixedFiles =
    [
        "PCL/Setup.ini",
        "PCL/ModpackOrigin.json"
    ];

    /// <summary>游戏根下需要递归全量收录的环境目录。</summary>
    public static readonly IReadOnlyList<string> GameEnvironmentFolders =
    [
        "mods",
        "coremods",
        "config",
        "defaultconfigs",
        "configureddefaults",
        "kubejs",
        "scripts",
        "resources",
        "global_packs",
        "openloader"
    ];

    /// <summary>不属于手动还原点的自动标签。标签比较使用序数比较（区分大小写、不做文化处理）。</summary>
    public static readonly IReadOnlyList<string> AutomaticLabels =
    [
        "游戏启动前",
        "修改游戏组件前",
        "更新模组前",
        "整合包更新前",
        "恢复前自动保存",
        "测试副本迁移前"
    ];

    /// <summary>任意层级出现即整棵跳过的目录名（存档、日志、截图、资源包等由游戏自行管理的内容）。</summary>
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves",
        "logs",
        "screenshots",
        "resourcepacks",
        "shaderpacks",
        "crash-reports"
    };

    /// <summary>游戏根下不参与收录的固定目录（不含 <c>PCL</c> 本身，它的收录由 <see cref="VersionFixedFiles"/> 负责）。</summary>
    private static readonly HashSet<string> ExcludedConfigFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "journeymap",
        "voxelmap"
    };

    /// <summary>判断名称是否属于自动标签（白名单内 6 个之一）。</summary>
    public static bool IsAutomaticLabel(string? label)
    {
        string text = (label ?? "").Trim();
        if (text.Length == 0) return false;
        foreach (string item in AutomaticLabels)
        {
            if (string.Equals(item, text, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// 判断版本目录顶层的某个文件是否需要收录：只收 <c>*.json</c> 与 <c>*.jar</c>
    /// （版本清单、Forge/NeoForge 安装器产物等）。子目录由 <see cref="VersionFixedFiles"/> 单独处理。
    /// </summary>
    public static bool IsVersionTopLevelFile(string fileName)
    {
        string extension = Path.GetExtension(fileName ?? "");
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断游戏根<b>顶层</b>的某个散文件是否需要收录：<c>options.txt</c>、<c>optionsof*</c>、
    /// <c>optionsshaders*</c>、<c>*.cfg</c>、<c>*.toml</c>、<c>*.properties</c>。
    /// </summary>
    public static bool IsGameRootFile(string fileName)
    {
        string name = fileName ?? "";
        if (name.Length == 0) return false;
        if (name.Equals("options.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("optionsof", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("optionsshaders", StringComparison.OrdinalIgnoreCase)) return true;
        string extension = Path.GetExtension(name);
        return extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".toml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".properties", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断相对路径是否落在覆盖范围内（不含版本目录顶层的特殊规则）。
    /// 支持两种写法：游戏根相对路径，或形如 <c>mods/a.jar</c> 的目录内路径。
    /// </summary>
    public static bool IsCoveredGamePath(string relativePath)
    {
        string[] segments = Split(relativePath);
        if (segments.Length == 0) return false;
        if (IsExcludedPath(relativePath)) return false;
        if (Contains(GameEnvironmentFolders, segments[0])) return true;
        return segments.Length == 1 && IsGameRootFile(segments[0]);
    }

    /// <summary>
    /// 判断相对路径是否命中排除项。<b>任意层级</b>命中即跳过：
    /// <c>saves / logs / screenshots / resourcepacks / shaderpacks / crash-reports</c>、
    /// <c>.pcl-</c> 前缀的目录、任一段名含 <c>waypoint</c>、
    /// 以及 <c>config/xaero*</c>、<c>config/journeymap</c>、<c>config/voxelmap</c>。
    /// </summary>
    public static bool IsExcludedPath(string? relativePath)
    {
        string[] segments = Split(relativePath);
        foreach (string segment in segments)
        {
            if (ExcludedSegments.Contains(segment)) return true;
            if (segment.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.Contains("waypoint", StringComparison.OrdinalIgnoreCase)) return true;
        }

        // config 下的小地图/路点数据由各自 mod 高频改写，收录进还原点只会制造噪声。
        if (segments.Length >= 2 && segments[0].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            string sub = segments[1];
            if (sub.StartsWith("xaero", StringComparison.OrdinalIgnoreCase)) return true;
            if (ExcludedConfigFolders.Contains(sub)) return true;
        }

        return false;
    }

    /// <summary>把相对路径拆成段，兼容 <c>/</c> 与 <c>\</c> 两种分隔符，并丢弃空段。</summary>
    internal static string[] Split(string? relativePath)
    {
        string text = (relativePath ?? "").Replace('\\', '/').Trim();
        if (text.Length == 0) return [];
        return text.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>大小写不敏感地判断列表是否包含某个名称。</summary>
    internal static bool Contains(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
