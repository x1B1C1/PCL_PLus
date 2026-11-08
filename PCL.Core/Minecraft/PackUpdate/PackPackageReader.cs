using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace PCL.Core.Minecraft.PackUpdate;

/// <summary>
/// 整合包压缩包读取器：优先解析 Modrinth 的 <c>modrinth.index.json</c>，
/// 否则解析 CurseForge 风格的 <c>manifest.json</c>，再把
/// <c>overrides/</c> 与 <c>client-overrides/</c> 中的真实文件合并进来。
/// 只读取元数据与覆盖层文件，不访问网络。
/// </summary>
public static class PackPackageReader
{
    private const string ModrinthIndexName = "modrinth.index.json";
    private const string CurseForgeManifestName = "manifest.json";
    private const string OverridesPrefix = "overrides/";
    private const string ClientOverridesPrefix = "client-overrides/";
    private const int BufferSize = 131_072;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// 读取一个 <c>.mrpack</c> 或 CurseForge 风格 <c>.zip</c>。
    /// </summary>
    /// <param name="archivePath">压缩包路径。</param>
    /// <param name="token">取消操作。</param>
    /// <exception cref="InvalidDataException">
    /// 压缩包缺少清单、清单损坏，或包含越界路径、超限条目。
    /// </exception>
    /// <exception cref="FileNotFoundException">压缩包不存在。</exception>
    public static PackPackage Read(string archivePath, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) throw new ArgumentException("未指定整合包压缩包路径。", nameof(archivePath));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(archivePath.Trim());
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("整合包压缩包路径无效。", nameof(archivePath), error);
        }

        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到整合包压缩包。", fullPath);

        using ZipArchive archive = ZipFile.OpenRead(fullPath);
        token.ThrowIfCancellationRequested();

        // 先统计条目数并过滤掉目录项，避免超大的压缩包把内存拖垮。
        int archiveFiles = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (IsDirectory(entry)) continue;
            if (++archiveFiles > PackUpdatePaths.MaxFiles)
            {
                throw new InvalidDataException($"整合包内的文件数超过上限 {PackUpdatePaths.MaxFiles}。");
            }
        }

        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
        string? name;
        string? versionId;

        ZipArchiveEntry? modrinth = FindManifest(archive, ModrinthIndexName);
        if (modrinth is not null)
        {
            ReadModrinthIndex(modrinth, files, out name, out versionId, token);
        }
        else
        {
            ZipArchiveEntry? curseForge = FindManifest(archive, CurseForgeManifestName);
            if (curseForge is null)
            {
                throw new InvalidDataException(
                    $"整合包中既没有 {ModrinthIndexName}，也没有 {CurseForgeManifestName}。");
            }

            ReadCurseForgeManifest(curseForge, files, out name, out versionId, token);
        }

        // 覆盖层按 overrides → client-overrides 的顺序合并，后者优先；
        // 同名时以压缩包中的真实内容与哈希为准。
        MergeOverrides(archive, OverridesPrefix, files, token);
        MergeOverrides(archive, ClientOverridesPrefix, files, token);

        if (files.Count > PackUpdatePaths.MaxFiles)
        {
            throw new InvalidDataException($"整合包内的文件数超过上限 {PackUpdatePaths.MaxFiles}。");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(fullPath);
        }

        return new PackPackage(
            name.Trim(),
            string.IsNullOrWhiteSpace(versionId) ? null : versionId.Trim(),
            new ReadOnlyDictionary<string, string>(files))
        {
            SourceArchive = fullPath
        };
    }

    private static void ReadModrinthIndex(
        ZipArchiveEntry entry,
        IDictionary<string, string> files,
        out string? name,
        out string? versionId,
        CancellationToken token)
    {
        using JsonDocument document = ParseJsonEntry(entry, ModrinthIndexName, token);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{ModrinthIndexName} 的根不是对象。");
        }

        name = ReadString(root, "name");
        versionId = ReadString(root, "versionId");

        if (!root.TryGetProperty("files", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{ModrinthIndexName} 缺少 files 数组。");
        }

        int index = 0;
        foreach (JsonElement item in list.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (++index > PackUpdatePaths.MaxFiles)
            {
                throw new InvalidDataException($"整合包清单中的文件数超过上限 {PackUpdatePaths.MaxFiles}。");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{ModrinthIndexName} 的 files[] 中存在非对象条目。");
            }

            string? path = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException($"{ModrinthIndexName} 的 files[] 缺少 path。");
            }

            string relative = PackUpdatePaths.NormalizeRelative(path);
            string hash = "";
            if (item.TryGetProperty("hashes", out JsonElement hashes) && hashes.ValueKind == JsonValueKind.Object)
            {
                hash = NormalizeSha1(ReadString(hashes, "sha1"));
            }

            files[relative] = hash;
        }
    }

    private static void ReadCurseForgeManifest(
        ZipArchiveEntry entry,
        IDictionary<string, string> files,
        out string? name,
        out string? versionId,
        CancellationToken token)
    {
        using JsonDocument document = ParseJsonEntry(entry, CurseForgeManifestName, token);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{CurseForgeManifestName} 的根不是对象。");
        }

        name = ReadString(root, "name");
        // CurseForge 官方字段叫 version，部分导出工具会写 versionId，两者都兼容。
        versionId = ReadString(root, "versionId") ?? ReadString(root, "version");

        if (!root.TryGetProperty("files", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{CurseForgeManifestName} 缺少 files 数组。");
        }

        int index = 0;
        foreach (JsonElement item in list.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (++index > PackUpdatePaths.MaxFiles)
            {
                throw new InvalidDataException($"整合包清单中的文件数超过上限 {PackUpdatePaths.MaxFiles}。");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{CurseForgeManifestName} 的 files[] 中存在非对象条目。");
            }

            // 官方 CurseForge 清单只有 projectID/fileID，没有 path；
            // 兼容带有 path 的导出格式，没有 path 的条目直接跳过（无法映射到本地文件）。
            string? path = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;

            // CurseForge 清单不提供文件哈希，规划阶段会退化为「覆盖」。
            files[PackUpdatePaths.NormalizeRelative(path)] = "";
        }
    }

    private static void MergeOverrides(
        ZipArchive archive,
        string prefix,
        IDictionary<string, string> files,
        CancellationToken token)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (IsDirectory(entry)) continue;

            string archiveName = entry.FullName.Replace('\\', '/');
            if (!archiveName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string relative = archiveName[prefix.Length..];
            if (relative.Length == 0) continue;
            string normalized = PackUpdatePaths.NormalizeRelative(relative);
            files[normalized] = HashEntry(entry, normalized, token);
        }
    }

    private static ZipArchiveEntry? FindManifest(ZipArchive archive, string fileName)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (IsDirectory(entry)) continue;
            string name = entry.FullName.Replace('\\', '/');
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        return null;
    }

    private static JsonDocument ParseJsonEntry(ZipArchiveEntry entry, string label, CancellationToken token)
    {
        if (entry.Length > PackUpdatePaths.MaxManifestBytes)
        {
            throw new InvalidDataException($"{label} 超过大小上限。");
        }

        using Stream input = entry.Open();
        using MemoryStream buffer = new();
        CopyLimited(input, buffer, PackUpdatePaths.MaxManifestBytes, token, label);
        buffer.Position = 0;
        try
        {
            return JsonDocument.Parse(buffer, DocumentOptions);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{label} 不是有效的 JSON。", error);
        }
    }

    private static string HashEntry(ZipArchiveEntry entry, string relative, CancellationToken token)
    {
        if (entry.Length > PackUpdatePaths.MaxEntryBytes)
        {
            throw new InvalidDataException("整合包条目超过大小上限：" + relative);
        }

        using Stream input = entry.Open();
        using SHA1 sha1 = SHA1.Create();
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > PackUpdatePaths.MaxEntryBytes)
            {
                throw new InvalidDataException("整合包条目超过大小上限：" + relative);
            }

            sha1.TransformBlock(buffer, 0, read, null, 0);
        }

        sha1.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha1.Hash!);
    }

    private static void CopyLimited(
        Stream input,
        Stream output,
        long limit,
        CancellationToken token,
        string label)
    {
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > limit) throw new InvalidDataException($"{label} 超过大小上限。");
            output.Write(buffer, 0, read);
        }
    }

    private static string NormalizeSha1(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string text = value.Trim();
        if (text.StartsWith("sha1:", StringComparison.OrdinalIgnoreCase)) text = text[5..];
        return text.ToLowerInvariant();
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
}
