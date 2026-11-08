using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.EnvironmentHistory;

/// <summary>
/// 恢复事务：先落盘日志再动文件，失败时逆序回滚；崩溃后由 <see cref="RecoverPending"/> 收尾。
/// </summary>
/// <remarks>
/// 日志放在 <c>{版本目录}PCL\EnvironmentBackups\{guid}\transaction.json</c>，与游戏目录同盘，
/// 这样即使数据目录被清理，恢复中断的现场依然能被下一次启动发现。
/// </remarks>
public sealed partial class EnvironmentHistoryEngine
{
    private const string TransactionInvalidMessage = "恢复事务日志已损坏。";
    private const string TransactionMismatchMessage = "中断的恢复事务与当前游戏目录不匹配，已跳过。";
    private const string TransactionRollbackFailedMessage = "恢复失败，且自动回滚未完成，请检查以下文件后重试：";

    /// <summary>事务目录的上级目录。</summary>
    private string BackupRoot => Path.Combine(VersionFolder, "PCL", "EnvironmentBackups");

    /// <summary>
    /// 列举仍然存在的事务目录（含已经完成但没来得及清理的）。
    /// </summary>
    private List<string> TransactionFolders()
    {
        var result = new List<string>();
        string root = BackupRoot;
        if (!Directory.Exists(root)) return result;
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(folder, "transaction.json"))) result.Add(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWrapper.Warn(ex, "读取恢复事务目录失败：" + root);
            return result;
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static EnvironmentHistoryTransaction ReadTransaction(string folder)
    {
        string path = Path.Combine(folder, "transaction.json");
        EnvironmentHistoryTransaction? log = EnvironmentHistoryPaths.ReadJson<EnvironmentHistoryTransaction>(path, TransactionInvalidMessage);
        if (log is null || log.Schema != 1) throw new IOException(TransactionInvalidMessage);
        log.Operations ??= [];
        foreach (EnvironmentHistoryOperation operation in log.Operations)
        {
            operation.Relative = EnvironmentHistoryPaths.NormalizeRelative(operation.Relative);
            if (operation.OldHash is not null) EnvironmentHistoryPaths.RequireHash(operation.OldHash);
        }

        return log;
    }

    /// <summary>
    /// 按顺序应用已算好的差集。每个操作应用前都会复核「当前摘要仍是 oldHash」，
    /// 被外部改动过的文件跳过并记一条 warning（不中断整次恢复）。
    /// </summary>
    private void ApplyTransaction(IReadOnlyList<EnvironmentHistoryOperation> operations, CancellationToken token, Action<double>? progress)
    {
        if (operations.Count == 0) return;

        string folder = Path.Combine(BackupRoot, EnvironmentHistoryPaths.NewToken());
        string journal = Path.Combine(folder, "transaction.json");
        var log = new EnvironmentHistoryTransaction
        {
            Schema = 1,
            Complete = false,
            CreatedUtc = DateTime.UtcNow,
            GameRoot = GameRoot,
            Operations = [.. operations]
        };
        Directory.CreateDirectory(folder);
        EnvironmentHistoryPaths.WriteJsonAtomic(journal, log);

        var applied = new List<int>(operations.Count);
        try
        {
            for (int index = 0; index < operations.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                EnvironmentHistoryOperation operation = operations[index];
                string destination = ResolveGamePath(operation.Relative);
                if (!TryDigest(destination, out string? current))
                {
                    // 读不出内容就无法确认现场，按「已被外部改动」处理并跳过。
                    continue;
                }

                if (!string.Equals(current, operation.OldHash, StringComparison.Ordinal))
                {
                    LogWrapper.Warn("恢复期间文件已被外部修改，已跳过：" + operation.Relative);
                    continue;
                }

                // 测试注入点：正式运行时为 null，不会影响任何行为。
                BeforeApply?.Invoke(operation.Relative);

                applied.Add(index);
                ApplyOperation(operation, destination, token);
                progress?.Invoke((index + 1d) / operations.Count);
            }

            log.Complete = true;
            EnvironmentHistoryPaths.WriteJsonAtomic(journal, log);
            Directory.Delete(folder, true);
        }
        catch (Exception failure)
        {
            string? errors = Rollback(operations, applied);
            if (errors is null)
            {
                EnvironmentHistoryPaths.TryDeleteDirectory(folder);
            }
            else
            {
                // 回滚不完整时保留事务目录，让下一次 RecoverPending 继续处理。
                throw new IOException(TransactionRollbackFailedMessage + errors, failure);
            }

            throw;
        }
    }

    private void ApplyOperation(EnvironmentHistoryOperation operation, string destination, CancellationToken token)
    {
        if (operation.Action == EnvironmentHistoryAction.Delete)
        {
            if (!File.Exists(destination)) return;
            File.SetAttributes(destination, FileAttributes.Normal);
            File.Delete(destination);
            return;
        }

        string newHash = EnvironmentHistoryPaths.RequireHash(operation.NewHash);
        string blob = BlobPath(newHash);
        if (!File.Exists(blob)) throw new IOException(BlobMissingMessage);
        EnvironmentHistoryPaths.CopyVerified(blob, destination, newHash, token);
        if (operation.NewWriteUtc == default) return;
        try
        {
            File.SetLastWriteTimeUtc(destination, operation.NewWriteUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 时间戳只是展示信息，写不进去不影响内容正确性。
            LogWrapper.Warn(ex, "恢复文件时间戳失败：" + operation.Relative);
        }
    }

    /// <summary>
    /// 逆序回滚已应用的步骤。返回 null 表示全部回滚成功。
    /// 回滚只用 <see cref="EnvironmentHistoryOperation.OldHash"/>（内容寻址），不依赖清单是否可读。
    /// </summary>
    private string? Rollback(IReadOnlyList<EnvironmentHistoryOperation> operations, IReadOnlyList<int> applied)
    {
        var errors = new List<string>();
        for (int cursor = applied.Count - 1; cursor >= 0; cursor--)
        {
            EnvironmentHistoryOperation operation = operations[applied[cursor]];
            try
            {
                string destination = ResolveGamePath(operation.Relative);
                if (TryDigest(destination, out string? current)
                    && string.Equals(current, operation.OldHash, StringComparison.Ordinal))
                {
                    continue; // 已经是恢复前的内容
                }

                if (operation.OldHash is null)
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    continue;
                }

                string blob = BlobPath(operation.OldHash);
                if (!File.Exists(blob))
                {
                    errors.Add(operation.Relative + "（原内容缺失）");
                    continue;
                }

                EnvironmentHistoryPaths.CopyVerified(blob, destination, operation.OldHash, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(operation.Relative + "（" + ex.Message + "）");
            }
        }

        return errors.Count == 0 ? null : string.Join("；", errors);
    }

    /// <summary>是否存在未完成的恢复事务。</summary>
    public bool HasPendingRestore
    {
        get
        {
            lock (Gate)
            {
                foreach (string folder in TransactionFolders())
                {
                    try
                    {
                        if (!ReadTransaction(folder).Complete) return true;
                    }
                    catch (IOException)
                    {
                        // 日志损坏也算「需要处理」，否则宿主会以为环境是干净的。
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// 处理遗留事务：已完成的直接清理目录，未完成的按日志逆序回滚到恢复前的内容。
    /// </summary>
    public void RecoverPending()
    {
        lock (Gate)
        using (new BusyScope(this))
        using (new StoreLease(StoreRoot))
        {
            if (RecoverCore(CancellationToken.None)) Changed?.Invoke();
        }
    }

    /// <summary>恢复策略的实际实现，返回是否改动过现场。</summary>
    private bool RecoverCore(CancellationToken token)
    {
        List<string> folders = TransactionFolders();
        if (folders.Count == 0) return false;

        var errors = new List<string>();
        bool changed = false;
        foreach (string folder in folders)
        {
            token.ThrowIfCancellationRequested();
            EnvironmentHistoryTransaction log;
            try
            {
                log = ReadTransaction(folder);
            }
            catch (IOException ex)
            {
                errors.Add(folder + "（" + ex.Message + "）");
                continue;
            }

            if (log.Complete)
            {
                EnvironmentHistoryPaths.TryDeleteDirectory(folder);
                changed = true;
                continue;
            }

            if (!EnvironmentHistoryPaths.PathEquals(log.GameRoot, GameRoot))
            {
                errors.Add(folder + "（" + TransactionMismatchMessage + "）");
                continue;
            }

            // 无法确定哪些步骤已经生效，因此全量逆序回滚：内容一致时自动跳过，天然幂等。
            var applied = Enumerable.Range(0, log.Operations.Count).ToArray();
            string? rollbackErrors = Rollback(log.Operations, applied);
            if (rollbackErrors is null)
            {
                EnvironmentHistoryPaths.TryDeleteDirectory(folder);
                changed = true;
            }
            else
            {
                errors.Add(folder + "（" + rollbackErrors + "）");
            }
        }

        if (errors.Count > 0)
        {
            throw new IOException(TransactionRollbackFailedMessage + string.Join("；", errors));
        }

        return changed;
    }
}
