// ============================================================================
// [新增文件] TerracottaHost.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本文件全部为新增代码，是插件与启动器之间的唯一胶水层。
//       插件核心（TerracottaAddon / 进程 / HTTP）不直接引用 ModBase、实例列表等
//       启动器内部类型；需要日志或“回填服务器地址”时由这里注入回调。
// ============================================================================

using System.Windows;
using PCL.Core.App;

namespace PCL.Modules.Terracotta;

/// <summary>
/// 陶瓦联机插件宿主。
/// <para>
/// 只在 <see cref="Initialize"/> 里注入日志与服务器地址回调，然后启动插件。
/// 删除本文件夹并去掉设置页入口后，启动器其余部分完全不受影响。
/// </para>
/// </summary>
internal static class TerracottaHost
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        TerracottaAddon.LogSink = WriteLog;
        TerracottaAddon.ServerAddressSink = FillCurrentInstanceServerAddress;
        TerracottaAddon.Initialize();
    }

    private static void WriteLog(string message, Exception? exception)
    {
        try
        {
            if (exception is null)
                ModBase.Log(message, ModBase.LogLevel.Developer);
            else
                ModBase.Log(exception, message, ModBase.LogLevel.Debug);
        }
        catch (Exception)
        {
            // 日志系统尚未初始化时忽略。
        }
    }

    /// <summary>
    /// 联机成功后，把 127.0.0.1:端口 写入当前实例的“启动时自动进入服务器”配置。
    /// 只在用户勾选“联机成功后自动填入当前版本的服务器地址”时才会被调用。
    /// </summary>
    private static void FillCurrentInstanceServerAddress(string address)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            WriteLog("启动器界面线程不可用，跳过服务器地址回填", null);
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.BeginInvoke(new Action(() => FillCurrentInstanceServerAddress(address)));
            }
            catch (Exception ex)
            {
                WriteLog("调度服务器地址回填失败", ex);
            }

            return;
        }

        try
        {
            var instance = ModInstanceList.McMcInstanceSelected;
            if (instance is null)
            {
                WriteLog("没有选中实例，跳过服务器地址回填", null);
                return;
            }

            Config.Instance.ServerToEnter[instance.PathInstance] = address;
            WriteLog($"已把联机地址 {address} 写入实例「{instance.Name}」的启动服务器配置", null);
        }
        catch (Exception ex)
        {
            WriteLog("回填实例服务器地址失败", ex);
        }
    }
}
