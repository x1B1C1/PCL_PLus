using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Options;

namespace PCL.Modules.Minecraft;

/// <summary>
///     个人游戏习惯：把按键绑定等设置跨版本同步。
///     <para>
///         启动前把全局档案合并进当前实例的 <c>options.txt</c>，退出后把变化写回档案。
///         只同步按键绑定（含鼠标键、视角键），不动音量与其它设置——那些改起来容易让人莫名，
///         而按键是换版本后最常丢、也最烦重新设的一项。
///     </para>
/// </summary>
public static class ModPersonalOptions
{
    private static PersonalOptionsFlow? _flow;
    private static PersonalOptionsSession? _session;

    private static PersonalOptionsFlow Flow => _flow ??= new PersonalOptionsFlow(
        new PersonalOptionsStore(Path.Combine(Paths.SharedLocalData, "personal-options")),
        (ex, message) => ModBase.Log(ex, "[PersonalOptions] " + message, ModBase.LogLevel.Debug),
        _ => ModWatcher.hasRunningMinecraft);

    /// <summary>
    ///     启动前把全局档案合并到实例的 options.txt。返回可传给 <see cref="Track" /> 的会话。
    ///     永远不抛异常：同步失败只写日志，不影响启动。
    /// </summary>
    public static PersonalOptionsSession? Prepare(McInstance? instance)
    {
        try
        {
            _session = null;
            if (instance is null || !IsEnabled(instance)) return null;

            var target = new PersonalOptionsTarget
            {
                InstancePath = instance.PathInstance,
                GameFolder = instance.PathIndie,
                VersionValid = instance.Info is not null,
                VanillaMajor = ParseVanillaMajor(instance.Info?.VanillaName)
            };
            var result = Flow.Prepare(target, [PersonalOptionsCategory.Keys, PersonalOptionsCategory.Mouse, PersonalOptionsCategory.View], CancellationToken.None);
            if (result.Skipped && !string.IsNullOrEmpty(result.Message))
                ModBase.Log($"[PersonalOptions] 已跳过同步：{result.Message}", ModBase.LogLevel.Developer);
            _session = result.Session;
            if (result.Written)
                ModBase.Log($"[PersonalOptions] 已同步 {result.AppliedKeys.Count} 项设置", ModBase.LogLevel.Developer);
            return _session;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[PersonalOptions] 启动前同步设置失败，已跳过", ModBase.LogLevel.Debug);
            return null;
        }
    }

    /// <summary>记录本次会话对应的游戏进程，崩溃后可用于补采。</summary>
    public static void Track(PersonalOptionsSession? session, Process? process)
    {
        try
        {
            if (session is null) return;
            Flow.Track(session, process);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[PersonalOptions] 记录会话失败", ModBase.LogLevel.Debug);
        }
    }

    /// <summary>游戏进程结束后把变化写回全局档案。</summary>
    public static void OnProcessStopped(McInstance? instance)
    {
        try
        {
            if (_session is null) return;
            var changed = Flow.Capture(_session);
            if (changed.Count > 0)
                ModBase.Log($"[PersonalOptions] 已保存 {changed.Count} 项设置到全局档案", ModBase.LogLevel.Developer);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[PersonalOptions] 保存设置失败", ModBase.LogLevel.Debug);
        }
        finally
        {
            _session = null;
            _ = instance;
        }
    }

    /// <summary>启动时补采上次异常退出遗留的会话。</summary>
    public static void RecoverExitedSessions()
    {
        try
        {
            var count = Flow.RecoverExitedSessions();
            if (count > 0) ModBase.Log($"[PersonalOptions] 补采了 {count} 个异常退出的会话", ModBase.LogLevel.Developer);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[PersonalOptions] 补采会话失败", ModBase.LogLevel.Debug);
        }
    }

    private static bool IsEnabled(McInstance instance)
    {
        if (!Config.Launch.PersonalOptionsSync) return false;
        // 0 = 跟随全局，1 = 强制开启，2 = 该版本不同步
        return Config.Instance.PersonalOptionsSync[instance.PathInstance] switch
        {
            2 => false,
            1 => true,
            _ => true
        };
    }

    private static int ParseVanillaMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[1], out var major) ? major : 0;
    }
}
