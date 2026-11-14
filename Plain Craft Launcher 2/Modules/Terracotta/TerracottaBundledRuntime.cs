// ============================================================================
// [新增文件] TerracottaBundledRuntime.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本文件全部为新增代码。把未经修改的陶瓦 0.4.2 Windows x64 发行版
//       作为嵌入资源随启动器一起分发，首次使用时释放到本机目录，
//       让「创建联机房间」无需用户手动准备 terracotta.exe。
//       符合陶瓦 AGPL 的例外条款：仅打包未修改的二进制，不静态/动态链接其代码。
// ============================================================================

using System.IO;
using System.Reflection;

namespace PCL.Modules.Terracotta;

/// <summary>
/// 内置陶瓦运行文件的释放器。
/// </summary>
internal static class TerracottaBundledRuntime
{
    private const string Version = "0.4.2";
    private const string ExeResource = "PCL.Terracotta.Runtime.terracotta.exe";
    private const string CrtResource = "PCL.Terracotta.Runtime.VCRUNTIME140.DLL";

    private static readonly object Gate = new();
    private static string? _cachedPath;

    /// <summary>
    /// 确保内置的 terracotta.exe 与 VCRUNTIME140.DLL 已释放到本机，返回 exe 路径。
    /// 失败返回 null，不影响启动器其他功能。
    /// </summary>
    public static string? TryEnsureExtracted()
    {
        lock (Gate)
        {
            if (_cachedPath is not null && File.Exists(_cachedPath)) return _cachedPath;

            try
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(root))
                    root = Path.GetTempPath();

                var candidates = new[]
                {
                    Path.Combine(root, "PCL", "Terracotta", Version),
                    Path.Combine(Path.GetTempPath(), "PCL", "Terracotta", Version)
                };

                foreach (var directory in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        var exePath = Path.Combine(directory, "terracotta.exe");
                        var crtPath = Path.Combine(directory, "VCRUNTIME140.DLL");

                        ExtractIfNeeded(ExeResource, exePath);
                        ExtractIfNeeded(CrtResource, crtPath);

                        if (File.Exists(exePath) && File.Exists(crtPath))
                        {
                            _cachedPath = exePath;
                            return exePath;
                        }
                    }
                    catch (Exception)
                    {
                        // 当前目录不可写，尝试下一个候选目录。
                    }
                }

                return null;
            }
            catch (Exception)
            {
                // 资源缺失或全部目录不可写时静默失败，由调用方给出提示。
                return null;
            }
        }
    }

    private static void ExtractIfNeeded(string resourceName, string destination)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null) return;

        // 已存在且大小一致就跳过，避免每次启动重复写 9 MB。
        if (File.Exists(destination) && new FileInfo(destination).Length == resource.Length) return;

        try
        {
            if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
        }
        catch (Exception)
        {
            // 忽略只读属性设置失败，下面直接覆盖。
        }

        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        resource.CopyTo(output);
        output.Flush();
    }
}
