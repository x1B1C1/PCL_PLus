using System.Net.Http;
using PCL.Core.Minecraft.Mod;
using System.Text.Json.Nodes;

namespace PCL.Modules.Minecraft;

/// <summary>
///     依赖自动补全：把依赖检查报出来的缺失 id 拿去 Modrinth 找对应的项目与文件，再下载进 mods。
///     只在启动前检查发现缺失、且用户选择「自动补全」时才会走到这里。
/// </summary>
internal static class ModDependencyInstaller
{
    private const string ApiRoot = "https://api.modrinth.com/v2";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static ModDependencyInstaller()
    {
        try
        {
            Client.DefaultRequestHeaders.UserAgent.ParseAdd($"PCL_Plus/{ModBase.versionBaseName}");
            Client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }
        catch (Exception)
        {
            // 头部写失败不影响请求本身，忽略。
        }
    }

    public sealed record InstallOutcome(int Installed, IReadOnlyList<string> Failed);

    /// <summary>
    ///     逐个尝试补齐缺失依赖。任何一个失败都不会中断其它依赖，最终把失败的 id 汇总返回。
    /// </summary>
    public static async Task<InstallOutcome> InstallAsync(
        ModDependencyReport report,
        McInstance instance,
        LoaderProfile profile,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modsFolder = System.IO.Path.Combine(instance.PathIndie, "mods");
        System.IO.Directory.CreateDirectory(modsFolder);

        var gameVersion = profile.MinecraftVersion;
        var loaders = profile.Loaders.Keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.ToLowerInvariant())
            .ToArray();

        var installed = 0;
        var failed = new List<string>();

        foreach (string missingId in report.Issues
                     .Select(issue => issue.MissingId)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(missingId);

            try
            {
                if (await InstallOneAsync(missingId, modsFolder, gameVersion, loaders, cancellationToken)
                        .ConfigureAwait(false))
                    installed++;
                else
                    failed.Add(missingId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Launch] 自动补全依赖 {missingId} 失败，已跳过", ModBase.LogLevel.Debug);
                failed.Add(missingId);
            }
        }

        return new InstallOutcome(installed, failed);
    }

    private static async Task<bool> InstallOneAsync(
        string missingId,
        string modsFolder,
        string? gameVersion,
        string[] loaders,
        CancellationToken cancellationToken)
    {
        string? slug = await ResolveSlugAsync(missingId, cancellationToken).ConfigureAwait(false);
        if (slug is null) return false;

        string versionsUrl = $"{ApiRoot}/project/{Uri.EscapeDataString(slug)}/version";
        var query = new List<string> { "featured=true" };
        if (!string.IsNullOrWhiteSpace(gameVersion))
            query.Add($"game_versions=%5B%22{Uri.EscapeDataString(gameVersion)}%22%5D");
        if (loaders.Length > 0)
            query.Add("loaders=%5B" + string.Join(",", loaders.Select(l => $"\"{Uri.EscapeDataString(l)}\"")) + "%5D");
        versionsUrl += "?" + string.Join("&", query);

        JsonArray? versions = await GetArrayAsync(versionsUrl, cancellationToken).ConfigureAwait(false);
        if (versions is null || versions.Count == 0)
        {
            // 版本筛选太严（例如加载器写法不同）时退回全量，再取第一个。
            versions = await GetArrayAsync($"{ApiRoot}/project/{Uri.EscapeDataString(slug)}/version",
                cancellationToken).ConfigureAwait(false);
        }

        if (versions is null || versions.Count == 0) return false;

        var file = versions
            .SelectMany(version => version?["files"] as JsonArray ?? [])
            .FirstOrDefault(node => node?["url"]?.GetValue<string>() is { Length: > 0 });
        if (file?["url"]?.GetValue<string>() is not { } downloadUrl) return false;

        string fileName = file["filename"]?.GetValue<string>() is { Length: > 0 } name
            ? System.IO.Path.GetFileName(name)
            : $"{slug}.jar";
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            fileName += ".jar";

        string target = System.IO.Path.Combine(modsFolder, fileName);
        if (System.IO.File.Exists(target)) return true;

        byte[] payload = await Client.GetByteArrayAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
        if (payload.Length < 4096) return false;

        string temp = target + ".pcltmp";
        await System.IO.File.WriteAllBytesAsync(temp, payload, cancellationToken).ConfigureAwait(false);
        if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
        System.IO.File.Move(temp, target);
        ModBase.Log($"[Launch] 已自动补全依赖 {missingId} → {fileName}", ModBase.LogLevel.Debug);
        return true;
    }

    /// <summary>
    ///     把 modid / 项目名换成 Modrinth 的 slug：先精确匹配 slug，再接受标题完全一致的第一个结果。
    /// </summary>
    private static async Task<string?> ResolveSlugAsync(string missingId, CancellationToken cancellationToken)
    {
        string url = $"{ApiRoot}/search?query={Uri.EscapeDataString(missingId)}&limit=8" +
                     "&facets=%5B%5B%22project_type%3Amod%22%5D%5D";
        JsonObject? root = await GetObjectAsync(url, cancellationToken).ConfigureAwait(false);
        if (root?["hits"] is not JsonArray hits) return null;

        var candidates = hits
            .OfType<JsonObject>()
            .Select(hit => (Slug: hit["slug"]?.GetValue<string>(),
                Title: hit["title"]?.GetValue<string>()))
            .Where(item => item.Slug is { Length: > 0 })
            .ToList();

        var exact = candidates.FirstOrDefault(item =>
            string.Equals(item.Slug, missingId, StringComparison.OrdinalIgnoreCase));
        if (exact.Slug is { Length: > 0 }) return exact.Slug;

        var byTitle = candidates.FirstOrDefault(item =>
            string.Equals(item.Title, missingId, StringComparison.OrdinalIgnoreCase));
        if (byTitle.Slug is { Length: > 0 }) return byTitle.Slug;

        return candidates.FirstOrDefault().Slug;
    }

    private static async Task<JsonObject?> GetObjectAsync(string url, CancellationToken cancellationToken)
    {
        string text = await Client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(text) as JsonObject;
    }

    private static async Task<JsonArray?> GetArrayAsync(string url, CancellationToken cancellationToken)
    {
        string text = await Client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(text) as JsonArray;
    }
}
