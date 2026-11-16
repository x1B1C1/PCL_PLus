using System;
using System.IO;
using PCL.Core.Logging;
using PCL.Core.Utils.OS;
using Special = System.Environment.SpecialFolder;

namespace PCL.Core.App;

/// <summary>
/// Application global paths.<br/>
/// <b>NOTE</b>: The behaviors of all path strings depends on <see cref="Path"/> API provided by
/// .NET standard library. You should use <see cref="Path"/> and other APIs relative to it to process
/// any path string from this service, rather than concat paths manually.
/// </summary>
public static class Paths
{
    /// <summary>
    /// The default directory used for relative path combining.
    /// </summary>
    public static string DefaultDirectory => Basics.ExecutableDirectory;

    private static string _data;
    private static string _sharedData;
    private static string _sharedLocalData;
    private static string _temp;
    
    /// <summary>
    /// Per-instance data directory.
    /// </summary>
    public static string Data { get => _data; set => _data = value; }

    /// <summary>
    /// Shared synchronized data directory.
    /// </summary>
    public static string SharedData { get => _sharedData; set => _sharedData = value; }
    
    /// <summary>
    /// Shared synchronized data directory of old versions.<br/>
    /// Keep the value just for migration, DO NOT USE IT.
    /// </summary>
    public static string OldSharedData { get; set; }

    /// <summary>
    /// Shared local data directory, used to put some large files that can be released or downloaded back anytime.
    /// </summary>
    public static string SharedLocalData { get => _sharedLocalData; set => _sharedLocalData = value; }
    
    /// <summary>
    /// Temporary files directory (can be deleted anytime, except when the program is running).
    /// </summary>
    public static string Temp { get => _temp; set => _temp = value; }

    /// <summary>
    /// Get path string relative to a special folder.
    /// </summary>
    /// <param name="folder">the special folder</param>
    /// <param name="relative">the relative path</param>
    /// <returns>the path string relative to the special folder</returns>
    public static string GetSpecialPath(Special folder, string relative)
    {
        var folderPath = Environment.GetFolderPath(folder);
        return Path.Combine(folderPath, relative);
    }

    static Paths()
    {
#if DEBUG
        const string name = "PCL_Plus_Debug";
        const string oldName = "PCLLite_Debug";
        const string legacyName = "PCL_Light_Debug";
#else
        const string name = "PCL_Plus";
        const string oldName = "PCLLite";
        const string legacyName = "PCL_Light";
#endif
        // fill paths
        _data = Path.Combine(DefaultDirectory, "PCL");
        _sharedData = GetSpecialPath(Special.ApplicationData, name);
        _sharedLocalData = GetSpecialPath(Special.LocalApplicationData, name);
        _temp = Path.Combine(Path.GetTempPath(), name);
        OldSharedData = GetSpecialPath(Special.ApplicationData, oldName);
#if DEBUG
        // read environment variables
        EnvironmentInterop.ReadVariable("PCL_PATH", ref _data);
        EnvironmentInterop.ReadVariable("PCL_PATH_SHARED", ref _sharedData);
        EnvironmentInterop.ReadVariable("PCL_PATH_LOCAL", ref _sharedLocalData);
        EnvironmentInterop.ReadVariable("PCL_PATH_TEMP", ref _temp);
#endif
        // 从旧品牌目录迁移配置与账号，避免改名后丢设置
        // 迁移链：PCL_Plus ← PCL_Light ← PCLLite，每一级只在目标缺文件时补，不会覆盖新版数据
        MigrateDirectory(OldSharedData, _sharedData);
        MigrateDirectory(GetSpecialPath(Special.ApplicationData, legacyName), _sharedData);
        MigrateBedrock(GetSpecialPath(Special.LocalApplicationData, oldName));
        MigrateBedrock(GetSpecialPath(Special.LocalApplicationData, legacyName));
        // create directories
        // 这里不能直接 CreateDirectory：把启动器放到 Program Files 之类的目录里运行时，
        // 静态构造函数一抛异常，整个进程还没开始就被带走了，用户看到的只是「双击没反应」。
        _data = EnsureDirectory(_data, null, "数据目录");
        _sharedLocalData = EnsureDirectory(_sharedLocalData, Path.Combine(_data, "Local"), "本地共享目录");
        _sharedData = EnsureDirectory(_sharedData, Path.Combine(_data, "Shared"), "共享目录");
        _temp = EnsureDirectory(_temp, Path.Combine(_data, "Temp"), "临时目录");
    }

    /// <summary>
    /// 确保目录存在且可写；不可写时换用回退目录。<br/>
    /// 回退失败也不抛异常，交给后续逻辑按「路径不可用」处理，避免启动期崩溃。
    /// </summary>
    private static string EnsureDirectory(string path, string? fallback, string name)
    {
        if (TryPrepareDirectory(path)) return path;
        if (fallback is not null && !string.Equals(fallback, path, StringComparison.OrdinalIgnoreCase) &&
            TryPrepareDirectory(fallback))
        {
            LogWrapper.Warn($"{name} {path} 不可写，已改用 {fallback}");
            return fallback;
        }

        LogWrapper.Warn($"{name} {path} 不可写，且没有可用的回退目录");
        return path;
    }

    private static bool TryPrepareDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            // 目录存在不代表可写（比如被 ACL 拒绝或只读介质），实际写一个探针文件才准
            var probe = Path.Combine(path, $".pcl-write-test-{Environment.ProcessId}.tmp");
            using (var _ = File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将旧品牌目录中已有的文件复制到新目录；已存在的文件不覆盖。
    /// 仅用于共享配置/账号这类小文件，缓存目录不做迁移。
    /// </summary>
    private static void MigrateDirectory(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
            }
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }
        catch
        {
            // 迁移失败不阻塞启动，后续仍可使用旧目录中的配置手动恢复。
        }
    }

    /// <summary>
    /// 基岩版数据量可能很大，使用同盘移动而不是复制；失败则保持旧目录不动。
    /// </summary>
    private static void MigrateBedrock(string oldLocal)
    {
        try
        {
            var oldBedrock = Path.Combine(oldLocal, "Bedrock");
            var newBedrock = Path.Combine(_sharedLocalData, "Bedrock");
            if (!Directory.Exists(oldBedrock) || Directory.Exists(newBedrock)) return;
            Directory.CreateDirectory(_sharedLocalData);
            Directory.Move(oldBedrock, newBedrock);
        }
        catch
        {
            // 文件被占用或跨盘时保持旧目录，避免阻塞启动。
        }
    }
}
