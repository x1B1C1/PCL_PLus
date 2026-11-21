using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Updates;
using PCL.Core.Utils;

namespace PCL.Modules.Updates;

/// <summary>
///     自定义更新源：从用户自己填的 HTTP 地址拉一份 JSON 清单。
///     <para>
///         这是「换更新源」的增量实现，不改动原有四套源。地址留空时
///         <see cref="IsAvailable" /> 返回 false，包装器会直接跳到下一个源，
///         对外行为与没有这个类完全一致。
///     </para>
///     <para>
///         清单契约见 <c>docs/CUSTOM-OTA.md</c>：
///         <c>{ "version": "4.0.5", "code": 42, "url": "https://…/PCL_Plus.exe",
///         "sha256": "…", "changelog": "…" }</c>。只有 version 与 url 是必填项。
///     </para>
/// </summary>
public class UpdatesCustomModel : IUpdateSource
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    ///     缓存存活时间。手动点「重新检查」时 <c>force</c> 为 true，会跳过它。
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private VersionDataModel _cache;
    private DateTime _cacheTime = DateTime.MinValue;

    public string SourceName { get; set; } = "自定义更新源";

    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(Config.Update.CustomSource);
    }

    public bool RefreshCache(bool force = false)
    {
        if (!IsAvailable()) return false;
        if (!force && _cache is not null && DateTime.Now - _cacheTime < CacheLifetime) return false;

        var url = BuildManifestUrl();
        ModBase.Log($"[Update] 正在读取自定义更新源：{url}");

        string raw;
        try
        {
            raw = Client.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new UpdateSourceException($"自定义更新源读取失败：{ex.Message}", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new UpdateSourceException($"自定义更新源的清单不是合法 JSON：{ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var version = GetString(root, "version")
                          ?? throw new UpdateSourceException("自定义更新源的清单缺少 version 字段。");
            var fileUrl = GetString(root, "url")
                          ?? throw new UpdateSourceException("自定义更新源的清单缺少 url 字段。");

            _cache = new VersionDataModel
            {
                VersionName = NormalizeVersion(version),
                VersionCode = root.TryGetProperty("code", out var code) && code.TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : 0,
                Changelog = GetString(root, "changelog") ?? "",
                Sha256 = GetString(root, "sha256") ?? "",
                Source = fileUrl
            };
        }

        _cacheTime = DateTime.Now;
        ModBase.Log($"[Update] 自定义更新源返回版本 {_cache.VersionName}（code {_cache.VersionCode}）");
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        EnsureCache();
        return _cache;
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        EnsureCache();
        // 用 IsNewer 而不是直接比 code：清单里 code 可以不填，
        // 版本号才是合同里唯一保证存在的字段。
        return !UpdateVersion.IsNewer(_cache.VersionName, currentVersion.ToString());
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        // 自定义源只负责版本，不承载公告。
        return new VersionAnnouncementDataModel();
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        EnsureCache();

        var loaders = new List<ModLoader.LoaderBase>();
        var tempPath = $@"{ModBase.pathTemp}Cache\Update\Download\";

        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(
            Lang.Text("Update.Task.GetVersionInfo"), load =>
            {
                var url = _cache.Source;
                if (string.IsNullOrWhiteSpace(url))
                    throw new UpdateSourceException("自定义更新源没有给出下载地址。");

                var fileName = url[(url.LastIndexOf('/') + 1)..];
                if (fileName.Contains('?')) fileName = fileName[..fileName.IndexOf('?')];
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "PCL_Plus_Update.exe";
                tempPath += fileName;

                load.output = [new DownloadFile([url], tempPath)];
            }));

        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), []));

        loaders.Add(new ModLoader.LoaderTask<string, int>(
            Lang.Text("Update.Task.ApplyFile"), _ => ApplyDownloaded(tempPath, output)));

        return loaders;
    }

    /// <summary>
    ///     清单地址：填的是目录就补 <c>latest.json</c>，填的是具体文件就直接用。
    /// </summary>
    private static string BuildManifestUrl()
    {
        var raw = Config.Update.CustomSource.Trim();
        if (raw.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return raw;
        return raw.TrimEnd('/') + "/latest.json";
    }

    private static string GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    ///     清单里的版本号可能写成 <c>v4.0.5</c> 或 <c>4.0</c>（缺段），
    ///     统一成合法 SemVer 再参与比较，缺的段补零。
    /// </summary>
    private static string NormalizeVersion(string version)
    {
        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        var core = value;
        var suffix = "";
        var index = value.IndexOfAny(['-', '+']);
        if (index >= 0)
        {
            core = value[..index];
            suffix = value[index..];
        }

        var parts = core.Split('.');
        if (parts.Length is 0 or > 3 || !parts.All(x => x.Length > 0 && x.All(char.IsAsciiDigit)))
            throw new UpdateSourceException($"自定义更新源的版本号「{version}」不是合法的 SemVer。");

        while (parts.Length < 3) parts = [.. parts, "0"];
        var normalized = string.Join('.', parts) + suffix;

        if (!SemVer.TryParse(normalized, out _))
            throw new UpdateSourceException($"自定义更新源的版本号「{version}」不是合法的 SemVer。");

        return normalized;
    }

    private void EnsureCache()
    {
        if (_cache is null) RefreshCache();
        if (_cache is null) throw new UpdateSourceException("自定义更新源没有返回任何版本信息。");
    }

    /// <summary>
    ///     落地：zip 就解出里面的 exe，否则直接复制。与 GitHub 源同一套规则。
    /// </summary>
    private static void ApplyDownloaded(string path, string output)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs);
            var entry = zip.Entries
                            .FirstOrDefault(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception(Lang.Text("Update.Error.FileNotFound"));
            entry.ExtractToFile(output, true);
            return;
        }

        File.Copy(path, output, true);
    }
}
