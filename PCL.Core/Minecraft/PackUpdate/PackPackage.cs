using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.Minecraft.PackUpdate;

/// <summary>
/// 一个整合包压缩包的只读视图。
/// </summary>
/// <param name="Name">整合包名称。</param>
/// <param name="VersionId">整合包版本号；清单未提供时为 null。</param>
/// <param name="Files">
/// 相对游戏根目录的正斜杠路径到 sha1 小写十六进制值的映射。
/// 键不包含 <c>overrides/</c> 或 <c>client-overrides/</c> 前缀；
/// CurseForge 清单没有哈希时值为空字符串。
/// </param>
public sealed record PackPackage(string Name, string? VersionId, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>
    /// 读取该包时使用的压缩包绝对路径；手工构造的包为 null。
    /// 应用更新需要据此重新读取文件内容，因此除纯规划外的场景应当从
    /// <see cref="PackPackageReader.Read"/> 取得包对象。
    /// </summary>
    public string? SourceArchive { get; init; }
}

/// <summary>
/// 整合包路径与安全边界的内部工具：路径归一化、越界防护、排除规则与大小上限。
/// 供读取器、规划器与应用器共用，保证三处判断完全一致。
/// </summary>
internal static class PackUpdatePaths
{
    /// <summary>整合包内的文件数上限。</summary>
    internal const int MaxFiles = 20_000;

    /// <summary>单个条目解压后的字节数上限（512 MiB）。</summary>
    internal const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>清单 JSON 的字节数上限。</summary>
    internal const long MaxManifestBytes = 16L * 1024 * 1024;

    /// <summary>命中任一路径段就永不参与更新的目录名（大小写不敏感）。</summary>
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves",
        "screenshots",
        "logs",
        "crash-reports",
        "resourcepacks",
        "shaderpacks",
        "schematics",
        "PCL"
    };

    /// <summary>命中文件名就永不参与更新的个人文件（大小写不敏感）。</summary>
    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt",
        "servers.dat",
        "usercache.json",
        "launcher_profiles.json"
    };

    /// <summary>
    /// 归一化整合包内的相对路径：统一为正斜杠、去掉空段与 <c>.</c> 段。
    /// 绝对路径、盘符、任何 <c>..</c> 段与 Windows 非法字符都会直接抛错。
    /// </summary>
    /// <exception cref="InvalidDataException">路径为空、越界或包含非法字符。</exception>
    internal static string NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("整合包内的文件路径为空。");

        string value = path.Trim().Replace('\\', '/');
        // Windows 上 Path.IsPathRooted 同时覆盖 "/x"、"//server/share" 与 "C:x" 这类写法。
        if (value.StartsWith("//", StringComparison.Ordinal) || Path.IsPathRooted(value))
        {
            throw new InvalidDataException("整合包内的文件路径不是相对路径：" + path);
        }

        string[] rawParts = value.Split('/');
        List<string> parts = new(rawParts.Length);
        foreach (string raw in rawParts)
        {
            if (raw.Length == 0 || raw == ".") continue;
            if (raw == "..") throw new InvalidDataException("整合包内的文件路径包含 .. 段：" + path);
            if (raw.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("整合包内的文件路径包含非法字符：" + path);
            }

            parts.Add(raw);
        }

        if (parts.Count == 0) throw new InvalidDataException("整合包内的文件路径为空：" + path);

        return string.Join('/', parts);
    }

    /// <summary>判断相对路径是否命中了永不纳入更新的保护规则。</summary>
    internal static bool IsExcluded(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return true;
        string[] parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        foreach (string part in parts)
        {
            if (ExcludedSegments.Contains(part)) return true;
            if (part.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase)) return true;
        }

        string fileName = parts[^1];
        if (ExcludedFileNames.Contains(fileName)) return true;
        return fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把相对路径安全地解析到根目录下；任何越界都会抛错。</summary>
    /// <exception cref="InvalidDataException">路径越界。</exception>
    internal static string Resolve(string root, string relativePath)
    {
        string normalized = NormalizeRelative(relativePath);
        string fullRoot = NormalizeRoot(root, "整合包目标目录无效。");
        string combined = Path.GetFullPath(
            Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(fullRoot, combined))
        {
            throw new InvalidDataException("整合包内的文件路径越界：" + relativePath);
        }

        return combined;
    }

    /// <summary>判断 <paramref name="path"/> 是否位于 <paramref name="root"/> 之内（含自身）。</summary>
    internal static bool IsWithin(string root, string path)
    {
        string fullRoot = NormalizeRoot(root, "整合包目标目录无效。");
        string fullPath = NormalizeRoot(path, "整合包文件路径无效。");
        if (string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把路径转换为不带结尾分隔符的绝对路径。</summary>
    internal static string NormalizeRoot(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException(message);
        string value = StripExtendedPrefix(path.Trim());
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException(message, error);
        }

        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static string StripExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path[uncPrefix.Length..];
        if (path.StartsWith(localPrefix, StringComparison.Ordinal)) return path[localPrefix.Length..];
        return path;
    }
}
