using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Options;

/// <summary>
/// 个人游戏习惯的数据层：负责全局档案 <c>personal-options.json</c>、逐次会话文件
/// <c>options-sessions/{Id}.json</c> 以及 <c>options.txt</c> 的读取与原子写回。
/// </summary>
/// <remarks>
/// <para>
/// 所有写入都走「同目录临时文件 + <see cref="File.Move(string, string, bool)"/> 覆盖」，
/// 避免写入过程中断电或崩溃把文件截断。
/// </para>
/// <para>
/// <c>options.txt</c> 的读取会拒绝大于 <see cref="MaxOptionsBytes"/> 的文件与符号链接（重解析点），
/// 与参考实现保持一致；文件不存在时返回 <see langword="null"/>，绝不创建。
/// </para>
/// </remarks>
public class PersonalOptionsStore
{
    /// <summary><c>options.txt</c> 的文件名。</summary>
    public const string OptionsFileName = "options.txt";

    /// <summary>全局档案的文件名。</summary>
    public const string ProfileFileName = "personal-options.json";

    /// <summary>会话文件所在的子目录名。</summary>
    public const string SessionDirectoryName = "options-sessions";

    /// <summary><c>options.txt</c> 允许的最大字节数（4 MiB）。</summary>
    public const int MaxOptionsBytes = 4 * 1024 * 1024;

    /// <summary>全局档案的格式版本，写入时固定为 1。</summary>
    private const int ProfileVersion = 1;

    private const string OptionsMissingMessage = "options.txt 尚未生成，跳过个人游戏习惯同步。";

    private static readonly JsonSerializerOptions _JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>创建数据层。</summary>
    /// <param name="dataDirectory">保存全局档案与会话文件的数据目录，不存在时会在首次写入时创建。</param>
    /// <exception cref="ArgumentException">数据目录为空时抛出。</exception>
    public PersonalOptionsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// <summary>数据目录的完整路径。</summary>
    public string DataDirectory { get; }

    /// <summary>全局档案 <c>personal-options.json</c> 的完整路径。</summary>
    public string ProfilePath => Path.Combine(DataDirectory, ProfileFileName);

    /// <summary>会话文件目录 <c>options-sessions</c> 的完整路径。</summary>
    public string SessionDirectory => Path.Combine(DataDirectory, SessionDirectoryName);

    /// <summary>返回指定游戏目录下 <c>options.txt</c> 的完整路径。</summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <returns><c>options.txt</c> 的完整路径。</returns>
    /// <exception cref="ArgumentException">游戏目录为空时抛出。</exception>
    public static string OptionsPath(string gameFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        return Path.Combine(Path.GetFullPath(gameFolder), OptionsFileName);
    }

    /// <summary>计算原始字节的 SHA-256，输出小写十六进制字符串。</summary>
    /// <param name="bytes">原始字节。</param>
    /// <returns>64 个小写十六进制字符。</returns>
    public static string ComputeHash(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// 读取 <c>options.txt</c> 并按 UTF-8 解码，同时记录写回时用于冲突检测的哈希。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <returns>快照；文件不存在时返回 <see langword="null"/>，此方法绝不创建文件。</returns>
    /// <exception cref="IOException">文件过大或属于重解析点时抛出。</exception>
    public virtual PersonalOptionsSnapshot? ReadOptions(string gameFolder)
    {
        string path = OptionsPath(gameFolder);
        if (!File.Exists(path)) return null;

        FileInfo info = new(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不读取链接形式或过大的 options.txt。");
        if (info.Length > MaxOptionsBytes)
            throw new IOException($"不读取链接形式或过大的 options.txt（{info.Length} 字节，上限 {MaxOptionsBytes} 字节）。");

        byte[] bytes = File.ReadAllBytes(path);
        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        // options.txt 由 Minecraft 以 UTF-8 写入；严格解码失败说明文件已被别的编码改坏，直接报错而不是悄悄替换字符。
        string text = PersonalOptionsRules.Decode(bytes);

        return new PersonalOptionsSnapshot
        {
            GameFolder = Path.GetFullPath(gameFolder),
            Text = text,
            Hash = ComputeHash(bytes),
            Length = bytes.Length,
            HasBom = hasBom,
            Values = PersonalOptionsRules.Parse(text)
        };
    }

    /// <summary>
    /// 把合并后的文本原子写回 <c>options.txt</c>。写入前会重新读取文件并比较
    /// <paramref name="expectedHash"/>，不一致说明文件已被游戏或别的程序改过，
    /// 此时放弃写入并抛出异常，避免覆盖玩家的新设置。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <param name="mergedText">合并后的完整文本。</param>
    /// <param name="expectedHash">读取时计算的哈希，用于冲突检测。</param>
    /// <param name="hasBom">写回时是否保留 UTF-8 BOM。</param>
    /// <returns>真正发生写入返回 <see langword="true"/>；内容无变化返回 <see langword="false"/>。</returns>
    /// <exception cref="IOException">目标文件消失、被修改、过大或属于重解析点时抛出。</exception>
    public virtual bool WriteOptions(string gameFolder, string mergedText, string expectedHash, bool hasBom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(mergedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);

        string path = OptionsPath(gameFolder);
        if (!File.Exists(path)) throw new IOException(OptionsMissingMessage);

        FileInfo info = new(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不写入链接形式的 options.txt，已跳过。");

        byte[] current = File.ReadAllBytes(path);
        if (!string.Equals(ComputeHash(current), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("配置文件在写入前被修改，已跳过。");

        Encoding encoding = hasBom ? new UTF8Encoding(true) : new UTF8Encoding(false);
        byte[] body = encoding.GetBytes(mergedText);
        if (body.Length > MaxOptionsBytes)
            throw new IOException($"合并后的 options.txt 超过 {MaxOptionsBytes} 字节上限，已跳过写入。");

        // 文本按字节比较：合并结果与原文件字节完全一致时不写盘，避免无意义的文件时间戳变化。
        if (current.AsSpan().SequenceEqual(body)) return false;

        _AtomicWrite(path, body);
        return true;
    }

    /// <summary>
    /// 读取全局档案。文件不存在、内容损坏或版本不受支持时返回空字典。
    /// </summary>
    /// <returns>键值字典，键已按 <see cref="PersonalOptionsRules.CanonicalKey"/> 归一。</returns>
    public virtual IReadOnlyDictionary<string, string> ReadProfile()
    {
        if (!File.Exists(ProfilePath)) return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            string text = File.ReadAllText(ProfilePath, Encoding.UTF8);
            _ProfileDocument? document = JsonSerializer.Deserialize<_ProfileDocument>(text, _JsonOptions);
            if (document is null || document.Version > ProfileVersion) return new Dictionary<string, string>(StringComparer.Ordinal);

            Dictionary<string, string> values = new(StringComparer.Ordinal);
            if (document.Values is not null)
            {
                foreach (KeyValuePair<string, string?> pair in document.Values)
                {
                    if (pair.Value is null) continue;
                    values[PersonalOptionsRules.CanonicalKey(pair.Key)] = pair.Value;
                }
            }
            return values;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 原子写入全局档案，键会先按 <see cref="PersonalOptionsRules.CanonicalKey"/> 归一。
    /// </summary>
    /// <param name="values">要保存的键值。</param>
    /// <exception cref="IOException">写入失败时抛出。</exception>
    public virtual void WriteProfile(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        Dictionary<string, string> normalized = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in values)
            normalized[PersonalOptionsRules.CanonicalKey(pair.Key)] = pair.Value;

        _ProfileDocument document = new()
        {
            Version = ProfileVersion,
            Values = new SortedDictionary<string, string?>(normalized.ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value,
                StringComparer.Ordinal), StringComparer.Ordinal)
        };

        string json = JsonSerializer.Serialize(document, _JsonOptions);
        Directory.CreateDirectory(DataDirectory);
        _AtomicWrite(ProfilePath, new UTF8Encoding(false).GetBytes(json + "\n"));
    }

    /// <summary>返回指定会话的文件路径。</summary>
    /// <param name="id">会话标识。</param>
    /// <returns>会话文件的完整路径。</returns>
    /// <exception cref="ArgumentException">标识为空或包含路径分隔符时抛出。</exception>
    public string SessionPath(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal))
            throw new ArgumentException("会话标识不能包含路径分隔符或非法字符。", nameof(id));
        return Path.Combine(SessionDirectory, id + ".json");
    }

    /// <summary>原子写入会话文件，并标记为已跟踪。</summary>
    /// <param name="session">会话。</param>
    /// <exception cref="IOException">写入失败时抛出。</exception>
    public void WriteSession(PersonalOptionsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        string json = JsonSerializer.Serialize(session, _JsonOptions);
        Directory.CreateDirectory(SessionDirectory);
        _AtomicWrite(SessionPath(session.Id), new UTF8Encoding(false).GetBytes(json + "\n"));
        session.Tracked = true;
    }

    /// <summary>删除会话文件；文件已不存在时静默返回。</summary>
    /// <param name="id">会话标识。</param>
    /// <returns>确实删除了文件返回 <see langword="true"/>。</returns>
    public bool DeleteSession(string id)
    {
        string path = SessionPath(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>读取全部可解析的会话文件，跳过损坏或不完整的条目。</summary>
    /// <returns>会话列表，按文件名排序以保证结果稳定。</returns>
    public IReadOnlyList<PersonalOptionsSession> ReadSessions()
    {
        if (!Directory.Exists(SessionDirectory)) return Array.Empty<PersonalOptionsSession>();

        List<PersonalOptionsSession> sessions = [];
        foreach (string path in Directory.EnumerateFiles(SessionDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                PersonalOptionsSession? session =
                    JsonSerializer.Deserialize<PersonalOptionsSession>(File.ReadAllText(path, Encoding.UTF8), _JsonOptions);
                if (session is null) continue;
                if (string.IsNullOrWhiteSpace(session.Id))
                    session.Id = Path.GetFileNameWithoutExtension(path);
                // 反序列化不会填充只读以外的状态，这里按「磁盘上存在」视为已跟踪。
                session.Tracked = true;
                sessions.Add(session);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 单个会话文件损坏不应阻塞其余会话的恢复。
            }
        }
        return sessions;
    }

    /// <summary>判断会话文件是否存在。</summary>
    /// <param name="id">会话标识。</param>
    /// <returns>存在返回 <see langword="true"/>。</returns>
    public bool SessionExists(string id) => File.Exists(SessionPath(id));

    /// <summary>同目录临时文件 + 覆盖换名的原子写入。</summary>
    private static void _AtomicWrite(string path, byte[] body)
    {
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".pcltmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(body);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时文件清理失败不影响已经完成的写入。
            }
        }
    }

    /// <summary>全局档案的 JSON 结构，字段名由 camelCase 策略决定。</summary>
    private sealed class _ProfileDocument
    {
        /// <summary>格式版本。</summary>
        public int Version { get; set; } = ProfileVersion;

        /// <summary>键值档案，使用有序字典保证文件内容稳定、便于比对。</summary>
        public SortedDictionary<string, string?>? Values { get; set; }
    }
}
