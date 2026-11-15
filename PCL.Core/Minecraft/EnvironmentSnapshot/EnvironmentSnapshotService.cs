using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using PCL.Core.Minecraft.Instance;

namespace PCL.Core.Minecraft.EnvironmentSnapshot;

/// <summary>
/// 实例环境快照的捕获、存储与差异比较。
/// 存储布局：<c>{dataRoot}/environment-snapshots/{base64url(sha256(lower(instanceFolder)))}/snapshots/</c>，
/// 每份快照单独一个 <c>{yyyyMMddHHmmssfff}.json</c>，最多保留最近若干份。
/// </summary>
public sealed class EnvironmentSnapshotService
{
    /// <summary>默认保留的快照份数。</summary>
    public const int DefaultKeep = 8;

    /// <summary>当前清单格式版本。</summary>
    public const int SchemaVersion = EnvironmentSnapshot.SchemaVersion;

    private const int MaxFiles = 200_000;
    private const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private static readonly Regex SnapshotNamePattern = new(@"\A\d{17}\.json\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sha1Pattern = new(@"\Asha1:[a-f0-9]{40}\z", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _dataRoot;

    /// <summary>创建绑定到指定数据根目录的快照服务。</summary>
    public EnvironmentSnapshotService(string dataRoot)
    {
        _dataRoot = NormalizePath(dataRoot, "未指定环境快照的存储目录。");
    }

    /// <summary>
    /// 把一份快照写入实例对应的存储目录。快照的实例路径、游戏根与文件清单会被规范化。
    /// </summary>
    public void Save(string instanceFolder, string gameRoot, EnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string instance = NormalizePath(instanceFolder, "未指定实例目录。");
        string root = NormalizePath(gameRoot, "未指定游戏目录。");
        if (snapshot.Schema != SchemaVersion) throw new IOException("环境快照的格式版本无效。");
        if (snapshot.Files is null) throw new IOException("环境快照的文件清单无效。");
        if (snapshot.Files.Count > MaxFiles) throw new IOException("环境快照的文件数量过多。");

        Dictionary<string, EnvironmentSnapshotFile> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, EnvironmentSnapshotFile> pair in snapshot.Files)
        {
            string key = NormalizeKey(pair.Key);
            EnvironmentSnapshotFile? file = pair.Value;
            if (file is null || !Sha1Pattern.IsMatch(file.Sha1 ?? "") || file.Length < 0)
            {
                throw new IOException("环境快照包含无效的文件记录。");
            }

            if (!files.TryAdd(key, new EnvironmentSnapshotFile
                {
                    Sha1 = file.Sha1!,
                    Length = file.Length,
                    WriteUtc = AsUtc(file.WriteUtc)
                }))
            {
                throw new IOException("环境快照包含重复的文件路径。");
            }
        }

        snapshot.Schema = SchemaVersion;
        snapshot.InstanceFolder = instance;
        snapshot.GameRoot = root;
        snapshot.Files = files;
        snapshot.CreatedUtc = snapshot.CreatedUtc == default ? DateTime.UtcNow : AsUtc(snapshot.CreatedUtc);

        string directory = Path.Combine(StoreRoot(instance), "snapshots");
        Directory.CreateDirectory(directory);

        DateTime stamp = snapshot.CreatedUtc;
        string address;
        do
        {
            address = Path.Combine(directory, stamp.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".json");
            stamp = stamp.AddMilliseconds(1);
        } while (File.Exists(address));

        WriteAtomic(address, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
    }

    /// <summary>
    /// 读取该实例最近一份健康快照。目录不存在、没有健康快照或文件损坏时返回 null。
    /// </summary>
    public EnvironmentSnapshot? LoadLatestHealthy(string instanceFolder)
    {
        try
        {
            string instance = NormalizePath(instanceFolder, "未指定实例目录。");
            string directory = Path.Combine(StoreRoot(instance), "snapshots");
            if (!Directory.Exists(directory)) return null;

            foreach (string address in Directory.EnumerateFiles(directory, "*.json")
                         .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                if (!SnapshotNamePattern.IsMatch(Path.GetFileName(address))) continue;
                EnvironmentSnapshot? snapshot = Read(address);
                if (snapshot is null || !snapshot.Healthy) continue;
                if (!PathEquals(snapshot.InstanceFolder, instance)) continue;
                return snapshot;
            }
        }
        catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// 捕获一份健康快照并保存；只保留最近 <paramref name="keep"/> 份快照。
    /// </summary>
    public EnvironmentSnapshot Capture(
        string instanceFolder,
        string gameRoot,
        int keep = DefaultKeep,
        CancellationToken token = default)
    {
        EnvironmentSnapshot snapshot = Build(instanceFolder, gameRoot, token);
        Save(instanceFolder, gameRoot, snapshot);
        Prune(NormalizePath(instanceFolder, "未指定实例目录。"), keep <= 0 ? DefaultKeep : keep);
        return snapshot;
    }

    /// <summary>
    ///     只扫描并构建一份快照，不落盘。用于崩溃时的「当前环境」对照，避免把坏状态写成基线。
    /// </summary>
    public EnvironmentSnapshot Build(
        string instanceFolder,
        string gameRoot,
        CancellationToken token = default)
    {
        string instance = NormalizePath(instanceFolder, "未指定实例目录。");
        string root = NormalizePath(gameRoot, "未指定游戏目录。");
        EnvironmentSnapshot snapshot = new()
        {
            Schema = SchemaVersion,
            InstanceFolder = instance,
            GameRoot = root,
            CreatedUtc = DateTime.UtcNow,
            Healthy = false
        };

        Dictionary<string, string> entries = Inventory(instance, root, token);
        foreach (KeyValuePair<string, string> pair in entries)
        {
            token.ThrowIfCancellationRequested();
            FileInfo info = new(pair.Value);
            snapshot.Files[pair.Key] = new EnvironmentSnapshotFile
            {
                Sha1 = Hash(pair.Value, token),
                Length = info.Length,
                WriteUtc = info.LastWriteTimeUtc
            };
        }

        return snapshot;
    }

    /// <summary>
    /// 比较两份快照。基线为 null 时把当前清单全部视为「新增」。
    /// 返回结果按分类、再按逻辑路径排序。
    /// </summary>
    public static IReadOnlyList<EnvironmentChange> Compare(EnvironmentSnapshot? baseline, EnvironmentSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Dictionary<string, EnvironmentSnapshotFile> before =
            baseline?.Files ?? new Dictionary<string, EnvironmentSnapshotFile>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, EnvironmentSnapshotFile> after =
            current.Files ?? new Dictionary<string, EnvironmentSnapshotFile>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> keys = new(before.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(after.Keys);

        List<EnvironmentChange> changes = [];
        foreach (string key in keys)
        {
            before.TryGetValue(key, out EnvironmentSnapshotFile? oldFile);
            after.TryGetValue(key, out EnvironmentSnapshotFile? newFile);
            string kind;
            if (oldFile is null)
            {
                kind = "新增";
            }
            else if (newFile is null)
            {
                kind = "删除";
            }
            else if (!string.Equals(oldFile.Sha1, newFile.Sha1, StringComparison.Ordinal))
            {
                kind = "修改";
            }
            else
            {
                continue;
            }

            changes.Add(new EnvironmentChange(Category(key), kind, key, Detail(key, newFile, current)));
        }

        return changes
            .OrderBy(item => CategoryRank(item.Category))
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string> Inventory(string instance, string gameRoot, CancellationToken token)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        void Add(string key, string address)
        {
            if (!result.TryAdd(key, address)) return;
            if (result.Count > MaxFiles) throw new IOException("环境文件超过 200000 个，已停止捕获环境快照。");
        }

        foreach (string address in Directory.EnumerateFiles(instance))
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(address);
            if (!InstanceCloneRules.IsVersionTopLevelFile(fileName)) continue;
            if (InstanceCloneRules.IsExcluded(fileName)) continue;
            if (IsLink(address)) throw LinkError(fileName);
            Add("v/" + fileName, address);
        }

        foreach (string fixedFile in InstanceCloneRules.VersionFixedFiles)
        {
            token.ThrowIfCancellationRequested();
            string address = Path.Combine(instance, fixedFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(address)) continue;
            if (InstanceCloneRules.IsExcluded(fixedFile)) continue;
            if (IsLink(address)) throw LinkError(fixedFile);
            Add("v/" + fixedFile, address);
        }

        foreach (string folder in InstanceCloneRules.EnvironmentFolders)
        {
            Walk(Path.Combine(gameRoot, folder), folder, token, Add);
        }

        foreach (string address in Directory.EnumerateFiles(gameRoot))
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(address);
            if (!InstanceCloneRules.IsGameRootFile(fileName) || InstanceCloneRules.IsExcluded(fileName)) continue;
            if (IsLink(address)) throw LinkError(fileName);
            Add("g/" + fileName, address);
        }

        return result;
    }

    private static void Walk(
        string folder,
        string relative,
        CancellationToken token,
        Action<string, string> add,
        int depth = 0)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder)) return;
        if (depth > 64) throw new IOException("环境目录嵌套过深，无法安全捕获快照。");
        if (IsLink(folder)) throw LinkError(relative);

        foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            string child = relative + "/" + name;
            if (InstanceCloneRules.IsExcluded(child)) continue;
            bool directory = Directory.Exists(entry);
            if (IsLink(entry)) throw LinkError(child);
            if (directory)
            {
                Walk(entry, child, token, add, depth + 1);
            }
            else
            {
                add("g/" + child, entry);
            }
        }
    }

    private static string Hash(string address, CancellationToken token)
    {
        using FileStream input = new(
            address,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.SequentialScan);
        using SHA1 sha1 = SHA1.Create();
        byte[] buffer = new byte[131_072];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            sha1.TransformBlock(buffer, 0, read, null, 0);
        }

        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return "sha1:" + Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    private static EnvironmentSnapshot? Read(string address)
    {
        try
        {
            FileInfo info = new(address);
            if (info.Length <= 0 || info.Length > MaxSnapshotBytes) return null;
            EnvironmentSnapshot? snapshot = JsonSerializer.Deserialize<EnvironmentSnapshot>(
                File.ReadAllText(address),
                JsonOptions);
            if (snapshot is null || snapshot.Schema != SchemaVersion || snapshot.Files is null) return null;
            if (snapshot.Files.Count > MaxFiles) return null;

            Dictionary<string, EnvironmentSnapshotFile> files = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, EnvironmentSnapshotFile> pair in snapshot.Files)
            {
                if (!IsValidKey(pair.Key)) return null;
                EnvironmentSnapshotFile? file = pair.Value;
                if (file is null || !Sha1Pattern.IsMatch(file.Sha1 ?? "") || file.Length < 0) return null;
                if (!files.TryAdd(pair.Key, file)) return null;
            }

            if (string.IsNullOrWhiteSpace(snapshot.InstanceFolder)
                || string.IsNullOrWhiteSpace(snapshot.GameRoot))
            {
                return null;
            }

            snapshot.Files = files;
            snapshot.CreatedUtc = AsUtc(snapshot.CreatedUtc);
            return snapshot;
        }
        catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return null;
        }
    }

    private void Prune(string instance, int keep)
    {
        try
        {
            string directory = Path.Combine(StoreRoot(instance), "snapshots");
            if (!Directory.Exists(directory)) return;
            List<string> files = Directory.EnumerateFiles(directory, "*.json")
                .Where(path => SnapshotNamePattern.IsMatch(Path.GetFileName(path)))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToList();
            for (int index = keep; index < files.Count; index++)
            {
                try
                {
                    File.Delete(files[index]);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // 快照清理失败不影响本次捕获；下次捕获会再次尝试。
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 存储目录暂时不可枚举时保留旧快照，不阻断本次捕获。
        }
    }

    private static string Category(string key)
    {
        if (key.StartsWith("g/mods/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("g/coremods/", StringComparison.OrdinalIgnoreCase))
        {
            return "模组";
        }

        if (key.StartsWith("v/PCL/", StringComparison.OrdinalIgnoreCase)) return "启动器版本配置";
        if (key.StartsWith("v/", StringComparison.OrdinalIgnoreCase)) return "Minecraft/加载器";
        return "配置/环境资源";
    }

    private static int CategoryRank(string category) => category switch
    {
        "模组" => 0,
        "启动器版本配置" => 1,
        "Minecraft/加载器" => 2,
        _ => 3
    };

    private static string? Detail(string key, EnvironmentSnapshotFile? file, EnvironmentSnapshot current)
    {
        if (file is null) return null;
        bool isMod = key.StartsWith("g/mods/", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("g/coremods/", StringComparison.OrdinalIgnoreCase);
        if (!isMod || key.Length <= 2) return null;

        string root = key.StartsWith("g/", StringComparison.OrdinalIgnoreCase)
            ? current.GameRoot
            : current.InstanceFolder;
        if (string.IsNullOrWhiteSpace(root)) return null;

        string address;
        try
        {
            address = Path.Combine(root, key[2..].Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return null;
        }

        EnvironmentModInfo? info = EnvironmentModReader.Read(address);
        if (info is null) return null;
        if (!string.IsNullOrWhiteSpace(info.Version)) return "版本 " + info.Version;
        if (!string.IsNullOrWhiteSpace(info.Name)) return "名称 " + info.Name;
        return null;
    }

    private string StoreRoot(string instance) =>
        Path.Combine(_dataRoot, "environment-snapshots", StoreKey(instance));

    private static string StoreKey(string instanceFolder)
    {
        string normalized = NormalizePath(instanceFolder, "实例目录无效。").ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void WriteAtomic(string address, byte[] bytes)
    {
        string temporary = address + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }

            File.Move(temporary, address, true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 临时文件清理失败不影响异常向上传递。
            }

            throw;
        }
    }

    private static string NormalizeKey(string? key)
    {
        string text = (key ?? "").Replace('\\', '/').Trim();
        if (!IsValidKey(text)) throw new IOException("环境快照的相对路径无效。");
        return text;
    }

    private static bool IsValidKey(string? key)
    {
        string text = (key ?? "").Trim();
        if (text.Length < 3 || text.Length > 500 || text.Contains('\0')) return false;
        if (!text.StartsWith("v/", StringComparison.Ordinal)
            && !text.StartsWith("g/", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = text[2..].Split('/');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or "..") return false;
        }

        return true;
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static InvalidOperationException LinkError(string relative) =>
        new("目录包含链接，已停止操作，请改用普通独立目录：" + relative);

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(
            NormalizePath(left, "路径无效。"),
            NormalizePath(right, "路径无效。"),
            StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string NormalizePath(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException(message);
        string value = StripExtendedPrefix(path.Trim());
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException(message, error);
        }

        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static string StripExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path[uncPrefix.Length..];
        if (path.StartsWith(localPrefix, StringComparison.Ordinal)) return path[localPrefix.Length..];
        return path;
    }
}
