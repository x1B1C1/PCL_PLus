using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Saves.Backups;

namespace PCL.Modules.Minecraft;

/// <summary>
/// 存档备份的前端协调器：把 <see cref="WorldBackupService" /> 接到启动器的配置、提示与任务栏上。
/// </summary>
public static class ModWorldBackups
{
    private static WorldBackupService? _service;

    private static WorldBackupService Service => _service ??= new WorldBackupService(new BackupHost());

    /// <summary>当前是否有备份任务在跑。</summary>
    public static bool IsBusy => Service.IsBusy;

    /// <summary>备份点发生变化（新建 / 删除 / 恢复）后触发，已切回 UI 线程。</summary>
    public static event Action? Changed;

    /// <summary>取该实例的游戏目录（<see cref="McInstance.PathIndie" />）下的全部备份点，按时间降序。</summary>
    public static IReadOnlyList<WorldBackupPoint> Points(McInstance instance)
        => Service.Points(instance.PathIndie);

    /// <summary>在资源管理器中打开该实例的备份目录。</summary>
    public static void OpenFolder(McInstance instance)
        => ModBase.OpenExplorer(Service.Store(instance.PathIndie));

    /// <summary>该实例的自动备份开关。</summary>
    public static bool AutoEnabled(McInstance instance)
        => Config.Instance.WorldBackupAutoEnabled[instance.PathInstance];

    /// <summary>该实例的自动备份保留份数（夹到 1..50）。</summary>
    public static int Retention(McInstance instance)
        => Math.Clamp(Config.Instance.WorldBackupRetention[instance.PathInstance], 1, 50);

    /// <summary>
    /// 启动一次备份 / 恢复 / 删除任务。
    /// </summary>
    /// <param name="instance">目标实例，不能为空。</param>
    /// <param name="restoreId">要恢复的备份点 ID；为空表示不恢复。</param>
    /// <param name="deleteIds">要移除的备份点 ID 列表；为空表示不删除。</param>
    /// <param name="worldIdentifier">只备份单个存档时的文件夹名；为空表示备份全部存档。</param>
    public static void Start(McInstance? instance, string? restoreId = null,
        IEnumerable<string>? deleteIds = null, string? worldIdentifier = null)
    {
        if (instance is null) return;
        if (Service.IsBusy || HasActiveGame(instance))
        {
            HintService.Hint(Lang.Text("Instance.Saves.Backup.Status.GameRunning"), HintType.Error);
            return;
        }

        var deleteList = deleteIds is null ? null : new List<string>(deleteIds);
        var gameFolder = instance.PathIndie;
        var retention = Retention(instance);
        const string loaderName = "Loader 存档备份";
        var loader = new ModLoader.LoaderTask<string, string>(loaderName, task =>
        {
            string result = "";
            if (restoreId is not null)
            {
                Service.Restore(gameFolder, restoreId, retention, task.AbortedToken,
                    progress => task.Progress = progress);
                Service.NotifyChanged();
                result = "restored";
            }
            else if (deleteList is { Count: > 0 })
            {
                Service.DeletePoints(gameFolder, deleteList);
                Service.NotifyChanged();
                result = "deleted";
            }
            else if (worldIdentifier is not null)
            {
                Service.CaptureWorld(gameFolder, worldIdentifier, Lang.Text("Instance.Saves.Backup.Create"),
                    retention, task.AbortedToken, progress => task.Progress = progress);
                result = "captured";
            }
            else
            {
                var ids = Service.CaptureFolder(gameFolder, Lang.Text("Instance.Saves.Backup.CreateAll"),
                    retention, task.AbortedToken, progress => task.Progress = progress);
                result = ids.Count == 0 ? "empty" : "captured";
            }

            ModBase.RunInUi(() => { Changed?.Invoke(); });
            task.output = result;
        });
        var combo = new ModLoader.LoaderCombo<string>(loaderName, new List<ModLoader.LoaderBase> { loader })
        {
            show = false
        };
        ModLoader.LoaderTaskbarAdd(combo);
        combo.Start();
    }

    /// <summary>正常退出游戏后按配置自动备份该实例的存档。</summary>
    public static void OnProcessStopped(McInstance? instance, Process? process, bool normalExit)
    {
        if (instance is null || !normalExit || process is null) return;
        try
        {
            if (process.ExitCode != 0) return;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取游戏退出码失败，跳过自动备份存档", ModBase.LogLevel.Debug);
            return;
        }

        if (!AutoEnabled(instance)) return;
        var gameFolder = instance.PathIndie;
        var retention = Retention(instance);
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var ids = Service.CaptureFolder(gameFolder, Lang.Text("Instance.Saves.Backup.AutoReason"),
                    retention, default);
                if (ids.Count == 0) return;
                HintService.Hint(Lang.Text("Instance.Saves.Backup.Hint.Success", ids.Count.ToString()));
                ModBase.RunInUi(() => Changed?.Invoke());
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "自动备份存档失败", ModBase.LogLevel.Debug);
            }
        });
    }

    private static bool HasActiveGame(McInstance instance)
    {
        try
        {
            return Service.HasActiveGame(instance.PathIndie);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "检查游戏是否仍在运行失败", ModBase.LogLevel.Debug);
            return true;
        }
    }

    private sealed class BackupHost : IWorldBackupHost
    {
        public string BackupRoot => Path.Combine(Paths.SharedLocalData, "world-backups");

        public string? WorldLabel(string worldFolder) => null;

        public string FormatSize(long bytes) => Lang.FileSize(bytes);

        public void Warn(Exception ex, string message) => ModBase.Log(ex, message, ModBase.LogLevel.Debug);

        public void Hint(string message, bool error)
            => HintService.Hint(message, error ? HintType.Error : HintType.Success);

        public void Dispatch(Action action) => ModBase.RunInUi(action);

        public void NotifyChanged() => ModBase.RunInUi(() => Changed?.Invoke());

        public void OpenFolder(string path) => ModBase.OpenExplorer(path);

        public IReadOnlyList<WorldBackupWatcher> Watchers()
        {
            var result = new List<WorldBackupWatcher>();
            foreach (var process in Process.GetProcessesByName("javaw").Concat(Process.GetProcessesByName("java")))
            {
                using (process)
                {
                    DateTime started;
                    try
                    {
                        started = process.StartTime.ToUniversalTime();
                    }
                    catch
                    {
                        continue;
                    }

                    result.Add(new WorldBackupWatcher(process.Id, started, null));
                }
            }

            return result;
        }
    }
}
