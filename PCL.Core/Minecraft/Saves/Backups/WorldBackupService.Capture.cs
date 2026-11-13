using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.Saves.Backups;

public sealed partial class WorldBackupService
{
    /// <summary>
    /// 备份单个存档世界。捕获期间会持锁读取 <c>session.lock</c>，并在写入前后各扫描一次世界目录，
    /// 任何变化都会导致本次备份失败且不留下半成品。
    /// </summary>
    /// <param name="gameFolder">游戏根目录（版本隔离后的 .minecraft 根）。</param>
    /// <param name="world">存档文件夹名（不是显示名）。</param>
    /// <param name="reason">备份原因，会显示在界面上；<c>手动备份</c> 或 <c>manual</c> 视为手动备份。</param>
    /// <param name="retention">每个世界保留的自动备份份数（1..50），手动备份不计入。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="progress">按字节计算的进度回调（0..1），可为 null。</param>
    /// <param name="exitedProcess">刚刚退出的游戏进程；用于避免误判该进程仍占用存档。</param>
    /// <returns>新备份的编号（32 位小写十六进制）。</returns>
    /// <exception cref="IOException">存档不存在、被占用、捕获期间发生变化或路径非法。</exception>
    /// <exception cref="OperationCanceledException">操作被取消。</exception>
    public string CaptureWorld(
        string gameFolder,
        string world,
        string reason,
        int retention,
        CancellationToken token,
        Action<double>? progress = null,
        Process? exitedProcess = null)
    {
        Retain(retention);
        ValidateWorldName(world);
        lock (Gate)
        using (new BusyScope(this))
        {
            string full = Full(gameFolder);
            RequireStopped(full, exitedProcess);
            string id = CaptureCore(full, world, reason ?? "", token, progress);
            try
            {
                PruneCore(full, world, retention);
            }
            catch (Exception ex)
            {
                _host.Warn(ex, "新备份已保存，但清理旧存档备份失败");
            }

            _host.NotifyChanged();
            return id;
        }
    }

    /// <summary>
    /// 备份游戏目录下所有有效存档（<c>saves\*</c> 中含 <c>level.dat</c> 的目录）。
    /// 会跳过 <c>.pcl-</c> 前缀的临时目录；没有存档时返回空列表。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <param name="reason">备份原因。</param>
    /// <param name="retention">每个世界保留的自动备份份数（1..50）。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="progress">整体进度回调（0..1），按世界数与单世界字节数加权。</param>
    /// <param name="exitedProcess">刚刚退出的游戏进程。</param>
    /// <returns>本次创建的备份编号列表。</returns>
    public IReadOnlyList<string> CaptureFolder(
        string gameFolder,
        string reason,
        int retention,
        CancellationToken token,
        Action<double>? progress = null,
        Process? exitedProcess = null)
    {
        Retain(retention);
        lock (Gate)
        using (new BusyScope(this))
        {
            string full = Full(gameFolder);
            RequireStopped(full, exitedProcess);
            string saves = Safe(full, "saves");
            if (!Directory.Exists(saves)) return [];

            var worlds = new List<string>();
            foreach (string directory in Directory.EnumerateDirectories(saves)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(directory);
                if (name.StartsWith(".pcl-", StringComparison.Ordinal)) continue;
                if (!File.Exists(Path.Combine(directory, "level.dat"))) continue;
                worlds.Add(name);
            }

            if (worlds.Count == 0) return [];

            var ids = new List<string>(worlds.Count);
            for (int index = 0; index < worlds.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                int current = index;
                Action<double>? slice = null;
                if (progress is not null)
                {
                    Action<double> callback = progress;
                    slice = value => callback((current + Math.Clamp(value, 0d, 1d)) / worlds.Count);
                }

                string id = CaptureCore(full, worlds[index], reason ?? "", token, slice);
                try
                {
                    PruneCore(full, worlds[index], retention);
                }
                catch (Exception ex)
                {
                    _host.Warn(ex, "新备份已保存，但清理旧存档备份失败");
                }

                ids.Add(id);
            }

            _host.NotifyChanged();
            return ids;
        }
    }

    /// <summary>
    /// 捕获单个世界的核心流程：临时目录 → 世界锁 → 扫描 → 写 zip → 再扫描比对 → 写清单 → 自校验 → 原子提交。
    /// 调用方必须已持有 <see cref="Gate"/> 且已通过保留份数与路径校验。
    /// </summary>
    private string CaptureCore(
        string gameFolder,
        string world,
        string reason,
        CancellationToken token,
        Action<double>? progress)
    {
        string worldFolder = Safe(gameFolder, "saves/" + world);
        if (!Directory.Exists(worldFolder)) throw new IOException("存档文件夹不存在。");
        EnsureNoLinks(worldFolder, gameFolder);

        string store = Store(gameFolder);
        string id = Guid.NewGuid().ToString("N");
        string pointFolder = Safe(store, "points/" + id);
        string stage = Safe(store, ".pcl-backup-" + id);
        Directory.CreateDirectory(stage);

        bool committed = false;
        try
        {
            using (SessionLock(worldFolder))
            {
                RequireStopped(gameFolder);

                var directories = new List<string>();
                SortedDictionary<string, WorldBackupSnapshot> before = ScanTree(worldFolder, token, directories);
                long totalBytes = before.Values.Sum(snapshot => snapshot.Length);
                string label = SafeLabel(worldFolder, world);

                string zipPath = Path.Combine(stage, "world.zip");
                var recorded = new Dictionary<string, WorldBackupFile>(StringComparer.OrdinalIgnoreCase);
                var reporter = new ByteProgress(progress, totalBytes);
                long done = 0;
                using (var zipStream = new FileStream(
                           zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           BufferSize, FileOptions.SequentialScan))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false))
                {
                    foreach ((string relative, WorldBackupSnapshot snapshot) in before)
                    {
                        token.ThrowIfCancellationRequested();
                        string source = Path.Combine(worldFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                        ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                        entry.LastWriteTime = ZipTime(snapshot.WriteUtc);
                        using Stream entryStream = entry.Open();
                        (long length, string sha) = CopyHash(
                            source,
                            entryStream,
                            token,
                            -1,
                            chunk =>
                            {
                                done += chunk;
                                reporter.Report(done);
                            });
                        recorded[relative] = new WorldBackupFile
                        {
                            Length = length,
                            Sha256 = sha,
                            WriteUtc = snapshot.WriteUtc
                        };
                    }
                }

                var afterDirectories = new List<string>();
                SortedDictionary<string, WorldBackupSnapshot> after = ScanTree(worldFolder, token, afterDirectories);
                if (after.Count != before.Count || !SetsEqual(directories, afterDirectories))
                    throw new IOException(ChangedMessage);
                foreach ((string relative, WorldBackupSnapshot snapshot) in before)
                {
                    if (!after.TryGetValue(relative, out WorldBackupSnapshot now) ||
                        now.Length != snapshot.Length ||
                        now.WriteUtc != snapshot.WriteUtc)
                        throw new IOException(ChangedMessage);
                }

                var manifest = new WorldBackupManifest
                {
                    Schema = 1,
                    Id = id,
                    GameFolder = gameFolder,
                    World = world,
                    Label = label,
                    Created = DateTime.UtcNow,
                    Reason = reason,
                    Manual = IsManualReason(reason),
                    Bytes = totalBytes,
                    ArchiveBytes = new FileInfo(zipPath).Length,
                    Files = recorded,
                    Directories = directories
                };

                WriteJson(Safe(stage, "manifest.json"), manifest);
                ReadArchive(stage, manifest, token, null, null);
                RequireStopped(gameFolder);
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Safe(store, "points"));
                Directory.Move(stage, pointFolder);
                committed = true;
            }

            progress?.Invoke(1d);
            return id;
        }
        finally
        {
            if (!committed) TryDeleteTree(store, stage);
        }
    }

    private static bool SetsEqual(List<string> left, List<string> right)
    {
        if (left.Count != right.Count) return false;
        var set = new HashSet<string>(left, StringComparer.OrdinalIgnoreCase);
        return right.All(set.Contains);
    }

    private static DateTimeOffset ZipTime(DateTime utc)
    {
        DateTime local = AsUtc(utc).ToLocalTime();
        DateTime min = new(1980, 1, 1);
        DateTime max = new(2107, 12, 31, 23, 59, 58);
        if (local < min) local = min;
        if (local > max) local = max;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
