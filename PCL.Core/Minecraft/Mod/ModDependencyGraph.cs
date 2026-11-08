using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.Minecraft.Mod;

/// <summary>一条「缺少前置」的检查结果。</summary>
/// <param name="FilePath">缺少前置的文件。</param>
/// <param name="MissingId">缺少的依赖 id。</param>
public sealed record ModDependencyIssue(string FilePath, string MissingId);

/// <summary>一次依赖检查的汇总。</summary>
/// <param name="Issues">缺失前置的清单。</param>
/// <param name="DisabledProviders">能提供这些 id、但当前处于禁用状态的文件（id → 文件路径）。</param>
public sealed record ModDependencyReport(
    IReadOnlyList<ModDependencyIssue> Issues,
    IReadOnlyDictionary<string, string> DisabledProviders)
{
    /// <summary>是否一切正常。</summary>
    public bool IsHealthy => Issues.Count == 0;
}

/// <summary>
///     模组依赖的前置检查。
///     <para>
///         只看「文件声明了哪些 id、哪些文件提供了哪些 id」，不下载、不写文件。
///         加载器 / 游戏 / Java 这类内置提供者不参与判断，避免把平台依赖报成缺失。
///     </para>
/// </summary>
public static class ModDependencyGraph
{
    /// <summary>
    ///     检查一批模组的必需前置是否齐全。
    /// </summary>
    /// <param name="enabledMods">已启用的模组：文件路径 → 元数据。</param>
    /// <param name="disabledMods">已禁用的模组：文件路径 → 元数据（用于提示「启用它就能解决」）。</param>
    /// <param name="profile">实例的加载器画像。</param>
    public static ModDependencyReport Build(
        IEnumerable<(string Path, ModArchiveMetadata Metadata)> enabledMods,
        IEnumerable<(string Path, ModArchiveMetadata Metadata)> disabledMods,
        LoaderProfile profile)
    {
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "minecraft", "java", "forge", "neoforge", "fabricloader", "quilt_loader", "liteloader" };
        foreach (var loader in profile.Loaders.Keys) provided.Add(loader);

        var disabledProviders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, metadata) in disabledMods)
        {
            if (metadata.ModId is { Length: > 0 } disabledId) disabledProviders.TryAdd(disabledId, path);
            foreach (var dependencyId in metadata.DependencyIds) disabledProviders.TryAdd(dependencyId, path);
        }

        var issues = new List<ModDependencyIssue>();
        foreach (var (path, metadata) in enabledMods)
        {
            if (metadata.ModId is { Length: > 0 } id) provided.Add(id);
        }

        // 提供者集合要先收集完整，再逐条比对，否则「后面的模组提供前面的依赖」会被误报。
        foreach (var (path, metadata) in enabledMods)
        {
            foreach (var dependencyId in metadata.DependencyIds)
            {
                if (string.IsNullOrWhiteSpace(dependencyId)) continue;
                if (provided.Contains(dependencyId)) continue;
                if (ModLoaderRules.IsInternalLoaderId(dependencyId)) continue;
                issues.Add(new ModDependencyIssue(path, dependencyId));
            }
        }

        return new ModDependencyReport(issues, disabledProviders);
    }

    /// <summary>扫描一个 <c>mods</c> 目录并检查依赖（禁用文件单独归类，用于「启用即可修复」的提示）。</summary>
    public static ModDependencyReport Scan(string modsFolder, LoaderProfile profile)
    {
        var enabled = new List<(string, ModArchiveMetadata)>();
        var disabled = new List<(string, ModArchiveMetadata)>();
        if (!Directory.Exists(modsFolder)) return new ModDependencyReport([], new Dictionary<string, string>());

        foreach (var file in Directory.EnumerateFiles(modsFolder, "*", SearchOption.TopDirectoryOnly))
        {
            if (!ModArchiveReader.IsModFile(file)) continue;
            var metadata = ModArchiveReader.Read(file);
            if (metadata is null) continue;
            var isDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ||
                             file.EndsWith(".old", StringComparison.OrdinalIgnoreCase);
            (isDisabled ? disabled : enabled).Add((file, metadata));
        }

        return Build(enabled, disabled, profile);
    }
}
