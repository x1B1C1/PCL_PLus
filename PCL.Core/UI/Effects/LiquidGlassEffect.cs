// ============================================================================
// [修改文件] LiquidGlassEffect.cs
// 着色器参数与原有 API 不变；本次只修正 Freezable 克隆顺序与着色器加载失败
// 时的降级逻辑。着色器本体（LiquidGlass.hlsl / LiquidGlass.ps）另行修复了
// 透明混合与分层合成，详见该文件头部说明。
// ============================================================================

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PCL.Core.UI.Effects;

/// <summary>
/// 液态玻璃材质。
/// <para>
/// 只在控件自身的边缘产生折射、色散与内侧高光，<b>不采样任何背景</b>，
/// 因此可以安全地挂在按钮、卡片这类小控件上，不会影响界面背景。
/// </para>
/// <para>
/// 中心区域（距边缘超过 <see cref="EffectWidth"/> 的部分）直接返回原像素，
/// 所以控件内的文字始终保持锐利，不会被材质糊掉。
/// </para>
/// </summary>
public sealed class LiquidGlassEffect : ShaderEffect
{
    private const string ShaderUri =
        "pack://application:,,,/PCL.Core;component/UI/Assets/Shaders/LiquidGlass.ps";

    private static readonly object ShaderLock = new();
    private static PixelShader? _cachedShader;
    // [修改] 记录一次失败，避免每个按钮都重复尝试加载损坏的着色器资源。
    private static bool _shaderFailed;

    static LiquidGlassEffect()
    {
        _EnsureShader();
    }

    /// <summary>
    /// 着色器资源是否可用。不可用时宿主应直接跳过玻璃覆盖层，避免挂一个没有效果的 Effect。
    /// </summary>
    public static bool IsAvailable => _cachedShader is not null;

    public LiquidGlassEffect()
    {
        PixelShader = _cachedShader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(BorderSizeProperty);
        UpdateShaderValue(CornerRadiusProperty);
        UpdateShaderValue(EffectWidthProperty);
        UpdateShaderValue(RefractionPowerProperty);
        UpdateShaderValue(HighlightProperty);
        UpdateShaderValue(TintProperty);
        UpdateShaderValue(DispersionProperty);
    }

    #region 属性

    /// <summary>
    /// 控件尺寸（像素）。由宿主在 SizeChanged 时写入，shader 用它把 UV 换算成像素坐标。
    /// </summary>
    public Point BorderSize
    {
        get => (Point)GetValue(BorderSizeProperty);
        set => SetValue(BorderSizeProperty, value);
    }

    /// <summary>
    /// 控件圆角半径，需与实际 Border.CornerRadius 一致。
    /// </summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>
    /// 折射影响带宽（像素）。越大玻璃越「厚」，超过控件高度一半时会盖住文字。
    /// </summary>
    public double EffectWidth
    {
        get => (double)GetValue(EffectWidthProperty);
        set => SetValue(EffectWidthProperty, Math.Clamp(value, 1d, 120d));
    }

    /// <summary>
    /// 折射强度（像素），边缘像素向外偏移的量。
    /// </summary>
    public double RefractionPower
    {
        get => (double)GetValue(RefractionPowerProperty);
        set => SetValue(RefractionPowerProperty, Math.Clamp(value, 0d, 40d));
    }

    /// <summary>
    /// 内侧高光强度，0 关闭。
    /// </summary>
    public double Highlight
    {
        get => (double)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, Math.Clamp(value, 0d, 1d));
    }

    /// <summary>
    /// 玻璃色调，Alpha 通道作为融合比例。
    /// </summary>
    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <summary>
    /// 色散强度，0 关闭。
    /// </summary>
    public double Dispersion
    {
        get => (double)GetValue(DispersionProperty);
        set => SetValue(DispersionProperty, Math.Clamp(value, 0d, 1d));
    }

    #endregion

    #region 依赖属性

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);

    public static readonly DependencyProperty BorderSizeProperty =
        DependencyProperty.Register(nameof(BorderSize), typeof(Point), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(new Point(120, 32), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(3d, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty EffectWidthProperty =
        DependencyProperty.Register(nameof(EffectWidth), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(9d, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty RefractionPowerProperty =
        DependencyProperty.Register(nameof(RefractionPower), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(3.5d, PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty HighlightProperty =
        DependencyProperty.Register(nameof(Highlight), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.22d, PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(Color.FromArgb(0, 255, 255, 255), PixelShaderConstantCallback(5)));

    public static readonly DependencyProperty DispersionProperty =
        DependencyProperty.Register(nameof(Dispersion), typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.55d, PixelShaderConstantCallback(6)));

    #endregion

    #region Freezable

    protected override Freezable CreateInstanceCore() => new LiquidGlassEffect();

    protected override void CloneCore(Freezable sourceFreezable)
    {
        // [修改] 按 WPF 的标准顺序：先让基类复制 Freezable 内部状态，再复制本类参数。
        base.CloneCore(sourceFreezable);
        CopyShaderValues(sourceFreezable);
    }

    protected override void CloneCurrentValueCore(Freezable sourceFreezable)
    {
        base.CloneCurrentValueCore(sourceFreezable);
        CopyShaderValues(sourceFreezable);
    }

    protected override void GetAsFrozenCore(Freezable sourceFreezable)
    {
        base.GetAsFrozenCore(sourceFreezable);
        CopyShaderValues(sourceFreezable);
    }

    protected override void GetCurrentValueAsFrozenCore(Freezable sourceFreezable)
    {
        base.GetCurrentValueAsFrozenCore(sourceFreezable);
        CopyShaderValues(sourceFreezable);
    }

    /// <summary>[修改] 复制本类的七个着色器参数，供上述四个克隆入口复用。</summary>
    private void CopyShaderValues(Freezable sourceFreezable)
    {
        if (sourceFreezable is not LiquidGlassEffect source) return;

        BorderSize = source.BorderSize;
        CornerRadius = source.CornerRadius;
        EffectWidth = source.EffectWidth;
        RefractionPower = source.RefractionPower;
        Highlight = source.Highlight;
        Tint = source.Tint;
        Dispersion = source.Dispersion;
    }

    #endregion

    private static void _EnsureShader()
    {
        if (_cachedShader is not null || _shaderFailed) return;

        lock (ShaderLock)
        {
            if (_cachedShader is not null || _shaderFailed) return;

            try
            {
                _cachedShader = new PixelShader
                {
                    UriSource = new Uri(ShaderUri, UriKind.Absolute)
                };
            }
            catch
            {
                // [修改] 着色器资源缺失时不再塞一个空 PixelShader（部分驱动会在渲染时抛异常），
                //        而是记录失败并让 PixelShader 保持 null，效果不生效但不影响启动。
                _shaderFailed = true;
                _cachedShader = null;
            }
        }
    }
}
