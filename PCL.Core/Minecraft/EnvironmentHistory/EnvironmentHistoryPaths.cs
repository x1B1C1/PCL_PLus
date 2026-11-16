using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCL.Core.Minecraft.EnvironmentHistory;

/// <summary>
/// 还原点存储的路径、内容寻址布局与文件系统原语。
/// 这里集中处理路径规范化、越界校验、原子写入与摘要计算，供捕获、恢复与事务复用。
/// </summary>
internal static class EnvironmentHistoryPaths
{
    /// <summary>流式复制与摘要计算使用的缓冲区大小。</summary>
    internal const int BufferSize = 131_072;

    internal const string InvalidPathMessage = "还原点路径无效。";
    internal const string EscapePathMessage = "还原点路径越界，已拒绝操作。";
    internal const string InvalidHashMessage = "还原点内容编号无效。";
    internal const string InvalidIdMessage = "还原点编号无效。";

    private static readonly Regex HashPattern = new(@"\Asha1:[a-f0-9]{40}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Hex40Pattern = new(@"\A[a-f0-9]{40}\z", RegexOptions.CultureInvariant);
    private static readonly Regex PointIdPattern = new(@"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReservedNamePattern = new(
        @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 取绝对路径：展开相对路径、去掉 <c>\\?\</c> 前缀并裁掉结尾分隔符（根目录除外）。
    /// 所有对外记录的路径都经过这一层，保证同一目录只有一种写法。
    /// </summary>
    internal static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException(InvalidPathMessage);
        string full;
        try
        {
            full = Path.GetFullPath(StripExtendedPrefix(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException(InvalidPathMessage, ex);
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

    /// <summary>判断两个路径是否指向同一位置（大小写不敏感，均先做 <see cref="Full"/>）。</summary>
    internal static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Full(left), Full(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 计算存储目录键：<c>Base64Url(sha256(小写规范化的版本目录绝对路径))</c>，无填充。
    /// 同一个版本目录在小写驱动器、尾部分隔符等写法差异下必须落到同一个键。
    /// </summary>
    internal static string StoreKey(string versionFolder)
    {
        string normalized = Full(versionFolder).ToLowerInvariant();
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// 计算存储根目录：<c>{dataRoot}/environment-history/{storeKey}</c>。
    /// </summary>
    internal static string StoreRoot(string dataRoot, string versionFolder) =>
        Path.Combine(Full(dataRoot), "environment-history", StoreKey(versionFolder));

    /// <summary>
    /// 计算跨进程租约名。名字取 storeRoot 摘要的前 12 位十六进制，
    /// 既避免路径里的非法字符，也避免超长对象名。
    /// </summary>
    internal static string LeaseName(string storeRoot)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Full(storeRoot).ToLowerInvariant()));
        return "Local\\PCL.EnvironmentHistory." + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>判断字符串是否是合法的还原点编号（32 位小写十六进制）。</summary>
    internal static bool IsPointId(string? id) => !string.IsNullOrEmpty(id) && PointIdPattern.IsMatch(id);

    /// <summary>校验并返回还原点编号；不合法时抛中文异常。</summary>
    internal static string RequirePointId(string? id)
    {
        string text = (id ?? "").Trim();
        if (!IsPointId(text)) throw new IOException(InvalidIdMessage);
        return text;
    }

    /// <summary>判断字符串是否是合法的内容校验值（<c>sha1:</c> + 40 位小写十六进制）。</summary>
    internal static bool IsHash(string? hash) => !string.IsNullOrEmpty(hash) && HashPattern.IsMatch(hash);

    /// <summary>校验并返回内容校验值；不合法时抛中文异常。</summary>
    internal static string RequireHash(string? hash)
    {
        string text = (hash ?? "").Trim();
        if (!IsHash(text)) throw new IOException(InvalidHashMessage);
        return text;
    }

    /// <summary>取校验值里的 40 位十六进制文件名（<c>objects/&lt;hex&gt;</c>）。</summary>
    internal static string Hex(string hash) => RequireHash(hash)[5..];

    /// <summary>判断文件名是否是 40 位小写十六进制，用于区分 blob 与残留临时文件。</summary>
    internal static bool IsHex40(string? name) => !string.IsNullOrEmpty(name) && Hex40Pattern.IsMatch(name);

    /// <summary>
    /// 规范化相对路径：统一 <c>/</c> 分隔、去掉首尾分隔符，并逐段校验
    /// （拒绝 <c>..</c>、绝对路径、非法字符、保留设备名与结尾空格/点）。
    /// </summary>
    internal static string NormalizeRelative(string? relative)
    {
        string text = (relative ?? "").Replace('\\', '/').Trim().Trim('/');
        if (text.Length == 0 || text.IndexOf('\0') >= 0) throw new IOException(InvalidPathMessage);
        if (Path.IsPathRooted(text)) throw new IOException(EscapePathMessage);
        string[] parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new IOException(InvalidPathMessage);
        foreach (string part in parts) SafePart(part);
        return string.Join('/', parts);
    }

    private static void SafePart(string part)
    {
        if (part.Length == 0 || part.Trim().Length == 0) throw new IOException(InvalidPathMessage);
        if (part is "." or "..") throw new IOException(EscapePathMessage);
        if (part.EndsWith(' ') || part.EndsWith('.')) throw new IOException(InvalidPathMessage);
        if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException(InvalidPathMessage);
        if (ReservedNamePattern.IsMatch(part)) throw new IOException(InvalidPathMessage);
    }

    /// <summary>
    /// 把相对路径解析为绝对路径，并确认结果仍位于 <paramref name="root"/> 之内。
    /// 恢复时所有目标路径都必须经过这里，防止被篡改的清单写到游戏目录之外。
    /// </summary>
    internal static string Resolve(string root, string relative)
    {
        string normalized = NormalizeRelative(relative);
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(Full(root), normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException(InvalidPathMessage, ex);
        }

        if (!IsWithin(root, combined)) throw new IOException(EscapePathMessage);
        return combined;
    }

    /// <summary>判断 <paramref name="path"/> 是否位于 <paramref name="root"/> 之内（含 root 自身）。</summary>
    internal static bool IsWithin(string root, string path)
    {
        string fullRoot = Full(root);
        string fullPath = Full(path);
        if (string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 求 <paramref name="path"/> 相对 <paramref name="root"/> 的路径（<c>/</c> 分隔）；
    /// 不在 root 之下时返回 null。
    /// </summary>
    internal static string? RelativePath(string root, string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(Full(root), Full(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (relative.Length == 0 || relative == ".") return null;
        if (Path.IsPathRooted(relative)) return null;
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return relative.Replace('\\', '/');
    }

    /// <summary>把时间统一成 UTC，避免清单里的 Kind 丢失后排序错乱。</summary>
    internal static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>
    /// 原子写入字节：先写同目录临时文件并刷到磁盘，再 <c>File.Move(..., true)</c> 覆盖。
    /// 中途掉电最多留下一个临时文件，不会出现半截 JSON。
    /// </summary>
    internal static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temp, path, true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>原子写入 JSON，序列化设置见 <see cref="EnvironmentHistoryJson.Options"/>。</summary>
    internal static void WriteJsonAtomic<T>(string path, T value) =>
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, EnvironmentHistoryJson.Options));

    /// <summary>读取 JSON；不存在、过大或格式错误都转成带中文说明的 <see cref="IOException"/>。</summary>
    internal static T ReadJson<T>(string path, string invalidMessage) where T : class
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(invalidMessage, ex);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, EnvironmentHistoryJson.Options)!;
        }
        catch (JsonException ex)
        {
            throw new IOException(invalidMessage, ex);
        }
    }

    /// <summary>以「允许别人同时读写、允许删除」的方式打开文件，减少与杀毒软件/游戏进程的争用。</summary>
    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);

    /// <summary>计算单个文件的 sha1 标签。文件被删除或拒绝访问时抛出中文异常。</summary>
    internal static string Sha1(string path, CancellationToken token)
    {
        try
        {
            using FileStream stream = OpenRead(path);
            return Transfer(stream, null, token).Hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("读取环境文件失败：" + path, ex);
        }
    }

    /// <summary>
    /// 单遍复制并计算 sha1：读一次源流，同时写目标流与摘要，避免为了校验多读一遍。
    /// </summary>
    internal static (long Length, string Hash) Transfer(Stream input, Stream? output, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, BufferSize);
                if (read <= 0) break;
                hash.AppendData(buffer, 0, read);
                output?.Write(buffer, 0, read);
                total += read;
            }

            return (total, "sha1:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 把 blob 复制到目标路径：先写同目录临时文件并在复制过程中校验摘要，
    /// 校验通过后再整体替换目标，失败时目标文件保持原样。
    /// </summary>
    internal static void CopyVerified(string source, string destination, string expectedHash, CancellationToken token)
    {
        string? folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temp = destination + ".pcl-restore-" + Guid.NewGuid().ToString("N");
        try
        {
            (long length, string hash) result;
            using (FileStream input = OpenRead(source))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                result = Transfer(input, output, token);
                output.Flush(true);
            }

            if (!string.Equals(result.hash, expectedHash, StringComparison.Ordinal))
            {
                throw new IOException("还原点内容校验失败，已停止写入：" + destination);
            }

            File.Move(temp, destination, true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>删除文件，忽略「本来就不存在」。</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不影响主流程。
        }
    }

    /// <summary>递归删除目录，忽略「本来就不存在」；调用方负责决定失败是否致命。</summary>
    internal static void TryDeleteDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 事务目录清理失败不影响已经完成的结果，下次恢复扫描会再处理。
        }
    }

    /// <summary>列出目录下的文件；目录不存在时返回空集合。</summary>
    internal static IEnumerable<string> EnumerateFiles(string folder, string pattern, SearchOption option)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder, pattern, option);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("读取环境目录失败：" + folder, ex);
        }
    }

    /// <summary>把 <see cref="Guid"/> 的 N 格式包装成小写，便于在路径中使用。</summary>
    internal static string NewToken() => Guid.NewGuid().ToString("N");
}
