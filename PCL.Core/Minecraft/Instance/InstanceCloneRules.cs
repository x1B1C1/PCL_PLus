using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Instance;

/// <summary>
/// 测试副本的固定规则：复制范围、排除项、名称校验与摘要格式。
/// 这里是「哪些文件属于实例环境」的唯一定义处，克隆与环境快照共用同一套判断。
/// </summary>
public static class InstanceCloneRules
{
    /// <summary>来源记录相对版本目录的固定路径（<c>/</c> 分隔）。</summary>
    public const string OriginRelativePath = "PCL/CloneOrigin.json";

    /// <summary>单个测试副本允许收录的默认最大文件数。</summary>
    public const int DefaultMaxFiles = 200_000;

    /// <summary>单个测试副本允许收录的默认最大字节数（512 GiB）。</summary>
    public const long DefaultMaxBytes = 512L * 1024 * 1024 * 1024;

    /// <summary>版本目录顶层需要收录的固定文件（相对版本目录，<c>/</c> 分隔）。</summary>
    public static readonly IReadOnlyList<string> VersionFixedFiles =
    [
        "PCL/Setup.ini",
        "PCL/config.json",
        "PCL/Logo.png"
    ];

    /// <summary>游戏根下需要递归收录的环境目录。</summary>
    public static readonly IReadOnlyList<string> EnvironmentFolders =
    [
        "mods",
        "coremods",
        "config",
        "defaultconfigs",
        "configureddefaults",
        "kubejs",
        "scripts",
        "resources",
        "resourcepacks",
        "shaderpacks",
        "texturepacks",
        "schematics",
        "global_packs",
        "openloader"
    ];

    /// <summary><c>PCL/Setup.ini</c> 中允许带进副本的键（序数比较，区分大小写）。</summary>
    public static readonly IReadOnlyList<string> SetupKeyWhitelist =
    [
        "VersionRamType",
        "VersionRamCustom",
        "VersionRamOptimize",
        "VersionArgumentTitle",
        "VersionArgumentInfo",
        "VersionArgumentJavaSelect",
        "VersionArgumentJavaV2",
        "VersionArgumentJavaRange",
        "VersionAdvanceAssets",
        "VersionAdvanceAssetsV2",
        "VersionAdvanceDisableJLW",
        "VersionAdvanceDisableLUA",
        "VersionAdvanceDisableModUpdate",
        "VersionAdvanceGC",
        "CustomInfo",
        "Logo",
        "LogoCustom"
    ];

    /// <summary>任意层级出现即整棵跳过的目录名。</summary>
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cache",
        "cache",
        "caches",
        ".connector",
        "logs",
        "crash-reports",
        "natives",
        "backups",
        "backup",
        "account",
        "accounts",
        ".git"
    };

    private static readonly Regex CredentialSegmentPattern = new(
        @"\A(?:accounts?|credentials?|access[_-]?tokens?|auth[_-]?tokens?|launcher_accounts|usercache|usernamecache|launcher_profiles)(?:\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Minecraft 与 Windows 都不支持的版本名：<c>COM0</c>/<c>LPT0</c>/<c>CLOCK$</c> 设备名，
    /// 以及形如 <c>abc~1</c> 的短文件名。
    /// </summary>
    private static readonly Regex ReservedNamePattern = new(
        @"\A(?:COM0|LPT0|CLOCK\$)(?:\.|$)|.{2,}~\d|\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Sha1Pattern = new(@"\Asha1:[a-f0-9]{40}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// 判断相对路径是否命中排除项。<b>任意层级</b>命中即跳过：
    /// <c>.pcl-</c> 前缀、缓存/日志/存档备份等目录、账号或令牌文件、
    /// <c>.tmp/.log/.lock</c> 后缀，以及 <c>PCL/Environment</c>。
    /// </summary>
    public static bool IsExcluded(string relativePath)
    {
        string text = (relativePath ?? "").Replace('\\', '/').Trim();
        string[] parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase)) return true;
            if (ExcludedSegments.Contains(part)) return true;
            if (CredentialSegmentPattern.IsMatch(part)) return true;
        }

        if (parts.Length >= 2
            && parts[0].Equals("PCL", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("Environment", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || text.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
               || text.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断版本目录顶层的某个文件是否需要收录：只收 <c>*.json</c> 与 <c>*.jar</c>。</summary>
    public static bool IsVersionTopLevelFile(string fileName)
    {
        string extension = Path.GetExtension(fileName ?? "");
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断游戏根<b>顶层</b>的散文件是否需要收录：<c>options.txt</c>、<c>optionsof*</c>、
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

    /// <summary>判断某个键是否允许从 <c>PCL/Setup.ini</c> 带入副本。</summary>
    public static bool IsSetupKey(string key)
    {
        foreach (string item in SetupKeyWhitelist)
        {
            if (string.Equals(item, key, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// 校验测试版本名称：非空、长度不超过 100、不含 <c>!</c> 或 <c>;</c>、
    /// 不是 Windows 保留设备名，且不以空格或点结尾。
    /// </summary>
    public static bool IsValidName(string? name)
    {
        string text = name ?? "";
        if (text.Length == 0 || text.Trim().Length == 0) return false;
        if (text.Length > 100) return false;
        if (text.IndexOf('!') >= 0 || text.IndexOf(';') >= 0) return false;
        if (ReservedNamePattern.IsMatch(text)) return false;
        if (text is "." or "..") return false;
        if (text.EndsWith(' ') || text.EndsWith('.')) return false;
        if (text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    /// <summary>判断字符串是否是合法的摘要（<c>sha1:</c> + 40 位小写十六进制）。</summary>
    public static bool IsSha1(string? hash) => !string.IsNullOrEmpty(hash) && Sha1Pattern.IsMatch(hash);

    /// <summary>计算文件的 SHA-1，返回 <c>sha1:&lt;40 位小写十六进制&gt;</c>。</summary>
    public static string Sha1File(string path)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, FileOptions.SequentialScan);
        using SHA1 sha1 = SHA1.Create();
        return "sha1:" + Convert.ToHexString(sha1.ComputeHash(input)).ToLowerInvariant();
    }
}
