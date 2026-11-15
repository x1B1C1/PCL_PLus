// ============================================================================
// [新增文件] TerracottaApiClient.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本文件全部为新增代码。只通过 127.0.0.1 上的本地 HTTP API 与陶瓦通信，
//       不访问任何外部网络，也不依赖启动器 UI。
// ============================================================================

using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PCL.Modules.Terracotta;

/// <summary>
/// 陶瓦联机本地 HTTP API 的最小客户端。
/// <para>
/// 接口来自 <c>tools/t/src/server/states.rs</c> 与 <c>tools/t/src/server/mod.rs</c>：
/// <c>GET /</c>、<c>GET /state</c>、<c>GET /state/scanning</c>、
/// <c>GET /state/guesting</c>、<c>GET /state/ide</c>。
/// </para>
/// <para>[新增] 全部请求固定 127.0.0.1，显式禁用代理，避免被启动器的网络代理配置带偏。</para>
/// </summary>
internal static class TerracottaApiClient
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string BaseUrl(int port) => $"http://127.0.0.1:{port}";

    /// <summary>读取当前状态。</summary>
    public static async Task<TerracottaSnapshot> GetStateAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken, TimeSpan.FromSeconds(2));
        using var response = await Client.GetAsync($"{BaseUrl(port)}/state",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return TerracottaStateParser.Parse(document.RootElement, port);
    }

    /// <summary>请求陶瓦扫描本机端口并创建房间。</summary>
    public static async Task<bool> RequestHostAsync(int port, string? player, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl(port)}/state/scanning";
        if (!string.IsNullOrWhiteSpace(player)) url += "?player=" + Uri.EscapeDataString(player);
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>请求加入指定房间，返回 HTTP 状态码（400 表示邀请码无效）。</summary>
    public static async Task<HttpStatusCode> RequestGuestAsync(int port, string code, string? player,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl(port)}/state/guesting?room=" + Uri.EscapeDataString(code);
        if (!string.IsNullOrWhiteSpace(player)) url += "&player=" + Uri.EscapeDataString(player);
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    /// <summary>把陶瓦重置回空闲状态（不影响已经建立的操作系统网络）。</summary>
    public static async Task<bool> ResetAsync(int port, CancellationToken cancellationToken)
    {
        using var response = await GetAsync($"{BaseUrl(port)}/state/ide", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>请求陶瓦进程正常退出。失败不影响调用方。</summary>
    public static async Task ShutdownAsync(int port, CancellationToken cancellationToken)
    {
        using var response = await GetAsync($"{BaseUrl(port)}/panic?peaceful=true", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken, TimeSpan.FromSeconds(6));
        return await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }
}
