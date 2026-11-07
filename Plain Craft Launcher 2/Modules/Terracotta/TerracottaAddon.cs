// ============================================================================
// [新增文件] TerracottaAddon.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本文件全部为新增代码，是插件的唯一入口。
//       - 默认关闭：只有 Config.Terracotta.Enabled 为 true 时才会启动进程/线程/HTTP 轮询。
//       - 不侵入启动流程：不拦截游戏启动、不修改 ModLaunch、不接管任何原有事件。
//       - 与 UI 解耦：只暴露状态快照和异步命令，设置页自行决定如何展示。
// ============================================================================

using System.IO;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Configuration;

namespace PCL.Modules.Terracotta;

/// <summary>命令执行结果，用于给设置页生成提示。</summary>
public sealed record TerracottaCommandResult(bool Success, string Message)
{
    public static TerracottaCommandResult Ok(string message) => new(true, message);
    public static TerracottaCommandResult Fail(string message) => new(false, message);
}

/// <summary>
/// 陶瓦联机插件入口。
/// <para>
/// 生命周期：<see cref="Initialize"/> 只注册配置观察者；用户打开总开关或点击“启动组件”后
/// 才真正拉起 terracotta.exe。停止时通过陶瓦的 <c>/panic?peaceful=true</c> 让它自行退出。
/// </para>
/// </summary>
public static class TerracottaAddon
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object SnapshotLock = new();

    private static TerracottaSnapshot _snapshot = TerracottaSnapshot.Empty;
    private static CancellationTokenSource? _pollSource;
    private static string? _portFile;
    private static int _port;
    private static int _pollGeneration;
    private static bool _initialized;
    private static bool _exitHooked;
    private static string _lastGuestAddress = "";

    /// <summary>日志回调，由宿主注入 <c>ModBase.Log</c>。</summary>
    public static Action<string, Exception?>? LogSink { get; set; }

    /// <summary>
    /// 联机成功后的服务器地址回调。由宿主决定是否写进当前实例配置。
    /// </summary>
    public static Action<string>? ServerAddressSink { get; set; }

    /// <summary>当前状态快照，可在任意线程读取。</summary>
    public static TerracottaSnapshot Snapshot
    {
        get
        {
            lock (SnapshotLock)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>陶瓦是否已经启动并可以访问。</summary>
    public static bool IsRunning => Snapshot.Running;

    #region 生命周期

    /// <summary>
    /// 在启动器初始化阶段调用一次（见 <c>Modules/Base/ModSetup.cs</c>）。
    /// 关闭状态下这里不会创建任何线程或进程。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // 总开关：关闭时立刻停止组件；打开时不自动拉起，除非用户同时打开了“自动启动”。
            Config.Terracotta.EnabledConfig.Observe(new ConfigObserver(ConfigEvent.Changed, _evt =>
            {
                _ = ApplyEnabledStateAsync();
            }));

            Config.Terracotta.AutoStartConfig.Observe(new ConfigObserver(ConfigEvent.Changed, _evt =>
            {
                if (Config.Terracotta.Enabled && Config.Terracotta.AutoStart && !IsRunning)
                    _ = StartAsync();
            }));

            // 退出启动器时的可选清理。默认关闭，避免影响其他正在使用陶瓦的启动器。
            TryHookExit();

            if (Config.Terracotta.Enabled && Config.Terracotta.AutoStart)
                _ = StartAsync();
        }
        catch (Exception ex)
        {
            Log("初始化失败，陶瓦联机功能不可用", ex);
        }
    }

    private static async Task ApplyEnabledStateAsync()
    {
        try
        {
            if (!Config.Terracotta.Enabled)
            {
                // 关闭开关只“摘掉”启动器这边的会话，不主动杀掉可能被其他启动器共享的陶瓦进程。
                // 需要真正退出陶瓦时请点设置页的「停止组件」。
                await StopSessionAsync("已关闭陶瓦联机").ConfigureAwait(false);
                return;
            }

            if (Config.Terracotta.AutoStart && !IsRunning) await StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("应用陶瓦联机开关时出错", ex);
        }
    }

    /// <summary>
    /// 尽量挂上 Application.Exit。Initialize 时 Application.Current 可能还没准备好，
    /// 因此启动组件时再重试一次。
    /// </summary>
    private static void TryHookExit()
    {
        if (_exitHooked) return;
        if (Application.Current is not { } application) return;

        application.Exit += (_, _) => OnApplicationExit();
        _exitHooked = true;
    }

    private static void OnApplicationExit()
    {
        if (!Config.Terracotta.StopOnExit || _port <= 0) return;

        try
        {
            // 退出阶段不能阻塞 UI 线程太久；最多等 2 秒，失败就让它继续运行。
            Task.Run(() => ShutdownAsync()).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Log("退出时关闭陶瓦联机失败", ex);
        }
    }

    #endregion

    #region 启动 / 停止

    /// <summary>启动陶瓦联机组件。已启动时直接返回成功。</summary>
    public static async Task<TerracottaCommandResult> StartAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Config.Terracotta.Enabled)
                return TerracottaCommandResult.Fail("请先在上方开启「启用陶瓦联机」。");

            if (IsRunning && _port > 0)
                return TerracottaCommandResult.Ok("陶瓦联机组件已经在运行。");

            // 某些启动时序下 Initialize 时 Application.Current 尚未就绪，这里补挂退出清理。
            TryHookExit();

            var executable = TerracottaProcess.DetectExecutable(Config.Terracotta.ExecutablePath);
            if (executable is null)
            {
                Publish(TerracottaSnapshot.Empty with
                {
                    Error = "未找到 terracotta.exe。请把它放到 PCL\\Terracotta\\ 下，或手动选择程序路径。",
                    UpdatedAt = DateTimeOffset.Now
                });
                return TerracottaCommandResult.Fail("未找到陶瓦联机程序。");
            }

            Publish(TerracottaSnapshot.Empty with
            {
                State = "starting",
                Hint = "正在启动陶瓦联机组件…",
                UpdatedAt = DateTimeOffset.Now
            });

            var launch = await TerracottaProcess.LaunchAsync(executable).ConfigureAwait(false);
            _port = launch.Port;
            _portFile = launch.PortFile;
            _lastGuestAddress = "";

            StartPolling(_port);
            Log($"已启动 {executable}，本地接口端口 {_port}", null);

            // 立刻取一次状态，避免界面停在上一步；失败也没关系，轮询会继续重试。
            try
            {
                Publish(await TerracottaApiClient.GetStateAsync(_port, CancellationToken.None)
                    .ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Log("启动后首次读取状态失败，等待轮询重试", ex);
            }

            return TerracottaCommandResult.Ok($"陶瓦联机组件已启动（本地端口 {_port}）。");
        }
        catch (Exception ex)
        {
            Publish(TerracottaSnapshot.Empty with
            {
                Error = "启动陶瓦联机失败：" + ex.Message,
                UpdatedAt = DateTimeOffset.Now
            });
            Log("启动陶瓦联机失败", ex);
            return TerracottaCommandResult.Fail("启动失败：" + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 只结束启动器这边的会话：停止轮询、清理端口文件，但保留陶瓦进程。
    /// <para>关闭总开关时使用，避免误杀其他启动器正在共享的陶瓦守护进程。</para>
    /// </summary>
    private static async Task StopSessionAsync(string reason)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopPolling();
            _port = 0;
            _lastGuestAddress = "";
            CleanupPortFile();
            Publish(TerracottaSnapshot.Empty with
            {
                UpdatedAt = DateTimeOffset.Now
            });
            Log(reason, null);
        }
        catch (Exception ex)
        {
            Log("关闭陶瓦联机会话失败", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>停止陶瓦联机组件。</summary>
    public static async Task<TerracottaCommandResult> ShutdownAsync(string reason = "已停止陶瓦联机")
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopPolling();

            var port = _port;
            _port = 0;
            _lastGuestAddress = "";

            if (port > 0)
                try
                {
                    await TerracottaApiClient.ShutdownAsync(port, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 组件可能已经自己退出（例如闲置 10 分钟后），这里不算失败。
                    Log("请求陶瓦退出失败，可能已经自行退出", ex);
                }

            CleanupPortFile();
            Publish(TerracottaSnapshot.Empty with
            {
                Hint = reason,
                UpdatedAt = DateTimeOffset.Now
            });
            Log(reason, null);
            return TerracottaCommandResult.Ok(reason);
        }
        catch (Exception ex)
        {
            Log("停止陶瓦联机失败", ex);
            return TerracottaCommandResult.Fail("停止失败：" + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>仅重置房间状态，不关闭组件。</summary>
    public static async Task<TerracottaCommandResult> ResetAsync()
    {
        if (!IsRunning || _port <= 0) return TerracottaCommandResult.Fail("陶瓦联机组件尚未启动。");

        try
        {
            var ok = await TerracottaApiClient.ResetAsync(_port, CancellationToken.None).ConfigureAwait(false);
            if (!ok) return TerracottaCommandResult.Fail("陶瓦拒绝了断开请求，请稍后重试。");

            Publish(TerracottaSnapshot.Empty with
            {
                Running = true,
                Port = _port,
                State = "waiting",
                UpdatedAt = DateTimeOffset.Now
            });
            return TerracottaCommandResult.Ok("已断开当前房间。");
        }
        catch (Exception ex)
        {
            Log("重置陶瓦状态失败", ex);
            return TerracottaCommandResult.Fail("断开失败：" + ex.Message);
        }
    }

    #endregion

    #region 房间操作

    /// <summary>扫描本机 Minecraft 端口并创建房间。</summary>
    public static async Task<TerracottaCommandResult> CreateRoomAsync()
    {
        if (!IsRunning || _port <= 0) return TerracottaCommandResult.Fail("请先启动陶瓦联机组件。");

        try
        {
            var player = (Config.Terracotta.PlayerName ?? "").Trim();
            var ok = await TerracottaApiClient.RequestHostAsync(_port,
                string.IsNullOrWhiteSpace(player) ? null : player, CancellationToken.None).ConfigureAwait(false);

            return ok
                ? TerracottaCommandResult.Ok("正在扫描本机 Minecraft 端口并创建房间，请稍候…")
                : TerracottaCommandResult.Fail("陶瓦拒绝了创建房间请求，请先打开一个单人存档。");
        }
        catch (Exception ex)
        {
            Log("创建陶瓦房间失败", ex);
            return TerracottaCommandResult.Fail("创建房间失败：" + ex.Message);
        }
    }

    /// <summary>使用邀请码加入房间。</summary>
    public static async Task<TerracottaCommandResult> JoinRoomAsync(string inviteCode)
    {
        if (!IsRunning || _port <= 0) return TerracottaCommandResult.Fail("请先启动陶瓦联机组件。");

        if (!TerracottaInviteCode.TryNormalize(inviteCode, out var normalized))
            return TerracottaCommandResult.Fail("邀请码格式不正确，应形如 U/XXXX-XXXX-XXXX-XXXX。");

        try
        {
            var player = (Config.Terracotta.PlayerName ?? "").Trim();
            var status = await TerracottaApiClient.RequestGuestAsync(_port, normalized,
                string.IsNullOrWhiteSpace(player) ? null : player, CancellationToken.None).ConfigureAwait(false);

            return status switch
            {
                System.Net.HttpStatusCode.OK => TerracottaCommandResult.Ok("正在连接房间，请稍候…"),
                System.Net.HttpStatusCode.BadRequest => TerracottaCommandResult.Fail("邀请码格式错误，或该房间已失效。"),
                _ => TerracottaCommandResult.Fail($"加入房间失败（HTTP {(int)status}）。")
            };
        }
        catch (Exception ex)
        {
            Log("加入陶瓦房间失败", ex);
            return TerracottaCommandResult.Fail("加入房间失败：" + ex.Message);
        }
    }

    #endregion

    #region 状态轮询

    private static void StartPolling(int port)
    {
        StopPolling();

        var generation = ++_pollGeneration;
        _pollSource = new CancellationTokenSource();
        var token = _pollSource.Token;
        _ = Task.Run(() => PollLoopAsync(port, generation, token), token);
    }

    private static void StopPolling()
    {
        _pollGeneration++;
        var source = _pollSource;
        _pollSource = null;

        try
        {
            source?.Cancel();
            source?.Dispose();
        }
        catch (Exception)
        {
            // 取消阶段不抛异常。
        }
    }

    private static async Task PollLoopAsync(int port, int generation, CancellationToken token)
    {
        var failures = 0;

        while (!token.IsCancellationRequested && generation == _pollGeneration)
        {
            try
            {
                var snapshot = await TerracottaApiClient.GetStateAsync(port, token).ConfigureAwait(false);
                failures = 0;

                if (generation != _pollGeneration) return;
                Publish(snapshot with { Running = true, Port = port, Hint = null });
                NotifyGuestReady(snapshot);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                if (failures >= 3)
                {
                    if (generation != _pollGeneration) return;
                    Publish(TerracottaSnapshot.Empty with
                    {
                        Error = "无法连接陶瓦联机组件，它可能已经退出。",
                        UpdatedAt = DateTimeOffset.Now
                    });
                    Log("状态轮询连续失败，停止轮询", ex);
                    return;
                }
            }

            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 只在“刚进入 guest-ok”时回填一次服务器地址，避免每秒覆盖用户手动修改。
    /// </summary>
    private static void NotifyGuestReady(TerracottaSnapshot snapshot)
    {
        if (snapshot.State != "guest-ok" || string.IsNullOrWhiteSpace(snapshot.Address))
        {
            _lastGuestAddress = "";
            return;
        }

        if (string.Equals(_lastGuestAddress, snapshot.Address, StringComparison.Ordinal)) return;
        _lastGuestAddress = snapshot.Address;

        if (!Config.Terracotta.AutoFillServer) return;

        try
        {
            ServerAddressSink?.Invoke(snapshot.Address);
        }
        catch (Exception ex)
        {
            Log("回填服务器地址失败", ex);
        }
    }

    private static void Publish(TerracottaSnapshot snapshot)
    {
        lock (SnapshotLock)
        {
            // 轮询可能比新状态慢半拍，序号更小的旧快照直接丢弃。
            if (snapshot.Index > 0 && snapshot.Index < _snapshot.Index && snapshot.Running == _snapshot.Running)
                return;
            _snapshot = snapshot;
        }
    }

    private static void CleanupPortFile()
    {
        var path = _portFile;
        _portFile = null;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件，删不掉也不影响功能。
        }
    }

    #endregion

    #region 辅助

    /// <summary>查找可执行文件，供设置页的“自动检测”按钮调用。</summary>
    public static string? DetectExecutable()
    {
        try
        {
            return TerracottaProcess.DetectExecutable(Config.Terracotta.ExecutablePath);
        }
        catch (Exception ex)
        {
            Log("自动检测 terracotta.exe 失败", ex);
            return null;
        }
    }

    private static void Log(string message, Exception? exception)
    {
        try
        {
            LogSink?.Invoke("[Terracotta] " + message, exception);
        }
        catch (Exception)
        {
            // 日志回调本身不能影响插件运行。
        }
    }

    #endregion
}
