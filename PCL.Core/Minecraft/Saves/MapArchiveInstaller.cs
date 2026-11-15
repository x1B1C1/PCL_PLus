using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace PCL.Core.Minecraft.Saves;

/// <summary>
///     地图压缩包的落点与安装。
///     <para>
///         地图（CurseForge / Modrinth 的 World）下载后不直接解进 <c>saves</c>，而是先落到
///         <c>saves\PCLLFWorldDownload</c>，再由 <see cref="Install" /> 解压到同级目录。
///         这样做的原因有两个：一是下载目录与存档目录分离，扫描存档时不会把半成品压缩包当成存档；
///         二是解压失败的包会原样留在下载目录里，用户还能自己处理，不会出现「解了一半的存档」。
///     </para>
/// </summary>
public static class MapArchiveInstaller
{
    /// <summary>地图压缩包的下载目录名（位于 <c>saves</c> 下）。</summary>
    public const string DownloadFolderName = "PCLLFWorldDownload";

    /// <summary>压缩包暂存目录前缀。</summary>
    private const string StagePrefix = ".pcl-import-";

    /// <summary>单个地图存档允许的最大文件数，防止异常压缩包把磁盘塞满。</summary>
    private const int MaxFiles = 50000;

    /// <summary>取地图压缩包的下载目录，必要时创建。</summary>
    /// <param name="savesFolder">存档目录（<c>&lt;实例&gt;\saves</c>）。</param>
    public static string GetDownloadFolder(string savesFolder)
    {
        if (string.IsNullOrWhiteSpace(savesFolder)) throw new ArgumentException("存档目录不能为空。", nameof(savesFolder));
        var folder = Path.Combine(savesFolder, DownloadFolderName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    ///     把地图压缩包安装成一个存档。
    ///     <para>压缩包必须只有一个顶层目录；安装成功后压缩包被删除，失败则原包保留。</para>
    /// </summary>
    /// <param name="archivePath">压缩包路径。</param>
    /// <param name="savesFolder">存档目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>安装后的存档目录。</returns>
    public static string Install(string archivePath, string savesFolder, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("地图压缩包不存在。", archivePath);
        if (string.IsNullOrWhiteSpace(savesFolder)) throw new ArgumentException("存档目录不能为空。", nameof(savesFolder));
        Directory.CreateDirectory(savesFolder);

        var stage = Path.Combine(savesFolder, StagePrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > MaxFiles)
                    throw new InvalidOperationException($"地图压缩包内文件过多（{archive.Entries.Count} 个），已停止安装。");
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                    if (!destination.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("地图压缩包包含越界路径，已停止安装。");
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, true);
                }
            }

            var roots = Directory.GetFileSystemEntries(stage).ToArray();
            if (roots.Length != 1 || !Directory.Exists(roots[0]))
                throw new InvalidOperationException("地图压缩包内应当只有一个顶层目录，已停止安装。");

            var source = roots[0];
            var baseName = Path.GetFileName(source);
            var target = FindAvailableName(savesFolder, baseName);
            Directory.Move(source, target);
            // 安装成功后删掉下载的压缩包：它只是一份临时物料，留在存档目录里会干扰存档扫描
            TryDeleteFile(archivePath);
            return target;
        }
        finally
        {
            TryDeleteDirectory(stage);
        }
    }

    /// <summary>在存档目录里找一个不冲突的名字：<c>名字</c>、<c>名字 (2)</c>、<c>名字 (3)</c>……</summary>
    private static string FindAvailableName(string savesFolder, string baseName)
    {
        for (var index = 1; index <= 10000; index++)
        {
            var candidate = index == 1 ? baseName : $"{baseName} ({index})";
            var full = Path.Combine(savesFolder, candidate);
            if (!Directory.Exists(full) && !File.Exists(full)) return full;
        }

        throw new IOException("没有可用的地图存档名称，原压缩包已保留。");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // 清理失败不影响安装结果，残留的暂存目录会在下次安装时被忽略。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就留着，不影响已经装好的存档。
        }
    }
}
