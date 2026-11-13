using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace PCL.Core.Minecraft.Saves.Backups;

public sealed partial class WorldBackupService
{
    /// <summary>
    /// 把指定备份恢复到对应世界。恢复前会自动备份当前世界（返回其编号，供再次恢复以回滚），
    /// 恢复过程通过 journal 记录阶段，异常或取消时自动回滚；无法回滚时抛出
    /// <see cref="IOException"/> 并保留现场，等待下次启动前由 <see cref="RecoverPending"/> 处理。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <param name="id">备份编号。</param>
    /// <param name="retention">恢复前自动备份后，每个世界保留的自动备份份数（1..50）。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="progress">按字节计算的进度回调（0..1）。</param>
    /// <param name="beforeCommit">提交阶段回调：0 = journal 已写入；1 = 原世界已移走；2 = 新世界已就位（尚未标记完成）。</param>
    /// <returns>恢复前自动备份的编号；原世界不存在时为 null。</returns>
    /// <exception cref="IOException">备份缺失、校验失败、世界被占用或恢复无法完成。</exception>
    /// <exception cref="OperationCanceledException">操作被取消（世界保持不变）。</exception>
    public string? Restore(
        string gameFolder,
        string id,
        int retention,
        CancellationToken token,
        Action<double>? progress = null,
        Action<int>? beforeCommit = null)
    {
        Retain(retention);
        lock (Gate)
        using (new BusyScope(this))
        {
            string full = Full(gameFolder);
            RequireStopped(full);
            RecoverCore(full, token);

            WorldBackupManifest manifest = ReadManifest(full, id);
            string pointFolder = Safe(Store(full), "points/" + id);
            string saves = Safe(full, "saves");
            if (!Directory.Exists(saves)) throw new IOException("存档目录不存在，无法恢复。");

            string worldFolder = Safe(saves, manifest.World);
            string store = Store(full);
            string tx = Guid.NewGuid().ToString("N");
            string stage = Safe(saves, ".pcl-world-" + tx + ".stage");
            string previous = Safe(saves, ".pcl-world-" + tx + ".before");
            string journalFile = Safe(store, "transactions/" + tx + ".json");

            TryDeleteTree(saves, stage);
            TryDeleteTree(saves, previous);

            bool existed = Directory.Exists(worldFolder);
            bool moved = false;
            bool installed = false;
            bool completed = false;
            string? undo = null;

            try
            {
                ReadArchive(pointFolder, manifest, token, stage, Slice(progress, 0d, 0.3d));

                if (existed)
                {
                    EnsureNoLinks(worldFolder, saves);
                    undo = CaptureCore(full, manifest.World, UndoReasonText, token, Slice(progress, 0.3d, 0.7d));
                }

                var journal = new WorldBackupJournal
                {
                    GameFolder = full,
                    World = manifest.World,
                    Point = manifest.Id,
                    Undo = undo,
                    Existed = existed,
                    Complete = false,
                    ProcessId = Environment.ProcessId,
                    StartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                    CreatedUtc = DateTime.UtcNow
                };
                WriteJson(journalFile, journal);

                beforeCommit?.Invoke(0);
                token.ThrowIfCancellationRequested();

                RequireStopped(full);
                if (existed)
                {
                    using (SessionLock(worldFolder))
                    {
                        MatchTree(worldFolder, undo is null ? manifest : ReadManifest(full, undo), token);
                    }

                    Directory.Move(worldFolder, previous);
                    moved = true;
                }

                beforeCommit?.Invoke(1);
                token.ThrowIfCancellationRequested();

                Directory.Move(stage, worldFolder);
                installed = true;

                beforeCommit?.Invoke(2);
                token.ThrowIfCancellationRequested();

                journal.Complete = true;
                WriteJson(journalFile, journal);
                completed = true;

                RecoverCore(full, CancellationToken.None);
                try
                {
                    PruneCore(full, manifest.World, retention);
                }
                catch (Exception ex)
                {
                    _host.Warn(ex, "恢复成功，但清理旧存档备份失败");
                }

                _host.NotifyChanged();
                progress?.Invoke(1d);
                return undo;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (TryRollback(saves, stage, previous, worldFolder, moved, installed))
                {
                    if (!completed) TryDeleteFile(journalFile);
                }
                else
                {
                    throw new IOException(
                        "恢复失败，且无法自动回到恢复前的状态。请勿启动游戏，并保留 " + saves + " 下的 .pcl-world-" + tx + " 目录。", ex);
                }

                throw;
            }
            finally
            {
                if (completed || !File.Exists(journalFile))
                {
                    TryDeleteTree(saves, stage);
                    TryDeleteTree(saves, previous);
                }
            }
        }
    }

    /// <summary>
    /// 是否存在尚未完成的恢复事务（游戏启动前应调用 <see cref="RecoverBeforeLaunch"/>）。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    public bool HasPendingRecovery(string gameFolder)
    {
        string transactions = Safe(Store(gameFolder), "transactions");
        return Directory.Exists(transactions) &&
               Directory.EnumerateFiles(transactions, "*.json").Any();
    }

    /// <summary>
    /// 回滚所有未完成的恢复事务。会先确认没有游戏正在使用该存档目录。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <param name="token">取消令牌。</param>
    /// <exception cref="IOException">存档被占用，或事务状态无法安全回滚。</exception>
    public void RecoverPending(string gameFolder, CancellationToken token = default)
    {
        lock (Gate)
        using (new BusyScope(this))
        {
            string full = Full(gameFolder);
            RequireStopped(full);
            RecoverCore(full, token);
            _host.NotifyChanged();
        }
    }

    /// <summary>
    /// 游戏启动前的恢复入口；没有待处理事务时立即返回，不产生任何磁盘访问之外的副作用。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <param name="token">取消令牌。</param>
    public void RecoverBeforeLaunch(string gameFolder, CancellationToken token = default)
    {
        if (!HasPendingRecovery(gameFolder)) return;
        RecoverPending(gameFolder, token);
    }

    /// <summary>
    /// 处理 <c>transactions</c> 下的事务：完成态只清理残留，未完成态把原世界移回。
    /// 调用方必须已持有 <see cref="Gate"/> 并确认游戏未运行。
    /// </summary>
    private void RecoverCore(string gameFolder, CancellationToken token)
    {
        string store = Store(gameFolder);
        string transactions = Safe(store, "transactions");
        if (!Directory.Exists(transactions)) return;

        string saves = Safe(gameFolder, "saves");
        foreach (string file in Directory.EnumerateFiles(transactions, "*.json")
                     .OrderBy(path => path, StringComparer.Ordinal).ToList())
        {
            token.ThrowIfCancellationRequested();
            string tx = Path.GetFileNameWithoutExtension(file);
            if (!PointIdPattern.IsMatch(tx)) continue;

            WorldBackupJournal journal;
            try
            {
                journal = ReadJson<WorldBackupJournal>(file);
            }
            catch (IOException) when (!File.Exists(file))
            {
                continue;
            }

            if (!PathEquals(journal.GameFolder, gameFolder)) continue;
            string worldName = (journal.World ?? "").Trim();
            ValidateWorldName(worldName);

            if (journal.ProcessId > 0 &&
                journal.ProcessId != Environment.ProcessId &&
                IsProcessAlive(journal.ProcessId, journal.StartedUtc))
            {
                throw new IOException("上一次恢复尚未结束，请关闭占用该存档的游戏后重试。");
            }

            string stage = Safe(saves, ".pcl-world-" + tx + ".stage");
            string previous = Safe(saves, ".pcl-world-" + tx + ".before");
            string worldFolder = Safe(saves, worldName);

            if (journal.Complete)
            {
                if (!Directory.Exists(worldFolder))
                    throw new IOException("恢复事务缺少目标世界，已保留恢复前目录：" + previous);
                TryDeleteTree(saves, previous);
                File.Delete(file);
                continue;
            }

            if (Directory.Exists(previous))
            {
                if (Directory.Exists(worldFolder))
                {
                    if (!string.IsNullOrEmpty(journal.Undo) &&
                        Directory.Exists(Safe(store, "points/" + journal.Undo)))
                    {
                        MatchTree(worldFolder, ReadManifest(gameFolder, journal.Undo!), token);
                    }

                    TryDeleteTree(saves, stage);
                    Directory.Move(worldFolder, stage);
                }

                Directory.Move(previous, worldFolder);
            }
            else if (!journal.Existed && Directory.Exists(worldFolder))
            {
                MatchTree(worldFolder, ReadManifest(gameFolder, journal.Point), token);
                TryDeleteTree(saves, stage);
                Directory.Move(worldFolder, stage);
            }

            TryDeleteTree(saves, stage);
            EnsureNoLinks(file, store);
            File.Delete(file);
        }
    }

    /// <summary>
    /// 逐文件比对世界内容与清单（文件数、长度、SHA256），不一致时拒绝继续。
    /// </summary>
    private void MatchTree(string worldFolder, WorldBackupManifest manifest, CancellationToken token)
    {
        SortedDictionary<string, WorldBackupSnapshot> actual = ScanTree(worldFolder, token);
        if (actual.Count != manifest.Files.Count) throw new IOException("世界内容与备份记录不一致，已取消恢复。");

        foreach ((string name, WorldBackupFile expected) in manifest.Files)
        {
            if (!actual.TryGetValue(name, out WorldBackupSnapshot snapshot) ||
                snapshot.Length != expected.Length)
            {
                throw new IOException("世界内容与备份记录不一致，已取消恢复。");
            }

            string path = Path.Combine(worldFolder, name.Replace('/', Path.DirectorySeparatorChar));
            string hash = HashFile(path, token, null);
            if (!string.Equals(hash, expected.Sha256, StringComparison.Ordinal))
                throw new IOException("世界内容与备份记录不一致，已取消恢复。");
        }
    }

    /// <summary>
    /// 校验并解压 <c>world.zip</c>。目标为 null 时只做校验（用于捕获后的自校验）。
    /// </summary>
    private void ReadArchive(
        string pointFolder,
        WorldBackupManifest manifest,
        CancellationToken token,
        string? destination,
        Action<double>? progress)
    {
        string archivePath = Safe(pointFolder, "world.zip");
        if (!File.Exists(archivePath)) throw new IOException("备份文件已丢失。");

        var reporter = new ByteProgress(progress, manifest.Bytes);
        long done = 0;
        int count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (destination is not null)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in manifest.Directories)
            {
                Directory.CreateDirectory(Safe(destination, directory, false));
            }
        }

        using var stream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                if (name.Length > 1) ValidateRelativeName(name.TrimEnd('/'));
                continue;
            }

            ValidateRelativeName(name);
            if (!seen.Add(name)) throw new IOException("备份文件已损坏。");
            if (!manifest.Files.TryGetValue(name, out WorldBackupFile? expected) || expected is null)
                throw new IOException("备份文件与记录不一致。");

            count++;
            using Stream input = entry.Open();
            if (destination is null)
            {
                (long length, string hash) = Transfer(input, null, token, expected.Length, chunk =>
                {
                    done += chunk;
                    reporter.Report(done);
                });
                if (length != expected.Length || !string.Equals(hash, expected.Sha256, StringComparison.Ordinal))
                    throw new IOException("备份文件校验失败。");
                continue;
            }

            string target = Safe(destination, name, false);
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            using (var output = new FileStream(
                       target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.SequentialScan))
            {
                (long length, string hash) = Transfer(input, output, token, expected.Length, chunk =>
                {
                    done += chunk;
                    reporter.Report(done);
                });
                output.Flush(true);
                if (length != expected.Length || !string.Equals(hash, expected.Sha256, StringComparison.Ordinal))
                    throw new IOException("备份文件校验失败。");
            }

            File.SetLastWriteTimeUtc(target, expected.WriteUtc);
        }

        if (count != manifest.Files.Count) throw new IOException("备份文件不完整。");
        progress?.Invoke(1d);
    }

    /// <summary>
    /// 尽力把世界恢复到恢复前状态；返回 false 表示现场无法安全回滚，已保留 journal 供下次启动前处理。
    /// </summary>
    private bool TryRollback(
        string saves,
        string stage,
        string previous,
        string worldFolder,
        bool moved,
        bool installed)
    {
        try
        {
            if (installed && Directory.Exists(worldFolder))
            {
                if (Directory.Exists(stage)) DeleteTree(saves, stage);
                Directory.Move(worldFolder, stage);
            }

            if (moved && Directory.Exists(previous))
            {
                if (Directory.Exists(worldFolder)) return false;
                Directory.Move(previous, worldFolder);
            }

            if (Directory.Exists(stage)) DeleteTree(saves, stage);
            return true;
        }
        catch (Exception ex)
        {
            _host.Warn(ex, "回滚存档恢复失败");
            return false;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _host.Warn(ex, "清理恢复记录失败");
        }
    }
}
