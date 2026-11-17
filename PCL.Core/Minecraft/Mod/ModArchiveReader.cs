using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PCL.Core.Minecraft.Mod;

/// <summary>一个模组文件（jar/zip/litemod）内声明的元数据摘要。</summary>
/// <param name="MetadataName">元数据来源：<c>fabric.mod.json</c>、<c>quilt.mod.json</c>、<c>META-INF/neoforge.mods.toml</c>、<c>META-INF/mods.toml</c>、<c>mcmod.info</c>、<c>litemod.json</c>；没有元数据时为 <c>legacy</c>。</param>
/// <param name="DeclaredPlatform">From <c>mods.toml</c> 内部声明的平台（<c>forge</c> / <c>neoforge</c>），没有时为空串。</param>
/// <param name="ModId">模组自身 id。</param>
/// <param name="Name">显示名。</param>
/// <param name="DependencyIds">声明的依赖 id 列表（不含版本约束）。</param>
public sealed record ModArchiveMetadata(
    string MetadataName,
    string DeclaredPlatform,
    string? ModId,
    string? Name,
    IReadOnlyList<string> DependencyIds);

/// <summary>
///     读取模组压缩包内的元数据。
///     <para>
///         只读、无缓存、不依赖 UI。为了在「遍历几百个 jar 刷新列表」时不把磁盘打满，
///         单个条目最多读 1 MiB，超出直接放弃该文件（返回 null，调用方按「未知格式」放行）。
///     </para>
/// </summary>
public static class ModArchiveReader
{
    /// <summary>单个元数据条目的最大字节数。</summary>
    private const long MaxEntryBytes = 1024 * 1024;

    /// <summary>按优先级排列的元数据候选，顺序与游戏加载器一致。</summary>
    private static readonly string[] CandidateEntries =
    [
        "fabric.mod.json",
        "quilt.mod.json",
        "META-INF/neoforge.mods.toml",
        "META-INF/mods.toml",
        "mcmod.info",
        "litemod.json"
    ];

    /// <summary>是否是模组文件（<c>.jar</c>/<c>.zip</c>/<c>.litemod</c>，允许带 <c>.disabled</c>/<c>.old</c> 后缀）。</summary>
    public static bool IsModFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var name = fileName;
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name = name[..^".disabled".Length];
        if (name.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) name = name[..^".old".Length];
        return name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取指定模组文件的元数据；读不到（不是压缩包、没有元数据、损坏）时返回 null。</summary>
    public static ModArchiveMetadata? Read(string jarPath)
    {
        try
        {
            using var stream = File.OpenRead(jarPath);
            return Read(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>从流读取（需要可随机访问，<see cref="ZipArchive" /> 的要求）。</summary>
    public static ModArchiveMetadata? Read(Stream stream)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
            foreach (var candidate in CandidateEntries)
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName.Replace('\\', '/'), candidate, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                if (entry.Length > MaxEntryBytes) continue;
                var text = ReadText(entry);
                if (text is null) continue;
                return candidate switch
                {
                    "fabric.mod.json" => ParseFabric(text, "fabric.mod.json"),
                    "quilt.mod.json" => ParseQuilt(text),
                    "META-INF/neoforge.mods.toml" => ParseToml(text, "META-INF/neoforge.mods.toml", "neoforge"),
                    "META-INF/mods.toml" => ParseToml(text, "META-INF/mods.toml", string.Empty),
                    "mcmod.info" => ParseMcmodInfo(text),
                    "litemod.json" => ParseLitemod(text),
                    _ => null
                };
            }
        }
        catch (Exception)
        {
            // 损坏的压缩包按「未知格式」处理，交给调用方放行。
        }

        return null;
    }

    private static string? ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    private static ModArchiveMetadata? ParseFabric(string text, string metadataName)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var modId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        var dependencies = new List<string>();
        CollectJsonDependencyKeys(root, "depends", dependencies);
        return new ModArchiveMetadata(metadataName, string.Empty, modId, name, dependencies);
    }

    private static ModArchiveMetadata? ParseQuilt(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        JsonElement quiltLoader = default;
        if (!root.TryGetProperty("quilt_loader", out quiltLoader)) return null;
        var modId = quiltLoader.TryGetProperty("id", out var id) ? id.GetString() : null;
        var name = quiltLoader.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("name", out var n)
            ? n.GetString()
            : null;
        var dependencies = new List<string>();
        CollectJsonDependencyKeys(quiltLoader, "depends", dependencies);
        return new ModArchiveMetadata("quilt.mod.json", string.Empty, modId, name, dependencies);
    }

    private static void CollectJsonDependencyKeys(JsonElement parent, string property, List<string> into)
    {
        if (!parent.TryGetProperty(property, out var depends)) return;
        switch (depends.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in depends.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                        into.Add(s);
                break;
            case JsonValueKind.Object:
                foreach (var item in depends.EnumerateObject())
                    into.Add(item.Name);
                break;
        }
    }

    private static ModArchiveMetadata? ParseToml(string text, string metadataName, string defaultPlatform)
    {
        string? modId = null;
        string? name = null;
        var dependencies = new List<string>();
        var platform = defaultPlatform;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var inDependencies = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("[[dependencies", StringComparison.OrdinalIgnoreCase))
            {
                inDependencies = true;
                continue;
            }

            if (line.StartsWith('[') && !line.StartsWith("[["))
            {
                inDependencies = false;
                continue;
            }

            var (key, value) = SplitTomlKeyValue(line);
            if (key is null || value is null) continue;
            if (inDependencies)
            {
                if (key.Equals("modId", StringComparison.OrdinalIgnoreCase))
                {
                    dependencies.Add(value);
                    if (value.Equals("neoforge", StringComparison.OrdinalIgnoreCase)) platform = "neoforge";
                    else if (value.Equals("forge", StringComparison.OrdinalIgnoreCase) && platform.Length == 0) platform = "forge";
                }

                continue;
            }

            if (key.Equals("modId", StringComparison.OrdinalIgnoreCase)) modId ??= value;
            else if (key.Equals("displayName", StringComparison.OrdinalIgnoreCase)) name ??= value;
        }

        return new ModArchiveMetadata(metadataName, platform, modId, name, dependencies);
    }

    private static (string? Key, string? Value) SplitTomlKeyValue(string line)
    {
        var equal = line.IndexOf('=');
        if (equal <= 0) return (null, null);
        var key = line[..equal].Trim();
        var value = line[(equal + 1)..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1];
        else if (value.Contains('#')) value = value[..value.IndexOf('#')].Trim();
        return (key, value);
    }

    private static ModArchiveMetadata? ParseMcmodInfo(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        JsonElement first = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0) return null;
            first = root[0];
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            first = root;
        }
        else
        {
            return null;
        }

        var modId = first.TryGetProperty("modid", out var id) ? id.GetString() : null;
        var name = first.TryGetProperty("name", out var n) ? n.GetString() : null;
        return new ModArchiveMetadata("mcmod.info", string.Empty, modId, name, []);
    }

    private static ModArchiveMetadata? ParseLitemod(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        return new ModArchiveMetadata("litemod.json", string.Empty, name, name, []);
    }
}
