using System;
using System.Runtime.InteropServices;

namespace PCL.Modules.UI.Theme;

/// <summary>
/// 读本进程 3D 引擎的 GPU 占用。仅 BF 构建会调用（见 <c>BuildLine.IsBf</c>）。
/// 走 PDH（pdh.dll）而不是 System.Diagnostics.PerformanceCounter——后者在 net10 上是独立 NuGet 包，
/// 而这个工程不保证能联网还原新包；PDH 是系统自带的，零依赖。
/// </summary>
internal static class GpuUsageMonitor
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";

    private static IntPtr _query;
    private static IntPtr _counter;
    private static bool _unavailable;
    private static readonly string OwnPidTag = $"pid_{Environment.ProcessId}_";

    /// <summary>返回 0–100 的百分比；环境不支持（虚拟机、无 WDDM 计数器）时返回 null。</summary>
    public static double? Sample()
    {
        if (_unavailable) return null;

        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
                {
                    _unavailable = true;
                    return null;
                }

                if (PdhAddEnglishCounterW(_query, CounterPath, IntPtr.Zero, out _counter) != 0)
                {
                    _unavailable = true;
                    return null;
                }
            }

            if (PdhCollectQueryData(_query) != 0) return null;

            uint size = 0;
            uint status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, out _, IntPtr.Zero);
            if (status != PdhMoreData || size == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, out uint count, buffer);
                if (status != 0) return null;

                double sum = 0d;
                int itemSize = Marshal.SizeOf<PdhCounterItem>();
                for (int index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<PdhCounterItem>(buffer + index * itemSize);
                    if (item.Name == IntPtr.Zero) continue;

                    string name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                    if (!name.Contains(OwnPidTag, StringComparison.Ordinal)) continue;
                    if (!name.Contains("engtype_3D", StringComparison.Ordinal)) continue;
                    if (item.Value.Status != 0) continue;

                    sum += item.Value.DoubleValue;
                }

                // 一块显卡的多个 3D 引擎会各占一部分，求和后封顶 100。
                return Math.Clamp(sum, 0d, 100d);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            _unavailable = true;
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterItem
    {
        public IntPtr Name;
        public PdhCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize,
        out uint itemCount, IntPtr buffer);
}
