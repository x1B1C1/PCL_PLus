using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using PCL.Core.App.IoC;
using PCL.Core.IO;
using PCL.Core.Utils;

namespace PCL.Core.App.Essentials;

[LifecycleService(LifecycleState.BeforeLoading, Priority = -2134567890)]
[LifecycleScope("single-instance", "单例", false)]
public sealed partial class SingleInstanceService
{
    private static FileStream? _lockStream;
    private static readonly string _LockFilePath = Path.Combine(Paths.SharedLocalData, SingleInstanceLock.FileName);

    /// <summary>
    /// 转交请求的重试次数与间隔。<br/>
    /// 已运行的实例在 <see cref="LifecycleState.Loaded"/> 才拉起 RPC 管道，比它拿到锁晚；
    /// 因此在它还没启动完时连不上属于正常情况，多等一会儿即可，不要直接判定为失败。
    /// </summary>
    private const int RpcRetryCount = 6;
    private const int RpcRetryDelayMs = 500;

    private static void _TryRpc(int processId, string content)
    {
        var pipeName = $"{RpcService.PipePrefix}@{processId}";
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(1000);
        using var sw = new StreamWriter(pipe, PipeComm.PipeEncoding);
        sw.WriteLine(content);
        sw.Write(PipeComm.PipeEndingChar);
        sw.Flush();
    }

    /// <summary>
    /// 把本次启动请求转交给已运行的实例。成功返回 <see langword="true"/>。
    /// </summary>
    private static bool _TryHandoff(int processId)
    {
        for (var attempt = 1; attempt <= RpcRetryCount; attempt++)
        {
            try
            {
                _TryRpc(processId,
                    "REQ cli\n" + JsonSerializer.Serialize(StartupService.UnhandledCommands, JsonCompat.SerializerOptions));
                _TryRpc(processId, "REQ activate");
                return true;
            }
            catch (Exception ex)
            {
                if (attempt < RpcRetryCount)
                {
                    Thread.Sleep(RpcRetryDelayMs);
                    continue;
                }

                Context.Warn($"无法唤醒已运行的实例（PID {processId}），它可能还在启动中", ex);
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 转交失败时给用户一个明确提示。<br/>
    /// 以前这里会直接静默退出，用户看到的现象就是「双击了但什么都没发生」，日志里也只有一句
    /// 「发现重复实例」，很难判断到底出了什么事。
    /// </summary>
    private static void _NotifyHandoffFailure(int processId)
    {
        try
        {
            MessageBox.Show(
                $"PCL 已经在运行（进程 {processId}），但这次没能唤醒它的窗口。\n\n" +
                "如果看不到它的界面，可以在任务管理器里结束这个进程，再重新打开 PCL。",
                "PCL_Plus", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Context.Warn("无法弹出提示窗口", ex);
        }
    }

    /// <summary>
    /// 诊断：锁文件创建被拒时，把当前进程能看到的环境记录一笔。
    /// </summary>
    private static void _DiagnoseLockAccess()
    {
        try
        {
            Context.Debug($"诊断：用户={Environment.UserName}，身份={WindowsIdentity.GetCurrent().Name}，" +
                          $"LOCALAPPDATA={Environment.GetEnvironmentVariable("LOCALAPPDATA")}，" +
                          $"目录存在={Directory.Exists(Paths.SharedLocalData)}，" +
                          $"目录可写={TryProbe(Paths.SharedLocalData)}，TEMP 可写={TryProbe(Path.GetTempPath())}");
        }
        catch (Exception ex)
        {
            Context.Debug("诊断失败：" + ex.Message);
        }

        static string TryProbe(string dir)
        {
            try
            {
                var path = Path.Combine(dir, "pcl-write-probe.tmp");
                using (var _ = File.Create(path)) { }
                File.Delete(path);
                return "是";
            }
            catch (Exception ex)
            {
                return $"否（{ex.GetType().Name}: {ex.Message}）";
            }
        }
    }

    [LifecycleStart]
    private static void _Start()
    {
        _lockStream = SingleInstanceLock.TryAcquire(_LockFilePath, Basics.CurrentProcessId, out var firstError);
        if (_lockStream is not null)
        {
            Context.Debug("未发现重复实例，正在向单例锁写入信息");
            return;
        }

        Context.Debug($"创建单例锁失败：{_LockFilePath}（{firstError?.GetType().Name}: {firstError?.Message}）");
        _DiagnoseLockAccess();

        var owner = SingleInstanceLock.ReadOwner(_LockFilePath);

        // 锁文件被占着，但持有者已经不在了：多半是别的东西（杀软扫描、资源管理器预览）
        // 短暂占用过这个文件，或者上次的进程没走正常退出流程。这时应当接管，而不是退出。
        if (owner is null || !SingleInstanceLock.IsAlive(owner.Value))
        {
            Context.Warn($"单例锁由 PID {owner?.ToString() ?? "(无法识别)"} 持有，但该进程已不在运行，正在接管");
            var deleted = SingleInstanceLock.TryDelete(_LockFilePath);
            _lockStream = SingleInstanceLock.TryAcquire(_LockFilePath, Basics.CurrentProcessId, out var retryError);
            if (_lockStream is not null)
            {
                Context.Info("已接管单例锁，继续启动");
                return;
            }

            Context.Warn(
                $"单例锁无法接管（删除{(deleted ? "成功" : "失败")}）：{retryError?.GetType().Name}: {retryError?.Message}。" +
                "本次启动将不使用单例保护，直接继续");
            // 锁不可用不该影响启动本身：最坏结果只是允许同时开多个窗口，
            // 比「双击了却什么都没有」好得多。
            return;
        }

        var pid = owner.Value;
        Context.Info($"已有实例在运行（PID {pid}），正在把本次启动请求转交给它");

        if (_TryHandoff(pid))
        {
            Context.Info("已转交启动请求，本进程退出");
            Context.RequestExit(0);
            return;
        }

        _NotifyHandoffFailure(pid);
        Context.RequestExit(1);
    }

    [LifecycleStop]
    private static void _Stop()
    {
        if (_lockStream is null) return;
        Context.Debug("正在删除单例锁");
        SingleInstanceLock.Release(_lockStream, _LockFilePath, Basics.CurrentProcessId);
        _lockStream = null;
    }
}
