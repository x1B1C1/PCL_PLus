// ============================================================================
// [新增文件] TerracottaProcess.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本文件全部为新增代码。负责查找 terracotta.exe 并用 --hmcl 模式拿到端口。
//       只读本地文件与启动本地进程，不访问网络。
// ============================================================================

using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PCL.Modules.Terracotta;

/// <summary>
/// 陶瓦启动结果。
/// </summary>
internal sealed record TerracottaLaunchResult(int Port, string PortFile);

/// <summary>
/// 陶瓦可执行文件的发现与启动。
/// <para>
/// 采用陶瓦为 HMCL 预留的 <c>--hmcl &lt;端口文件&gt;</c> 模式：由陶瓦把实际端口写进文件，
/// 启动器读取后即可访问 <c>http://127.0.0.1:&lt;port&gt;/</c>，因此不依赖固定端口。
/// </para>
/// </summary>
internal static class TerracottaProcess
{
    private const string ExeName = "terracotta.exe";

    /// <summary>
    /// 查找可执行文件：优先使用用户显式配置的路径，其次是启动器目录下的常见位置，最后搜索 PATH。
    /// </summary>
    public static string? DetectExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = ResolveCandidate(configuredPath.Trim().Trim('"'));
            if (configured is not null) return configured;
        }

        var roots = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        var relativeCandidates = new[]
        {
            Path.Combine("PCL", "Terracotta", ExeName),
            Path.Combine("PCL", "Terracotta", "Terracotta.exe"),
            Path.Combine("PCL", ExeName),
            Path.Combine("Terracotta", ExeName),
            ExeName,
            // 方便从源码目录直接调试：tools/t 编译产物的默认位置
            Path.Combine("tools", "t", "target", "release", ExeName),
            Path.Combine("tools", "t", "target", "debug", ExeName)
        };

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        foreach (var relative in relativeCandidates)
        {
            var candidate = ResolveCandidate(Path.Combine(root, relative));
            if (candidate is not null) return candidate;
        }

        foreach (var directory in EnumeratePathDirectories())
        {
            var candidate = ResolveCandidate(Path.Combine(directory, ExeName));
            if (candidate is not null) return candidate;
        }

        // [新增] 以上都没找到时，释放随启动器内置的陶瓦运行文件。
        // 这样用户不需要手动准备 terracotta.exe，也能直接创建联机房间。
        var bundled = TerracottaBundledRuntime.TryEnsureExtracted();
        if (bundled is not null) return bundled;

        return null;
    }

    /// <summary>
    /// 启动陶瓦并等待端口文件。失败时抛异常，由调用方转成用户提示。
    /// </summary>
    public static async Task<TerracottaLaunchResult> LaunchAsync(string executablePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var workDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(workDirectory))
            throw new InvalidOperationException("无法确定 terracotta.exe 所在目录。");

        var portFile = CreatePortFilePath();
        TryDelete(portFile);

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--hmcl");
        startInfo.ArgumentList.Add(portFile);

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法创建陶瓦联机进程。");
        using var processHandle = process;

        var startedAt = DateTime.UtcNow;
        var deadline = startedAt + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(portFile))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(portFile, cancellationToken).ConfigureAwait(false);
                    if (TryParsePort(text, out var port)) return new TerracottaLaunchResult(port, portFile);
                }
                catch (IOException)
                {
                    // 陶瓦用「先写 .tmp 再改名」的方式落盘，读得太早会碰到瞬时状态，下一轮再试。
                }
                catch (UnauthorizedAccessException)
                {
                    // 文件刚建出来时偶发占用，下一轮再试。
                }
                catch (JsonException)
                {
                    // 内容还没写完，下一轮再试。
                }
            }

            if (process.HasExited && DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(2))
            {
                // 进程已经退出且没有端口文件，通常意味着程序缺失依赖或参数不被支持。
                throw new InvalidOperationException(
                    $"陶瓦联机进程提前退出（退出码 {SafeExitCode(process)}），请确认 terracotta.exe 可独立运行。");
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("等待陶瓦联机启动超时。请确认程序完整，并检查杀毒软件是否拦截。");
    }

    /// <summary>端口文件放在系统临时目录，避免安装目录不可写。</summary>
    private static string CreatePortFilePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PCL-Terracotta");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"port-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
    }

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("port", out var value)) return false;
        if (!value.TryGetInt32(out var parsed) || parsed is <= 0 or > 65535) return false;
        port = parsed;
        return true;
    }

    private static string? ResolveCandidate(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var lower = Path.Combine(path, ExeName);
                var upper = Path.Combine(path, "Terracotta.exe");
                path = File.Exists(lower) ? lower : upper;
            }

            if (!File.Exists(path)) return null;
            if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            yield return directory.Trim().Trim('"');
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 删不掉就算了，使用独立的 GUID 文件名，不会互相覆盖。
        }
    }
}
