// ============================================================================
// [新增文件] TerracottaModels.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本文件全部为新增代码，不修改启动器原有类型。
//       只放与 UI 无关的状态快照、状态解析与文案映射。
// ============================================================================

using System.Text.Json;

namespace PCL.Modules.Terracotta;

/// <summary>
/// 陶瓦联机状态快照。由后台轮询线程写入，UI 只读。
/// <para>[新增] 与 <c>Terracotta</c> 的 HTTP API 字段一一对应，不做任何 UI 处理。</para>
/// </summary>
public sealed record TerracottaSnapshot
{
    /// <summary>陶瓦联机组件是否已经成功启动并可访问。</summary>
    public bool Running { get; init; }

    /// <summary>陶瓦本地 HTTP API 端口，0 表示尚未启动。</summary>
    public int Port { get; init; }

    /// <summary>原始状态名：waiting / host-scanning / host-starting / host-ok / ...</summary>
    public string State { get; init; } = "waiting";

    /// <summary>当前房间邀请码，仅 host-ok 时有值。</summary>
    public string Room { get; init; } = "";

    /// <summary>访客可用的本地服务器地址，仅 guest-ok 时有值。</summary>
    public string Address { get; init; } = "";

    /// <summary>连接难度（EASIEST / SIMPLE / MEDIUM / TOUGH / UNKNOWN）。</summary>
    public string Difficulty { get; init; } = "";

    /// <summary>房间内玩家昵称列表。</summary>
    public IReadOnlyList<string> Players { get; init; } = Array.Empty<string>();

    /// <summary>面向用户展示的错误信息。</summary>
    public string? Error { get; init; }

    /// <summary>面向用户展示的进度提示（启动中、停止中等）。</summary>
    public string? Hint { get; init; }

    /// <summary>快照更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>单调递增的状态序号，用于丢弃过期轮询结果。</summary>
    public long Index { get; init; }

    /// <summary>空白快照。</summary>
    public static TerracottaSnapshot Empty { get; } = new();

    /// <summary>
    /// 把内部状态翻译成一行中文描述。
    /// </summary>
    public static string Describe(TerracottaSnapshot snapshot)
    {
        if (!snapshot.Running)
            return snapshot.Error ?? snapshot.Hint ?? "未启动";

        return snapshot.State switch
        {
            "waiting" => "空闲，等待操作",
            "host-scanning" => "正在扫描本机开放的 Minecraft 端口…",
            "host-starting" => string.IsNullOrEmpty(snapshot.Room)
                ? "正在创建房间…"
                : $"正在创建房间…（{snapshot.Room}）",
            "host-ok" => $"房间已创建：{snapshot.Room}（{snapshot.Players.Count} 人在线）",
            "guest-connecting" => "正在连接房间…",
            "guest-starting" => string.IsNullOrEmpty(snapshot.Difficulty)
                ? "正在建立联机通道…"
                : $"正在建立联机通道…（{snapshot.Difficulty}）",
            "guest-ok" => $"联机成功：{snapshot.Address}（{snapshot.Players.Count} 人在线）",
            "exception" => snapshot.Error ?? "陶瓦联机出现异常",
            _ => snapshot.State
        };
    }

    /// <summary>
    /// 把陶瓦 API 的异常编号翻译成中文。
    /// 编号含义与 <c>tools/t/web/_.html</c> 中的前端提示保持一致。
    /// </summary>
    public static string DescribeException(int type)
    {
        return type switch
        {
            0 => "加入房间失败：房间已关闭或网络不稳定",
            1 => "房间连接断开：房间已关闭或网络不稳定",
            2 => "加入房间失败：EasyTier 已崩溃，请向陶瓦开发者反馈",
            3 => "创建房间失败：EasyTier 已崩溃，请向陶瓦开发者反馈",
            4 => "房间已关闭：你已退出游戏存档，房间已自动关闭",
            5 => "协议错误：房主返回了无效数据，请向陶瓦开发者反馈",
            _ => "陶瓦联机出现未知异常"
        };
    }
}

/// <summary>
/// 陶瓦 <c>/state</c> 响应解析。
/// <para>[新增] 独立成类，便于在没有 WPF 环境时单独验证。</para>
/// </summary>
internal static class TerracottaStateParser
{
    public static TerracottaSnapshot Parse(JsonElement root, int port)
    {
        var state = ReadString(root, "state") ?? "waiting";
        var room = ReadString(root, "room") ?? "";
        var address = ReadString(root, "url") ?? "";
        var difficulty = ReadString(root, "difficulty") ?? "";
        var index = ReadInt64(root, "index");

        var players = new List<string>();
        if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
            foreach (var profile in profiles.EnumerateArray())
            {
                var name = ReadString(profile, "name");
                if (!string.IsNullOrWhiteSpace(name)) players.Add(name!);
            }

        string? error = null;
        if (state == "exception")
            error = TerracottaSnapshot.DescribeException((int)ReadInt64(root, "type"));

        return new TerracottaSnapshot
        {
            Running = true,
            Port = port,
            State = state,
            Room = room,
            Address = address,
            Difficulty = difficulty,
            Players = players,
            Error = error,
            Index = index,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    }
}
