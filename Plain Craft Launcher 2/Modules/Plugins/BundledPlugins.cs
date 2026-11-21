using System;
using System.IO;
using System.IO.Compression;
using PCL.Core.App;

namespace PCL.Modules.Plugins;

/// <summary>
///     随主程序内置的插件。启动时把嵌入的 <c>.pclplugin</c> 解压到
///     <c>PCL\Plugins\&lt;id&gt;\</c>，之后由插件运行时按普通本地插件加载，
///     与用户自己安装的插件走同一套校验、依赖与启停逻辑。
/// </summary>
internal static class BundledPlugins
{
    private const string ResourceName = "PCL.Plugins.IMCProxy.pclplugin";

    /// <summary>内置插件的安装目录名必须与 plugin.json 里的 id 一致。</summary>
    private const string PluginId = "imcproxy.control";

    /// <summary>
    ///     释放标记。文件名带包内容指纹：换了内置包就会解压到新的目录，
    ///     旧的坏包（比如清单带 BOM 而校验不通过）不会因为「目录已存在」被继续沿用。
    /// </summary>
    private const string StampFileName = ".bundled-c3fc943c";

    /// <summary>确保内置插件已释放到磁盘；任何失败都不应阻断启动。</summary>
    public static void EnsureExtracted()
    {
        try
        {
            var directory = Path.Combine(Paths.Data, "Plugins", PluginId);
            if (File.Exists(Path.Combine(directory, StampFileName))) return;

            using var stream = typeof(BundledPlugins).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return;

            var staging = directory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // 目录条目
                        var target = Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, true);
                    }
                }

                File.WriteAllText(Path.Combine(staging, StampFileName), "IMCProxy 2.0.0");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                Directory.Move(staging, directory);
            }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* 忽略 */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            // 释放失败只影响插件可用性，不能让启动器起不来
            System.Diagnostics.Debug.WriteLine("[Plugins] 内置插件释放失败：" + ex.Message);
        }
    }
}
