using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.EnvironmentHistory;

/// <summary>
/// 恢复：把环境还原到某份清单记录的状态。顺序固定为
/// 「校验内容 → 计算差集 → 恢复前自动保存 → 事务应用 → 清理空目录」。
/// </summary>
/// <remarks>
/// 恢复 <c>PCL\Setup.ini</c> 之后，实例配置（内存里的 ini 缓存）需要调用方重新加载。
/// 引擎不负责刷新缓存，也不会主动通知宿主；请在恢复成功后重新读取一次实例信息。
/// </remarks>
public sealed partial class EnvironmentHistoryEngine
{
    /// <summary>
    /// 把当前环境恢复到指定还原点。
    /// </summary>
    /// <param name="id">还原点编号。</param>
    /// <param name="token">取消标记；取消会触发逆序回滚。</param>
    /// <param name="progress">进度回调，取值 0~1。</param>
    /// <exception cref="IOException">
    /// 清单缺失/损坏、内容缺失、版本隔离设置变化，或恢复过程中出现无法回滚的失败。
    /// </exception>
    public void Restore(string id, CancellationToken token = default, Action<double>? progress = null)
    {
        string safeId = EnvironmentHistoryPaths.RequirePointId(id);
        lock (Gate)
        using (new BusyScope(this))
        using (new StoreLease(StoreRoot))
        {
            EnvironmentHistoryManifest manifest = ReadManifest(safeId);
            if (!EnvironmentHistoryPaths.PathEquals(manifest.GameRoot, GameRoot))
            {
                // 相对路径的含义会随版本隔离设置改变，宁可拒绝也不要把文件写到错误的目录。
                throw new IOException(GameRootMismatchMessage);
            }

            // 1. 先确认所有内容都在。缺失或损坏时一个文件都不动。
            VerifyPoint(manifest, token, Slice(progress, 0d, 0.10d));

            // 2. 计算差集：清单有磁盘没有 → 写；摘要不同 → 覆盖；磁盘有清单没有 → 删。
            (Dictionary<string, string?> digests, List<string> directories) =
                ScanForRestore(token, Slice(progress, 0.10d, 0.30d));
            List<EnvironmentHistoryOperation> operations = BuildOperations(manifest, digests);
            progress?.Invoke(0.32d);

            // 3. 恢复前自动保存：保证回滚所需的「旧内容」一定已经落在 objects 里。
            _ = CaptureCore(EnvironmentHistoryRules.UndoLabel, false, token, Slice(progress, 0.32d, 0.60d));

            // 4. 事务应用（失败时逆序回滚已应用的步骤）。
            ApplyTransaction(operations, token, Slice(progress, 0.60d, 0.98d));

            // 5. 清单里不存在的目录如果已经空了，一并收掉，避免留下空壳。
            RemoveEmptyDirectories(directories, manifest);

            progress?.Invoke(1d);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// 逐个确认清单引用的内容都在 <c>objects</c> 里，并且长度与摘要都对得上。
    /// 任何一处不符都抛「还原点文件缺失或损坏，当前环境未修改。」。
    /// </summary>
    private void VerifyPoint(EnvironmentHistoryManifest manifest, CancellationToken token, Action<double>? progress)
    {
        int count = Math.Max(1, manifest.Files.Count);
        int done = 0;
        foreach (KeyValuePair<string, EnvironmentHistoryFile> pair in manifest.Files
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string blob = BlobPath(pair.Value.Hash);
            long length;
            try
            {
                var info = new FileInfo(blob);
                if (!info.Exists) throw new IOException(BlobMissingMessage);
                length = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(BlobMissingMessage, ex);
            }

            if (length != pair.Value.Length) throw new IOException(BlobMissingMessage);

            string digest;
            try
            {
                digest = EnvironmentHistoryPaths.Sha1(blob, token);
            }
            catch (IOException ex)
            {
                throw new IOException(BlobMissingMessage, ex);
            }

            if (!string.Equals(digest, pair.Value.Hash, StringComparison.Ordinal)) throw new IOException(BlobMissingMessage);
            progress?.Invoke(++done / (double)count);
        }
    }

    /// <summary>扫描当前环境并算出每个文件的摘要，同时带出目录列表供恢复后清理空目录。</summary>
    private (Dictionary<string, string?> Digests, List<string> Directories) ScanForRestore(
        CancellationToken token,
        Action<double>? progress)
    {
        ScanResult scan = Scan(token);
        var digests = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int count = Math.Max(1, scan.Files.Count);
        int done = 0;
        foreach (KeyValuePair<string, ScanEntry> pair in scan.Files
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            digests[pair.Key] = EnvironmentHistoryPaths.Sha1(pair.Value.FullPath, token);
            progress?.Invoke(++done / (double)count);
        }

        return (digests, scan.Directories);
    }

    /// <summary>
    /// 计算差集并按相对路径排序，让同样的现场产生同样的操作顺序（便于复现与测试）。
    /// </summary>
    private static List<EnvironmentHistoryOperation> BuildOperations(
        EnvironmentHistoryManifest manifest,
        IReadOnlyDictionary<string, string?> current)
    {
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in manifest.Files.Keys) keys.Add(key);
        foreach (string key in current.Keys) keys.Add(key);

        var operations = new List<EnvironmentHistoryOperation>(keys.Count);
        foreach (string key in keys)
        {
            manifest.Files.TryGetValue(key, out EnvironmentHistoryFile? wanted);
            current.TryGetValue(key, out string? have);
            if (wanted is not null && string.Equals(wanted.Hash, have, StringComparison.Ordinal)) continue;

            operations.Add(new EnvironmentHistoryOperation
            {
                Relative = key,
                Action = wanted is null ? EnvironmentHistoryAction.Delete : EnvironmentHistoryAction.Write,
                OldHash = have,
                NewHash = wanted?.Hash,
                NewWriteUtc = wanted?.WriteUtc ?? default
            });
        }

        return operations;
    }

    /// <summary>删除「当前有、清单里没有」且已经变空的目录（由深到浅，只删空目录）。</summary>
    private void RemoveEmptyDirectories(IReadOnlyList<string> currentDirectories, EnvironmentHistoryManifest manifest)
    {
        var keep = new HashSet<string>(manifest.Directories, StringComparer.OrdinalIgnoreCase);
        var extras = new List<string>();
        foreach (string directory in currentDirectories)
        {
            if (!keep.Contains(directory)) extras.Add(directory);
        }

        extras.Sort((left, right) => Depth(right).CompareTo(Depth(left)));
        foreach (string relative in extras)
        {
            try
            {
                string path = ResolveGamePath(relative);
                if (!Directory.Exists(path)) continue;
                if (Directory.EnumerateFileSystemEntries(path).Any()) continue;
                Directory.Delete(path, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录被占用时留着即可，不影响内容正确性。
                LogWrapper.Warn(ex, "清理空目录失败：" + relative);
            }
        }
    }

    private static int Depth(string relative) => relative.Count(character => character == '/');
}
