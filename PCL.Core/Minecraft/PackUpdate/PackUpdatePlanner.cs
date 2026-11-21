using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace PCL.Core.Minecraft.PackUpdate;

/// <summary>
/// 更新预览规划器：把整合包清单与本地实例逐文件比对，生成
/// 「新增 / 更新 / 移除 / 保留」四类差异。本类只读，不修改任何文件。
/// </summary>
/// <remarks>
/// 规划阶段只能看到「新整合包 + 当前实例」，无法判断某个本地文件是不是旧版整合包带入的，
/// 因此本地独有文件一律归为 <see cref="PackUpdateKind.Preserved"/>，不会产生
/// <see cref="PackUpdateKind.Removed"/>。需要移除旧版作者文件时，由调用方依据上次安装
/// 记录构造 <see cref="PackUpdateKind.Removed"/> 条目，再交给
/// <see cref="PackUpdateApplier"/> 执行；应用器会自动跳过被用户修改过的移除项。
/// </remarks>
public static class PackUpdatePlanner
{
    private const int BufferSize = 131_072;
    private const int MaxDepth = 64;

    /// <summary>
    /// 生成更新预览。
    /// </summary>
    /// <param name="package">已读取的整合包。</param>
    /// <param name="gameRoot">实例游戏根目录（版本隔离时就是版本目录）。</param>
    /// <param name="token">取消操作。</param>
    /// <exception cref="DirectoryNotFoundException">游戏根目录不存在。</exception>
    /// <exception cref="InvalidDataException">包内路径越界或包含非法字符。</exception>
    public static PackUpdatePlan Plan(PackPackage package, string gameRoot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(gameRoot)) throw new ArgumentException("未指定实例游戏目录。", nameof(gameRoot));

        string root = PackUpdatePaths.NormalizeRoot(gameRoot, "实例游戏目录无效。");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("找不到实例游戏目录。");
        token.ThrowIfCancellationRequested();

        List<PackUpdateEntry> entries = [];
        HashSet<string> packagePaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> pair in package.Files)
        {
            token.ThrowIfCancellationRequested();
            string relative = PackUpdatePaths.NormalizeRelative(pair.Key);
            if (!packagePaths.Add(relative)) continue;
            if (PackUpdatePaths.IsExcluded(relative)) continue;

            string target = PackUpdatePaths.Resolve(root, relative);
            string? incomingSha1 = string.IsNullOrWhiteSpace(pair.Value)
                ? null
                : pair.Value.Trim().ToLowerInvariant();

            if (!File.Exists(target))
            {
                entries.Add(new PackUpdateEntry(
                    relative,
                    PackUpdateKind.Added,
                    "本地不存在",
                    null,
                    incomingSha1,
                    false));
                continue;
            }

            string existingSha1 = HashFile(target, token);
            if (incomingSha1 is not null
                && string.Equals(existingSha1, incomingSha1, StringComparison.OrdinalIgnoreCase))
            {
                // 内容完全一致，不需要写盘，但仍然列进预览的「保留」表。
                entries.Add(new PackUpdateEntry(
                    relative,
                    PackUpdateKind.Preserved,
                    "内容一致",
                    existingSha1,
                    incomingSha1,
                    false));
                continue;
            }

            entries.Add(new PackUpdateEntry(
                relative,
                PackUpdateKind.Updated,
                incomingSha1 is null ? "整合包未提供哈希，无法校验，将覆盖" : "本地内容与整合包不一致",
                existingSha1,
                incomingSha1,
                // 用户完全可能手工改过本地文件，覆盖前必须让界面提示风险。
                true));
        }

        // 本地独有的文件：只读遍历，绝不触碰保护目录。
        WalkLocal(root, "", entries, packagePaths, token, 0);

        entries.Sort(static (left, right) =>
        {
            int byKind = left.Kind.CompareTo(right.Kind);
            if (byKind != 0) return byKind;
            int byPath = string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase);
            return byPath != 0
                ? byPath
                : string.CompareOrdinal(left.RelativePath, right.RelativePath);
        });

        token.ThrowIfCancellationRequested();
        return new PackUpdatePlan(package.Name, package.VersionId, entries);
    }

    private static void WalkLocal(
        string folder,
        string relative,
        List<PackUpdateEntry> entries,
        HashSet<string> packagePaths,
        CancellationToken token,
        int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > MaxDepth) throw new IOException("实例目录嵌套过深，无法安全生成更新预览。");

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(folder);
        }
        catch (Exception error) when (error is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // 枚举过程中目录被删除或不可读时，跳过这一层，不让预览整体失败。
            return;
        }

        foreach (string address in children)
        {
            token.ThrowIfCancellationRequested();

            string name = Path.GetFileName(address);
            string child = relative.Length == 0 ? name : relative + "/" + name;
            if (PackUpdatePaths.IsExcluded(child)) continue;

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(address);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            // 不跟随符号链接/目录联接，避免预览把实例外部的内容也算进来。
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

            if ((attributes & FileAttributes.Directory) != 0)
            {
                WalkLocal(address, child, entries, packagePaths, token, depth + 1);
                continue;
            }

            if (packagePaths.Contains(child)) continue;
            entries.Add(new PackUpdateEntry(
                child,
                PackUpdateKind.Preserved,
                "个人文件或非整合包内容",
                null,
                null,
                false));
        }
    }

    /// <summary>计算文件的 sha1 小写十六进制值；大文件分块读取并响应取消。</summary>
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
}
