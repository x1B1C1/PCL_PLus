// ============================================================================
// [新增文件] TerracottaRuntimeCheck.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：联机要求参与双方都具备陶瓦运行文件。本文件负责在启动组件、
//       创建房间、加入房间之前做一次自检：找不到就释放随包内置的运行时，
//       释放出来的文件损坏或被拦杀就删掉重来，最后把结果如实报给界面。
//       对方的机器我们无法直接操作，能保证的是「我方一定可用」。
// ============================================================================

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;

namespace PCL.Modules.Terracotta;

/// <summary>一次运行时自检的结果。</summary>
/// <param name="Ready">是否已经可以启动陶瓦。</param>
/// <param name="Path">可执行文件的绝对路径；不可用时为 null。</param>
/// <param name="Message">给用户看的一句话。</param>
/// <param name="InstalledNow">本次是否执行了安装或修复动作。</param>
public sealed record TerracottaRuntimeStatus(bool Ready, string? Path, string Message, bool InstalledNow);

/// <summary>
/// 陶瓦运行文件的检查与自动安装。
/// <para>安装来源只有一处：启动器自带的嵌入资源（0.4.2 原版二进制）。</para>
/// </summary>
internal static class TerracottaRuntimeCheck
{
    /// <summary>0.4.2 的单文件约 9 MB，小于这个数基本可以判定是被截断或被杀软替换成了占位文件。</summary>
    private const long MinimumSize = 512 * 1024;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// 确保本机存在可用的 terracotta.exe。
    /// </summary>
    /// <param name="repair">校验不通过时是否删除并重新释放内置文件。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task<TerracottaRuntimeStatus> EnsureAsync(bool repair = true,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 第一次机会：用户指定路径、启动器目录、PATH、内置目录，全都由 DetectExecutable 负责。
            var candidate = TerracottaProcess.DetectExecutable(Config.Terracotta.ExecutablePath);
            if (candidate is not null && LooksUsable(candidate))
                return new TerracottaRuntimeStatus(true, candidate, "陶瓦联机运行文件已就绪。", false);

            if (!repair)
                return new TerracottaRuntimeStatus(false, null,
                    candidate is null
                        ? "未找到 terracotta.exe。"
                        : "找到的 terracotta.exe 不完整，可能被杀毒软件拦截或复制中断。", false);

            // 第二次机会：随包内置的那份，本地释放、离线也能用，先保证「点了就能用」。
            if (candidate is not null)
            {
                Log($"已存在的运行文件校验失败，挪走：{candidate}（{SafeLength(candidate)} 字节）");
                TryDelete(candidate);
            }

            var extracted = TerracottaBundledRuntime.TryEnsureExtracted();
            if (extracted is not null && LooksUsable(extracted))
            {
                Log($"已自动安装陶瓦运行文件（随包内置）：{extracted}");
                // 内置的版本可能比 GitHub 上的旧，后台悄悄拉一次新的；
                // 拉下来会落在 PCL\Terracotta\ 下，下次查找时优先级比内置目录高。
                _ = Task.Run(() => TerracottaDownloader.TryDownloadAsync(CancellationToken.None));
                return new TerracottaRuntimeStatus(true, extracted, "已自动安装陶瓦联机运行文件。", true);
            }

            // 第三次机会：内置资源缺失（例如裁剪构建）时只能真去下载。
            var downloaded = await TerracottaDownloader.TryDownloadAsync(cancellationToken).ConfigureAwait(false);
            if (downloaded is not null && LooksUsable(downloaded))
            {
                Log($"已从 GitHub 下载陶瓦运行文件：{downloaded}");
                return new TerracottaRuntimeStatus(true, downloaded, "已自动下载陶瓦联机运行文件。", true);
            }

            // 第四次机会：内置与下载都不行，再找一次非内置位置。
            candidate = TerracottaProcess.DetectExecutable(null);
            if (candidate is not null && LooksUsable(candidate))
                return new TerracottaRuntimeStatus(true, candidate, "陶瓦联机运行文件已就绪。", true);

            return new TerracottaRuntimeStatus(false, null,
                "自动安装失败：启动器内置的那份不可用，从 GitHub 下载也没成功。" +
                "请检查网络与杀毒软件是否拦截了 PCL\\Terracotta 目录，或手动指定 terracotta.exe。", false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("检查陶瓦运行文件时出错", ex);
            return new TerracottaRuntimeStatus(false, null, "检查运行文件失败：" + ex.Message, false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>同步版本，供只做展示的调用方使用（不做修复）。</summary>
    public static TerracottaRuntimeStatus Peek()
    {
        var candidate = TerracottaProcess.DetectExecutable(Config.Terracotta.ExecutablePath);
        if (candidate is not null && LooksUsable(candidate))
            return new TerracottaRuntimeStatus(true, candidate, "陶瓦联机运行文件已就绪。", false);
        return new TerracottaRuntimeStatus(false, candidate,
            candidate is null ? "尚未安装陶瓦联机运行文件。" : "运行文件不完整，需要重新安装。", false);
    }

    /// <summary>文件存在、大小合理、且开头是 MZ —— PE 可执行文件的最小特征。</summary>
    private static bool LooksUsable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinimumSize) return false;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[2];
            return stream.Read(header, 0, 2) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
        }
        catch (Exception ex)
        {
            Log("校验运行文件失败：" + path, ex);
            return false;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log("删除损坏的运行文件失败：" + path, ex);
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
