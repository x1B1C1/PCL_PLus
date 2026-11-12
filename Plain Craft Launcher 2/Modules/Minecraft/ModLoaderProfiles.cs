using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using PCL.Core.Minecraft.Mod;

namespace PCL.Modules.Minecraft;

/// <summary>
///     把实例的版本信息翻译成加载器画像，并给出单个模组文件的加载器警示。
/// </summary>
internal static class ModLoaderProfiles
{
    /// <summary>元数据缓存：路径 → (长度, 最后写入时间, 元数据)。避免每次刷新列表都重开 jar。</summary>
    private static readonly Dictionary<string, (long Length, DateTime WriteTime, ModArchiveMetadata? Metadata)> Cache = [];

    /// <summary>取实例的加载器画像；拿不到时返回空画像（一切放行）。</summary>
    public static LoaderProfile Of(McInstance? instance)
    {
        if (instance is null) return LoaderProfile.Empty;
        try
        {
            var names = new List<string>();
            if (instance.JsonObject?["libraries"] is JsonArray libraries)
            {
                foreach (var node in libraries)
                {
                    if (node?["name"]?.GetValue<string>() is { } name) names.Add(name);
                }
            }

            return ModLoaderRules.BuildProfile(names, instance.Info?.VanillaName);
        }
        catch (Exception)
        {
            return LoaderProfile.Empty;
        }
    }

    /// <summary>
    ///     检查一个模组文件是否与当前实例的加载器匹配；匹配或判断不了时返回 null，不匹配时返回给用户看的说明。
    /// </summary>
    public static string? WarningFor(string modPath, LoaderProfile profile)
    {
        if (!profile.IsKnown) return null;
        if (!ModArchiveReader.IsModFile(modPath)) return null;

        FileInfo file;
        try
        {
            file = new FileInfo(modPath);
            if (!file.Exists) return null;
        }
        catch (Exception)
        {
            return null;
        }

        ModArchiveMetadata? metadata;
        if (Cache.TryGetValue(modPath, out var cached) && cached.Length == file.Length &&
            cached.WriteTime == file.LastWriteTimeUtc)
        {
            metadata = cached.Metadata;
        }
        else
        {
            metadata = ModArchiveReader.Read(modPath);
            Cache[modPath] = (file.Length, file.LastWriteTimeUtc, metadata);
        }

        if (metadata is null) return null;
        if (ModLoaderRules.LoaderAccepts(metadata.MetadataName, profile, metadata.DeclaredPlatform)) return null;
        return PCL.Core.App.Localization.Lang.Text("Instance.Mod.LoaderMismatch",
            DescribeSource(metadata), ModLoaderRules.LoaderDisplay(profile));
    }

    private static string DescribeSource(ModArchiveMetadata metadata) => metadata.MetadataName switch
    {
        "fabric.mod.json" => "Fabric",
        "quilt.mod.json" => "Quilt",
        "META-INF/neoforge.mods.toml" => "NeoForge",
        "META-INF/mods.toml" => metadata.DeclaredPlatform == "neoforge" ? "NeoForge" : "Forge",
        "mcmod.info" => "Forge（旧版）",
        "litemod.json" => "LiteLoader",
        _ => metadata.MetadataName
    };

    /// <summary>清空元数据缓存（重装模组或切换实例后可调用）。</summary>
    public static void ClearCache() => Cache.Clear();
}
