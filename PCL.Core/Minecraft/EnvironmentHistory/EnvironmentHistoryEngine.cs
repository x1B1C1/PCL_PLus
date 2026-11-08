using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.EnvironmentHistory;

/// <summary>
/// 环境还原点引擎：把「版本目录 + 游戏根的环境目录」做成内容寻址快照，并负责恢复。
/// </summary>
/// <remarks>
/// <para>
/// 存储布局：<c>{dataRoot}/environment-history/{storeKey}/</c>，
/// 其中 <c>storeKey = Base64Url(sha256(小写规范化的版本目录绝对路径))</c>。
/// <c>objects/{40hex}</c> 保存按内容寻址的 blob，<c>points/{32hex}.json</c> 保存清单，
/// <c>deleted/{批次 guid}/{32hex}.json</c> 保存被删除的清单（blob 延迟回收）。
/// </para>
/// <para>
/// 引擎不读取启动器配置、不做界面提示：异常与日志都通过中文文案和 <see cref="LogWrapper"/> 表达，
/// 列表变化通过 <see cref="Changed"/> 通知宿主，事件不切换线程。
/// </para>
/// </remarks>
public sealed partial class EnvironmentHistoryEngine
{
    private const string BusyMessage = "其他启动器正在处理此版本的还原点，请稍后重试。";
    private const string LabelEmptyMessage = "还原点名称不能为空。";
    private const string KeepRangeMessage = "自动还原点保留数量必须为 1 到 100。";
    private const string TooLargeMessage = "环境文件过多/过大，已跳过本次还原点。";
    private const string ManifestTooLargeMessage = "还原点清单过大。";
    private const string ManifestInvalidMessage = "还原点清单已损坏。";
    private const string PointMissingMessage = "找不到该环境还原点。";
    private const string BlobMissingMessage = "还原点文件缺失或损坏，当前环境未修改。";
    private const string BlobCorruptMessage = "已有还原点内容校验失败，请检查磁盘后重试。";
    private const string ChangedDuringCaptureMessage = "创建还原点期间目录内容发生变化，已放弃本次还原点。";
    private const string SchemaUnsupportedMessage = "不支持此版本的环境还原点。";
    private const string CoverageUnsupportedMessage = "此还原点使用了不支持的覆盖范围，请使用对应版本的启动器。";
    private const string GameRootMismatchMessage = "此还原点的游戏目录与当前版本隔离设置不同，请先恢复原隔离设置。";
    private const string VersionOutsideMessage = "版本目录不在游戏目录下，无法创建环境还原点。";
    private const string GameRootMissingMessage = "游戏目录不存在，无法创建环境还原点。";
    private const string InvalidPathMessage = "还原点路径无效。";

    private readonly object Gate = new();
    private int _busy;

    /// <summary>创建引擎。路径会在构造时规范化，之后的比较都用规范化结果。</summary>
    /// <param name="dataRoot">启动器数据目录，还原点存放于其下的 <c>environment-history</c>。</param>
    /// <param name="versionFolder">版本目录（版本隔离时为游戏根）。</param>
    /// <param name="gameRoot">游戏根目录（版本隔离时为版本目录）。</param>
    public EnvironmentHistoryEngine(string dataRoot, string versionFolder, string gameRoot)
    {
        DataRoot = EnvironmentHistoryPaths.Full(dataRoot);
        VersionFolder = EnvironmentHistoryPaths.Full(versionFolder);
        GameRoot = EnvironmentHistoryPaths.Full(gameRoot);
        StoreRoot = EnvironmentHistoryPaths.StoreRoot(DataRoot, VersionFolder);
        string name = Path.GetFileName(VersionFolder);
        InstanceName = string.IsNullOrWhiteSpace(name) ? "未命名版本" : name;
    }

    /// <summary>启动器数据目录（绝对路径）。</summary>
    public string DataRoot { get; }

    /// <summary>版本目录（绝对路径）。</summary>
    public string VersionFolder { get; }

    /// <summary>游戏根目录（绝对路径）。</summary>
    public string GameRoot { get; }

    /// <summary>该版本的还原点存储根目录。</summary>
    public string StoreRoot { get; }

    /// <summary>实例名称（通常等于版本目录名），写入清单用于展示。</summary>
    public string InstanceName { get; }

    /// <summary>当前是否正在执行捕获、恢复、删除或保留策略。</summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>还原点列表发生变化。事件在调用线程上触发，不切换线程，由宿主负责调度。</summary>
    public event Action? Changed;

    /// <summary>
    /// 仅供测试：第一遍扫描结束后、第二遍重新枚举之前调用，用来注入「扫描期间目录被改动」。
    /// </summary>
    internal Action? BeforeRescan { get; set; }

    /// <summary>
    /// 仅供测试：每个恢复操作应用之前用相对路径调用一次，抛异常即可模拟「中途失败」。
    /// 正式运行时保持为 null，不参与任何逻辑。
    /// </summary>
    internal Action<string>? BeforeApply { get; set; }

    #region 查询

    /// <summary>列出全部还原点，按创建时间从新到旧排列；损坏的记录会被跳过并记日志。</summary>
    public IReadOnlyList<EnvironmentHistoryPoint> Points()
    {
        lock (Gate)
        {
            return LoadManifests()
                .OrderByDescending(manifest => manifest.CreatedUtc)
                .ThenByDescending(manifest => manifest.Id, StringComparer.Ordinal)
                .Select(ToPoint)
                .ToList();
        }
    }

    /// <summary>读出所有可解析的清单，损坏的跳过。</summary>
    private List<EnvironmentHistoryManifest> LoadManifests()
    {
        var result = new List<EnvironmentHistoryManifest>();
        string folder = Path.Combine(StoreRoot, "points");
        if (!Directory.Exists(folder)) return result;
        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWrapper.Warn(ex, "读取还原点目录失败：" + folder);
            return result;
        }

        foreach (string file in files)
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (!EnvironmentHistoryPaths.IsPointId(id)) continue;
            try
            {
                result.Add(ReadManifest(id));
            }
            catch (IOException ex)
            {
                LogWrapper.Warn(ex, "跳过损坏的环境还原点记录：" + file);
            }
        }

        return result;
    }

    private static EnvironmentHistoryPoint ToPoint(EnvironmentHistoryManifest manifest)
    {
        long bytes = 0;
        foreach (EnvironmentHistoryFile file in manifest.Files.Values)
        {
            if (file.Length <= 0) continue;
            if (bytes > long.MaxValue - file.Length) bytes = long.MaxValue;
            else bytes += file.Length;
        }

        return new EnvironmentHistoryPoint(
            manifest.Id,
            manifest.Label,
            manifest.CreatedUtc,
            manifest.Manual,
            manifest.Healthy,
            manifest.Files.Count,
            bytes);
    }

    #endregion

    #region 捕获

    /// <summary>
    /// 创建还原点并返回它的编号。两遍扫描期间目录内容若发生变化，会放弃本次还原点
    /// （已写入的 blob 按内容寻址保留，可被后续还原点复用）。
    /// </summary>
    /// <param name="label">还原点名称；不属于 6 个自动标签时记为手动还原点。</param>
    /// <param name="healthy">是否直接标记为「正常运行过」。</param>
    /// <param name="token">取消标记。</param>
    /// <param name="progress">进度回调，取值 0~1。</param>
    public string Capture(string label, bool healthy = false, CancellationToken token = default, Action<double>? progress = null)
    {
        string text = (label ?? "").Trim();
        if (text.Length == 0) throw new IOException(LabelEmptyMessage);
        lock (Gate)
        using (new BusyScope(this))
        using (new StoreLease(StoreRoot))
        {
            string id = CaptureCore(text, healthy, token, progress);
            Changed?.Invoke();
            return id;
        }
    }

    private string CaptureCore(string label, bool healthy, CancellationToken token, Action<double>? progress)
    {
        if (!Directory.Exists(GameRoot)) throw new IOException(GameRootMissingMessage);
        token.ThrowIfCancellationRequested();

        string id = EnvironmentHistoryPaths.NewToken();
        string store = StoreRoot;
        string objects = Path.Combine(store, "objects");
        Directory.CreateDirectory(objects);
        CleanStaleTemps(objects);
        progress?.Invoke(0.01);

        ScanResult first = Scan(token);
        progress?.Invoke(0.02);

        long total = 0;
        foreach (ScanEntry entry in first.Files.Values) total += Math.Max(1, entry.Length);

        var files = new Dictionary<string, EnvironmentHistoryFile>(StringComparer.OrdinalIgnoreCase);
        long done = 0;
        foreach (KeyValuePair<string, ScanEntry> pair in first.Files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            ScanEntry entry = pair.Value;
            string hash = StoreBlob(entry, objects, token);
            files[pair.Key] = new EnvironmentHistoryFile
            {
                Hash = hash,
                Length = entry.Length,
                WriteUtc = entry.WriteUtc
            };
            done += Math.Max(1, entry.Length);
            progress?.Invoke(0.02 + 0.80 * Math.Min(1d, (double)done / Math.Max(1, total)));
        }

        // 第二遍扫描：重新枚举并比对路径集合、长度与写入时间，确认扫描期间目录没被游戏或用户改动。
        BeforeRescan?.Invoke();
        ScanResult second = Scan(token);
        if (!SameContent(first, second)) throw new IOException(ChangedDuringCaptureMessage);

        var manifest = new EnvironmentHistoryManifest
        {
            Schema = EnvironmentHistoryRules.SchemaVersion,
            Id = id,
            CoverageVersion = EnvironmentHistoryRules.CurrentCoverageVersion,
            CreatedUtc = DateTime.UtcNow,
            Label = label,
            Manual = !EnvironmentHistoryRules.IsAutomaticLabel(label),
            Healthy = healthy,
            InstanceName = InstanceName,
            GameRoot = GameRoot,
            Files = files,
            Directories = [.. second.Directories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)]
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, EnvironmentHistoryJson.Options);
        if (bytes.Length > EnvironmentHistoryRules.MaxManifestBytes) throw new IOException(ManifestTooLargeMessage);
        EnvironmentHistoryPaths.WriteAtomic(Path.Combine(store, "points", id + ".json"), bytes);
        progress?.Invoke(1d);
        return id;
    }

    /// <summary>
    /// 把单个文件的内容写入 blob 目录。已存在同名 blob 时按内容寻址直接复用，只核对长度。
    /// </summary>
    private static string StoreBlob(ScanEntry entry, string objects, CancellationToken token)
    {
        string temp = Path.Combine(objects, ".tmp-" + EnvironmentHistoryPaths.NewToken());
        try
        {
            (long Length, string Hash) result;
            using (FileStream input = EnvironmentHistoryPaths.OpenRead(entry.FullPath))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       EnvironmentHistoryPaths.BufferSize, FileOptions.SequentialScan))
            {
                result = EnvironmentHistoryPaths.Transfer(input, output, token);
                output.Flush(true);
            }

            if (result.Length != entry.Length) throw new IOException(ChangedDuringCaptureMessage);
            string blob = Path.Combine(objects, EnvironmentHistoryPaths.Hex(result.Hash));
            if (File.Exists(blob))
            {
                if (new FileInfo(blob).Length != result.Length) throw new IOException(BlobCorruptMessage);
                return result.Hash;
            }

            File.Move(temp, blob);
            return result.Hash;
        }
        finally
        {
            EnvironmentHistoryPaths.TryDelete(temp);
        }
    }

    /// <summary>清理上次异常退出留下的捕获临时文件（调用时已持有租约，不会有并发写入者）。</summary>
    private static void CleanStaleTemps(string objects)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(objects, ".tmp-*", SearchOption.TopDirectoryOnly))
            {
                EnvironmentHistoryPaths.TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWrapper.Warn(ex, "清理还原点临时文件失败：" + objects);
        }
    }

    #endregion

    #region 扫描

    /// <summary>一次扫描的结果：相对路径到文件信息的映射，外加遍历到的目录。</summary>
    private sealed class ScanResult
    {
        internal Dictionary<string, ScanEntry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Directories { get; } = [];
        internal long TotalBytes { get; set; }
    }

    private readonly record struct ScanEntry(string FullPath, long Length, DateTime WriteUtc);

    /// <summary>
    /// 按 coverageVersion 2 的规则扫描当前环境。路径一律相对游戏根目录，
    /// 版本目录里的文件也写成它的真实相对路径（例如 <c>versions/1.20.1/1.20.1.json</c>）。
    /// </summary>
    private ScanResult Scan(CancellationToken token)
    {
        var result = new ScanResult();
        ValidateVersionRoot();

        if (Directory.Exists(VersionFolder))
        {
            foreach (string file in EnvironmentHistoryPaths.EnumerateFiles(VersionFolder, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (!EnvironmentHistoryRules.IsVersionTopLevelFile(Path.GetFileName(file))) continue;
                AddAbsoluteFile(result, file);
            }

            foreach (string name in EnvironmentHistoryRules.VersionFixedFiles)
            {
                token.ThrowIfCancellationRequested();
                string file = Path.Combine(VersionFolder, name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file)) AddAbsoluteFile(result, file);
            }
        }

        if (Directory.Exists(GameRoot))
        {
            foreach (string folder in EnvironmentHistoryRules.GameEnvironmentFolders)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(GameRoot, folder);
                if (Directory.Exists(path)) Walk(folder, path, result, token);
            }

            foreach (string file in EnvironmentHistoryPaths.EnumerateFiles(GameRoot, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (!EnvironmentHistoryRules.IsGameRootFile(Path.GetFileName(file))) continue;
                AddAbsoluteFile(result, file);
            }
        }

        return result;
    }

    /// <summary>版本目录必须位于游戏根之内（版本隔离时两者相等），否则清单里的相对路径无法表达。</summary>
    private void ValidateVersionRoot()
    {
        if (EnvironmentHistoryPaths.PathEquals(GameRoot, VersionFolder)) return;
        if (EnvironmentHistoryPaths.RelativePath(GameRoot, VersionFolder) is null) throw new IOException(VersionOutsideMessage);
    }

    private void AddAbsoluteFile(ScanResult result, string path)
    {
        string? relative = EnvironmentHistoryPaths.RelativePath(GameRoot, path);
        if (relative is null) return;
        if (EnvironmentHistoryRules.IsExcludedPath(relative)) return;
        var info = new FileInfo(path);
        if (!info.Exists) return;
        AddFile(result, relative, info.FullName, info.Length, info.LastWriteTimeUtc);
    }

    private static void AddFile(ScanResult result, string relative, string fullPath, long length, DateTime writeUtc)
    {
        if (result.Files.Count >= EnvironmentHistoryRules.MaxFiles) throw new IOException(TooLargeMessage);
        if (length > 0 && result.TotalBytes > EnvironmentHistoryRules.MaxBytes - length) throw new IOException(TooLargeMessage);
        result.TotalBytes += Math.Max(0, length);
        result.Files[relative] = new ScanEntry(fullPath, length, EnvironmentHistoryPaths.AsUtc(writeUtc));
    }

    /// <summary>递归遍历一个环境目录；命中排除项、链接目录的一律跳过。</summary>
    private void Walk(string relative, string folder, ScanResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWrapper.Warn(ex, "读取环境目录属性失败，已跳过：" + relative);
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            LogWrapper.Warn("已跳过链接目录：" + relative);
            return;
        }

        result.Directories.Add(relative);

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
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
                    throw new IOException("读取环境目录失败：" + folder, ex);
                }

                WalkEntry(relative, entry, result, token);
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private void WalkEntry(string parentRelative, string path, ScanResult result, CancellationToken token)
    {
        string name = Path.GetFileName(path);
        if (name.Length == 0) return;
        string relative = parentRelative + "/" + name;

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWrapper.Warn(ex, "读取环境文件属性失败，已跳过：" + relative);
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            LogWrapper.Warn("已跳过链接项：" + relative);
            return;
        }

        if (EnvironmentHistoryRules.IsExcludedPath(relative)) return;

        if ((attributes & FileAttributes.Directory) != 0)
        {
            Walk(relative, path, result, token);
            return;
        }

        var info = new FileInfo(path);
        if (!info.Exists) return;
        AddFile(result, relative, info.FullName, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>比对两遍扫描结果：路径集合、长度、写入时间全部一致才算稳定。</summary>
    private static bool SameContent(ScanResult first, ScanResult second)
    {
        if (first.Files.Count != second.Files.Count) return false;
        foreach (KeyValuePair<string, ScanEntry> pair in first.Files)
        {
            if (!second.Files.TryGetValue(pair.Key, out ScanEntry other)) return false;
            if (pair.Value.Length != other.Length) return false;
            if (pair.Value.WriteUtc != other.WriteUtc) return false;
        }

        return true;
    }

    #endregion

    #region 清单读写

    /// <summary>读取并校验一份清单；任何不一致都转成中文异常，绝不返回半成品。</summary>
    private EnvironmentHistoryManifest ReadManifest(string id)
    {
        string safeId = EnvironmentHistoryPaths.RequirePointId(id);
        string path = Path.Combine(StoreRoot, "points", safeId + ".json");
        var info = new FileInfo(path);
        if (!info.Exists) throw new IOException(PointMissingMessage);
        if (info.Length > EnvironmentHistoryRules.MaxManifestBytes) throw new IOException(ManifestTooLargeMessage);
        EnvironmentHistoryManifest? manifest = EnvironmentHistoryPaths.ReadJson<EnvironmentHistoryManifest>(path, ManifestInvalidMessage);
        if (manifest is null) throw new IOException(ManifestInvalidMessage);
        NormalizeManifest(manifest);
        ValidateManifest(manifest, safeId);
        return manifest;
    }

    /// <summary>把清单里的路径与时间字段统一规范化，顺便换掉大小写敏感的字典。</summary>
    private static void NormalizeManifest(EnvironmentHistoryManifest manifest)
    {
        manifest.Id = (manifest.Id ?? "").Trim();
        manifest.Label = (manifest.Label ?? "").Trim();
        manifest.InstanceName = (manifest.InstanceName ?? "").Trim();
        manifest.GameRoot = (manifest.GameRoot ?? "").Trim();
        manifest.CreatedUtc = EnvironmentHistoryPaths.AsUtc(manifest.CreatedUtc);
        if (manifest.Label.Length == 0) manifest.Label = "未命名还原点";

        manifest.Files ??= new Dictionary<string, EnvironmentHistoryFile>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, EnvironmentHistoryFile>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, EnvironmentHistoryFile> pair in manifest.Files)
        {
            if (pair.Value is null) throw new IOException(ManifestInvalidMessage);
            string name = EnvironmentHistoryPaths.NormalizeRelative(pair.Key);
            pair.Value.Hash = (pair.Value.Hash ?? "").Trim();
            pair.Value.WriteUtc = EnvironmentHistoryPaths.AsUtc(pair.Value.WriteUtc);
            if (!files.TryAdd(name, pair.Value)) throw new IOException(ManifestInvalidMessage);
        }

        manifest.Files = files;

        List<string> rawDirectories = manifest.Directories;
        if (rawDirectories is null) rawDirectories = [];
        var directories = new List<string>(rawDirectories.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in rawDirectories)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            string name = EnvironmentHistoryPaths.NormalizeRelative(item);
            if (seen.Add(name)) directories.Add(name);
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        manifest.Directories = directories;
    }

    private static void ValidateManifest(EnvironmentHistoryManifest manifest, string id)
    {
        if (manifest.Schema != EnvironmentHistoryRules.SchemaVersion) throw new IOException(SchemaUnsupportedMessage);
        if (!EnvironmentHistoryPaths.IsPointId(manifest.Id)) throw new IOException(ManifestInvalidMessage);
        if (!string.Equals(manifest.Id, id, StringComparison.Ordinal)) throw new IOException(ManifestInvalidMessage);
        if (manifest.CoverageVersion != EnvironmentHistoryRules.CurrentCoverageVersion) throw new IOException(CoverageUnsupportedMessage);
        if (manifest.GameRoot.Length == 0) throw new IOException(ManifestInvalidMessage);
        if (manifest.Files.Count > EnvironmentHistoryRules.MaxFiles) throw new IOException(ManifestInvalidMessage);
        foreach (KeyValuePair<string, EnvironmentHistoryFile> pair in manifest.Files)
        {
            if (!EnvironmentHistoryPaths.IsHash(pair.Value.Hash)) throw new IOException(ManifestInvalidMessage);
            if (pair.Value.Length < 0) throw new IOException(ManifestInvalidMessage);
        }
    }

    /// <summary>重写一份清单（目前只有标记健康状态会用到）。</summary>
    private void WriteManifest(EnvironmentHistoryManifest manifest)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, EnvironmentHistoryJson.Options);
        if (bytes.Length > EnvironmentHistoryRules.MaxManifestBytes) throw new IOException(ManifestTooLargeMessage);
        EnvironmentHistoryPaths.WriteAtomic(Path.Combine(StoreRoot, "points", manifest.Id + ".json"), bytes);
    }

    #endregion

    #region 删除与保留策略

    /// <summary>删除指定还原点：清单挪到 <c>deleted/</c> 下保留，blob 等保留策略回收。</summary>
    public void Delete(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string safeId = EnvironmentHistoryPaths.RequirePointId(id);
            if (seen.Add(safeId)) targets.Add(safeId);
        }

        if (targets.Count == 0) return;

        lock (Gate)
        using (new BusyScope(this))
        using (new StoreLease(StoreRoot))
        {
            // 先确认都在，避免删到一半才发现编号无效。
            foreach (string id in targets) _ = ReadManifest(id);
            MovePoints(targets);
            Changed?.Invoke();
        }
    }

    /// <summary>把清单移动到 <c>deleted/{批次 guid}/</c>；任何一步失败都按逆序移回。</summary>
    private void MovePoints(IReadOnlyList<string> ids)
    {
        string points = Path.Combine(StoreRoot, "points");
        string batch = Path.Combine(StoreRoot, "deleted", EnvironmentHistoryPaths.NewToken());
        Directory.CreateDirectory(batch);
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (string id in ids)
            {
                string from = Path.Combine(points, id + ".json");
                string to = Path.Combine(batch, id + ".json");
                File.Move(from, to);
                moved.Add((from, to));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (int index = moved.Count - 1; index >= 0; index--)
            {
                try
                {
                    File.Move(moved[index].To, moved[index].From);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    LogWrapper.Warn(rollbackError, "回滚还原点删除失败：" + moved[index].From);
                }
            }

            throw new IOException("删除还原点失败，已恢复原来的记录：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// 保留策略：保留全部手动还原点与最近 <paramref name="keep"/> 个自动还原点，其余移入 <c>deleted/</c> 并回收无引用内容。
    /// </summary>
    /// <param name="keep">自动还原点保留数量，取值 1~100。</param>
    public void PruneAutomatic(int keep)
    {
        if (keep is < 1 or > 100) throw new IOException(KeepRangeMessage);
        lock (Gate)
        using (new BusyScope(this))
        using (new StoreLease(StoreRoot))
        {
            List<EnvironmentHistoryManifest> all = LoadManifests();
            List<EnvironmentHistoryManifest> automatic = [.. all
                .Where(manifest => !manifest.Manual)
                .OrderByDescending(manifest => manifest.CreatedUtc)
                .ThenByDescending(manifest => manifest.Id, StringComparer.Ordinal)];
            if (automatic.Count <= keep) return;

            MovePoints([.. automatic.Skip(keep).Select(manifest => manifest.Id)]);
            try
            {
                CollectGarbage();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 内容回收失败不影响「保留策略已生效」，下次删除或裁剪时会再试。
                LogWrapper.Warn(ex, "清理旧还原点内容失败：" + StoreRoot);
            }

            Changed?.Invoke();
        }
    }

    /// <summary>
    /// 回收没有清单引用的 blob。<c>deleted/</c> 下的清单同样算引用，
    /// 因此刚删除的还原点内容仍然可以用来撤销删除。
    /// </summary>
    private void CollectGarbage()
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EnvironmentHistoryManifest manifest in LoadManifests())
        {
            foreach (EnvironmentHistoryFile file in manifest.Files.Values)
            {
                if (!EnvironmentHistoryPaths.IsHash(file.Hash)) continue;
                references.Add(EnvironmentHistoryPaths.Hex(file.Hash));
            }
        }

        string deleted = Path.Combine(StoreRoot, "deleted");
        if (Directory.Exists(deleted))
        {
            foreach (string file in Directory.EnumerateFiles(deleted, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    EnvironmentHistoryManifest? manifest = EnvironmentHistoryPaths.ReadJson<EnvironmentHistoryManifest>(file, ManifestInvalidMessage);
                    if (manifest?.Files is null) continue;
                    foreach (EnvironmentHistoryFile item in manifest.Files.Values)
                    {
                        if (item is null || !EnvironmentHistoryPaths.IsHash(item.Hash)) continue;
                        references.Add(EnvironmentHistoryPaths.Hex(item.Hash));
                    }
                }
                catch (IOException ex)
                {
                    LogWrapper.Warn(ex, "跳过损坏的删除记录：" + file);
                }
            }
        }

        string objects = Path.Combine(StoreRoot, "objects");
        if (!Directory.Exists(objects)) return;
        foreach (string file in Directory.EnumerateFiles(objects, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (!EnvironmentHistoryPaths.IsHex40(name)) continue;
            if (references.Contains(name)) continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogWrapper.Warn(ex, "删除未被引用的还原点内容失败：" + file);
            }
        }
    }

    /// <summary>标记或取消「正常运行过」；只有这个操作会改写清单的 healthy 字段。</summary>
    public void MarkHealthy(string id, bool healthy = true)
    {
        string safeId = EnvironmentHistoryPaths.RequirePointId(id);
        lock (Gate)
        using (new BusyScope(this))
        using (new StoreLease(StoreRoot))
        {
            EnvironmentHistoryManifest manifest = ReadManifest(safeId);
            if (manifest.Healthy == healthy) return;
            manifest.Healthy = healthy;
            WriteManifest(manifest);
            Changed?.Invoke();
        }
    }

    #endregion

    #region 路径与辅助

    /// <summary>判断名称是否属于 6 个自动标签之一。</summary>
    public static bool IsAutomaticLabel(string label) => EnvironmentHistoryRules.IsAutomaticLabel(label);

    private string BlobPath(string hash) => Path.Combine(StoreRoot, "objects", EnvironmentHistoryPaths.Hex(hash));

    /// <summary>把清单里的相对路径解析成游戏目录下的绝对路径（含越界校验）。</summary>
    private string ResolveGamePath(string relative)
    {
        try
        {
            return EnvironmentHistoryPaths.Resolve(GameRoot, relative);
        }
        catch (IOException ex)
        {
            throw new IOException(InvalidPathMessage + "（" + relative + "）", ex);
        }
    }

    /// <summary>读取摘要；读不出内容时记日志并返回 false（调用方按「已被外部改动」处理）。</summary>
    private static bool TryDigest(string path, out string? hash)
    {
        hash = null;
        try
        {
            if (!File.Exists(path)) return true;
            hash = EnvironmentHistoryPaths.Sha1(path, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWrapper.Warn(ex, "无法读取环境文件：" + path);
            return false;
        }
    }

    /// <summary>把 0~1 的进度映射到 <paramref name="from"/>~<paramref name="to"/> 这一段。</summary>
    private static Action<double>? Slice(Action<double>? progress, double from, double to) =>
        progress is null ? null : value => progress(from + Math.Clamp(value, 0d, 1d) * (to - from));

    /// <summary>忙碌计数作用域，保证 <see cref="IsBusy"/> 在异常路径上也能回落。</summary>
    private sealed class BusyScope : IDisposable
    {
        private readonly EnvironmentHistoryEngine _owner;

        internal BusyScope(EnvironmentHistoryEngine owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._busy);
        }

        public void Dispose() => Interlocked.Decrement(ref _owner._busy);
    }

    /// <summary>
    /// 跨进程租约：同名 Mutex 保证同一版本目录同时只有一个启动器在写还原点。
    /// 上一个进程异常退出时 Mutex 会被放弃，这里把 <see cref="AbandonedMutexException"/> 视为已取得。
    /// </summary>
    private sealed class StoreLease : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _released;

        internal StoreLease(string storeRoot)
        {
            string name = EnvironmentHistoryPaths.LeaseName(storeRoot);
            var mutex = new Mutex(false, name);
            try
            {
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(30));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired) throw new IOException(BusyMessage);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }

            _mutex = mutex;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                LogWrapper.Warn(ex, "释放还原点租约失败。");
            }
            finally
            {
                _mutex.Dispose();
            }
        }
    }

    #endregion
}
