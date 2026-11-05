// ============================================================================
// [新增文件] AcrylicHelper.cs
// 所属功能：窗口亚克力（DWM 桌面模糊）开关
// 说明：本文件全部为新增代码。
//       - Windows 11 22H2+ 优先使用 DWMWA_SYSTEMBACKDROP_TYPE = Acrylic；
//       - 旧系统回退到 SetWindowCompositionAttribute 的 ACRYLICBLURBEHIND；
//       - 开启时隐藏自定义背景图/视频，避免盖住桌面模糊；
//       - 关闭时完整恢复原有背景、标题栏颜色和控件可见性。
// ============================================================================

using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.UI.Controls;
using PCL.Core.UI.Theme;

namespace PCL.Modules.UI.Theme;

/// <summary>
/// 主窗口亚克力效果。
/// </summary>
public static class AcrylicHelper
{
    private const string CardBackgroundResource = "ColorBrushTransparentBackground";
    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;
    private const int DwmsbtTransientWindow = 3; // Acrylic
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    private static Window? _window;
    private static bool _saved;

    private static Brush? _oldWindowBackground;
    private static Brush? _oldPanFormBackground;
    private static double _oldWindowOpacity;
    private static WindowChrome? _oldChrome;
    private static Thickness _oldGlassFrame;
    private static Thickness _oldPanBackMargin;
    private static object? _oldCardBackground;
    private static SolidColorBrush? _cardBackgroundBrush;
    private static object? _oldSemiTransparent;
    private static Color _cardBaseColor = Color.FromRgb(251, 251, 251);
    private static Visibility _oldImgBackVisibility;
    private static Visibility _oldImgTitleVisibility;
    private static Visibility _oldImgTitleGradientVisibility;
    private static Visibility _oldLeftBlurVisibility;
    private static SolidColorBrush? _oldTitleBrush;
    private static SolidColorBrush? _oldLeftBrush;
    private static Brush? _oldPanLeftBackground;
    private static Color _titleBaseColor = Color.FromRgb(19, 112, 243);
    private static Color _leftBaseColor = Color.FromRgb(32, 32, 32);

    private static BlurBorder? _leftBlur;
    private static FormMain? _form;
    private static System.Windows.Shapes.Rectangle[] _shadowRects = [];
    private static Visibility[] _oldShadowVisibility = [];
    private static readonly List<(MyRadioButton Control, Brush Background)> _titleButtons = [];

    /// <summary>当前配置是否开启亚克力。</summary>
    public static bool IsEnabled => Config.Preference.Theme.AcrylicEnabled;

    /// <summary>
    /// 应用或移除亚克力。可在任意线程调用，但会切回 UI 线程执行。
    /// </summary>
    public static void Apply(Window? window = null)
    {
        window ??= ModMain.frmMain;
        if (window is null) return;

        var dispatcher = window.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Apply(window)));
            return;
        }

        try
        {
            if (!IsEnabled)
            {
                Disable(window);
                return;
            }

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return; // 窗口句柄还没创建，等下一次 Apply

            if (!_saved || !ReferenceEquals(_window, window))
            {
                if (_saved) Disable(_window!);
                Save(window);
            }

            ApplyVisuals(window);
            ApplyBackdrop(hwnd);
            ModBase.Log($"[Acrylic] 已应用（hwnd={hwnd}, chrome={_oldChrome?.GlassFrameThickness}, build={Environment.OSVersion.Version.Build}）",
                ModBase.LogLevel.Debug);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Acrylic] 应用亚克力失败", ModBase.LogLevel.Debug);
        }
    }

    /// <summary>背景图/视频刷新后重新压住，避免把桌面模糊盖回去。</summary>
    public static void Sync()
    {
        if (IsEnabled) Apply(ModMain.frmMain);
    }

    #region 视觉状态

    private static void Save(Window window)
    {
        _window = window;
        _saved = true;
        _oldWindowBackground = window.Background;
        _oldWindowOpacity = window.Opacity;
        _oldPanFormBackground = ((FormMain)window).PanForm.Background;
        _oldPanBackMargin = ((FormMain)window).PanBack.Margin;
        // [关键] WindowChrome 默认 GlassFrameThickness=0 时，WPF 会让客户区保持不透明，
        // DWM 的系统背景只会画在窗口外框，看不到桌面。设为 -1 才会延伸到整个客户区。
        _oldChrome = WindowChrome.GetWindowChrome(window);
        if (_oldChrome is not null)
        {
            _oldGlassFrame = _oldChrome.GlassFrameThickness;
            try
            {
                _oldChrome.GlassFrameThickness = new Thickness(-1);
            }
            catch (InvalidOperationException)
            {
                // 某些主题会把 WindowChrome 冻结，此时只能保留原样。
            }
        }

        if (window is not FormMain form) return;
        _form = form;
        _oldImgBackVisibility = form.ImgBack.Visibility;
        _oldImgTitleVisibility = form.ImgTitle.Visibility;
        _oldImgTitleGradientVisibility = form.ImgTitleGradient.Visibility;
        _oldTitleBrush = form.PanTitle.BackgroundBrush;
        _oldLeftBrush = form.RectLeftBackground.BackgroundBrush;
        _oldPanLeftBackground = form.PanLeft.Background;

        _leftBlur = form.PanLeft.Children.OfType<BlurBorder>().FirstOrDefault();
        if (_leftBlur is not null) _oldLeftBlurVisibility = _leftBlur.Visibility;

        // 根 Grid 上那圈 10px 阴影是给普通窗口用的；亚克力铺满整窗后它会在
        // 窗外侧形成一圈亮边，看起来像模糊“漏出去”了，开启时一并隐藏。
        if (form.PanBack.Parent is Grid root)
        {
            _shadowRects = root.Children.OfType<System.Windows.Shapes.Rectangle>().ToArray();
            _oldShadowVisibility = _shadowRects.Select(r => r.Visibility).ToArray();
        }

        if (_oldTitleBrush is SolidColorBrush title) _titleBaseColor = title.Color;
        if (_oldLeftBrush is SolidColorBrush left) _leftBaseColor = left.Color;

        // 卡片背景（MyCard 的 ColorBrushTransparentBackground）也降低不透明度，
        // 否则只有卡片之间的缝隙能透出桌面。只改这一项，消息框等仍保持可读。
        var resources = Application.Current?.Resources;
        if (resources is not null && resources[CardBackgroundResource] is SolidColorBrush card)
        {
            _oldCardBackground = card;
            _cardBaseColor = card.Color;
        }
        if (resources is not null)
            _oldSemiTransparent = resources["ColorBrushSemiTransparent"];
    }

    private static void ApplyVisuals(Window window)
    {
        window.Background = Brushes.Transparent;
        // DWM 亚克力需要窗口本身不透明，否则 WPF 会把模糊层再乘一次透明度。
        window.Opacity = 1d;

        // 确保每次重新应用时玻璃边框仍是全窗口（主题/尺寸变化可能重置）。
        if (_oldChrome is not null)
        {
            try
            {
                _oldChrome.GlassFrameThickness = new Thickness(-1);
            }
            catch (InvalidOperationException)
            {
                // 忽略冻结的 WindowChrome。
            }
        }

        if (window is not FormMain form) return;
        _form = form;

        // 主题背景由 ThemeManager 写在 PanForm 上；不清掉它会盖住整个客户区的桌面模糊。
        // HyperOS 3 浅色激活态：淡蓝 → 淡紫 → 淡青，给整窗亚克力定一个浅色底。
        form.PanForm.Background = CreateAcrylicTint();
        // 去掉窗口内容外层 10px 的透明/白色边距，让内容填满整个亚克力窗口。
        form.PanBack.Margin = new Thickness(0);
        // 窗口圆角交给 DWM；WPF 内容层不再自己留 6px 圆角，否则四角会露出
        // 一圈未被内容覆盖的亚克力/边框。
        form.RectForm.RadiusX = 0d;
        form.RectForm.RadiusY = 0d;
        foreach (var shadow in _shadowRects) shadow.Visibility = Visibility.Collapsed;
        UpdateLayoutMetrics(form);
        // 自定义背景图/视频会完全盖住桌面模糊。
        form.ImgBack.Visibility = Visibility.Collapsed;
        form.ImgTitle.Visibility = Visibility.Collapsed;
        form.ImgTitleGradient.Visibility = Visibility.Collapsed;

        // 标题栏与左侧栏：给一层跟随深浅色的半透明底。
        // 可读性优先——完全透明会让标题文字直接压在桌面壁纸上，壁纸一花就彻底看不见了。
        // 这里保留足够厚的一层底色，只让边缘透出 DWM 的模糊。
        form.PanTitle.BackgroundBrush = new SolidColorBrush(TitleFill);
        form.RectLeftBackground.BackgroundBrush = new SolidColorBrush(SideFill);
        form.PanLeft.Background = new SolidColorBrush(SideFill);

        // 功能栏里的导航按钮：跟主题走的玻璃底，避免一条灰按钮横在透明标题栏上。
        if (_titleButtons.Count == 0)
            foreach (var button in form.PanTitleSelect.Children.OfType<MyRadioButton>())
                _titleButtons.Add((button, button.Background));
        foreach (var (button, _) in _titleButtons)
            button.Background = new SolidColorBrush(NavFill);

        // 卡片：不透明度按「正文压上去仍然清晰」取值（深色约 78%、浅色约 74%）。
        // 早先用的 0x55 浅蓝在深色主题下会让白字直接糊在玻璃上，这里按主题分两套。
        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            _cardBackgroundBrush = new SolidColorBrush(CardFill);
            resources[CardBackgroundResource] = _cardBackgroundBrush;
            // 悬停态与普通按钮都读这个资源，一起换成同色系。
            resources["ColorBrushSemiTransparent"] = new SolidColorBrush(SemiFill);
        }

        // 左侧的 BlurBorder 采样的是应用内内容，不是桌面；隐藏后由 DWM 负责模糊。
        if (_leftBlur is not null) _leftBlur.Visibility = Visibility.Collapsed;
    }

    private static void RestoreVisuals(Window window)
    {
        window.Background = _oldWindowBackground;
        window.Opacity = _oldWindowOpacity;
        if (_oldChrome is not null)
        {
            try
            {
                _oldChrome.GlassFrameThickness = _oldGlassFrame;
            }
            catch (InvalidOperationException)
            {
                // 忽略冻结的 WindowChrome。
            }
        }

        if (window is not FormMain form) return;
        form.ImgBack.Visibility = _oldImgBackVisibility;
        form.ImgTitle.Visibility = _oldImgTitleVisibility;
        form.ImgTitleGradient.Visibility = _oldImgTitleGradientVisibility;
        form.PanTitle.BackgroundBrush = _oldTitleBrush ?? new SolidColorBrush(_titleBaseColor);
        form.RectLeftBackground.BackgroundBrush = _oldLeftBrush ?? new SolidColorBrush(_leftBaseColor);
        form.PanLeft.Background = _oldPanLeftBackground;
        foreach (var (button, background) in _titleButtons)
            button.Background = background;
        _titleButtons.Clear();
        if (_leftBlur is not null) _leftBlur.Visibility = _oldLeftBlurVisibility;
        form.PanForm.Background = _oldPanFormBackground;
        form.PanBack.Margin = _oldPanBackMargin;
        form.RectForm.RadiusX = 6d;
        form.RectForm.RadiusY = 6d;
        for (var i = 0; i < _shadowRects.Length && i < _oldShadowVisibility.Length; i++)
            _shadowRects[i].Visibility = _oldShadowVisibility[i];
        _shadowRects = [];
        _oldShadowVisibility = [];
        UpdateLayoutMetrics(form);

        var resources = Application.Current?.Resources;
        if (resources is not null && _oldCardBackground is not null)
        {
            resources[CardBackgroundResource] = _oldCardBackground;
            _cardBackgroundBrush = null;
        }
        if (resources is not null && _oldSemiTransparent is not null)
        {
            resources["ColorBrushSemiTransparent"] = _oldSemiTransparent;
            _oldSemiTransparent = null;
        }
    }

    #region 亚克力配色（跟随深浅色，保证文字对比度）

    /// <summary>当前是否深色主题。DWM 的着色与卡片底色都按它分套。</summary>
    private static bool Dark => ThemeService.IsDarkMode;

    /// <summary>
    /// 标题栏底色。深色下压一层近黑，浅色下压一层白——
    /// 两边都保留一点透明度，让窗口顶部的模糊还看得见。
    /// </summary>
    private static Color TitleFill => Dark
        ? Color.FromArgb(0x80, 0x12, 0x16, 0x1C)
        : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);

    /// <summary>左侧功能栏底色。比标题栏稍厚，因为它是整块内容区。</summary>
    private static Color SideFill => Dark
        ? Color.FromArgb(0x94, 0x14, 0x18, 0x1F)
        : Color.FromArgb(0x74, 0xFF, 0xFF, 0xFF);

    /// <summary>导航按钮底色（选中/悬停姿态由控件自己再叠一层）。</summary>
    private static Color NavFill => Dark
        ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x3C, 0xD5, 0xE5, 0xF7);

    /// <summary>
    /// 卡片底色。正文、列表、按钮全压在卡片上，透明度不能低：
    /// 深色取约 78% 的深灰蓝，浅色取约 74% 的冷白，正文对比度都能过 7:1。
    /// </summary>
    private static Color CardFill => Dark
        ? Color.FromArgb(0xC8, 0x1E, 0x23, 0x2C)
        : Color.FromArgb(0xBE, 0xF8, 0xFB, 0xFF);

    /// <summary>控件悬停用的半透明底色（深色下是提亮，浅色下是压暗）。</summary>
    private static Color SemiFill => Dark
        ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x1C, 0x00, 0x00, 0x00);

    #endregion

    private static Brush CreateAcrylicTint()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0d, 0d),
            EndPoint = new Point(1d, 1d)
        };

        if (Dark)
        {
            // 深色：近黑蓝 → 微紫 → 微青，透明度压低，避免整窗发灰。
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x2A, 0x18, 0x1E, 0x29), 0d));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, 0x22, 0x1C, 0x33), 0.52d));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, 0x14, 0x2A, 0x28), 1d));
        }
        else
        {
            // 浅色：淡蓝 → 淡紫 → 淡青，保持原来的 HyperOS 味道。
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x3C, 0xD7, 0xE8, 0xFF), 0d));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x32, 0xEC, 0xE3, 0xFF), 0.52d));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0xDF, 0xF8, 0xF3), 1d));
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 边距变化后同步 FormMain 的裁剪矩形与内部画布尺寸。
    /// 不能直接调用 FormMain_SizeChanged：它会在最大化时强制还原窗口。
    /// </summary>
    private static void UpdateLayoutMetrics(FormMain form)
    {
        form.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (form.PanBack is null) return;

            form.RectForm.Rect = new Rect(0d, 0d, form.PanBack.ActualWidth, form.PanBack.ActualHeight);
            var width = form.PanBack.ActualWidth + 0.001d;
            var height = form.PanBack.ActualHeight + 0.001d;
            form.PanForm.Width = width;
            form.PanForm.Height = height;
            form.PanMain.Width = width;
            form.PanMain.Height = form.PanTitle is null
                ? height
                : Math.Max(0d, height - form.PanTitle.ActualHeight);
            form.VideoBack.Width = width;
            form.VideoBack.Height = height;
        }));
    }

    #endregion

    #region DWM

    private static void ApplyBackdrop(IntPtr hwnd)
    {
        ApplyGlassFrame(hwnd);
        var system = TrySystemBackdrop(hwnd);
        ModBase.Log($"[Acrylic] systemBackdrop={system}", ModBase.LogLevel.Debug);
        if (system) return;

        try
        {
            var tint = ThemeService.IsDarkMode
                ? unchecked((int)0xB0202020)
                : unchecked((int)0xB0F2F2F2);
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = tint
            };
            var size = Marshal.SizeOf<AccentPolicy>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Acrylic] 旧系统亚克力接口失败", ModBase.LogLevel.Debug);
        }
    }

    private static bool TrySystemBackdrop(IntPtr hwnd)
    {
        // DWMWA_SYSTEMBACKDROP_TYPE 从 Windows 11 22621 开始可用。
        if (Environment.OSVersion.Version.Build < 22621) return false;

        try
        {
            var type = DwmsbtTransientWindow;
            if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int)) != 0)
                return false;

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ApplyGlassFrame(IntPtr hwnd)
    {
        try
        {
            // 去掉 DWM 的 1px 亮边框，并让系统按圆角裁切窗口。
            // 否则亚克力会沿着窗口外框多出一圈，看起来像“漏到窗口外”。
            var border = DwmColorNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
            var corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch (Exception)
        {
            // 旧系统不支持时忽略。
        }
    }

    private static void Disable(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (_saved && ReferenceEquals(_window, window)) RestoreVisuals(window);

        if (hwnd != IntPtr.Zero)
        {
            try
            {
                var type = DwmsbtNone;
                _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int));
                var margins = new Margins();
                _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
                var border = DwmColorDefault;
                _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
                var corner = 0;
                _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

                var accent = new AccentPolicy { AccentState = 0 };
                var size = Marshal.SizeOf<AccentPolicy>();
                var pointer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, pointer, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute = WcaAccentPolicy,
                        Data = pointer,
                        SizeOfData = size
                    };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }
            catch (Exception)
            {
                // 退出或系统不支持时忽略。
            }
        }

        _saved = false;
        _window = null;
        _form = null;
        _leftBlur = null;
    }

    #endregion

    #region 原生

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    #endregion
}
