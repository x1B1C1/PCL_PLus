using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Updates;

namespace PCL.Modules.Updates;

/// <summary>
/// 「恢复旧版」：把当前这份测试版换回正式线最新一版。
/// <para>
/// 直接覆盖运行中的 exe 会失败，所以先下载到临时目录，再交给一个独立的 cmd：
/// 等本进程退出 → 覆盖 → 启动 → 删掉自己。调用方只要负责退出。
/// </para>
/// </summary>
internal static class LegacyRestore
{
    private const string TempFolderName = "PCL_Plus_Restore";

    /// <summary>打算恢复到哪个版本，用于界面展示。</summary>
    public static string TargetVersion { get; private set; } = "";

    /// <summary>
    /// 查正式线最新版本并下载它的 exe，成功后启动替换脚本。
    /// 返回说明文字；失败时调用方直接展示，不必区分异常类型。
    /// </summary>
    public static async Task<(bool Ok, string Message)> RunAsync(CancellationToken cancellationToken = default)
    {
        string folder = Path.Combine(Path.GetTempPath(), TempFolderName);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            return (false, "创建临时目录失败：" + ex.Message);
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL_Plus/" + ModBase.versionBaseName);

        UpdateTarget target;
        try
        {
            target = await FindStableAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, "查询正式版失败：" + ex.Message);
        }

        if (target.AssetUrl.Length == 0) return (false, "正式版没有可用的 exe 资产。");
        TargetVersion = target.Version;

        string fileName = "PCL_Plus_Release_" + target.Version + ".exe";
        string downloaded = Path.Combine(folder, fileName);
        try
        {
            using var response = await client.GetAsync(target.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(downloaded, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, "下载正式版失败：" + ex.Message);
        }

        long length = new FileInfo(downloaded).Length;
        if (length < 1024 * 1024) return (false, "下载到的文件不完整（" + length + " 字节）。");

        string? current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current)) return (false, "找不到当前启动器的路径。");

        try
        {
            StartSwapper(downloaded, current!, folder);
        }
        catch (Exception ex)
        {
            return (false, "启动替换脚本失败：" + ex.Message);
        }

        return (true, "正在恢复到 " + target.Version + "，启动器会自动重启。");
    }

    /// <summary>从正式线里挑出版本号最高的一版，并取出它的 exe 下载地址。</summary>
    private static async Task<UpdateTarget> FindStableAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ProjectLinks.ReleasesApi, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var candidates = new List<UpdateTarget>();
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            string tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            if (tag.Length == 0) continue;
            string version = Normalize(tag);

            string url = "";
            if (release.TryGetProperty("assets", out var assets))
            {
                var names = assets.EnumerateArray()
                    .Select(asset => new
                    {
                        Name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "",
                        Url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() ?? "" : ""
                    })
                    .Where(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // 优先发布线专用名，其次带版本号的留档名，最后任何 exe
                url = names.FirstOrDefault(asset => asset.Name == "PCL_Plus_Release_x64.exe")?.Url
                      ?? names.FirstOrDefault(asset => asset.Name.StartsWith("PCL_Plus_v", StringComparison.OrdinalIgnoreCase))?.Url
                      ?? names.FirstOrDefault(asset => asset.Name.StartsWith("PCL_Plus", StringComparison.OrdinalIgnoreCase))?.Url
                      ?? names.FirstOrDefault()?.Url
                      ?? "";
            }

            if (url.Length == 0) continue;
            candidates.Add(new UpdateTarget(version, url));
        }

        if (candidates.Count == 0) throw new InvalidOperationException("没有找到任何正式版。");

        // 按版本号排序，取最大的一版
        UpdateTarget best = candidates[0];
        foreach (UpdateTarget candidate in candidates.Skip(1))
        {
            if (UpdateVersion.IsNewer(candidate.Version, best.Version)) best = candidate;
        }

        return best;
    }

    /// <summary>把 <c>v4.0</c> 这种 tag 归一成 <c>4.0</c>，去掉测试线后缀。</summary>
    private static string Normalize(string tag)
    {
        string value = tag.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        return value;
    }

    /// <summary>写一个等待本进程退出的批处理，由它完成覆盖与重启。</summary>
    private static void StartSwapper(string downloaded, string currentExe, string folder)
    {
        string script = Path.Combine(folder, "restore.cmd");
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("chcp 65001 >nul");
        builder.AppendLine("timeout /t 4 /nobreak >nul");
        builder.AppendLine("copy /y \"" + downloaded + "\" \"" + currentExe + "\" >nul 2>&1");
        builder.AppendLine("start \"\" \"" + currentExe + "\"");
        builder.AppendLine("timeout /t 2 /nobreak >nul");
        builder.AppendLine("del \"" + downloaded + "\" >nul 2>&1");
        builder.AppendLine("del \"%~f0\" >nul 2>&1");
        File.WriteAllText(script, builder.ToString(), new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo(script)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            WorkingDirectory = folder
        };
        Process.Start(startInfo);
    }

    private sealed record UpdateTarget(string Version, string AssetUrl);
}
