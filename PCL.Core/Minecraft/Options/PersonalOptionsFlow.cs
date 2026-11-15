using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.Options;

/// <summary>
/// 「个人游戏习惯」的编排层：在启动游戏前把档案合并进 <c>options.txt</c>，
/// 记录会话，玩家退出后只回收真正被改过的键。
/// </summary>
/// <remarks>
/// <para>
/// 本类不参与界面逻辑，也不改 Config；是否同步、勾选了哪些类别由调用方传入。
/// </para>
/// <para>
/// 关键的保守行为：<c>options.txt</c> 不存在时直接跳过（首次启动时该文件由游戏或
/// 默认配置 Mod 生成，启动器绝不代劳）；若文件在读取与写回之间被外部修改，写回会
/// 放弃并抛出 <see cref="IOException"/>，消息为「配置文件在写入前被修改，已跳过。」。
/// </para>
/// </remarks>
public sealed class PersonalOptionsFlow
{
    /// <summary>判定游戏进程未退出时，进程启动时间与记录时间的最大容差。</summary>
    private static readonly TimeSpan _ProcessStartTolerance = TimeSpan.FromSeconds(2);

    private readonly PersonalOptionsStore _store;
    private readonly Action<Exception, string>? _warn;
    private readonly Func<string, bool>? _hasRunningGame;

    /// <summary>创建编排层。</summary>
    /// <param name="store">数据层，用于读写档案、会话与 <c>options.txt</c>。</param>
    /// <param name="warn">警告回调，用于记录恢复会话时的非致命异常；可为 <see langword="null"/>。</param>
    /// <param name="hasRunningGame">查询指定实例是否已有游戏在运行的委托；返回 <see langword="true"/> 时跳过准备；可为 <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> 为 <see langword="null"/> 时抛出。</exception>
    public PersonalOptionsFlow(PersonalOptionsStore store, Action<Exception, string>? warn = null,
        Func<string, bool>? hasRunningGame = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _warn = warn;
        _hasRunningGame = hasRunningGame;
    }

    /// <summary>数据层。</summary>
    public PersonalOptionsStore Store => _store;

    /// <summary>
    /// 启动游戏前调用：把全局档案中、属于已勾选类别且目标版本支持的键合并进
    /// <c>options.txt</c>，并返回一个等待 <see cref="Track"/> 的会话。
    /// </summary>
    /// <param name="instancePath">实例（版本）目录的完整路径，仅用于写入会话以便恢复。</param>
    /// <param name="gameFolder">游戏根目录，即 <c>options.txt</c> 所在目录；为空时回退到实例目录。</param>
    /// <param name="categories">已勾选的类别；为空表示不同步任何键。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>操作结果；跳过时 <see cref="PersonalOptionsResult.Session"/> 为 <see langword="null"/>。</returns>
    /// <exception cref="IOException">配置文件在写入前被修改时抛出。</exception>
    public PersonalOptionsResult Prepare(string instancePath, string gameFolder,
        IReadOnlyCollection<PersonalOptionsCategory> categories, CancellationToken token = default) =>
        Prepare(new PersonalOptionsTarget { InstancePath = instancePath, GameFolder = gameFolder }, categories, token);

    /// <summary>
    /// 启动游戏前调用：把全局档案中、属于已勾选类别且目标版本支持的键合并进
    /// <c>options.txt</c>，并返回一个等待 <see cref="Track"/> 的会话。
    /// </summary>
    /// <param name="target">实例信息与版本判定。</param>
    /// <param name="categories">已勾选的类别；为空表示不同步任何键。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>操作结果；跳过时 <see cref="PersonalOptionsResult.Session"/> 为 <see langword="null"/>。</returns>
    /// <exception cref="IOException">配置文件在写入前被修改时抛出。</exception>
    public PersonalOptionsResult Prepare(PersonalOptionsTarget target,
        IReadOnlyCollection<PersonalOptionsCategory> categories, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(categories);
        token.ThrowIfCancellationRequested();

        string folder = target.ResolvedGameFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return PersonalOptionsResult.Skip("游戏目录为空，跳过个人游戏习惯同步。");
        if (!Directory.Exists(folder))
            return PersonalOptionsResult.Skip($"游戏目录不存在：{folder}");

        IReadOnlySet<PersonalOptionsCategory> selectedCategories = PersonalOptionsCategories.ToSet(categories);
        if (selectedCategories.Count == 0)
            return PersonalOptionsResult.Skip("未勾选任何个人游戏习惯类别。");

        if (_hasRunningGame?.Invoke(folder) == true)
            return PersonalOptionsResult.Skip("该实例已有正在运行的游戏，跳过个人游戏习惯同步。");

        // 先用上次异常退出遗留的会话补采集，避免它们和本次准备互相覆盖。
        try
        {
            RecoverExitedSessions();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke(ex, "恢复个人游戏设置同步会话失败");
        }

        if (HasActiveSession(folder))
            return PersonalOptionsResult.Skip("该实例已有正在运行的游戏，跳过个人游戏习惯同步。");

        PersonalOptionsSnapshot? snapshot;
        try
        {
            snapshot = _store.ReadOptions(folder);
        }
        catch (IOException ex)
        {
            // 文件过大或为链接形式：属于应当拒绝而非崩溃的情况。
            return PersonalOptionsResult.Skip(ex.Message);
        }

        if (snapshot is null)
            return PersonalOptionsResult.Skip("options.txt 尚未生成，跳过个人游戏习惯同步（该文件由游戏或默认配置 Mod 创建）。");

        IReadOnlyDictionary<string, string> profile = _store.ReadProfile();
        if (profile.Count == 0)
            return PersonalOptionsResult.Skip("个人游戏习惯档案为空，无需同步。", snapshot);

        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string key in profile.Keys)
        {
            string canonical = PersonalOptionsRules.CanonicalKey(key);
            if (PersonalOptionsCategories.IsSelected(canonical, selectedCategories)) keys.Add(canonical);
        }

        // 版本信息不可靠时不碰按键绑定：无法判断该写字符串还是旧版数字码。
        if (!target.VersionValid) keys.RemoveWhere(key => key.StartsWith("key_", StringComparison.Ordinal));

        if (keys.Count == 0)
            return PersonalOptionsResult.Skip("档案中没有属于所选类别、且与目标版本兼容的键。", snapshot);

        bool isModern = target.VersionValid && target.VanillaMajor >= PersonalOptionsRules.ModernVanillaMajor;
        bool fovDegrees = target.UseFovDegrees ??
                          (target.VersionValid && target.VanillaMajor >= PersonalOptionsRules.FovDegreesVanillaMajor);

        string merged = PersonalOptionsRules.MergeText(snapshot.Text, profile, isModern, keys,
            key => PersonalOptionsCategories.IsSelected(key, selectedCategories), fovDegrees);

        token.ThrowIfCancellationRequested();

        bool written = false;
        if (!string.Equals(merged, snapshot.Text, StringComparison.Ordinal))
        {
            // 写回失败（被外部修改、无权限等）会以 IOException 抛出，交由调用方提示，绝不静默覆盖。
            written = _store.WriteOptions(folder, merged, snapshot.Hash, snapshot.HasBom);
        }

        Dictionary<string, string> mergedValues = PersonalOptionsRules.Parse(merged);
        Dictionary<string, string> baseline = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            if (!mergedValues.TryGetValue(key, out string? current)) continue;
            string? canonical = PersonalOptionsRules.CanonicalValue(key, current);
            if (canonical is not null) baseline[key] = canonical;
        }

        PersonalOptionsSession session = new()
        {
            VersionPath = string.IsNullOrWhiteSpace(target.InstancePath) ? folder : target.InstancePath,
            GameFolder = folder,
            Baseline = baseline,
            Keys = keys
        };

        return new PersonalOptionsResult
        {
            Status = written ? PersonalOptionsStatus.Applied : PersonalOptionsStatus.Unchanged,
            Message = written
                ? $"已同步 {baseline.Count} 项个人游戏习惯到 options.txt。"
                : "options.txt 与个人游戏习惯档案一致，无需写入。",
            Written = written,
            Snapshot = snapshot,
            Session = session,
            AppliedKeys = baseline.Keys.ToArray()
        };
    }

    /// <summary>
    /// 游戏进程启动后调用：把进程信息写入会话文件，供退出后补采集使用。
    /// </summary>
    /// <param name="session"><see cref="Prepare"/> 返回的会话；为 <see langword="null"/> 时直接返回。</param>
    /// <param name="process">游戏进程；为 <see langword="null"/> 或已退出时仍会写出会话，只是无法按进程匹配。</param>
    /// <returns>会话文件确实写出返回 <see langword="true"/>。</returns>
    /// <exception cref="IOException">写会话文件失败时抛出。</exception>
    public bool Track(PersonalOptionsSession? session, Process? process)
    {
        if (session is null) return false;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    session.Pid = process.Id;
                    session.StartedUtc = process.StartTime.ToUniversalTime();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                // 进程已退出或无权读取启动时间：保持 Pid = 0，恢复时会按「已退出」处理。
                _warn?.Invoke(ex, "读取游戏进程启动时间失败，会话将按退出后补采集处理");
            }
        }

        _store.WriteSession(session);
        return true;
    }

    /// <summary>
    /// 游戏退出后调用：读取 <c>options.txt</c>，只把「当前值不同于 baseline」的会话键写回全局档案。
    /// </summary>
    /// <param name="session">要采集的会话；为 <see langword="null"/> 或无键时返回空集合。</param>
    /// <returns>本次实际写回档案的键。</returns>
    /// <exception cref="IOException">读取 <c>options.txt</c> 或写入档案失败时抛出。</exception>
    public IReadOnlyCollection<string> Capture(PersonalOptionsSession? session)
    {
        if (session is null || session.Keys.Count == 0) return Array.Empty<string>();

        PersonalOptionsSnapshot? snapshot = _store.ReadOptions(session.GameFolder);
        if (snapshot is null) return Array.Empty<string>();

        Dictionary<string, string> profile = new(_store.ReadProfile(), StringComparer.Ordinal);
        List<string> changed = [];
        foreach (string key in session.Keys)
        {
            if (!session.Baseline.TryGetValue(key, out string? baseline)) continue;
            if (!snapshot.Values.TryGetValue(key, out string? current)) continue;

            string? canonical = PersonalOptionsRules.CanonicalValue(key, current);
            if (canonical is null || string.Equals(canonical, baseline, StringComparison.Ordinal)) continue;

            profile[key] = canonical;
            changed.Add(key);
        }

        if (changed.Count > 0) _store.WriteProfile(profile);
        return changed;
    }

    /// <summary>
    /// 遍历会话文件，对已经退出的进程补采集并删除会话；仍在运行的会话保持不变。
    /// </summary>
    /// <returns>成功补采集的会话数量。</returns>
    public int RecoverExitedSessions()
    {
        int recovered = 0;
        foreach (PersonalOptionsSession session in _store.ReadSessions())
        {
            if (_ProcessMatches(session.Pid, session.StartedUtc)) continue;

            try
            {
                Capture(session);
                _store.DeleteSession(session.Id);
                recovered++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn?.Invoke(ex, "恢复个人游戏设置同步会话失败");
            }
        }
        return recovered;
    }

    /// <summary>
    /// 判断指定游戏目录是否已有正在运行的会话。目录比较忽略大小写与末尾分隔符。
    /// </summary>
    /// <param name="gameFolder">游戏根目录。</param>
    /// <returns>存在且对应进程仍在运行则返回 <see langword="true"/>。</returns>
    public bool HasActiveSession(string gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder)) return false;

        foreach (PersonalOptionsSession session in _store.ReadSessions())
        {
            if (!_SamePath(session.GameFolder, gameFolder)) continue;
            if (_ProcessMatches(session.Pid, session.StartedUtc)) return true;
        }
        return false;
    }

    /// <summary>
    /// 判断会话记录的进程是否仍然存在。进程不存在、无权限、启动时间对不上，
    /// 一律视为已退出——宁可多采集一次，也不要卡住后续同步。
    /// </summary>
    private static bool _ProcessMatches(int pid, DateTimeOffset startedUtc)
    {
        if (pid <= 0 || startedUtc == DateTimeOffset.MinValue) return false;

        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return false;

            DateTime startTime = process.StartTime.ToUniversalTime();
            return (startTime - startedUtc.UtcDateTime).Duration() <= _ProcessStartTolerance;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>比较两个目录是否指向同一位置。</summary>
    private static bool _SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        try
        {
            string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
