using System;
using System.Collections.Generic;
using System.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.EnvironmentHistory;
using PCL.Core.Minecraft.EnvironmentSnapshot;

namespace PCL;

/// <summary>
///     环境还原点的前端协调器。
///     <para>负责按实例缓存引擎、把引擎回调切回 UI 线程，并用 LoaderTask 承载耗时操作。</para>
///     <para>引擎本身不认识实例，只认「版本目录 + 游戏目录 + 数据根」这三个路径。</para>
/// </summary>
public static class ModEnvironmentHistory
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, EnvironmentHistoryEngine> Engines =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>任一实例的还原点发生变化后触发，已在 UI 线程。</summary>
    public static event Action? Changed;

    /// <summary>所有实例共用一个数据根，引擎内部再按版本目录哈希隔离。</summary>
    private static string DataRoot => Paths.SharedLocalData;

    /// <summary>
    ///     取得（或建立）某个实例的引擎。路径变化时会重建，实例为空则返回 null。
    /// </summary>
    public static EnvironmentHistoryEngine? Engine(McInstance? instance)
    {
        if (instance is null) return null;
        string key = instance.PathInstance + "|" + instance.PathIndie;
        lock (Gate)
        {
            if (Engines.TryGetValue(key, out EnvironmentHistoryEngine? cached)) return cached;
            try
            {
                var engine = new EnvironmentHistoryEngine(DataRoot, instance.PathInstance, instance.PathIndie);
                engine.Changed += () => ModBase.RunInUi(() => Changed?.Invoke());
                Engines[key] = engine;
                return engine;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "初始化环境还原点引擎失败", ModBase.LogLevel.Debug);
                return null;
            }
        }
    }

    /// <summary>读取某个实例的还原点列表，失败时返回空表。</summary>
    public static IReadOnlyList<EnvironmentHistoryPoint> Points(McInstance? instance)
    {
        try
        {
            return Engine(instance)?.Points() ?? [];
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取环境还原点列表失败", ModBase.LogLevel.Debug);
            return [];
        }
    }

    /// <summary>还原点的存储目录，供「打开文件夹」使用。</summary>
    public static string? StoreRoot(McInstance? instance)
    {
        try
        {
            return Engine(instance)?.StoreRoot;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取还原点存储目录失败", ModBase.LogLevel.Debug);
            return null;
        }
    }

    /// <summary>
    ///     创建一个手动还原点。label 为空时用默认文案。
    /// </summary>
    public static void Capture(McInstance? instance, string? label = null)
    {
        var engine = Engine(instance);
        if (engine is null) return;
        string pointLabel = string.IsNullOrWhiteSpace(label)
            ? Lang.Text("Instance.RestorePoints.Label.Manual")
            : label;

        var loader = new ModLoader.LoaderTask<string, string>("Loader EnvironmentHistory Capture", task =>
        {
            engine.Capture(pointLabel, false, task.AbortedToken, progress => task.Progress = progress);
            ModBase.RunInUi(() => Changed?.Invoke());
            task.output = "captured";
        });
        StartLoader("环境还原点", loader);
        HintService.Hint(Lang.Text("Instance.RestorePoints.Hint.Capturing"), log: false);
    }

    /// <summary>
    ///     恢复到一个还原点。恢复会改动游戏目录，因此调用前必须先由界面确认。
    /// </summary>
    public static void Restore(McInstance? instance, string id)
    {
        var engine = Engine(instance);
        if (engine is null) return;

        var loader = new ModLoader.LoaderTask<string, string>("Loader EnvironmentHistory Restore", task =>
        {
            engine.Restore(id, task.AbortedToken, progress => task.Progress = progress);
            ModBase.RunInUi(() =>
            {
                Changed?.Invoke();
                HintService.Hint(Lang.Text("Instance.RestorePoints.Hint.Restored"),
                    HintType.Success);
            });
            task.output = "restored";
        });
        StartLoader("恢复环境还原点", loader);
    }

    /// <summary>移除若干个还原点（清单挪进 deleted，不立即删 blob）。</summary>
    public static void Delete(McInstance? instance, IEnumerable<string> ids)
    {
        var engine = Engine(instance);
        if (engine is null) return;
        var list = ids.ToList();
        if (list.Count == 0) return;

        var loader = new ModLoader.LoaderTask<string, string>("Loader EnvironmentHistory Delete", _ =>
        {
            engine.Delete(list);
            ModBase.RunInUi(() => Changed?.Invoke());
        });
        StartLoader("移除环境还原点", loader);
    }

    /// <summary>按保留数量裁剪自动还原点，手动点永不参与。</summary>
    public static void PruneAutomatic(McInstance? instance, int keep)
    {
        var engine = Engine(instance);
        if (engine is null) return;
        try
        {
            engine.PruneAutomatic(keep);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "裁剪自动还原点失败", ModBase.LogLevel.Debug);
        }
    }

    /// <summary>把某个还原点标记为「健康」，崩溃对照只拿它当基准。</summary>
    public static void MarkHealthy(McInstance? instance, string id, bool healthy = true)
    {
        try
        {
            Engine(instance)?.MarkHealthy(id, healthy);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "标记还原点健康状态失败", ModBase.LogLevel.Debug);
        }
    }

    /// <summary>
    ///     在后台打一个自动还原点（游戏启动前、修改组件前等）。
    ///     任何失败都只记日志，绝不阻塞启动流程。
    /// </summary>
    public static void CaptureAutomatic(McInstance? instance, string label, bool healthy = false)
    {
        if (instance is null) return;
        var engine = Engine(instance);
        if (engine is null) return;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                engine.Capture(label, healthy, default);
                if (healthy) engine.MarkHealthy(engine.Points()[0].Id);
                ModBase.RunInUi(() => Changed?.Invoke());
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"自动创建环境还原点失败（{label}）", ModBase.LogLevel.Debug);
            }
        });
    }

    /// <summary>正常退出游戏后，把最近一个自动点标记为健康基准。</summary>
    public static void OnProcessStopped(McInstance? instance, bool normalExit)
    {
        if (instance is null || !normalExit) return;
        var engine = Engine(instance);
        if (engine is null) return;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var newest = engine.Points().FirstOrDefault();
                if (newest is null) return;
                engine.MarkHealthy(newest.Id);
                ModBase.RunInUi(() => Changed?.Invoke());
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "标记健康还原点失败", ModBase.LogLevel.Debug);
            }
        });

        // 同时留一份轻量的文件清单快照，崩溃对照用它当基线
        CaptureHealthy(instance);
    }

    /// <summary>
    ///     正常退出游戏后打一份「健康」环境快照，作为崩溃对照的基线。
    ///     <para>失败只记日志：快照只是辅助信息，不能影响退出流程。</para>
    /// </summary>
    public static void CaptureHealthy(McInstance? instance)
    {
        if (instance is null) return;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                // Build 出来的是「未标记健康」的清单，作为基线必须显式置 Healthy，
                // 否则 LoadLatestHealthy 永远不会返回它，崩溃对照就永远是空的。
                var service = SnapshotService();
                var snapshot = service.Build(instance.PathInstance, instance.PathIndie);
                snapshot.Healthy = true;
                service.Save(instance.PathInstance, instance.PathIndie, snapshot);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Environment] 保存健康环境快照失败", ModBase.LogLevel.Debug);
            }
        });
    }

    /// <summary>
    ///     把「最近一次健康快照」与当前环境对比，返回差异列表；没有基线时返回空表。
    /// </summary>
    public static IReadOnlyList<EnvironmentChange> CompareWithHealthy(McInstance? instance)
    {
        if (instance is null) return [];
        try
        {
            var service = SnapshotService();
            var baseline = service.LoadLatestHealthy(instance.PathInstance);
            if (baseline is null) return [];
            var current = service.Build(instance.PathInstance, instance.PathIndie);
            return EnvironmentSnapshotService.Compare(baseline, current);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Environment] 对比健康环境快照失败", ModBase.LogLevel.Debug);
            return [];
        }
    }

    private static EnvironmentSnapshotService SnapshotService() => new(Paths.SharedLocalData);

    private static void StartLoader(string name, ModLoader.LoaderBase loader)    {
        var combo = new ModLoader.LoaderCombo<string>(name, new List<ModLoader.LoaderBase> { loader })
        {
            show = false
        };
        ModLoader.LoaderTaskbarAdd(combo);
        combo.Start();
    }
}
