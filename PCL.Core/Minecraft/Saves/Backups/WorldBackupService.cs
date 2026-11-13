using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCL.Core.Minecraft.Saves.Backups;

/// <summary>
/// 存档世界备份引擎。负责捕获、恢复、清理与事务回滚，
/// 不读取配置、不触碰界面，所有环境交互通过 <see cref="IWorldBackupHost"/> 注入。
/// </summary>
/// <remarks>
/// 备份目录布局：<c>&lt;BackupRoot&gt;\&lt;sha256(大写游戏目录)&gt;\</c>，
/// 其下包含 <c>points\&lt;id&gt;\</c>、<c>transactions\&lt;tx&gt;.json</c>、<c>deleted\&lt;guid&gt;\&lt;id&gt;\</c>
/// 与临时目录 <c>.pcl-backup-&lt;id&gt;\</c>。
/// 所有写操作串行执行（<see cref="Gate"/>），读取操作也持同一把锁，避免读到半成品。
/// </remarks>
public sealed partial class WorldBackupService
{
    private const int MaxFiles = 500_000;
    private const long MaxManifest = 64L * 1024 * 1024;
    private const int BufferSize = 131_072;
    private const string ManualReasonText = "手动备份";
    private const string UndoReasonText = "恢复前自动保存";
    private const string ChangedMessage = "存档在备份期间发生变化，未保存本次备份。";
    private const string GameRunningMessage = "请先关闭使用此存档目录的游戏，再备份或恢复存档。";
    private const string PathInvalidMessage = "备份路径无效。";
    private const string PathEscapeMessage = "备份路径越界，已拒绝操作。";
    private const string ManifestInvalidMessage = "备份记录已损坏。";
    private const string ManifestMissingMessage = "备份记录已丢失或过大。";
    private const string LinkMessage = "存档备份不支持链接文件或链接目录。";

    private static readonly Regex PointIdPattern = new(@"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReservedNamePattern = new(
        @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IWorldBackupHost _host;
    private int _busy;

    /// <summary>
    /// 创建备份引擎。
    /// </summary>
    /// <param name="host">宿主适配器，不能为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> 为 null。</exception>
    public WorldBackupService(IWorldBackupHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>
    /// 全局互斥对象。调用方若需把备份与其它文件操作串行化，可锁定同一对象。
    /// </summary>
    public object Gate { get; } = new();

    /// <summary>当前正在执行的写操作数量。</summary>
    public int BusyCount => Volatile.Read(ref _busy);

    /// <summary>是否有写操作正在执行。</summary>
    public bool IsBusy => BusyCount != 0;

    /// <summary>
    /// 返回指定游戏目录对应的备份根目录。目录不要求已存在。
    /// </summary>
    /// <param name="gameFolder">游戏根目录（版本隔离后的 .minecraft 根）。</param>
    /// <exception cref="IOException">路径非法或包含链接。</exception>
    public string Store(string gameFolder)
    {
        string full = Full(gameFolder);
        string root = Full(_host.BackupRoot);
        return Safe(root, DigestName(full.ToUpperInvariant()));
    }

    /// <summary>
    /// 枚举指定游戏目录下所有可读的存档备份，按创建时间降序排列。
    /// 损坏或缺失清单的记录会被跳过并记录警告，不会抛出。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    public IReadOnlyList<WorldBackupPoint> Points(string gameFolder)
    {
        lock (Gate)
        using (new BusyScope(this))
        {
            string full = Full(gameFolder);
            string points = Safe(Store(full), "points");
            if (!Directory.Exists(points)) return [];

            var result = new List<WorldBackupPoint>();
            foreach (string folder in Directory.EnumerateDirectories(points))
            {
                string id = Path.GetFileName(folder);
                if (!PointIdPattern.IsMatch(id)) continue;
                try
                {
                    WorldBackupManifest manifest = ReadManifest(full, id);
                    result.Add(ToPoint(manifest, folder));
                }
                catch (Exception ex)
                {
                    _host.Warn(ex, "跳过损坏的存档备份记录。");
                }
            }

            return result
                .OrderByDescending(point => point.CreatedUtc)
                .ThenByDescending(point => point.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>
    /// 读取并校验一份备份的清单。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <param name="id">备份编号（32 位小写十六进制）。</param>
    /// <exception cref="IOException">编号非法、备份不存在、清单损坏或与目录不匹配。</exception>
    public WorldBackupPoint Point(string gameFolder, string id)
    {
        lock (Gate)
        using (new BusyScope(this))
        {
            string full = Full(gameFolder);
            WorldBackupManifest manifest = ReadManifest(full, id);
            string folder = Safe(Store(full), "points/" + id);
            return ToPoint(manifest, folder);
        }
    }

    /// <summary>
    /// 把选中的备份移动到 <c>deleted</c> 目录（可撤销的软删除）。
    /// 任一移动失败时会把已移动的备份移回原处。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <param name="ids">要删除的备份编号集合。</param>
    /// <returns>本次删除使用的 <c>deleted</c> 目录；没有需要删除的项时返回 null。</returns>
    /// <exception cref="IOException">编号非法、备份不存在或移动失败。</exception>
    public string? DeletePoints(string gameFolder, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (Gate)
        using (new BusyScope(this))
        {
            string full = Full(gameFolder);
            string[] list = ids.Distinct(StringComparer.Ordinal).ToArray();
            if (list.Length == 0) return null;

            string store = Store(full);
            string points = Safe(store, "points");
            foreach (string id in list)
            {
                PointId(id);
                _ = ReadManifest(full, id);
            }

            string deleted = Safe(store, "deleted/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(deleted);

            var moved = new List<(string From, string To)>();
            try
            {
                foreach (string id in list)
                {
                    string from = Safe(points, id);
                    string to = Safe(deleted, id);
                    Directory.Move(from, to);
                    moved.Add((from, to));
                }
            }
            catch
            {
                foreach ((string from, string to) in moved.AsEnumerable().Reverse())
                {
                    try
                    {
                        Directory.Move(to, from);
                    }
                    catch (Exception ex)
                    {
                        _host.Warn(ex, "回滚备份删除失败");
                    }
                }

                throw;
            }

            _host.NotifyChanged();
            return deleted;
        }
    }

    /// <summary>
    /// 判断指定游戏目录下是否仍有游戏进程在使用存档。无法判断时按“仍在使用”处理。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <param name="exitedProcess">刚刚退出的进程；该进程已退出时会被忽略。</param>
    public bool HasActiveGame(string gameFolder, Process? exitedProcess = null)
    {
        try
        {
            if (exitedProcess is { HasExited: false }) return true;
            string full = Full(gameFolder);
            foreach (WorldBackupWatcher watcher in _host.Watchers())
            {
                if (watcher.Pid <= 0) continue;
                if (!IsProcessAlive(watcher.Pid, watcher.StartedUtc)) continue;
                if (string.IsNullOrEmpty(watcher.GameFolder) || PathEquals(watcher.GameFolder!, full)) return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _host.Warn(ex, "检查游戏是否仍在运行失败，已按仍在运行处理。");
            return true;
        }
    }

    /// <summary>
    /// 在文件管理器中打开指定游戏目录的备份根目录；没有备份时给出提示。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    public void OpenFolder(string gameFolder)
    {
        string store = Store(gameFolder);
        if (!Directory.Exists(store))
        {
            _host.Hint("此游戏目录还没有存档备份。", false);
            return;
        }

        _host.OpenFolder(store);
    }

    /// <summary>通知界面备份列表已变化。</summary>
    public void NotifyChanged() => _host.NotifyChanged();

    /// <summary>
    /// 判断备份原因是否代表手动备份；手动备份不参与保留份数裁剪。
    /// </summary>
    public static bool IsManualReason(string? reason) =>
        string.Equals(reason, ManualReasonText, StringComparison.Ordinal) ||
        string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase);

    #region 路径与校验

    private static string DigestName(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException(PathInvalidMessage);
        string full;
        try
        {
            full = Path.GetFullPath(StripExtendedPrefix(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException(PathInvalidMessage, ex);
        }

        string root = Path.GetPathRoot(full) ?? "";
        if (full.Length > root.Length)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    private string Safe(string root, string relative, bool checkLinks = true)
    {
        if (string.IsNullOrEmpty(relative) || relative.IndexOf('\0') >= 0) throw new IOException(PathInvalidMessage);
        if (relative[0] is '/' or '\\' || Path.IsPathRooted(relative)) throw new IOException(PathEscapeMessage);

        string[] parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new IOException(PathInvalidMessage);
        foreach (string part in parts) SafePart(part);

        string combined = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, parts)));
        if (!IsWithin(root, combined)) throw new IOException(PathEscapeMessage);
        if (checkLinks) EnsureNoLinks(combined, root);
        return combined;
    }

    private static void SafePart(string part)
    {
        if (part.Length == 0 || part.Trim().Length == 0) throw new IOException(PathInvalidMessage);
        if (part is "." or "..") throw new IOException(PathEscapeMessage);
        if (part.EndsWith(' ') || part.EndsWith('.')) throw new IOException(PathInvalidMessage);
        if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException(PathInvalidMessage);
        if (ReservedNamePattern.IsMatch(part)) throw new IOException(PathInvalidMessage);
    }

    private static void ValidateRelativeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('\\') >= 0 || name.StartsWith('/')) throw new IOException(ManifestInvalidMessage);
        foreach (string part in name.Split('/')) SafePart(part);
    }

    private static bool IsWithin(string root, string path)
    {
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoLinks(string path, string stopAt)
    {
        string current = path;
        while (current.Length >= stopAt.Length)
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(LinkMessage);
            }

            if (string.Equals(current, stopAt, StringComparison.OrdinalIgnoreCase)) return;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) return;
            current = parent;
        }
    }

    private static string PointId(string id)
    {
        if (string.IsNullOrEmpty(id) || !PointIdPattern.IsMatch(id)) throw new IOException("存档备份编号无效。");
        return id;
    }

    private static void ValidateWorldName(string world)
    {
        if (string.IsNullOrWhiteSpace(world) ||
            Path.GetFileName(world) != world ||
            world is "." or ".." ||
            world.StartsWith(".pcl-", StringComparison.Ordinal) ||
            world.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("存档文件夹名称无效。");
    }

    private static int Retain(int retention)
    {
        if (retention is < 1 or > 50) throw new IOException("每个世界保留的备份份数必须为 1 到 50。");
        return retention;
    }

    #endregion

    #region 清单读写

    private WorldBackupManifest ReadManifest(string gameFolder, string id)
    {
        PointId(id);
        string folder = Safe(Store(gameFolder), "points/" + id);
        if (!Directory.Exists(folder)) throw new IOException("存档备份不存在或已被移除。");

        WorldBackupManifest manifest = ReadJson<WorldBackupManifest>(Safe(folder, "manifest.json"));
        NormalizeManifest(manifest);
        ValidateManifest(manifest, folder, gameFolder);
        return manifest;
    }

    private static void NormalizeManifest(WorldBackupManifest manifest)
    {
        manifest.Id = (manifest.Id ?? "").Trim();
        manifest.GameFolder = (manifest.GameFolder ?? "").Trim();
        manifest.World = (manifest.World ?? "").Trim();
        manifest.Label = (manifest.Label ?? "").Trim();
        manifest.Reason = (manifest.Reason ?? "").Trim();
        manifest.Created = AsUtc(manifest.Created);
        if (manifest.Label.Length == 0) manifest.Label = manifest.World;

        var files = new Dictionary<string, WorldBackupFile>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, WorldBackupFile value) in manifest.Files ?? [])
        {
            string name = (key ?? "").Replace('\\', '/');
            if (value is null) throw new IOException(ManifestInvalidMessage);
            value.WriteUtc = AsUtc(value.WriteUtc);
            if (!files.TryAdd(name, value)) throw new IOException(ManifestInvalidMessage);
        }

        manifest.Files = files;

        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in manifest.Directories ?? [])
        {
            string name = (directory ?? "").Replace('\\', '/').TrimEnd('/');
            if (seen.Add(name)) directories.Add(name);
        }

        manifest.Directories = directories;
    }

    private void ValidateManifest(WorldBackupManifest manifest, string pointFolder, string gameFolder)
    {
        if (manifest.Schema != 1) throw new IOException("备份记录版本不受支持。");
        PointId(manifest.Id);
        string recorded = Full(manifest.GameFolder);
        if (!string.Equals(recorded, gameFolder, StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份记录与当前游戏目录不匹配。");
        ValidateWorldName(manifest.World);
        if (manifest.Files.Count > MaxFiles || manifest.Directories.Count > MaxFiles)
            throw new IOException(ManifestInvalidMessage);

        long total = 0;
        foreach ((string name, WorldBackupFile file) in manifest.Files)
        {
            ValidateRelativeName(name);
            if (string.Equals(name, "session.lock", StringComparison.OrdinalIgnoreCase))
                throw new IOException(ManifestInvalidMessage);
            if (file is null || file.Length < 0 || total > long.MaxValue - file.Length)
                throw new IOException(ManifestInvalidMessage);
            total += file.Length;

            byte[] hash;
            try
            {
                hash = Convert.FromBase64String(file.Sha256);
            }
            catch (FormatException ex)
            {
                throw new IOException(ManifestInvalidMessage, ex);
            }

            if (hash.Length != 32) throw new IOException(ManifestInvalidMessage);
        }

        if (total != manifest.Bytes) throw new IOException("备份记录与实际文件不一致。");
        foreach (string directory in manifest.Directories) ValidateRelativeName(directory);

        if (!File.Exists(Safe(pointFolder, "world.zip"))) throw new IOException("备份文件已丢失。");
    }

    private WorldBackupPoint ToPoint(WorldBackupManifest manifest, string folder) => new()
    {
        Id = manifest.Id,
        World = manifest.World,
        Label = manifest.Label,
        Reason = manifest.Reason,
        CreatedUtc = manifest.Created,
        Manual = manifest.Manual,
        Bytes = manifest.Bytes,
        ArchiveBytes = manifest.ArchiveBytes,
        FileCount = manifest.Files.Count,
        Files = manifest.Files,
        Directories = manifest.Directories,
        Healthy = true,
        Folder = folder,
        Description = manifest.World + " · " + manifest.Reason + " · " + _host.FormatSize(manifest.Bytes)
    };

    private static T ReadJson<T>(string path) where T : class
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxManifest) throw new IOException(ManifestMissingMessage);

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(ManifestMissingMessage, ex);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, WorldBackupJson.Options)
                   ?? throw new IOException(ManifestInvalidMessage);
        }
        catch (JsonException ex)
        {
            throw new IOException(ManifestInvalidMessage, ex);
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = path + ".pcl-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, WorldBackupJson.Options);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // 临时文件清理失败不影响主流程。
                }
            }
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    #endregion

    #region 保留策略

    private void PruneCore(string gameFolder, string world, int retention)
    {
        string points = Safe(Store(gameFolder), "points");
        if (!Directory.Exists(points)) return;

        var candidates = new List<(string Id, DateTime Created)>();
        foreach (string folder in Directory.EnumerateDirectories(points))
        {
            string id = Path.GetFileName(folder);
            if (!PointIdPattern.IsMatch(id)) continue;
            try
            {
                WorldBackupManifest manifest = ReadManifest(gameFolder, id);
                if (manifest.Manual) continue;
                if (!string.Equals(manifest.World, world, StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add((id, manifest.Created));
            }
            catch (Exception ex)
            {
                _host.Warn(ex, "跳过损坏的存档备份记录。");
            }
        }

        foreach ((string id, _) in candidates
                     .OrderByDescending(candidate => candidate.Created)
                     .ThenByDescending(candidate => candidate.Id, StringComparer.Ordinal)
                     .Skip(retention))
        {
            try
            {
                DeletePointFolder(gameFolder, id);
            }
            catch (Exception ex)
            {
                _host.Warn(ex, "新备份已保存，但清理旧存档备份失败");
            }
        }
    }

    private void DeletePointFolder(string gameFolder, string id)
    {
        string points = Safe(Store(gameFolder), "points");
        PointId(id);
        DeleteTree(points, Safe(points, id));
    }

    #endregion

    #region 文件系统

    private static SortedDictionary<string, WorldBackupSnapshot> ScanTree(
        string folder,
        CancellationToken token,
        List<string>? directories = null)
    {
        var files = new SortedDictionary<string, WorldBackupSnapshot>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(folder);
        int count = 0;

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string current = stack.Pop();
            IEnumerator<string>? enumerator = null;
            try
            {
                enumerator = Directory.EnumerateFileSystemEntries(current).GetEnumerator();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string entry;
                    try
                    {
                        if (!enumerator.MoveNext()) break;
                        entry = enumerator.Current;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException("读取存档目录失败：" + current, ex);
                    }

                    if (++count > MaxFiles) throw new IOException("存档文件过多，无法备份。");

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException("读取存档文件失败：" + entry, ex);
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(LinkMessage);

                    string relative = Path.GetRelativePath(folder, entry).Replace(Path.DirectorySeparatorChar, '/');
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories?.Add(relative);
                        stack.Push(entry);
                    }
                    else
                    {
                        if (string.Equals(Path.GetFileName(entry), "session.lock", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var info = new FileInfo(entry);
                        if (!info.Exists) continue;
                        files[relative] = new WorldBackupSnapshot(info.Length, info.LastWriteTimeUtc);
                    }
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }

        return files;
    }

    private static (long Length, string Sha256) CopyHash(
        string path,
        Stream? output,
        CancellationToken token,
        long expectedLength,
        Action<long>? onChunk)
    {
        using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.SequentialScan);
        return Transfer(input, output, token, expectedLength, onChunk);
    }

    private static string HashFile(string path, CancellationToken token, Action<long>? onChunk) =>
        CopyHash(path, null, token, -1, onChunk).Sha256;

    private static (long Length, string Sha256) Transfer(
        Stream input,
        Stream? output,
        CancellationToken token,
        long expectedLength,
        Action<long>? onChunk)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, BufferSize);
                if (read <= 0) break;
                hash.AppendData(buffer, 0, read);
                output?.Write(buffer, 0, read);
                total += read;
                if (expectedLength >= 0 && total > expectedLength)
                    throw new IOException("备份文件长度与记录不一致。");
                onChunk?.Invoke(read);
            }

            if (expectedLength >= 0 && total != expectedLength)
                throw new IOException("备份文件长度与记录不一致。");
            return (total, Convert.ToBase64String(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void DeleteTree(string root, string folder)
    {
        if (!IsWithin(root, folder) || !Directory.Exists(folder)) throw new IOException(PathEscapeMessage);
        EnsureNoLinks(folder, root);

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(folder))
        {
            EnsureNoLinks(directory, root);
            DeleteTree(root, directory);
        }

        Directory.Delete(folder, false);
    }

    private void TryDeleteTree(string root, string folder)
    {
        try
        {
            if (Directory.Exists(folder)) DeleteTree(root, folder);
        }
        catch (Exception ex)
        {
            _host.Warn(ex, "清理存档备份临时目录失败");
        }
    }

    private FileStream? SessionLock(string worldFolder)
    {
        string path = Path.Combine(worldFolder, "session.lock");
        if (!File.Exists(path)) return null;
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("该存档仍被其他程序使用，请关闭游戏后重试。", ex);
        }
    }

    private void RequireStopped(string gameFolder, Process? exitedProcess = null)
    {
        if (HasActiveGame(gameFolder, exitedProcess)) throw new IOException(GameRunningMessage);
    }

    private string SafeLabel(string worldFolder, string world)
    {
        try
        {
            string? label = _host.WorldLabel(worldFolder);
            if (!string.IsNullOrWhiteSpace(label)) return label.Trim();
        }
        catch (Exception ex)
        {
            _host.Warn(ex, "读取存档显示名称失败，已使用文件夹名。");
        }

        return world;
    }

    private static bool IsProcessAlive(int pid, DateTime startedUtc)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return false;
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (startedUtc == default) return true;
            return Math.Abs((process.StartTime.ToUniversalTime() - AsUtc(startedUtc)).TotalSeconds) <= 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    #endregion

    #region 辅助类型

    private static Action<double>? Slice(Action<double>? callback, double from, double to) =>
        callback is null ? null : value => callback(from + (Math.Clamp(value, 0d, 1d) * (to - from)));

    private readonly record struct WorldBackupSnapshot(long Length, DateTime WriteUtc);

    private sealed class ByteProgress
    {
        private readonly Action<double>? _callback;
        private readonly long _total;
        private long _lastBytes = -1;
        private long _lastTicks;

        internal ByteProgress(Action<double>? callback, long total)
        {
            _callback = callback;
            _total = total;
        }

        internal void Report(long done)
        {
            if (_callback is null) return;
            long now = Environment.TickCount64;
            if (_lastBytes >= 0 && done - _lastBytes < 4L * 1024 * 1024 && now - _lastTicks < 200) return;
            _lastBytes = done;
            _lastTicks = now;
            _callback(_total <= 0 ? 1d : Math.Clamp((double)done / _total, 0d, 1d));
        }
    }

    private sealed class BusyScope : IDisposable
    {
        private WorldBackupService? _owner;

        internal BusyScope(WorldBackupService owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._busy);
        }

        public void Dispose()
        {
            WorldBackupService? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) Interlocked.Decrement(ref owner._busy);
        }
    }

    #endregion
}
