using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCL.Core.Minecraft.Instance;

/// <summary>
/// 「测试副本」核心引擎：扫描实例环境、生成独立副本、复核来源未被修改，并写入来源记录。
/// 只负责 Core 层的文件操作，不感知界面、进程或启动器状态。
/// </summary>
public sealed class InstanceCloneEngine
{
    private const int BufferSize = 131_072;
    private const long MaxVersionJsonBytes = 16L * 1024 * 1024;
    private const long MaxSmallTextBytes = 1L * 1024 * 1024;

    private const string SourceChangedMessage = "在复制期间来源目录发生变化，已放弃本次测试副本。";
    private const string DuplicateNameMessage = "同名版本已存在，请使用新的测试版本名称。";
    private const string LinkMessagePrefix = "目录包含链接，已停止操作，请改用普通独立目录：";

    private static readonly Regex StageNamePattern = new(@"\A\.pcl-clone-[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> ForcedSetupKeys = new(StringComparer.Ordinal)
    {
        "VersionArgumentIndie",
        "VersionArgumentIndieV2",
        "IsStar",
        "CustomInfo",
        "VersionLaunchCount"
    };

    private readonly object _gate = new();
    private readonly string _versionsFolder;

    /// <summary>创建绑定到指定 <c>versions</c> 目录的克隆引擎。</summary>
    public InstanceCloneEngine(string versionsFolder)
    {
        _versionsFolder = NormalizePath(versionsFolder, "未指定 versions 目录。");
    }

    /// <summary>
    /// 仅供测试：在复核来源指纹之前触发，用于模拟「复制期间来源目录发生变化」。
    /// </summary>
    internal Action? BeforeFingerprint { get; set; }

    /// <summary>
    /// 生成一个测试副本。来源与目标必须位于同一个 <c>versions</c> 目录下。
    /// </summary>
    /// <param name="sourceInstanceFolder">原版本目录的绝对路径。</param>
    /// <param name="sourceGameRoot">原实例当前使用的游戏根目录（隔离时为版本目录本身）。</param>
    /// <param name="newName">新版本名称，也是新目录名。</param>
    /// <param name="options">复制范围与安全开关；为 null 时使用默认值。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <param name="progress">进度回调。</param>
    /// <exception cref="InvalidOperationException">目标名称已存在，或来源在复制期间发生变化。</exception>
    /// <exception cref="IOException">名称、目录结构或文件读取不合法。</exception>
    public InstanceCloneResult Clone(
        string sourceInstanceFolder,
        string sourceGameRoot,
        string newName,
        InstanceCloneOptions? options = null,
        CancellationToken cancellationToken = default,
        Action<InstanceCloneProgress>? progress = null)
    {
        lock (_gate)
        {
            options ??= new InstanceCloneOptions();
            try
            {
                return CloneCore(sourceInstanceFolder, sourceGameRoot, newName, options, cancellationToken, progress);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new IOException("无法访问来源或目标目录，请检查权限后重试。", error);
            }
            catch (PathTooLongException error)
            {
                throw new IOException("文件路径过长，无法生成测试副本。", error);
            }
        }
    }

    private InstanceCloneResult CloneCore(
        string sourceInstanceFolder,
        string sourceGameRoot,
        string newName,
        InstanceCloneOptions options,
        CancellationToken cancellationToken,
        Action<InstanceCloneProgress>? progress)
    {
        CancellationToken token = cancellationToken;
        if (options.MaxFiles <= 0 || options.MaxBytes <= 0) throw new IOException("测试副本的文件数量或大小上限无效。");
        Report(progress, 0, "准备", 0, 0);

        if (!Directory.Exists(_versionsFolder)) throw new IOException("找不到 versions 目录。");
        string source = NormalizePath(sourceInstanceFolder, "未指定原版本目录。");
        string gameRoot = NormalizePath(sourceGameRoot, "未指定游戏目录。");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("找不到原版本目录。");

        string name = (newName ?? "").Trim();
        if (!InstanceCloneRules.IsValidName(name)) throw new IOException("测试版本名称包含 Minecraft 不支持的字符。");

        string? sourceParent = Path.GetDirectoryName(source);
        if (!PathEquals(sourceParent, _versionsFolder)) throw new IOException("原版本目录必须位于 versions 目录中。");
        string minecraftRoot = Path.GetDirectoryName(_versionsFolder) ?? _versionsFolder;
        if (!PathEquals(gameRoot, source) && !PathEquals(gameRoot, minecraftRoot))
        {
            throw new IOException("当前游戏目录不属于此 Minecraft 文件夹。");
        }

        string destination = Path.Combine(_versionsFolder, name);
        if (File.Exists(destination) || Directory.Exists(destination) || PathEquals(source, destination))
        {
            throw new InvalidOperationException(DuplicateNameMessage);
        }

        string sourceName = Path.GetFileName(source);
        string jsonName = FindVersionJsonName(source, sourceName);
        List<string> warnings = [];
        void Warn(string message)
        {
            if (!warnings.Contains(message, StringComparer.Ordinal)) warnings.Add(message);
            options.Warn?.Invoke(message);
        }

        string stage = Path.Combine(_versionsFolder, ".pcl-clone-" + Guid.NewGuid().ToString("N"));
        try
        {
            Dictionary<string, string> entries = Inventory(source, gameRoot, options, jsonName, token, Warn);
            long totalWeight = ProgressWeight(entries, options, token);

            Report(progress, 0.05, "校验来源", 0, 0);
            Dictionary<string, string> before = Fingerprint(
                entries,
                options,
                totalWeight,
                token,
                fraction => Report(progress, Map(0.05, 0.20, fraction), "校验来源", 0, 0));

            int fileCount = 0;
            long bytes = 0;
            long completed = 0;
            Directory.CreateDirectory(stage);
            Report(progress, 0.20, "复制文件", 0, 0);

            foreach (KeyValuePair<string, string> pair in entries)
            {
                token.ThrowIfCancellationRequested();
                string target = Resolve(stage, pair.Key[2..]);
                long length = new FileInfo(pair.Value).Length;
                long weight = Math.Max(1L, length);
                double from = Map(0.20, 0.85, completed / (double)totalWeight);
                double to = Map(0.20, 0.85, (completed + weight) / (double)totalWeight);
                CopyFile(
                    pair.Value,
                    target,
                    before[pair.Key],
                    token,
                    fraction => Report(progress, Map(from, to, fraction), "复制文件", fileCount, bytes));
                completed = checked(completed + weight);
                fileCount = checked(fileCount + 1);
                bytes = checked(bytes + new FileInfo(target).Length);
                Report(progress, to, "复制文件", fileCount, bytes);
            }

            PrepareVersion(stage, source, destination, jsonName, name, token);
            WriteOrigin(stage, source, gameRoot, sourceName, name);

            BeforeFingerprint?.Invoke();
            Report(progress, 0.85, "复核来源", fileCount, bytes);
            Dictionary<string, string> after = Fingerprint(
                Inventory(source, gameRoot, options, jsonName, token, Warn),
                options,
                totalWeight,
                token,
                fraction => Report(progress, Map(0.85, 0.98, fraction), "复核来源", fileCount, bytes));
            Verify(before, after);

            token.ThrowIfCancellationRequested();
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new InvalidOperationException(DuplicateNameMessage);
            }

            Directory.Move(stage, destination);
            Report(progress, 1.0, "完成", fileCount, bytes);
            return new InstanceCloneResult
            {
                DestinationFolder = destination,
                Files = fileCount,
                Bytes = bytes,
                Warnings = warnings
            };
        }
        finally
        {
            DeleteStage(_versionsFolder, stage);
        }
    }

    /// <summary>估算一次克隆会复制的字节数；不写入任何文件。</summary>
    public static long EstimateBytes(
        string sourceInstanceFolder,
        string sourceGameRoot,
        InstanceCloneOptions? options = null)
    {
        options ??= new InstanceCloneOptions();
        if (options.MaxFiles <= 0 || options.MaxBytes <= 0) throw new IOException("测试副本的文件数量或大小上限无效。");
        string source = NormalizePath(sourceInstanceFolder, "未指定原版本目录。");
        string gameRoot = NormalizePath(sourceGameRoot, "未指定游戏目录。");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("找不到原版本目录。");

        string jsonName = FindVersionJsonName(source, Path.GetFileName(source));
        List<string> warnings = [];
        Dictionary<string, string> entries = Inventory(source, gameRoot, options, jsonName, CancellationToken.None, message =>
        {
            warnings.Add(message);
            options.Warn?.Invoke(message);
        });

        long bytes = 0;
        foreach (string address in entries.Values)
        {
            bytes = checked(bytes + new FileInfo(address).Length);
        }

        return bytes;
    }

    /// <summary>
    /// 读取副本的 <c>PCL/CloneOrigin.json</c>。字段缺失、路径不合法、
    /// 或来源与副本不在同一个 <c>versions</c> 目录时返回 null。
    /// </summary>
    public static InstanceCloneOrigin? ReadOrigin(string instanceFolder)
    {
        try
        {
            string clone = NormalizePath(instanceFolder, "未指定副本目录。");
            string address = Path.Combine(clone, "PCL", "CloneOrigin.json");
            if (!File.Exists(address)) return null;
            FileInfo info = new(address);
            if (info.Length <= 0 || info.Length > MaxSmallTextBytes) return null;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(address), DocumentOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("schema", out JsonElement schema)
                || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out int schemaValue)
                || schemaValue != 1)
            {
                return null;
            }

            string? source = ReadString(root, "sourceVersionFolder");
            string? gameRoot = ReadString(root, "sourceGameRoot");
            string? sourceName = ReadString(root, "sourceName");
            string? cloneName = ReadString(root, "cloneName");
            string? created = ReadString(root, "createdUtc") ?? ReadString(root, "created");
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(gameRoot)
                || string.IsNullOrEmpty(sourceName) || string.IsNullOrEmpty(cloneName)
                || string.IsNullOrEmpty(created))
            {
                return null;
            }

            if (!Path.IsPathRooted(source) || !Path.IsPathRooted(gameRoot)) return null;
            if (!DateTime.TryParse(
                    created,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out DateTime createdUtc))
            {
                return null;
            }

            string sourceFull = NormalizePath(source, "来源路径无效。");
            string gameRootFull = NormalizePath(gameRoot, "来源游戏目录无效。");
            if (PathEquals(sourceFull, clone)) return null;
            if (!PathEquals(Path.GetDirectoryName(sourceFull), Path.GetDirectoryName(clone))) return null;
            if (!string.Equals(Path.GetFileName(sourceFull), sourceName, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(Path.GetFileName(clone), cloneName, StringComparison.OrdinalIgnoreCase)) return null;

            return new InstanceCloneOrigin(
                sourceFull,
                gameRootFull,
                sourceName,
                cloneName,
                DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc));
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

    private static Dictionary<string, string> Inventory(
        string versionFolder,
        string gameRoot,
        InstanceCloneOptions options,
        string jsonName,
        CancellationToken token,
        Action<string> warn)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        token.ThrowIfCancellationRequested();

        Add(result, "v/" + jsonName, Path.Combine(versionFolder, jsonName), options);
        foreach (string address in Directory.EnumerateFiles(versionFolder))
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(address);
            if (!Path.GetExtension(fileName).Equals(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            if (InstanceCloneRules.IsExcluded(fileName)) continue;
            if (IsLink(address))
            {
                HandleLink(fileName, options, warn);
                continue;
            }

            Add(result, "v/" + fileName, address, options);
        }

        foreach (string fixedFile in InstanceCloneRules.VersionFixedFiles)
        {
            token.ThrowIfCancellationRequested();
            string address = Path.Combine(versionFolder, fixedFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(address)) continue;
            if (IsLink(address))
            {
                HandleLink(fixedFile, options, warn);
                continue;
            }

            Add(result, "v/" + fixedFile, address, options);
        }

        foreach (string folder in InstanceCloneRules.EnvironmentFolders)
        {
            Walk(Path.Combine(gameRoot, folder), folder, options, result, token, warn);
        }

        if (options.CopySaves) Walk(Path.Combine(gameRoot, "saves"), "saves", options, result, token, warn);
        if (options.CopyScreenshots) Walk(Path.Combine(gameRoot, "screenshots"), "screenshots", options, result, token, warn);

        foreach (string address in Directory.EnumerateFiles(gameRoot))
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(address);
            bool wanted = InstanceCloneRules.IsGameRootFile(fileName)
                          || (options.CopyServers
                              && (fileName.Equals("servers.dat", StringComparison.OrdinalIgnoreCase)
                                  || fileName.Equals("servers.dat_old", StringComparison.OrdinalIgnoreCase)));
            if (!wanted || InstanceCloneRules.IsExcluded(fileName)) continue;
            if (IsLink(address))
            {
                HandleLink(fileName, options, warn);
                continue;
            }

            Add(result, "g/" + fileName, address, options);
        }

        return result;
    }

    private static void Walk(
        string folder,
        string relative,
        InstanceCloneOptions options,
        Dictionary<string, string> result,
        CancellationToken token,
        Action<string> warn,
        int depth = 0)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder)) return;
        if (depth > 64) throw new IOException("环境目录嵌套过深，无法安全复制。");
        if (IsLink(folder))
        {
            HandleLink(relative, options, warn);
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            string child = relative + "/" + name;
            if (InstanceCloneRules.IsExcluded(child)) continue;
            bool directory = Directory.Exists(entry);
            if (IsLink(entry))
            {
                HandleLink(child, options, warn);
                continue;
            }

            if (directory)
            {
                Walk(entry, child, options, result, token, warn, depth + 1);
            }
            else
            {
                Add(result, "g/" + child, entry, options);
            }
        }
    }

    private static void HandleLink(string relative, InstanceCloneOptions options, Action<string> warn)
    {
        if (!options.SkipLinks) throw new InvalidOperationException(LinkMessagePrefix + relative);
        warn("已跳过链接目录：" + relative);
    }

    private static void Add(Dictionary<string, string> result, string key, string address, InstanceCloneOptions options)
    {
        if (!result.TryAdd(key, address)) return;
        if (result.Count > options.MaxFiles) throw new IOException("复制文件过多，请先缩小复制范围。");
    }

    private static long ProgressWeight(Dictionary<string, string> entries, InstanceCloneOptions options, CancellationToken token)
    {
        long bytes = 0;
        long weight = 0;
        foreach (string address in entries.Values)
        {
            token.ThrowIfCancellationRequested();
            long length = new FileInfo(address).Length;
            bytes = checked(bytes + length);
            if (bytes > options.MaxBytes) throw new IOException("复制范围超过大小上限，请先缩小复制范围。");
            weight = checked(weight + Math.Max(1L, length));
        }

        return Math.Max(1L, weight);
    }

    private static Dictionary<string, string> Fingerprint(
        Dictionary<string, string> entries,
        InstanceCloneOptions options,
        long totalWeight,
        CancellationToken token,
        Action<double>? progress)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        long completed = 0;
        foreach (KeyValuePair<string, string> pair in entries)
        {
            token.ThrowIfCancellationRequested();
            long length = new FileInfo(pair.Value).Length;
            bytes = checked(bytes + length);
            if (bytes > options.MaxBytes) throw new IOException("复制范围超过大小上限，请先缩小复制范围。");
            long weight = Math.Max(1L, length);
            double from = completed / (double)totalWeight;
            double to = (completed + weight) / (double)totalWeight;
            result.Add(
                pair.Key,
                Digest(pair.Value, token, fraction => progress?.Invoke(Map(from, to, fraction))));
            completed = checked(completed + weight);
        }

        progress?.Invoke(1);
        return result;
    }

    private static string Digest(string address, CancellationToken token, Action<double>? progress = null)
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
        long length = input.Length;
        long readTotal = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            sha1.TransformBlock(buffer, 0, read, null, 0);
            readTotal += read;
            if (length > 0) progress?.Invoke(Math.Min(1d, readTotal / (double)length));
        }

        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        progress?.Invoke(1);
        return "sha1:" + Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    private static void CopyFile(
        string source,
        string destination,
        string expectedHash,
        CancellationToken token,
        Action<double>? progress)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        using (FileStream input = new(
                   source,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   BufferSize,
                   FileOptions.SequentialScan))
        using (FileStream output = new(
                   destination,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   BufferSize,
                   FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[BufferSize];
            long length = input.Length;
            long readTotal = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                readTotal += read;
                if (length > 0) progress?.Invoke(0.75 * Math.Min(1d, readTotal / (double)length));
            }

            output.Flush(true);
        }

        string actualHash = Digest(destination, token, fraction => progress?.Invoke(0.75 + 0.25 * fraction));
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new IOException("复制期间来源文件发生变化，未生成测试副本。");
        }

        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        progress?.Invoke(1);
    }

    private static void PrepareVersion(
        string stage,
        string oldFolder,
        string newFolder,
        string oldJsonName,
        string newName,
        CancellationToken token)
    {
        string oldName = Path.GetFileName(oldFolder);
        string jsonAddress = Path.Combine(stage, oldJsonName);
        string text = ReadSmallText(jsonAddress, MaxVersionJsonBytes);
        JsonObject data;
        try
        {
            data = JsonNode.Parse(text, null, DocumentOptions) as JsonObject
                   ?? throw new IOException("版本 JSON 的根不是对象，无法安全克隆。");
        }
        catch (JsonException error)
        {
            throw new IOException("版本 JSON 无法解析，已停止生成测试副本。", error);
        }

        string? inherit = Text(data["inheritsFrom"]);
        if (string.Equals(inherit, oldName, StringComparison.Ordinal))
        {
            throw new IOException("版本 JSON 包含自引用继承关系，无法安全克隆。");
        }

        JsonNode? jarNode = data["jar"];
        if (jarNode is not null)
        {
            string? jarName = Text(jarNode);
            if (jarName is null) throw new IOException("版本 JSON 的 jar 字段无效。");
            if (string.Equals(jarName, oldName, StringComparison.Ordinal)) data["jar"] = newName;
        }

        data["id"] = newName;
        RewritePaths(data, oldFolder, newFolder);

        string newJson = Path.Combine(stage, newName + ".json");
        if (!PathEquals(jsonAddress, newJson) && File.Exists(newJson))
        {
            throw new IOException("目标版本目录已存在同名 JSON 文件，无法安全改名。");
        }

        File.Delete(jsonAddress);
        File.WriteAllText(newJson, data.ToJsonString(JsonOptions), new UTF8Encoding(false));

        string oldJar = Path.Combine(stage, oldName + ".jar");
        string newJar = Path.Combine(stage, newName + ".jar");
        if (File.Exists(oldJar) && !PathEquals(oldJar, newJar))
        {
            if (File.Exists(newJar)) throw new IOException("目标版本目录已存在同名 jar 文件，无法安全改名。");
            File.Move(oldJar, newJar);
        }

        string setupAddress = Path.Combine(stage, "PCL", "Setup.ini");
        StringBuilder setup = new();
        if (File.Exists(setupAddress))
        {
            foreach (string line in ReadSmallText(setupAddress, MaxSmallTextBytes)
                         .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                int separator = line.IndexOf(':');
                if (separator <= 0) continue;
                string key = line[..separator].Trim();
                if (!InstanceCloneRules.IsSetupKey(key)) continue;
                if (ForcedSetupKeys.Contains(key)) continue;
                setup.AppendLine(ReplacePaths(line, oldFolder, newFolder));
            }
        }

        setup.AppendLine("VersionLaunchCount:0");
        setup.AppendLine("VersionArgumentIndie:1");
        setup.AppendLine("VersionArgumentIndieV2:True");
        setup.AppendLine("IsStar:False");
        setup.AppendLine("CustomInfo:测试副本 · 来自 " + oldName);
        Directory.CreateDirectory(Path.GetDirectoryName(setupAddress)!);
        File.WriteAllText(setupAddress, setup.ToString(), new UTF8Encoding(false));

        string configAddress = Path.Combine(stage, "PCL", "config.json");
        if (File.Exists(configAddress))
        {
            string configText = ReadSmallText(configAddress, MaxVersionJsonBytes);
            JsonNode? config;
            try
            {
                config = JsonNode.Parse(configText, null, DocumentOptions);
            }
            catch (JsonException error)
            {
                throw new IOException("PCL 配置无法解析，已停止生成测试副本。", error);
            }

            if (config is null) throw new IOException("PCL 配置无法解析，已停止生成测试副本。");
            RewritePaths(config, oldFolder, newFolder);
            File.WriteAllText(configAddress, config.ToJsonString(JsonOptions), new UTF8Encoding(false));
        }

        token.ThrowIfCancellationRequested();
    }

    private static void RewritePaths(JsonNode? node, string oldFolder, string newFolder)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> pair in obj.ToList())
                {
                    RewritePaths(pair.Value, oldFolder, newFolder);
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    RewritePaths(array[i], oldFolder, newFolder);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out string? text) && text is not null:
                string replaced = ReplacePaths(text, oldFolder, newFolder);
                if (!string.Equals(replaced, text, StringComparison.Ordinal))
                {
                    value.ReplaceWith(JsonValue.Create(replaced));
                }

                break;
        }
    }

    private static string ReplacePaths(string text, string oldFolder, string newFolder)
    {
        string result = text
            .Replace(oldFolder + "\\", newFolder + "\\", StringComparison.Ordinal)
            .Replace(oldFolder.Replace('\\', '/') + "/", newFolder.Replace('\\', '/') + "/", StringComparison.Ordinal);
        return string.Equals(result, oldFolder, StringComparison.Ordinal) ? newFolder : result;
    }

    private static void WriteOrigin(
        string stage,
        string source,
        string gameRoot,
        string sourceName,
        string newName)
    {
        string address = Path.Combine(stage, "PCL", "CloneOrigin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(address)!);
        var payload = new
        {
            schema = 1,
            sourceVersionFolder = source,
            sourceGameRoot = gameRoot,
            sourceName,
            cloneName = newName,
            createdUtc = DateTime.UtcNow
        };
        File.WriteAllText(address, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
    }

    private static void Verify(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        if (expected.Count != actual.Count) throw new InvalidOperationException(SourceChangedMessage);
        foreach (KeyValuePair<string, string> pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out string? hash)
                || !string.Equals(hash, pair.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(SourceChangedMessage);
            }
        }
    }

    private static string FindVersionJsonName(string folder, string versionName)
    {
        string exact = Path.Combine(folder, versionName + ".json");
        if (File.Exists(exact)) return Path.GetFileName(exact);

        int inspected = 0;
        foreach (string address in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (++inspected > 128) break;
            try
            {
                if (new FileInfo(address).Length > MaxVersionJsonBytes) continue;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(address), DocumentOptions);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("id", out _)
                    && root.TryGetProperty("type", out _)
                    && root.TryGetProperty("mainClass", out _))
                {
                    return Path.GetFileName(address);
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                // 候选 JSON 不可读或不是版本清单时继续找下一个。
            }
        }

        throw new IOException("没有找到有效的 Minecraft 版本 JSON。");
    }

    private static string ReadSmallText(string address, long limit)
    {
        FileInfo info = new(address);
        if (info.Length > limit) throw new IOException("环境描述文件过大，已停止生成测试副本。");
        return File.ReadAllText(address);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void DeleteStage(string parent, string stage)
    {
        try
        {
            if (!Directory.Exists(stage)) return;
            if (!StageNamePattern.IsMatch(Path.GetFileName(stage)) || !IsWithin(parent, stage)) return;
            DeleteDirectory(stage);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 清理失败不应掩盖真正的复制错误；残留目录以 .pcl-clone- 开头，可被后续清理识别。
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

    private static string Resolve(string root, string relative)
    {
        string normalized = (relative ?? "").Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) throw new IOException("环境文件路径无效。");
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part is "." or "..") throw new IOException("环境文件路径越界，已停止操作。");
        }

        string combined = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, combined)) throw new IOException("环境文件路径越界，已停止操作。");
        return combined;
    }

    private static bool IsWithin(string root, string path)
    {
        string fullRoot = NormalizePath(root, "环境目录无效。");
        string fullPath = NormalizePath(path, "环境文件路径无效。");
        if (string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(
            NormalizePath(left, "路径无效。"),
            NormalizePath(right, "路径无效。"),
            StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static double Map(double from, double to, double fraction) =>
        from + (to - from) * Math.Clamp(fraction, 0d, 1d);

    private static void Report(
        Action<InstanceCloneProgress>? progress,
        double value,
        string stage,
        int files,
        long bytes) =>
        progress?.Invoke(new InstanceCloneProgress(Math.Clamp(value, 0d, 1d), stage, files, bytes));
}
