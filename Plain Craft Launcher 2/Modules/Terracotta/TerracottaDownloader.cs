// ============================================================================
// [新增文件] TerracottaDownloader.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本机没有可用的 terracotta.exe 时，先从 GitHub 下载官方发布包并解出
//       Windows x64 版本，解出来的文件落到 PCL\Terracotta\ 下，和手动放置的
//       位置一致，后续 DetectExecutable 能直接找到。
//       下载失败（断网、被墙、GitHub 挂了）由调用方退回随包内置的那一份，
//       所以这里只负责「尽力而为」，不抛异常。
// ============================================================================

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;

namespace PCL.Modules.Terracotta;

/// <summary>
/// 从 GitHub Release 下载并解出 Windows 版陶瓦运行文件。
/// </summary>
internal static class TerracottaDownloader
{
    private const string ApiUrl = "https://api.github.com/repos/burningtnt/Terracotta/releases/latest";

    /// <summary>官方给 Windows 打的包名固定是 <c>terracotta-&lt;版本&gt;-windows-x86_64-pkg.tar.gz</c>。</summary>
    private const string AssetSuffix = "windows-x86_64-pkg.tar.gz";

    private const string ExecutableName = "terracotta.exe";

    /// <summary>下载落地目录：<c>&lt;启动器目录&gt;\PCL\Terracotta\</c>，与手动放置的位置相同。</summary>
    public static string TargetDirectory => Path.Combine(Paths.Data, "Terracotta");

    /// <summary>
    /// 下载最新版并解出运行文件。任何一步失败都返回 <see langword="null" />，由调用方兜底。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task<string?> TryDownloadAsync(CancellationToken cancellationToken = default)
    {
        string? package = null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL_Plus/" + ModBase.versionBaseName);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var assetUrl = await FindAssetUrlAsync(client, cancellationToken).ConfigureAwait(false);
            if (assetUrl is null)
            {
                Log("GitHub 上没有找到 Windows x64 发布包。");
                return null;
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), "PCL_Terracotta");
            Directory.CreateDirectory(tempDirectory);
            package = Path.Combine(tempDirectory, "terracotta-pkg.tar.gz");

            Log("开始下载陶瓦运行文件：" + assetUrl);
            using (var response = await client
                       .GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var destination = File.Create(package);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(TargetDirectory);
            Extract(package);

            var executable = Path.Combine(TargetDirectory, ExecutableName);
            if (!File.Exists(executable))
            {
                Log("发布包里没有 terracotta.exe。");
                return null;
            }

            Log("已下载并解出陶瓦运行文件：" + executable);
            return executable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("下载陶瓦运行文件失败", ex);
            return null;
        }
        finally
        {
            if (package is not null)
            {
                try
                {
                    if (File.Exists(package)) File.Delete(package);
                }
                catch (Exception)
                {
                    // 临时文件删不掉不影响主流程，下次覆盖即可。
                }
            }
        }
    }

    /// <summary>在最新一版 Release 的资产里挑出 Windows x64 包。</summary>
    private static async Task<string?> FindAssetUrlAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var json = await client.GetStringAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
        var assets = JsonNode.Parse(json)?["assets"]?.AsArray();
        if (assets is null) return null;

        foreach (var asset in assets)
        {
            var name = (string?)asset?["name"];
            if (name is null || !name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            return (string?)asset?["browser_download_url"];
        }

        return null;
    }

    /// <summary>解 tar.gz，只取运行文件和 VC 运行库，其余（文档、示例）跳过。</summary>
    private static void Extract(string package)
    {
        using var file = File.OpenRead(package);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;

            var name = Path.GetFileName(entry.Name);
            if (string.IsNullOrEmpty(name)) continue;

            var keep = name.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase);
            if (!keep) continue;

            entry.ExtractToFile(Path.Combine(TargetDirectory, name), true);
        }
    }

    private static void Log(string message, Exception? exception = null)
    {
        if (exception is null)
            ModBase.Log("[Terracotta] " + message, ModBase.LogLevel.Debug);
        else
            ModBase.Log(exception, "[Terracotta] " + message);
    }
}
