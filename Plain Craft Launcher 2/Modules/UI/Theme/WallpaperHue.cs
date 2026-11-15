using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.UI.Theme;

namespace PCL.Modules.UI.Theme;

/// <summary>
///     从当前壁纸里取主色相，让主题色跟着壁纸走。
///     <para>
///         做法是<b>色相投票</b>而不是求平均：把 0–360° 分成 24 个 15° 的桶，每个像素按饱和度平方
///         给它所在的桶投一票，票数最高的桶就是这张图的主色。求平均会把「蓝天空 + 绿草地」混成
///         壁纸里根本不存在的青色，投票不会——谁占得多就选谁。<br />
///         先对整张图投票；整张图没有占优色（灰调、黑白）时，再裁掉四周取中间 3/4 投一次，
///         避开边角的小物件。两次都取不到就返回 <see langword="null"/>，保持主题原本的色相。
///     </para>
/// </summary>
internal static class WallpaperHue
{
    /// <summary>采样宽度：缩到这个尺寸再统计，48 像素宽已经足够反映色相分布。</summary>
    private const int SampleWidth = 48;

    /// <summary>色相桶数量，24 桶即每桶 15°。</summary>
    private const int Buckets = 24;

    private const double BucketWidth = 360d / Buckets;

    /// <summary>最高桶至少占到全部票数的这个比例，才算「有主导色」。</summary>
    private const double DominantRatio = 0.15d;

    /// <summary>
    ///     重新从主窗口当前的背景图取色并刷新主题。
    ///     背景不是图片（纯色 / 视频 / 未设置）时清空覆盖色，回到所选主题原本的色相。
    /// </summary>
    /// <param name="force">
    ///     忽略「和上次取到的一样」的短路，强制重算一遍。
    ///     关闭液态玻璃时必须这样调：玻璃开着时 <see cref="ThemeService.WallpaperHue" /> 被清成
    ///     <see langword="null" />，界面停在主题原色上；关掉后如果取色结果恰好也是 null，
    ///     短路会让界面永远停在原色，表现就是「开过一次玻璃以后自动取色再也不生效」。
    /// </param>
    public static void Refresh(bool force = false)
    {

        try
        {
            // 液态玻璃开着的时候不参与自动取色：玻璃本身要从壁纸里折射颜色，
            // 再把主题色也染成壁纸色，整个界面就只剩一种色调、层次全没了。
            if (LiquidGlassCore.IsEnabled)
            {
                if (ThemeService.WallpaperHue is null) return;
                ThemeService.WallpaperHue = null;
                ModBase.RunInUi(() =>
                {
                    ThemeService.ApplyColorResources();
                    ThemeService.ApplyGrayResources();
                    ThemeManager.ThemeRefreshMain();
                }, false);
                return;
            }

            var hue = Extract();
            ModBase.Log($"[Wallpaper] 取色结果 {(hue is { } h ? h.ToString("F1") + "°" : "null（保持主题色相）")}",
                ModBase.LogLevel.Debug);
            if (!force && ThemeService.WallpaperHue == hue) return;
            ThemeService.WallpaperHue = hue;
            if (!Config.Preference.Theme.AutoHueFromWallpaper) return;
            ModBase.RunInUi(() =>
            {
                ThemeService.ApplyColorResources();
                ThemeService.ApplyGrayResources();
                // 标题栏渐变是缓存的画刷，只改资源字典不会重画，必须让 ThemeManager 重建一次
                ThemeManager.ThemeRefreshMain();
            }, false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[UI] 从壁纸提取主题色失败", ModBase.LogLevel.Developer);
        }
    }

    private static double? Extract()
    {
        if (!Config.Preference.Theme.AutoHueFromWallpaper)
        {
            ModBase.Log("[Wallpaper] 跳过取色：设置里关闭了「主题色跟随壁纸」", ModBase.LogLevel.Debug);
            return null;
        }
        var background = ModMain.frmMain?.ImgBack?.Background;
        if (background is not ImageBrush { ImageSource: { } source })
        {
            ModBase.Log($"[Wallpaper] 跳过取色：背景是 {background?.GetType().Name ?? "null"}，不是带图源的 ImageBrush",
                ModBase.LogLevel.Debug);
            return null;
        }

        // 壁纸是用 BitmapImage 异步解码的，刚换完背景时像素还没就绪（PixelWidth 为 0），
        // 这时候取色只会拿到空图。等解码完成再补一次，否则换壁纸后主色永远是上一次的。
        if (source is BitmapImage { IsDownloading: true } downloading)
        {
            ModBase.Log($"[Wallpaper] 壁纸仍在解码（{downloading.PixelWidth}x{downloading.PixelHeight}），等解码完成后重试",
                ModBase.LogLevel.Debug);
            Watch(downloading);
            return null;
        }

        var hue = ExtractFrom(source);
        if (hue is not null) return hue;

        // 当前壁纸取不到主色（灰调、像素未就绪、或者是纯色背景）时，用随包内置的默认壁纸兜底：
        // 内置图一定存在、而且能同步解码，不会出现「有壁纸但主题色永远是默认值」。
        ModBase.Log("[Wallpaper] 当前壁纸取不到主色，改用内置壁纸兜底", ModBase.LogLevel.Debug);
        return ExtractFrom(BuiltInWallpaper());
    }

    /// <summary>内置默认壁纸（随 PCL.Core 打包），按当前明暗取昼 / 夜两套；同步解码，缓存一份。</summary>
    private static BitmapSource? _builtInWallpaper;

    private static BitmapSource? BuiltInWallpaper()
    {
        if (_builtInWallpaper is not null) return _builtInWallpaper;
        try
        {
            var name = ThemeService.IsDarkMode ? "DefaultBackgroundNight" : "DefaultBackground";
            var uri = new Uri($"pack://application:,,,/PCL.Core;component/UI/Assets/Backgrounds/{name}.png",
                UriKind.Absolute);
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad; // 立即解码，EndInit 之后 PixelWidth 就可读
            image.EndInit();
            image.Freeze();
            _builtInWallpaper = image;
            ModBase.Log($"[Wallpaper] 已载入内置壁纸 {name}（{image.PixelWidth}x{image.PixelHeight}）",
                ModBase.LogLevel.Debug);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Wallpaper] 载入内置壁纸失败");
        }

        return _builtInWallpaper;
    }

    /// <summary>正在异步解码的壁纸，避免重复挂事件。</summary>
    private static readonly HashSet<BitmapImage> PendingImages = [];

    /// <summary>解码完成后重新取一次色。</summary>
    private static void Watch(BitmapImage image)
    {
        if (!PendingImages.Add(image)) return;
        image.DownloadCompleted += (_, _) =>
        {
            PendingImages.Remove(image);
            Refresh();
        };
        image.DownloadFailed += (_, _) => PendingImages.Remove(image);
    }

    /// <summary>对任意图像取主色相；取不到可用颜色时返回 <see langword="null"/>。</summary>
    public static double? ExtractFrom(ImageSource source)
    {
        var frame = source as BitmapSource ?? source as BitmapFrame;
        if (frame is null) return null;
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
        {
            // 解码还没完成：若还在下载就等完成事件，否则放弃（下次换壁纸或重开设置页会再试）
            if (source is BitmapImage pending && pending.IsDownloading) Watch(pending);
            ModBase.Log($"[Wallpaper] 跳过取色：像素未就绪（{frame.PixelWidth}x{frame.PixelHeight}）",
                ModBase.LogLevel.Debug);
            return null;
        }

        var scale = SampleWidth / (double)frame.PixelWidth;
        var target = scale < 1d ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);

        // 先对整张图投票：整张图的主色最能代表壁纸的基调
        var whole = Vote(converted);
        if (whole is not null) return whole;

        // 整张图没有占优色（灰调、黑白、噪点），再只看中间 3/4，排除边角的小物件
        var marginX = Math.Max(1, converted.PixelWidth / 8);
        var marginY = Math.Max(1, converted.PixelHeight / 8);
        var width = converted.PixelWidth - marginX * 2;
        var height = converted.PixelHeight - marginY * 2;
        if (width <= 0 || height <= 0) return null;
        var crop = new CroppedBitmap(converted, new Int32Rect(marginX, marginY, width, height));
        return Vote(crop);
    }

    /// <summary>对一张图做色相投票，返回占优色相；没有主导色时返回 <see langword="null"/>。</summary>
    private static double? Vote(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        if (pixels.Length < 4) return null;

        var votes = new double[Buckets];
        double total = 0d;

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var b = pixels[i] / 255d;
            var g = pixels[i + 1] / 255d;
            var r = pixels[i + 2] / 255d;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var lightness = (max + min) / 2d;
            var delta = max - min;
            if (delta < 0.06d) continue; // 近似灰，没有可用色相
            if (lightness < 0.10d || lightness > 0.94d) continue; // 过暗/过曝

            var saturation = delta / (1d - Math.Abs(2d * lightness - 1d));
            if (saturation < 0.15d) continue; // 太淡，不具代表性

            var hue = HueOf(r, g, b);
            if (hue is null) continue;

            votes[(int)(hue.Value / BucketWidth) % Buckets] += saturation * saturation; // 越鲜艳越有代表性
            total += saturation * saturation;
        }

        if (total < 0.5d) return null;

        var best = 0;
        for (var i = 1; i < Buckets; i++)
            if (votes[i] > votes[best]) best = i;
        if (votes[best] / total < DominantRatio) return null;

        // 只在最高桶 ±1 的范围内做圆平均，得到更平滑的色相，同时不会被相邻色系拉偏
        double sin = 0d, cos = 0d, weightSum = 0d;
        for (var d = -1; d <= 1; d++)
        {
            var i = (best + d + Buckets) % Buckets;
            var weight = votes[i];
            if (weight <= 0d) continue;
            var rad = (i + 0.5d) * BucketWidth * Math.PI / 180d;
            sin += Math.Sin(rad) * weight;
            cos += Math.Cos(rad) * weight;
            weightSum += weight;
        }

        if (weightSum <= 0d) return null;
        var average = Math.Atan2(sin, cos) * 180d / Math.PI;
        if (average < 0d) average += 360d;
        return Math.Round(average, 1);
    }

    /// <summary>RGB（0-1）转色相；输入无彩时返回 <see langword="null"/>。</summary>
    private static double? HueOf(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta < 1e-6d) return null;

        double hue;
        if (Math.Abs(max - r) < 1e-6d) hue = 60d * (((g - b) / delta) % 6d);
        else if (Math.Abs(max - g) < 1e-6d) hue = 60d * ((b - r) / delta + 2d);
        else hue = 60d * ((r - g) / delta + 4d);

        if (hue < 0d) hue += 360d;
        if (hue >= 360d) hue -= 360d;
        return hue;
    }
}
