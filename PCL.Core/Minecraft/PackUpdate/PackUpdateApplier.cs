using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCL.Core.Minecraft.PackUpdate;

/// <summary>
/// 更新落地器：先在暂存目录里解压并校验，再逐个文件「备份旧文件 → 移入新文件」，
/// 全部成功后删除计划中的移除项。任一步失败或取消都会逆序回滚。
/// </summary>
public static class PackUpdateApplier
{
    private const string StagePrefix = ".pcl-packupdate-";
    private const int BufferSize = 131_072;

    private static readonly Regex StageNamePattern = new(
        @"\A\.pcl-packupdate-[0-9a-f]{32}\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 把更新计划落地到实例目录。
    /// </summary>
    /// <param name="package">已读取的整合包；必须带有 <see cref="PackPackage.SourceArchive"/>。</param>
    /// <param name="plan">更新预览。</param>
    /// <param name="gameRoot">实例游戏根目录。</param>
    /// <param name="workRoot">暂存目录的父目录，通常是版本目录下的 <c>PCL\</c>；不存在时会自动创建。</param>
    /// <param name="token">取消操作。</param>
    /// <param name="progress">进度回调，参数范围 0 到 1。</param>
    /// <exception cref="PackUpdateApplyException">执行失败，文件已经回滚。</exception>
    /// <exception cref="OperationCanceledException">操作被取消，文件已经回滚。</exception>
    public static PackUpdateApplyResult Apply(
        PackPackage package,
        PackUpdatePlan plan,
        string gameRoot,
        string workRoot,
        CancellationToken token = default,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(gameRoot)) throw new ArgumentException("未指定实例游戏目录。", nameof(gameRoot));
        if (string.IsNullOrWhiteSpace(workRoot)) throw new ArgumentException("未指定暂存目录。", nameof(workRoot));

        string root = PackUpdatePaths.NormalizeRoot(gameRoot, "实例游戏目录无效。");
        string work = PackUpdatePaths.NormalizeRoot(workRoot, "暂存目录无效。");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("找不到实例游戏目录。");
        token.ThrowIfCancellationRequested();

        // 先把计划整理成去重后的写入与删除操作；保护路径无论计划怎么写都不会被触碰。
        List<PackUpdateEntry> writes = [];
        List<PackUpdateEntry> deletes = [];
        HashSet<string> writePaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> deletePaths = new(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;

        foreach (PackUpdateEntry entry in plan.Entries)
        {
            token.ThrowIfCancellationRequested();
            string relative = PackUpdatePaths.NormalizeRelative(entry.RelativePath);
            if (PackUpdatePaths.IsExcluded(relative))
            {
                skipped++;
                continue;
            }

            switch (entry.Kind)
            {
                case PackUpdateKind.Added:
                case PackUpdateKind.Updated:
                    if (writePaths.Add(relative)) writes.Add(entry);
                    break;
                case PackUpdateKind.Removed:
                    if (entry.PersonalModified
                        || writePaths.Contains(relative)
                        || !deletePaths.Add(relative))
                    {
                        skipped++;
                        break;
                    }

                    deletes.Add(entry);
                    break;
                default:
                    break;
            }
        }

        int preserved = plan.Preserved;
        int total = writes.Count + deletes.Count;
        if (total == 0)
        {
            progress?.Invoke(1);
            return new PackUpdateApplyResult(0, 0, preserved, skipped, false);
        }

        string stage = Path.Combine(work, StagePrefix + Guid.NewGuid().ToString("N"));
        string extractRoot = Path.Combine(stage, "extract");
        string backupRoot = Path.Combine(stage, "backup");

        List<WrittenFile> writtenFiles = [];
        List<string[]> deletedFiles = [];
        int written = 0;
        int deleted = 0;
        int processed = 0;

        void Report() => progress?.Invoke(Math.Clamp(processed / (double)total, 0d, 1d));

        try
        {
            Directory.CreateDirectory(extractRoot);
            Directory.CreateDirectory(backupRoot);
            progress?.Invoke(0);

            // 阶段一：把要写入的内容全部解压到暂存目录，并逐个校验大小与哈希。
            Dictionary<string, string> staged = writes.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : Extract(package, writes, extractRoot, token);

            // 阶段二：逐个文件先备份旧文件，再移入新文件。
            foreach (PackUpdateEntry entry in writes)
            {
                token.ThrowIfCancellationRequested();
                string relative = PackUpdatePaths.NormalizeRelative(entry.RelativePath);
                processed++;

                if (!staged.TryGetValue(relative, out string? stagedPath))
                {
                    // 清单声明了文件但压缩包没带内容（例如 Modrinth 的下载项）。
                    // 本引擎不联网下载，明确计入 Skipped，交给上层决定是否补下载。
                    skipped++;
                    Report();
                    continue;
                }

                string target = PackUpdatePaths.Resolve(root, relative);
                string backup = PackUpdatePaths.Resolve(backupRoot, relative);
                bool hadExisting = File.Exists(target);
                writtenFiles.Add(new WrittenFile(target, backup, hadExisting));

                if (hadExisting)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(stagedPath, target);
                written++;
                Report();
            }

            // 阶段三：删除新版整合包不再包含的作者文件。
            foreach (PackUpdateEntry entry in deletes)
            {
                token.ThrowIfCancellationRequested();
                string relative = PackUpdatePaths.NormalizeRelative(entry.RelativePath);
                string target = PackUpdatePaths.Resolve(root, relative);
                string backup = PackUpdatePaths.Resolve(backupRoot, relative);
                processed++;

                if (!File.Exists(target))
                {
                    // 文件已经不在了，不算失败，也不计入 Deleted。
                    skipped++;
                    Report();
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(target, backup);
                deletedFiles.Add([target, backup]);
                deleted++;
                Report();
            }

            progress?.Invoke(1);
            return new PackUpdateApplyResult(written, deleted, preserved, skipped, false);
        }
        catch (Exception error)
        {
            Rollback(writtenFiles, deletedFiles);
            if (error is OperationCanceledException) throw;
            throw new PackUpdateApplyException(
                "应用整合包更新失败，已回滚到更新前的状态。",
                new PackUpdateApplyResult(written, deleted, preserved, skipped, true),
                error);
        }
        finally
        {
            DeleteStage(work, stage);
        }
    }

    private static Dictionary<string, string> Extract(
        PackPackage package,
        IReadOnlyList<PackUpdateEntry> writes,
        string extractRoot,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(package.SourceArchive) || !File.Exists(package.SourceArchive))
        {
            throw new InvalidOperationException("整合包没有关联的压缩包，无法读取文件内容。");
        }

        Dictionary<string, string> staged = new(StringComparer.OrdinalIgnoreCase);
        using ZipArchive archive = ZipFile.OpenRead(package.SourceArchive);

        Dictionary<string, ZipArchiveEntry> index = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (ZipArchiveEntry item in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = item.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (++count > PackUpdatePaths.MaxFiles)
            {
                throw new InvalidDataException($"整合包内的文件数超过上限 {PackUpdatePaths.MaxFiles}。");
            }

            index.TryAdd(name, item);
        }

        foreach (PackUpdateEntry entry in writes)
        {
            token.ThrowIfCancellationRequested();
            string relative = PackUpdatePaths.NormalizeRelative(entry.RelativePath);
            ZipArchiveEntry? source = FindContent(index, relative);
            if (source is null) continue;

            if (source.Length > PackUpdatePaths.MaxEntryBytes)
            {
                throw new InvalidDataException("整合包条目超过大小上限：" + relative);
            }

            string destination = PackUpdatePaths.Resolve(extractRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using (Stream input = source.Open())
            using (FileStream output = new(
                       destination,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       BufferSize,
                       FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferSize];
                long total = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    total += read;
                    if (total > PackUpdatePaths.MaxEntryBytes)
                    {
                        throw new InvalidDataException("整合包条目超过大小上限：" + relative);
                    }

                    output.Write(buffer, 0, read);
                }

                output.Flush(true);
            }

            long length = new FileInfo(destination).Length;
            if (source.Length >= 0 && length != source.Length)
            {
                throw new InvalidDataException("整合包条目大小校验失败：" + relative);
            }

            if (!string.IsNullOrWhiteSpace(entry.IncomingSha1))
            {
                string actual = HashFile(destination, token);
                if (!string.Equals(actual, entry.IncomingSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("整合包条目哈希校验失败：" + relative);
                }
            }

            staged[relative] = destination;
        }

        return staged;
    }

    private static ZipArchiveEntry? FindContent(
        Dictionary<string, ZipArchiveEntry> index,
        string relative)
    {
        // 与读取器保持一致：client-overrides 优先于 overrides，最后才是压缩包根目录。
        if (index.TryGetValue("client-overrides/" + relative, out ZipArchiveEntry? entry)) return entry;
        if (index.TryGetValue("overrides/" + relative, out entry)) return entry;
        if (index.TryGetValue(relative, out entry)) return entry;
        return null;
    }

    private static void Rollback(List<WrittenFile> writtenFiles, List<string[]> deletedFiles)
    {
        // 写入项逆序恢复：覆盖项搬回旧文件，新增项删除已写入的新文件。
        for (int i = writtenFiles.Count - 1; i >= 0; i--)
        {
            WrittenFile item = writtenFiles[i];
            try
            {
                if (item.HadExisting)
                {
                    if (File.Exists(item.Target)) File.Delete(item.Target);
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Backup)!);
                    File.Move(item.Backup, item.Target);
                }
                else if (File.Exists(item.Target))
                {
                    File.Delete(item.Target);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 回滚失败不覆盖原始异常；残留文件保留在暂存目录里，便于人工恢复。
            }
        }

        // 删除项逆序搬回原位置。
        for (int i = deletedFiles.Count - 1; i >= 0; i--)
        {
            string[] item = deletedFiles[i];
            try
            {
                if (!File.Exists(item[1])) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(item[0])!);
                if (File.Exists(item[0])) File.Delete(item[0]);
                File.Move(item[1], item[0]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 同上：不掩盖原始异常。
            }
        }
    }

    private static string HashFile(string address, CancellationToken token)
    {
        using FileStream input = new(
            address,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        using SHA1 sha1 = SHA1.Create();
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            sha1.TransformBlock(buffer, 0, read, null, 0);
        }

        sha1.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha1.Hash!);
    }

    private static void DeleteStage(string workRoot, string stage)
    {
        try
        {
            if (!Directory.Exists(stage)) return;
            if (!StageNamePattern.IsMatch(Path.GetFileName(stage))
                || !PackUpdatePaths.IsWithin(workRoot, stage))
            {
                return;
            }

            DeleteDirectory(stage);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 清理失败不应掩盖真正的更新错误；残留目录仍以 .pcl-packupdate- 开头，可被后续识别。
        }
    }

    private static void DeleteDirectory(string folder)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (Directory.Exists(entry))
            {
                DeleteDirectory(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        Directory.Delete(folder, false);
    }

    private sealed record WrittenFile(string Target, string Backup, bool HadExisting);
}

/// <summary>更新落地失败并完成回滚后抛出的异常。</summary>
public sealed class PackUpdateApplyException : Exception
{
    /// <summary>创建异常并附带回滚后的统计结果。</summary>
    public PackUpdateApplyException(string message, PackUpdateApplyResult result, Exception innerException)
        : base(message, innerException)
    {
        Result = result;
    }

    /// <summary>失败时的统计结果，<see cref="PackUpdateApplyResult.RolledBack"/> 恒为 true。</summary>
    public PackUpdateApplyResult Result { get; }
}
