using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Minecraft.Mod;

/// <summary>某个实例的加载器画像：Minecraft 版本 + 已装载的加载器及其版本。</summary>
/// <param name="MinecraftVersion">原版版本号，例如 <c>1.20.1</c>；未知时为 null。</param>
/// <param name="Loaders">加载器 id（小写，如 <c>forge</c>/<c>neoforge</c>/<c>fabricloader</c>/<c>quilt_loader</c>/<c>liteloader</c>）到版本的映射。</param>
public sealed record LoaderProfile(string? MinecraftVersion, IReadOnlyDictionary<string, string> Loaders)
{
    /// <summary>加载器信息是否可用；不可用时一切检查都放行（宁可漏报不误报）。</summary>
    public bool IsKnown => Loaders.Count > 0;

    /// <summary>是否装有指定加载器。</summary>
    public bool Has(string id) => Loaders.ContainsKey(id);

    /// <summary>空画像：什么都不知道。</summary>
    public static LoaderProfile Empty { get; } = new(null, new Dictionary<string, string>());
}

/// <summary>
///     模组元数据与加载器的兼容规则。
///     <para>
///         判定只回答一个问题：这份元数据声明的加载器，当前实例能不能加载？
///         不知道当前加载器（或没有版本号）时一律返回 <c>true</c>，避免把能用的模组标成错误。
///     </para>
/// </summary>
public static class ModLoaderRules
{
    /// <summary>判断某份元数据是否被当前加载器接受。</summary>
    /// <param name="metadata">元数据文件名，例如 <c>fabric.mod.json</c>、<c>META-INF/mods.toml</c>、<c>legacy</c>（Forge 1.12 及更早的注解式模组）。</param>
    /// <param name="profile">实例的加载器画像。</param>
    /// <param name="declaredPlatform"><c>mods.toml</c> 内部声明的平台（<c>forge</c> / <c>neoforge</c>），用于区分 NeoForge 的两种元数据时代。</param>
    public static bool LoaderAccepts(string? metadata, LoaderProfile? profile, string? declaredPlatform = "")
    {
        if (profile is null || !profile.IsKnown) return true;
        var loaders = profile.Loaders;
        return (metadata ?? string.Empty) switch
        {
            "fabric.mod.json" => loaders.ContainsKey("fabricloader") || loaders.ContainsKey("quilt_loader"),
            "quilt.mod.json" => loaders.ContainsKey("quilt_loader"),
            "META-INF/neoforge.mods.toml" => loaders.ContainsKey("neoforge") && NeoModernMetadata(profile).GetValueOrDefault(true),
            "META-INF/mods.toml" => declaredPlatform switch
            {
                "neoforge" => loaders.ContainsKey("neoforge") && !NeoModernMetadata(profile).GetValueOrDefault(false),
                "forge" => (loaders.ContainsKey("forge") && !IsLegacyForge(profile)) ||
                           (loaders.ContainsKey("neoforge") && profile.MinecraftVersion == "1.20.1"),
                _ => (loaders.ContainsKey("forge") && !IsLegacyForge(profile)) ||
                     (loaders.ContainsKey("neoforge") && !NeoModernMetadata(profile).GetValueOrDefault(false))
            },
            "legacy" => IsLegacyForge(profile),
            "litemod.json" => loaders.ContainsKey("liteloader"),
            _ => true
        };
    }

    /// <summary>当前实例是否是「注解式旧版 Forge」（1.12.2 及更早，或 Forge 版本号 1.x–14.x）。</summary>
    public static bool IsLegacyForge(LoaderProfile profile)
    {
        if (profile.Has("forge"))
        {
            var version = profile.Loaders["forge"];
            if (version.Length > 0 && (version[0] is >= '1' and <= '9')) return true;
            if (version.StartsWith("1", StringComparison.Ordinal) && version.Length > 1 && version[1] == '.') return true;
        }

        var mc = profile.MinecraftVersion;
        if (string.IsNullOrWhiteSpace(mc) || !mc.StartsWith("1.", StringComparison.Ordinal)) return false;
        var rest = mc[2..];
        var dot = rest.IndexOf('.');
        var minorText = dot < 0 ? rest : rest[..dot];
        return int.TryParse(minorText, out var minor) && minor <= 12;
    }

    /// <summary>
    ///     是否已经是 NeoForge 的「新元数据」时代（Minecraft 1.20.5 起）。
    ///     没有 NeoForge、或版本号解析不出来时返回 null，表示「判断不了」。
    /// </summary>
    public static bool? NeoModernMetadata(LoaderProfile profile)
    {
        if (!profile.Has("neoforge")) return null;
        if (!Version.TryParse(profile.MinecraftVersion, out var version)) return null;
        return version >= new Version(1, 20, 5);
    }

    /// <summary>给用户看的加载器名称。</summary>
    public static string LoaderDisplay(LoaderProfile profile)
    {
        if (profile.Has("quilt_loader")) return "Quilt";
        if (profile.Has("fabricloader")) return "Fabric";
        if (profile.Has("neoforge")) return "NeoForge";
        if (profile.Has("forge")) return IsLegacyForge(profile) ? "Forge（旧版）" : "Forge";
        if (profile.Has("liteloader")) return "LiteLoader";
        return "当前加载器";
    }

    /// <summary>把模组声明的依赖 id 归到「由谁提供」：加载器 / 游戏 / Java 内置的一律返回对应 id，其余返回空串。</summary>
    public static string RuntimeOwnerId(string? id, LoaderProfile profile)
    {
        return (id ?? string.Empty) switch
        {
            "javafml" or "lowcodefml" or "fml" or "FML" => profile.Has("neoforge") ? "neoforge" : "forge",
            "forge" or "neoforge" or "fabricloader" or "quilt_loader" or "liteloader" or "minecraft" or "java" => id!,
            _ => string.Empty
        };
    }

    /// <summary>是否是加载器自身的内部 id（这些 id 不算「缺失前置」）。</summary>
    public static bool IsInternalLoaderId(string? id) =>
        (id ?? string.Empty) is "javafml" or "lowcodefml" or "fml" or "FML";

    /// <summary>从版本 json 的 <c>libraries</c> 里提取加载器 id 与版本。</summary>
    /// <param name="libraryNames">形如 <c>net.fabricmc:fabric-loader:0.15.11</c> 的坐标字符串。</param>
    /// <param name="minecraftVersion">原版版本号。</param>
    public static LoaderProfile BuildProfile(IEnumerable<string> libraryNames, string? minecraftVersion)
    {
        var loaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var coordinate in libraryNames)
        {
            if (string.IsNullOrWhiteSpace(coordinate)) continue;
            var parts = coordinate.Split(':');
            if (parts.Length < 3) continue;
            var artifact = parts[1].ToLowerInvariant();
            switch (artifact)
            {
                case "fabric-loader":
                    loaders["fabricloader"] = parts[2];
                    break;
                case "quilt-loader":
                    loaders["quilt_loader"] = parts[2];
                    break;
                case "forge":
                    loaders["forge"] = parts[2];
                    break;
                case "neoforge":
                    loaders["neoforge"] = parts[2];
                    break;
                case "loader" when parts[0].Contains("neoforged", StringComparison.OrdinalIgnoreCase):
                    loaders["neoforge"] = parts[2];
                    break;
                case "modlauncher":
                case "fmlloader":
                    break;
            }
        }

        return new LoaderProfile(minecraftVersion, loaders);
    }
}
