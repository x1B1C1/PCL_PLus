#if BETA_A
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PCL.Controls;

namespace PCL.Pages.PageSetup;

/// <summary>
/// 工程调试用的「多层玻璃」实验：给每张卡片的底板再叠一层模糊，
/// 用来复现「主窗口一层 + 卡片各一层」的叠加开销。
/// 只在本工程测试版编译，正式产物里不存在。
/// </summary>
internal static class EngineeringLayers
{
    private static readonly Dictionary<Border, Effect?> Originals = new();
    private static int _current;

    public static int Current => _current;

    /// <summary>layers = 0 表示不加；1–3 给每张卡片底板挂一层 BlurEffect。</summary>
    public static void Apply(int layers)
    {
        Revert();
        if (layers <= 0) return;

        var window = PCL.Modules.ModMain.frmMain;
        if (window is null) return;

        foreach (var border in Enumerate<Border>(window))
        {
            if (border.Name != "mainBorder") continue;
            Originals[border] = border.Effect;
            border.Effect = new BlurEffect
            {
                Radius = 6d * layers,
                RenderingBias = RenderingBias.Performance
            };
        }

        _current = layers;
    }

    public static void Revert()
    {
        foreach (var pair in Originals) pair.Key.Effect = pair.Value;
        Originals.Clear();
        _current = 0;
    }

    /// <summary>当前可见的玻璃面数量，用于判断「少了一层」是否真的生效。</summary>
    public static int CountGlassSurfaces()
    {
        var window = PCL.Modules.ModMain.frmMain;
        if (window is null) return 0;

        int count = 0;
        foreach (var surface in Enumerate<TahoeGlassSurface>(window))
            if (surface.Visibility == Visibility.Visible) count++;
        return count;
    }

    private static IEnumerable<T> Enumerate<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (T nested in Enumerate<T>(child)) yield return nested;
        }
    }
}
#endif
