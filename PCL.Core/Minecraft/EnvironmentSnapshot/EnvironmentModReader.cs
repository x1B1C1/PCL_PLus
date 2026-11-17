using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.EnvironmentSnapshot;

/// <summary>从模组 jar 中读到的展示信息；读不到的字段为 null。</summary>
/// <param name="Name">展示名称。</param>
/// <param name="Version">模组版本。</param>
/// <param name="ModId">模组标识。</param>
public sealed record EnvironmentModInfo(string? Name, string? Version, string? ModId);

/// <summary>
/// 读取 Forge、NeoForge、Fabric、Quilt 与旧版 LiteLoader 模组 jar 的元数据。
/// 只读取清单内不超过 1 MiB 的描述文件，不执行模组代码。
/// </summary>
public static class EnvironmentModReader
{
    private const long MaxEntryBytes = 1024 * 1024;

    private static readonly string[] CandidateEntries =
    [
        "fabric.mod.json",
        "quilt.mod.json",
        "META-INF/mods.toml",
        "META-INF/neoforge.mods.toml",
        "mcmod.info"
    ];

    private static readonly Regex TomlModIdPattern = new(
        @"(?m)^\s*modId\s*=\s*['""]([^'""]+)['""]",
        RegexOptions.CultureInvariant);

    private static readonly Regex TomlVersionPattern = new(
        @"(?m)^\s*version\s*=\s*['""]([^'""]+)['""]",
        RegexOptions.CultureInvariant);

    private static readonly Regex TomlNamePattern = new(
        @"(?m)^\s*(?:displayName|name)\s*=\s*['""]([^'""]+)['""]",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 读取 jar 内的模组信息。文件不存在、不是 zip、描述文件缺失或无法解析时返回 null。
    /// </summary>
    public static EnvironmentModInfo? Read(string jarPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath)) return null;
            using ZipArchive archive = ZipFile.OpenRead(jarPath);
            foreach (string entryName in CandidateEntries)
            {
                ZipArchiveEntry? entry = archive.GetEntry(entryName);
                if (entry is null || entry.Length <= 0 || entry.Length > MaxEntryBytes) continue;
                string text;
                using (Stream stream = entry.Open())
                using (StreamReader reader = new(stream, Encoding.UTF8, true))
                {
                    text = reader.ReadToEnd();
                }

                EnvironmentModInfo? info = entryName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
                    ? ParseToml(text)
                    : ParseJson(entryName, text);
                if (info is not null) return info;
            }
        }
        catch (Exception error) when (error is IOException
                                          or InvalidDataException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or JsonException)
        {
            // 无法读取的模组仍按文件变化展示，不猜测元数据。
        }

        return null;
    }

    /// <summary>
    /// 从文件名提取模组家族名：去掉禁用/旧版后缀与扩展名，再去掉第一个版本号及其后的内容。
    /// 例如 <c>jei-1.20.1-15.2.0.27.jar</c> 返回 <c>jei</c>。
    /// </summary>
    public static string Family(string fileName)
    {
        string name = Path.GetFileName(fileName ?? "");
        while (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        if (name.Length == 0) return "";
        string stem = Path.GetFileNameWithoutExtension(name);
        return Regex.Replace(stem, @"[-_]v?\d.*\z", "", RegexOptions.IgnoreCase);
    }

    private static EnvironmentModInfo? ParseJson(string entryName, string text)
    {
        JsonNode? node = JsonNode.Parse(text);
        JsonObject? root = node as JsonObject;
        if (root is null && node is JsonArray array && entryName == "mcmod.info")
        {
            return FromArray(array);
        }

        if (root is null) return null;
        if (entryName == "mcmod.info")
        {
            if (root["modList"] is JsonArray modList) return FromArray(modList);
            return FromObject(root, "modid", "name", "version");
        }

        if (entryName == "quilt.mod.json")
        {
            JsonObject? loader = root["quilt_loader"] as JsonObject;
            if (loader is not null)
            {
                string? name = null;
                if (loader["metadata"] is JsonObject metadata) name = Clean(Text(metadata["name"]));
                name ??= Clean(Text(loader["name"]));
                return Build(name, Clean(Text(loader["version"])), Clean(Text(loader["id"])));
            }
        }

        return FromObject(root, "id", "name", "version");
    }

    private static EnvironmentModInfo? FromArray(JsonArray array)
    {
        foreach (JsonNode? item in array)
        {
            if (item is JsonObject obj) return FromObject(obj, "modid", "name", "version");
        }

        return null;
    }

    private static EnvironmentModInfo? FromObject(JsonObject root, string idKey, string nameKey, string versionKey) =>
        Build(Clean(Text(root[nameKey])), Clean(Text(root[versionKey])), Clean(Text(root[idKey])));

    private static EnvironmentModInfo? ParseToml(string text)
    {
        string? modId = Match(TomlModIdPattern, text);
        string? version = Match(TomlVersionPattern, text);
        string? name = Match(TomlNamePattern, text);
        return Build(name ?? modId, version, modId);
    }

    private static string? Match(Regex pattern, string text)
    {
        Match match = pattern.Match(text);
        return match.Success ? Clean(match.Groups[1].Value) : null;
    }

    private static EnvironmentModInfo? Build(string? name, string? version, string? modId)
    {
        if (name is null && version is null && modId is null) return null;
        return new EnvironmentModInfo(name, version, modId);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static string? Clean(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0) return null;
        return text.Length <= 200 ? text : text[..200];
    }
}
