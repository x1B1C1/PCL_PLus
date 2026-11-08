using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

/// <summary>
/// 配色模式更改事件。
/// </summary>
/// <param name="isDarkMode">当前是否为深色模式</param>
/// <param name="theme">当前配色主题</param>
public delegate void ColorModeChangedEvent(bool isDarkMode, ColorTheme theme);

/// <summary>
/// 配色主题更改事件。
/// </summary>
/// <param name="theme">当前配色主题</param>
public delegate void ColorThemeChangedEvent(ColorTheme theme);

[LifecycleScope("theme", "主题", false)]
[LifecycleService(LifecycleState.WindowCreating)]
public sealed partial class ThemeService
{
    [AnyConfigItem<ToneProfileConfig>("UiToneProfiles", ConfigSource.Local)]
    public static partial ToneProfileConfig ToneProfiles { get; set; }

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnColorModeConfigChanged => new(
        scope: Config.Preference.Theme.ColorModeConfig,
        trigger: ConfigEvent.Update,
        handler: _ => RefreshColorMode()
    );

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnColorThemeConfigChanged => new(
        scope: [Config.Preference.Theme.DarkColorConfig, Config.Preference.Theme.LightColorConfig],
        trigger: ConfigEvent.Update,
        handler: e =>
        {
            // ignore no change or non-current color theme change
            if (e.OldValue == e.Value) return;
            if (IsDarkMode) { if (e.Item == Config.Preference.Theme.LightColorConfig) return; }
            else { if (e.Item == Config.Preference.Theme.DarkColorConfig) return; }
            // trigger color refresh
            if (Lifecycle.CurrentState > LifecycleState.Loading)
            {
                Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(() =>
                {
                    ApplyColorResources();
                    ColorThemeChanged?.Invoke(CurrentTheme);
                    _AprilFoolLogic();
                });
            }
        }
    );

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnCustomThemeConfigChanged => new(
        scope:
        [
            Config.Preference.Theme.CustomThemeEnabledConfig,
            Config.Preference.Theme.WindowHueConfig,
            Config.Preference.Theme.WindowSatConfig,
            Config.Preference.Theme.WindowLightConfig,
            Config.Preference.Theme.WindowDeltaConfig,
            Config.Preference.Theme.WindowDeltaTripleConfig
        ],
        trigger: ConfigEvent.Update,
        handler: e =>
        {
            if (e.OldValue == e.Value) return;
            if (Lifecycle.CurrentState <= LifecycleState.Loading) return;
            Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(() =>
            {
                ApplyColorResources();
                ColorThemeChanged?.Invoke(CurrentTheme);
            });
        }
    );

    [LifecycleStart]
    private static void _Start()
    {
        IsDarkMode = _IsDarkMode();
        _LogStatus();
        _RefreshAll();
    }

    private static void _LogStatus()
    {
        Context.Debug($"当前状态: {(IsDarkMode ? "Dark" : "Light")}, {CurrentTheme}");
    }

    private static bool _IsDarkMode() => Config.Preference.Theme.ColorMode switch
    {
        ColorMode.Light => false,
        ColorMode.Dark => true,
        ColorMode.System => SystemThemeHelper.IsSystemInDarkMode(),
        _ => false
    };

    /// <summary>
    /// 当前是否为深色主题。
    /// </summary>
    public static bool IsDarkMode { get; private set; }

    /// <summary>
    /// 配色模式更改事件。
    /// </summary>
    public static event ColorModeChangedEvent? ColorModeChanged;

    /// <summary>
    /// 配色主题更改事件。
    /// </summary>
    public static event ColorThemeChangedEvent? ColorThemeChanged;

    private static void _RefreshAll()
    {
        ApplyGrayResources();
        ApplyColorResources();
        ColorModeChanged?.Invoke(IsDarkMode, CurrentTheme);
        _AprilFoolLogic();
    }

    private static void _AprilFoolLogic()
    {
        // for HMCL theme on April Fools' Day
        if (Basics.IsAprilFool) Config.Preference.WindowTitleTypeConfig.TriggerEvent(ConfigEvent.Changed, null);
        else if (CurrentTheme == ColorTheme.HmclBlue) CurrentTheme = ColorTheme.CatBlue;
    }

    /// <summary>
    /// 刷新配色模式，若检测到当前配色模式有实际更改，则会触发主题刷新。
    /// </summary>
    public static void RefreshColorMode()
    {
        var isDarkMode = _IsDarkMode();
        if (IsDarkMode == isDarkMode) return;
        Context.Info("正在更改配色模式");
        IsDarkMode = isDarkMode;
        _LogStatus();
        if (Lifecycle.CurrentState > LifecycleState.Loading)
        {
            Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(_RefreshAll);
        }
    }

    /// <summary>
    /// 当前使用的色彩属性。
    /// </summary>
    public static ToneProfile CurrentTone => IsDarkMode ? ToneProfiles.Dark : ToneProfiles.Light;

    /// <summary>
    /// 当前使用的主题色。
    /// </summary>
    public static ColorTheme CurrentTheme
    {
        get
        {
            var theme = Config.Preference.Theme;
            return IsDarkMode ? theme.DarkColor : theme.LightColor;
        }
        set
        {
            var theme = Config.Preference.Theme;
            var config = IsDarkMode ? theme.DarkColorConfig : theme.LightColorConfig;
            config.SetValue(value);
        }
    }

    /// <summary>
    /// 获取当前色彩主题对应的各种参数。
    /// </summary>
    public static (int Hue, double LightAdjust, double ChromaAdjust) GetCurrentThemeArgs()
    {
        var theme = CurrentTheme;
        return theme switch
        {
            ColorTheme.SkyBlue => (235, 0.36, 0.2),
            ColorTheme.CatBlue => (255, 0, -0.2),
            ColorTheme.DeathBlue => (268, -0.05, -0.1),
            ColorTheme.HmclBlue => (275, -0.03, -0.35),
#if DEBUG
            _ => ((int)theme, 0, 0)
#else
            _ => throw new IndexOutOfRangeException($"Invalid theme index: {(int)theme}")
#endif
        };
    }

    /// <summary>
    /// 当前是否启用了自定义主题。
    /// </summary>
    public static bool IsCustomTheme => Config.Preference.Theme.CustomThemeEnabled;

    /// <summary>
    /// 读取自定义主题参数；未启用时返回 <see langword="null"/>。
    /// <para>
    /// 存储值到实际偏移的换算与原版 PCL2 一致：明度 20 为不调整，标题栏偏移 90 为纯色渐变。
    /// </para>
    /// </summary>
    public static PclThemeArgs? GetCustomThemeArgs()
    {
        var config = Config.Preference.Theme;
        if (!config.CustomThemeEnabled) return null;
        var args = new PclThemeArgs(
            Hue: Math.Clamp(config.WindowHue, 0, 360),
            Saturation: Math.Clamp(config.WindowSat, 0, 100),
            LightAdjust: Math.Clamp(config.WindowLight - 20, -20, 20),
            TopbarDelta: Math.Clamp(config.WindowDelta - 90, -180, 180),
            TopbarHueTriple: _ParseTriple(config.WindowDeltaTriple));

        // 预设里有几个主题的标题栏形态无法用「色相 + 明度 + 偏移」三个滑块表达：
        // 玄素黑中间更暗（明度 25/15/25），欧皇彩是四点宽渐变。
        // 这些属于结构性参数，只有选中预设时才生效；手动拖过滑块会把编号打回「自定义」，因此不会误用。
        if (PclThemePresets.ByIndex(config.ThemeSelected) is { } preset)
            args = args with
            {
                TopbarHueTriple = args.TopbarHueTriple ?? preset.Args.TopbarHueTriple,
                TopbarLightTriple = preset.Args.TopbarLightTriple,
                TopbarWide = preset.Args.TopbarWide
            };

        return args;
    }

    /// <summary>
    /// 解析形如 <c>-30,10,50</c> 的三元组；格式不符时返回 <see langword="null"/>。
    /// </summary>
    private static int[]? _ParseTriple(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',');
        if (parts.Length != 3) return null;
        var result = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i].Trim(), out result[i]))
                return null;
        return result;
    }

    private static double _AdjustLinear(double value, double adjustment)
    {
        if (adjustment == 0) return value;
        // 确保输入在合理范围内
        value = Math.Clamp(value, 0.0, 1.0);
        adjustment = Math.Clamp(adjustment, -1.0, 1.0);
        // 非对称线性插值
        return adjustment switch
        {
            > 0 => value + (1.0 - value) * adjustment,
            _ => value + value * adjustment
        };
    }

    private static CatColorResource[] _CalculateGrays(ToneProfile tone) => [
        LabColor.FromLch(tone.L1).ToCatColor("Gray1"),
        LabColor.FromLch(tone.L2).ToCatColor("Gray2"),
        LabColor.FromLch(tone.L3).ToCatColor("Gray3"),
        LabColor.FromLch(tone.L4).ToCatColor("Gray4"),
        LabColor.FromLch(tone.L5).ToCatColor("Gray5"),
        LabColor.FromLch(tone.L6).ToCatColor("Gray6"),
        LabColor.FromLch(tone.L7).ToCatColor("Gray7"),
        LabColor.FromLch(tone.L8).ToCatColor("Gray8"),
        LabColor.FromLch(tone.LWhite, alpha:tone.AHalfWhite).ToCatColor("HalfWhite", false),
        LabColor.FromLch(tone.LWhite, alpha:tone.ASemiWhite).ToCatColor("SemiWhite", false),
        LabColor.FromLch(tone.LWhite).ToCatColor("White", false),
        LabColor.FromLch(tone.LWhite, alpha:tone.ATransparent).ToCatColor("Transparent", false),
        LabColor.FromLch(tone.LBackground, alpha:tone.ABackground).ToCatColor("TransparentBackground", false),
        LabColor.FromLch(tone.LBackground).ToCatColor("Background", false),
        LabColor.FromLch(tone.LBackground, alpha:tone.AToolTip).ToCatColor("ToolTip", false),
        LabColor.FromLch(tone.L7, 0.25, 30, tone.AHalfTransparent).ToCatColor("RedBack", false),
        LabColor.FromLch(tone.LForeground).ToCatColor("Memory", false),
    ];

    private static CatColorResource[] _CalculateColors(ToneProfile tone, (int hue, double lightAdj, double chromaAdj) args) => [
        LabColor.FromLch(_AdjustLinear(tone.L1, args.lightAdj * 0.1), _AdjustLinear(tone.C1, args.chromaAdj * 0.25), args.hue).ToCatColor("1"),
        LabColor.FromLch(_AdjustLinear(tone.L2, args.lightAdj), _AdjustLinear(tone.C2, args.chromaAdj), args.hue).ToCatColor("2"),
        LabColor.FromLch(_AdjustLinear(tone.L3, args.lightAdj), _AdjustLinear(tone.C3, args.chromaAdj), args.hue).ToCatColor("3"),
        LabColor.FromLch(_AdjustLinear(tone.L4, args.lightAdj), _AdjustLinear(tone.C4, args.chromaAdj), args.hue).ToCatColor("4"),
        LabColor.FromLch(_AdjustLinear(tone.L5, args.lightAdj), _AdjustLinear(tone.C5, args.chromaAdj), args.hue).ToCatColor("5"),
        LabColor.FromLch(_AdjustLinear(tone.L6, args.lightAdj), _AdjustLinear(tone.C6, args.chromaAdj), args.hue).ToCatColor("6"),
        LabColor.FromLch(_AdjustLinear(tone.L7, args.lightAdj), _AdjustLinear(tone.C7, args.chromaAdj), args.hue).ToCatColor("7"),
        LabColor.FromLch(_AdjustLinear(tone.L8, args.lightAdj), _AdjustLinear(tone.C8, args.chromaAdj), args.hue).ToCatColor("8"),
        LabColor.FromLch(_AdjustLinear(tone.L8, args.lightAdj), _AdjustLinear(tone.C8, args.chromaAdj), args.hue, tone.ASemiTransparent).ToCatColor("SemiTransparent", false),
        LabColor.FromLch(_AdjustLinear(tone.L5, args.lightAdj), _AdjustLinear(tone.C5, args.chromaAdj), args.hue).ToCatColor("Bg0"),
        LabColor.FromLch(_AdjustLinear(tone.L7, args.lightAdj), _AdjustLinear(tone.C7, args.chromaAdj), args.hue, tone.ASemiWhite).ToCatColor("Bg1"),
    ];

    private static CatColorResource[] LightGrayCache { get => field ??= _CalculateGrays(ToneProfiles.Light); set; } = null!;

    private static CatColorResource[] DarkGrayCache { get => field ??= _CalculateGrays(ToneProfiles.Dark); set; } = null!;

    /// <summary>
    /// 清除灰度配色的计算缓存。
    /// </summary>
    public static void InvalidateGrayCache()
    {
        LightGrayCache = null!;
        DarkGrayCache = null!;
    }

    /// <summary>
    /// 应用灰度配色到 WPF 资源字典。
    /// </summary>
    public static void ApplyGrayResources()
    {
        if (!IsDarkMode) { ApplyLivingForkPalette(includeAccent: false); return; }
        var cache = IsDarkMode ? DarkGrayCache : LightGrayCache;
        foreach (var c in cache) c.Apply();
    }

    // ===== PCL Living Fork 主题（移植自 presentation\Foundation\LegacyThemeService） =====
    // 对应 ModSecret 的默认值：Hue=225、Saturation=100、Light=40、Delta=115。
    private const int LfHue = 225;
    private const int LfSat = 100;
    private const int LfLight = 40;
    private const int LfDelta = 115;

    /// <summary>LF 的浅色灰度表（深色是另一套，见 <see cref="LfDarkGrays" />）。</summary>
    private static readonly string[] LfLightGrays =
        ["#404040", "#737373", "#8c8c8c", "#a6a6a6", "#cccccc", "#ebebeb", "#f0f0f0", "#f5f5f5"];

    private static readonly string[] LfDarkGrays =
        ["#DCE2EB", "#B4BECD", "#A1ACBD", "#7F8B9E", "#586275", "#3C4656", "#303846", "#272F3B"];

    /// <summary>语义角色色：浅色 / 深色两套，对应 LF 的 <c>Roles</c> 表。</summary>
    private static readonly (string Name, string Light, string Dark)[] LfRoles =
    [
        ("White", "#FFFFFF", "#252932"),
        ("HalfWhite", "#55FFFFFF", "#55252932"),
        ("SemiWhite", "#BBFFFFFF", "#BB252932"),
        ("Card", "#F5FFFFFF", "#F5252932"),
        ("Dialog", "#FBFBFB", "#252A34"),
        ("Popup", "#FFFFFF", "#2B303B"),
        ("Background", "#F5F5F5", "#171B22"),
        ("BackgroundTransparentSidebar", "#F1FFFFFF", "#F11C2028"),
        ("TextSecondary", "#5C5C5C", "#B4BECD"),
        ("OnAccent", "#FFFFFF", "#141B2B"),
        ("TitleForeground", "#FFFFFF", "#DCE2EB"),
        ("TitleSelected", "#FFFFFF", "#303B52"),
        ("RedBack", "#80FBDDDD", "#804E252C"),
        ("RedLight", "#FF4C4C", "#FF9A9F"),
        ("RedDark", "#CE2111", "#FF8B91"),
        ("InfoBack", "#D9ECFF", "#24364A"),
        ("InfoBorder", "#1172D4", "#6AAFF3"),
        ("InfoText", "#0F64B8", "#A9D5FF"),
        ("WarningBack", "#FFEBD7", "#413421"),
        ("WarningBorder", "#F57A00", "#DAAE58"),
        ("WarningText", "#D86C00", "#F4CE8A"),
        ("DangerBack", "#FFDDDF", "#43272D"),
        ("DangerBorder", "#D82929", "#EA818B"),
        ("DangerText", "#BF0B0B", "#FFB3BA"),
        ("SuccessText", "#218A43", "#8ED8AF"),
        ("Shadow", "#343D4A", "#000000")
    ];

    /// <summary>HSL 转 RGB；<paramref name="h" /> 是 0-360 色相，<paramref name="s" />/<paramref name="v" /> 是 0-100。</summary>
    private static Color Hsl(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        s = Math.Clamp(s, 0, 100) / 100d;
        v = Math.Clamp(v, 0, 100) / 100d;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60d % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Blend(Color first, double firstWeight, Color second, double secondWeight)
    {
        var total = firstWeight + secondWeight;
        if (total <= 0) return first;
        return Color.FromRgb(
            (byte)Math.Round((first.R * firstWeight + second.R * secondWeight) / total),
            (byte)Math.Round((first.G * firstWeight + second.G * secondWeight) / total),
            (byte)Math.Round((first.B * firstWeight + second.B * secondWeight) / total));
    }

    /// <summary>按权重把多个颜色逐通道相加（结果截断到 0-255），对应 LF 的 <c>Add(Scale(...))</c>。</summary>
    private static Color AddScaled(params (Color Color, double Scale)[] parts)
    {
        double r = 0, g = 0, b = 0;
        foreach (var (color, scale) in parts)
        {
            r += color.R * scale;
            g += color.G * scale;
            b += color.B * scale;
        }

        return Color.FromRgb((byte)Math.Min(255, Math.Round(r)), (byte)Math.Min(255, Math.Round(g)),
            (byte)Math.Min(255, Math.Round(b)));
    }

    private static void PutColor(ResourceDictionary res, string suffix, Color color)
    {
        res["ColorObject" + suffix] = color;
        res["ColorBrush" + suffix] = new SolidColorBrush(color);
    }

    /// <summary>
    ///     取当前主题的 LF 配色参数：色相与饱和度来自所选预设（或自定义滑块），
    ///     亮度与标题栏偏移再用 LF 的默认值折算。这样每个内置主题都有各自的 LF 配色。
    /// </summary>
    private static (double Hue, double Sat, double Light, double Delta) GetLivingForkArgs()
    {
        // 内置预设：色相/饱和度/亮度偏移直接取预设表，保证每个主题都对应 LF 原有的色相
        var selected = Config.Preference.Theme.ThemeSelected;
        if (selected != PclThemePresets.CustomIndex
            && PclThemePresets.ByIndex(selected) is { } preset)
        {
            var a = preset.Args;
            return (a.Hue, a.Saturation, LfLight - 20 + a.LightAdjust, LfDelta - 90 + a.TopbarDelta);
        }

        // 自定义主题：用滑块上的值
        var args = GetCustomThemeArgs();
        var hue = args?.Hue ?? LfHue;
        var sat = args?.Saturation ?? LfSat;
        var light = LfLight - 20 + (args?.LightAdjust ?? 0);
        var delta = LfDelta - 90 + (args?.TopbarDelta ?? 0);
        return (hue, sat, light, delta);
    }

    /// <summary>
    ///     按 LF 的公式计算并写入整套配色（<c>ColorBrush1..8</c>、灰度、Bg0/Bg1、语义角色）。
    ///     <para>
    ///         浅色与 LF 的 <c>LegacyThemeService.Refresh</c> 完全一致，色相取自当前所选主题；
    ///         深色用 LF 自己的固定 tone 表（LF 的深色本来就不跟随主题色相）。
    ///     </para>
    /// </summary>
    private static void ApplyLivingForkPalette(bool includeAccent)
    {
        var res = Lifecycle.CurrentApplication.Resources;
        var dark = IsDarkMode;
        var grays = dark ? LfDarkGrays : LfLightGrays;
        var grayColors = new Color[grays.Length];
        for (var i = 0; i < grays.Length; i++)
        {
            grayColors[i] = ParseColor(grays[i]);
            PutColor(res, "Gray" + (i + 1), grayColors[i]);
        }

        if (includeAccent)
        {
            if (dark)
            {
                string[] tones =
                [
                    "#DCE2EB", "#AABFFF", "#91AAFF", "#ADC2FF", "#6583B7",
                    "#394A69", "#2B3851", "#252F44", "#7894C5", "#BE252F44"
                ];
                for (var i = 0; i < 8; i++) PutColor(res, (i + 1).ToString(), ParseColor(tones[i]));
                PutColor(res, "Bg0", ParseColor(tones[8]));
                PutColor(res, "Bg1", ParseColor(tones[9]));
                PutColor(res, "SemiTransparent", WithAlpha(ParseColor(tones[7]), 1));
            }
            else
            {
                var (hue, sat, light, _) = GetLivingForkArgs();
                var colors = new[]
                {
                    Hsl(hue, sat * 0.2, 25 + light * 0.3),
                    Hsl(hue, sat, 45 + light),
                    Hsl(hue, sat, 55 + light),
                    Hsl(hue, sat, 65 + light),
                    Hsl(hue, sat, 80 + light * 0.4),
                    Hsl(hue, sat, 91 + light * 0.1),
                    Hsl(hue, sat, 95),
                    Hsl(hue, sat, 97)
                };
                for (var i = 0; i < colors.Length; i++) PutColor(res, (i + 1).ToString(), colors[i]);
                // LF 是「按权重相加」而不是混合：0.4×c4 + 0.4×c5 + 0.2×gray4
                PutColor(res, "Bg0", AddScaled((colors[3], 0.4), (colors[4], 0.4), (grayColors[3], 0.2)));
                PutColor(res, "Bg1", WithAlpha(colors[6], 190));
                PutColor(res, "SemiTransparent", WithAlpha(colors[7], 1));
            }
        }

        foreach (var role in LfRoles) PutColor(res, role.Name, ParseColor(dark ? role.Dark : role.Light));

        // 兼容旧键名：卡片等控件用的是这些名字，不补的话它们会保留 Application.xaml 里的
        // 静态深色值，出现「浅色底 + 深色卡片」的割裂。
        PutColor(res, "TransparentBackground", ParseColor(dark ? "#F5252932" : "#F5FFFFFF"));
        PutColor(res, "ToolTip", ParseColor(dark ? "#2B303B" : "#E6F5F5F5"));
        PutColor(res, "Memory", ParseColor(dark ? "#B4BECD" : "#343D4A"));
    }

    /// <summary>
    ///     生成 LF 的标题栏渐变：三点渐变，两端色相各偏 <c>Delta-90</c>，中间居中。
    /// </summary>
    public static LinearGradientBrush CreateLivingForkTitleBrush()
    {
        var dark = IsDarkMode;
        var (hue, sat, light, delta) = GetLivingForkArgs();
        var saturation = dark ? 72 : sat;
        var hueScale = dark ? 0.5 : 1.0;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(
            Hsl(hue - delta * hueScale, saturation, dark ? 20 : 48 + light), 0));
        brush.GradientStops.Add(new GradientStop(
            Hsl(hue, saturation, dark ? 24 : 54 + light), 0.5));
        brush.GradientStops.Add(new GradientStop(
            Hsl(hue + delta * hueScale, saturation, dark ? 20 : 48 + light), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 应用彩色配色到 WPF 资源字典。
    /// <para>
    /// 启用自定义主题时走原版 PCL2 的 HSL 引擎，否则使用内置的 LCh 预设配色。
    /// </para>
    /// </summary>
    public static void ApplyColorResources()
    {
        // 只有「自定义」主题才交给 HSL 引擎；内置预设（龙猫蓝等）一律走 LF 色板，
        // 这样浅色与深色的观感都与 PCL Living Fork 一致。
        if (Config.Preference.Theme.ThemeSelected == PclThemePresets.CustomIndex
            && GetCustomThemeArgs() is { } custom)
        {
            foreach (var c in PclThemeEngine.Generate(custom, IsDarkMode, CurrentTone)) c.Apply();
            ApplySystemColors();
            return;
        }

        ApplyLivingForkPalette(includeAccent: true);
        ApplySystemColors();
    }

    /// <summary>
    ///     把 <see cref="SystemColors" /> 一并对齐到当前主题。
    ///     <para>
    ///         WPF 的原生控件（滚动条、文本框右键菜单、系统对话框里的静态控件）读取的是
    ///         <c>SystemColors.*BrushKey</c>，不跟随我们自己的 <c>ColorBrushN</c>。不覆写的话，
    ///         深色主题下会出现「白底黑字的滚动条」这类割裂感。
    ///     </para>
    ///     <para>
    ///         这是全局副作用：覆写后所有依赖系统色画的控件都会变，所以只在主题重新计算时整体刷新一次，
    ///         而不是逐处零散设置。
    ///     </para>
    /// </summary>
    public static void ApplySystemColors()
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        void Set(ResourceKey key, string brushKey)
        {
            if (resources[brushKey] is SolidColorBrush brush) resources[key] = brush;
        }

        Set(SystemColors.WindowBrushKey, "ColorBrushBackground");
        Set(SystemColors.ControlBrushKey, "ColorBrushBackground");
        Set(SystemColors.WindowTextBrushKey, "ColorBrush1");
        Set(SystemColors.ControlTextBrushKey, "ColorBrush1");
        Set(SystemColors.MenuBrushKey, "ColorBrushBackground");
        Set(SystemColors.MenuTextBrushKey, "ColorBrush1");
        Set(SystemColors.MenuBarBrushKey, "ColorBrushBackground");
        Set(SystemColors.HighlightBrushKey, "ColorBrush3");
        Set(SystemColors.HighlightTextBrushKey, "ColorBrushWhite");
        Set(SystemColors.GrayTextBrushKey, "ColorBrushGray4");
        Set(SystemColors.ScrollBarBrushKey, "ColorBrushGray5");
        Set(SystemColors.ActiveBorderBrushKey, "ColorBrush6");
        Set(SystemColors.InactiveBorderBrushKey, "ColorBrush7");
        Set(SystemColors.ControlLightBrushKey, "ColorBrush7");
        Set(SystemColors.ControlDarkBrushKey, "ColorBrush6");
    }
}
