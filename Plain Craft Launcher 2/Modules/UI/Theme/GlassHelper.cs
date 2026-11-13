// ============================================================================
// [修改文件] GlassHelper.cs
// 原有 API（Apply / RefreshAll / RefreshParams）与控件调用点保持不变。
// 本次液态玻璃修复集中在本文件：
//   1. 用 CompositionTarget.Rendering 同步覆盖层，修复 Adorner 不继承页面
//      切换动画 Opacity 导致的“玻璃幽灵”；
//   2. 手动按 AdornerLayer + ScrollViewer 视口裁剪覆盖层，修复滚动后玻璃
//      浮在页头/滚动条上的问题；
//   3. 监听 SizeChanged 并重新计算距离场，修复窗口缩放/延迟布局后玻璃尺寸
//      仍是旧值的问题；
//   4. 在 AdornerLayer 上按页面状态隐藏/显示覆盖层，保证叠放次序与页面一致；
//   5. 每帧补偿 RenderTransform 残差，修复按钮按压缩放、页面位移动画时的玻璃错位。
// 代码内以 [修改] 标记新增或重写的片段；未标记部分为原有逻辑。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.UI.Effects;
using PCL.Core.UI.Theme;

namespace PCL.Modules.UI.Theme;

/// <summary>
/// 液态玻璃材质的挂载器。
/// <para>
/// 只作用于控件自身（药丸形导航、圆形图标按钮等），<b>不会修改界面背景</b>。
/// 由配置项 <c>UiLiquidGlass</c> 统一开关，默认关闭。
/// </para>
/// <para>
/// 实现上<b>不会给控件本身加 Effect</b>：WPF 把带 Effect 的元素先渲染进一张中间位图
/// 再贴回来，控件落在非整数像素上时这张位图会被重采样，边缘那圈抗锯齿像素的透明度
/// 会被改写，看起来就是一圈毛边。所以玻璃是画在控件上方的一层独立覆盖层（Adorner），
/// 控件本体一个像素都不碰，文字和轮廓保持原样。
/// </para>
/// <para>
/// 只挂在真正圆形的控件上（圆角半径 ≥ 8）：药丸形导航、圆形图标按钮。
/// 方角按钮（圆角 3）边缘太长，玻璃会在描边上糊成一条脏亮边，所以直接跳过。
/// </para>
/// </summary>
public static class GlassHelper
{
    /// <summary>
    /// 判定「够圆」的圆角半径下限（像素）。
    /// </summary>
    private const double MinCornerRadius = 8d;

    /// <summary>
    /// 当前挂在界面上的所有玻璃覆盖层，用于拖滑块时批量刷新参数。
    /// </summary>
    private static readonly List<GlassAdorner> Live = [];

    /// <summary>
    /// 挂在控件上的玻璃覆盖层。
    /// </summary>
    private static readonly DependencyProperty AdornerProperty =
        DependencyProperty.RegisterAttached("GlassAdorner", typeof(GlassAdorner), typeof(GlassHelper),
            new PropertyMetadata(null));

    // ===== [修改] 新增：渲染帧钩子。只在玻璃开启且至少有一个覆盖层时挂上，关闭时立刻摘掉。 =====
    private static bool _renderHooked;
    private static long _lastTick;

    /// <summary>
    /// 当前是否启用液态玻璃。
    /// </summary>
    public static bool IsEnabled => false;

    static GlassHelper()
    {
        // 主题/明暗切换后重挂玻璃，按钮底色与玻璃参数都重新跟随主题。
        ThemeService.ColorModeChanged += (_, _) => RefreshAll();
        ThemeService.ColorThemeChanged += _ => RefreshAll();
        LiquidGlassCore.Changed += (_, _) => RefreshAll();
    }

    #region 入口

    /// <summary>给普通按钮应用材质（作用在内层 PanFore 上）。可重复调用。</summary>
    public static void Apply(MyButton? button)
    {
        if (button is null) return;
        ApplyCore(button, button.PanFore);
    }

    /// <summary>给药丸形导航按钮应用材质。可重复调用。</summary>
    public static void Apply(MyRadioButton? radio)
    {
        if (radio is null) return;
        ApplyCore(radio, radio);
    }

    /// <summary>给圆形图标按钮应用材质。可重复调用。</summary>
    public static void Apply(MyIconButton? icon)
    {
        if (icon is null) return;
        ApplyCore(icon, icon);
    }

    #endregion

    #region 核心

    private static void ApplyCore(FrameworkElement owner, Border? target)
    {
        if (target is null) return;

        if (!IsEligible(owner, target))
        {
            Remove(target);
            return;
        }

        if (target.GetValue(AdornerProperty) is GlassAdorner existing)
        {
            existing.Sync();
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null)
        {
            // [修改] 在 Loaded 时机太早时 AdornerLayer 可能还没接上，等下一次布局重试，
            //        而不是永久放弃（原实现会直接返回，导致部分按钮始终没有玻璃）。
            EventHandler? retry = null;
            RoutedEventHandler? cancelRetry = null;
            retry = (_, _) =>
            {
                target.LayoutUpdated -= retry;
                ApplyCore(owner, target);
            };
            cancelRetry = (_, _) =>
            {
                target.LayoutUpdated -= retry;
                target.Unloaded -= cancelRetry;
            };
            target.LayoutUpdated += retry;
            target.Unloaded += cancelRetry;
            return;
        }

        var adorner = new GlassAdorner(target, layer);
        layer.Add(adorner);
        target.SetValue(AdornerProperty, adorner);

        // 页面切走时把覆盖层摘掉，免得它留在 AdornerLayer 上变成幽灵
        target.Unloaded += OnTargetUnloaded;
        EnsureRenderHook();
    }

    private static void OnTargetUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border target) return;
        target.Unloaded -= OnTargetUnloaded;
        Remove(target);
    }

    private static void Remove(Border target)
    {
        if (target.GetValue(AdornerProperty) is not GlassAdorner adorner) return;
        target.ClearValue(AdornerProperty);
        target.Unloaded -= OnTargetUnloaded;
        adorner.Detach();
        // 覆盖层可能挂在更近的一层 AdornerLayer 上（比如页面自己的 AdornerDecorator）
        (adorner.Layer ?? AdornerLayer.GetAdornerLayer(target))?.Remove(adorner);
        if (Live.Count == 0) ReleaseRenderHook();
    }

    /// <summary>
    /// 用户在设置页拖滑块时，只刷新已经存在的覆盖层，不重走一遍视觉树。
    /// </summary>
    public static void RefreshParams()
    {
        foreach (var adorner in Live.ToArray()) adorner.ApplyParams();
    }

    /// <summary>
    /// 该控件是否适合加玻璃。
    /// </summary>
    private static bool IsEligible(FrameworkElement owner, Border target)
    {
        if (!IsEnabled) return false;

        // 着色器资源缺失时直接不挂覆盖层，避免留下一层看不见却参与命中的 Effect。
        if (!LiquidGlassEffect.IsAvailable) return false;

        // 实心强调色按钮（如「启动游戏」）上面加玻璃只会变成一圈糊在描边上的亮边。
        if (owner is MyButton button && button.ColorType != MyButton.ColorState.Normal) return false;

        var w = target.ActualWidth;
        var h = target.ActualHeight;
        // 尺寸还没算出来时先放行，等 Sync 时再补
        if (w > 0 && h > 0 && (w < 18d || h < 18d)) return false;

        return EffectiveRadius(target) >= MinCornerRadius;
    }

    /// <summary>
    /// 控件实际的圆角半径。
    /// <para>
    /// PCL 用 <c>CornerRadius="1000"</c> 表示「尽可能圆」，必须按短边的一半收敛，
    /// 否则传给距离场的半径会大得离谱。
    /// </para>
    /// </summary>
    private static double EffectiveRadius(Border target)
    {
        var radius = target.CornerRadius.TopLeft;
        var w = target.ActualWidth;
        var h = target.ActualHeight;
        if (w <= 0 || h <= 0) return radius;
        return Math.Min(radius, Math.Min(w, h) * 0.5d);
    }

    #endregion

    #region 渲染帧

    private static void EnsureRenderHook()
    {
        if (_renderHooked) return;
        CompositionTarget.Rendering += OnRendering;
        _renderHooked = true;
    }

    private static void ReleaseRenderHook()
    {
        if (!_renderHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderHooked = false;
    }

    /// <summary>
    /// 每帧最多 30 次地同步透明度与裁剪。
    /// <para>
    /// [修改] 这是本次修复的核心之一：AdornerLayer 上的覆盖层不是控件的视觉子级，
    /// 不会继承页面切换动画的 Opacity，也不会被 ScrollViewer 裁剪。原实现因此会出现
    /// “页面已经淡出、玻璃还亮着”和“滚动后玻璃浮在滚动条/页头上”的显示 bug。
    /// </para>
    /// </summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        if (now - _lastTick < 32) return;
        _lastTick = now;

        foreach (var adorner in Live.ToArray()) adorner.Tick();
        if (Live.Count == 0) ReleaseRenderHook();
    }

    #endregion

    #region 覆盖层

    /// <summary>
    /// 画在控件上方的玻璃覆盖层。自身没有任何可见内容，颜色全部由着色器按距离场现算。
    /// </summary>
    private sealed class GlassAdorner : Adorner
    {
        private readonly Border _target;
        private readonly Border _overlay;
        private readonly LiquidGlassEffect _effect;
        private readonly ScrollViewer? _scroll;

        private Size _lastSize;
        private double _lastRadius = -1d;
        private double _lastOpacity = -1d;
        private bool _hidden;
        private Rect _lastClip = Rect.Empty;
        private bool _clipped;
        private Matrix _lastOverlayTransform = Matrix.Identity;

        public AdornerLayer? Layer { get; }

        public GlassAdorner(Border target, AdornerLayer layer) : base(target)
        {
            _target = target;
            Layer = layer;
            _scroll = FindAncestor<ScrollViewer>(target);
            IsHitTestVisible = false;

            _effect = new LiquidGlassEffect
            {
                // [修改] 给玻璃一层极淡的白色“体积感”；alpha 很低，不会洗掉主题色。
                Tint = Color.FromArgb(10, 255, 255, 255)
            };

            _overlay = new Border
            {
                // 极低透明度而不是全透明：确保 WPF 一定会把这个元素渲染出来，
                // 着色器才有画布可画（输出会整体覆盖掉这块底色）。
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                IsHitTestVisible = false,
                Effect = _effect,
                SnapsToDevicePixels = false
            };

            AddVisualChild(_overlay);
            AddLogicalChild(_overlay);

            // [修改] 尺寸变化后重新同步距离场，避免窗口缩放后玻璃还是旧尺寸。
            target.SizeChanged += OnTargetSizeChanged;
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;

            Live.Add(this);
            Sync();
        }

        /// <summary>从 AdornerLayer 上摘掉自己。</summary>
        public void Detach()
        {
            Live.Remove(this);
            _target.SizeChanged -= OnTargetSizeChanged;
            if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;

            // 主动断开 Effect，让 WPF 尽早释放中间位图。
            try
            {
                _overlay.Effect = null;
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => Sync();

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 滚动时布局会变，强制让 AdornerLayer 重新计算覆盖层位置。
            try
            {
                Layer?.Update(_target);
            }
            catch (Exception)
            {
                // 覆盖层可能刚被移除，忽略。
            }

            UpdatePresentation();
        }

        /// <summary>把控件尺寸与圆角同步给着色器。</summary>
        public void Sync()
        {
            var w = _target.ActualWidth;
            var h = _target.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                Hide();
                return;
            }

            _lastSize = new Size(w, h);
            _lastRadius = EffectiveRadius(_target);
            _effect.BorderSize = new Point(w, h);
            _effect.CornerRadius = _lastRadius;
            ApplyParams();
            UpdatePresentation();

            try
            {
                // 尺寸变化后让 AdornerLayer 立刻按新尺寸重新排一次。
                Layer?.Update(_target);
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        /// <summary>
        /// 把用户在设置页调好的参数写进着色器。
        /// <para>
        /// 「折射强度」同时决定聚光强度和影响带宽：调大了不光边缘更亮，
        /// 那圈亮光也会往控件内部铺得更深，看起来更像一块有厚度的玻璃。
        /// </para>
        /// </summary>
        public void ApplyParams()
        {
            var refraction = Math.Clamp(Config.Preference.Theme.LiquidGlassRefraction, 0, 100) / 100d;
            var highlight = Math.Clamp(Config.Preference.Theme.LiquidGlassHighlight, 0, 100) / 100d;
            var dispersion = Math.Clamp(Config.Preference.Theme.LiquidGlassDispersion, 0, 100) / 100d;

            _effect.RefractionPower = refraction;
            _effect.Highlight = highlight;
            _effect.Dispersion = dispersion;

            var min = Math.Min(_target.ActualWidth, _target.ActualHeight);
            if (min <= 0) return;
            _effect.EffectWidth = Math.Clamp(min * (0.10d + 0.14d * refraction), 3d, 12d);
        }

        /// <summary>
        /// 同步覆盖层的透明度与裁剪区域。每帧调用，但只有数值真的变化时才写 WPF 属性。
        /// </summary>
        public void Tick()
        {
            if (!_target.IsVisible)
            {
                Hide();
                return;
            }

            var w = _target.ActualWidth;
            var h = _target.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                Hide();
                return;
            }

            if (Math.Abs(w - _lastSize.Width) > 0.01d || Math.Abs(h - _lastSize.Height) > 0.01d ||
                Math.Abs(EffectiveRadius(_target) - _lastRadius) > 0.05d)
            {
                Sync();
                return;
            }

            UpdatePresentation();
        }

        private void UpdatePresentation()
        {
            if (!_target.IsVisible || Layer is null || !Layer.IsVisible)
            {
                Hide();
                return;
            }

            // 1) 透明度：把从控件到 AdornerLayer 之间所有祖先的 Opacity 乘起来。
            //    页面切换动画会改 MyCard 的 Opacity，但覆盖层不是它的视觉子级，
            //    原实现不会跟着淡出，这就是“玻璃幽灵”的来源。
            var opacity = ComputeOpacity();
            if (opacity <= 0.001d)
            {
                Hide();
                return;
            }

            // 2) 裁剪：AdornerLayer 在 ScrollViewer 外面，必须手动裁到滚动视口，
            //    否则滚出屏幕的按钮玻璃会浮在页头/滚动条上。
            if (!TryComputeClip(out var clip, out var needClip))
            {
                Hide();
                return;
            }

            if (_hidden) _overlay.Visibility = Visibility.Visible;
            _hidden = false;

            // 3) 变换：AdornerLayer 只跟随布局位置，不会自动跟随控件的 RenderTransform
            //    （按钮按压缩放、页面切换位移等）。这里把“目标实际变换”与“覆盖层当前变换”
            //    的差值补到覆盖层上，保证玻璃始终贴在控件上。
            UpdateRenderTransform();

            if (Math.Abs(_lastOpacity - opacity) > 0.001d)
            {
                _overlay.Opacity = opacity;
                _lastOpacity = opacity;
            }

            if (!needClip)
            {
                if (_clipped)
                {
                    _overlay.Clip = null;
                    _clipped = false;
                    _lastClip = Rect.Empty;
                }

                return;
            }

            if (!_clipped || !RectsClose(_lastClip, clip))
            {
                var geometry = new RectangleGeometry(clip);
                geometry.Freeze();
                _overlay.Clip = geometry;
                _lastClip = clip;
                _clipped = true;
            }
        }

        private void Hide()
        {
            if (_hidden) return;
            _hidden = true;
            _overlay.Visibility = Visibility.Collapsed;
        }

        private double ComputeOpacity()
        {
            // 覆盖层挂在 AdornerLayer 上，已经是这个共同祖先的视觉子级：
            // 共同祖先及更上层的 Opacity 会被 WPF 自动应用一次，这里不能再乘一遍，
            // 否则窗口透明度会被平方（例如 0.6 变成 0.36，玻璃明显偏淡）。
            var common = FindCommonAncestor(Layer, _target);
            var opacity = 1d;
            DependencyObject? node = _target;

            while (node is UIElement element)
            {
                if (ReferenceEquals(element, common)) break;
                opacity *= element.Opacity;
                node = VisualTreeHelper.GetParent(element);
            }

            return Math.Clamp(opacity, 0d, 1d);
        }

        /// <summary>
        /// 计算覆盖层在自身坐标系里的裁剪矩形。
        /// <para>返回 false 表示完全不可见。</para>
        /// </summary>
        private bool TryComputeClip(out Rect clip, out bool needClip)
        {
            clip = Rect.Empty;
            needClip = false;
            if (Layer is null) return false;

            try
            {
                // AdornerLayer 与控件不是父子关系：AdornerLayer 是 AdornerDecorator 的
                // 兄弟节点，控件是 Decorator.Child。必须用共同祖先做坐标换算，
                // 直接 TransformToAncestor(Layer) 会抛“指定的 Visual 不是此 Visual 的上级”。
                var common = FindCommonAncestor(Layer, _target);
                if (common is null) return false;

                var targetToCommon = _target.TransformToAncestor((Visual)common);
                var layerToCommon = Layer.TransformToAncestor((Visual)common);
                var targetSize = _target.RenderSize;
                if (targetSize.Width <= 0 || targetSize.Height <= 0) return false;

                var visible = targetToCommon.TransformBounds(new Rect(targetSize));

                // 页面本身的范围
                var layerBounds = layerToCommon.TransformBounds(
                    new Rect(0d, 0d, Layer.ActualWidth, Layer.ActualHeight));
                visible = Rect.Intersect(visible, layerBounds);

                // 滚动视口的范围
                if (_scroll is not null && _scroll.ViewportWidth > 0 && _scroll.ViewportHeight > 0)
                {
                    try
                    {
                        var scrollToCommon = _scroll.TransformToAncestor((Visual)common);
                        var viewport = scrollToCommon.TransformBounds(
                            new Rect(0d, 0d, _scroll.ViewportWidth, _scroll.ViewportHeight));
                        visible = Rect.Intersect(visible, viewport);
                    }
                    catch (Exception)
                    {
                        // 某些控件位于嵌套的 AdornerDecorator（如 FontSelector 下拉层）里，
                        // ScrollViewer 不在同一个 AdornerDecorator 内；此时只按页面范围裁剪即可。
                    }
                }

                if (visible.IsEmpty || visible.Width < 0.5d || visible.Height < 0.5d) return false;

                var inverse = targetToCommon.Inverse;
                var local = inverse.TransformBounds(visible);
                var full = new Rect(0d, 0d, targetSize.Width, targetSize.Height);

                const double epsilon = 0.25d;
                needClip = local.Left > epsilon || local.Top > epsilon ||
                           local.Right < full.Right - epsilon || local.Bottom < full.Bottom - epsilon;

                clip = needClip ? local : full;
                return true;
            }
            catch (Exception)
            {
                // 目标已经脱离可视树或变换不可逆。
                return false;
            }
        }

        /// <summary>
        /// 把控件实际渲染变换与覆盖层当前变换之间的差值补到覆盖层上。
        /// <para>
        /// WPF 的 <see cref="AdornerLayer"/> 只按布局位置摆放 Adorner，不会跟随控件的
        /// <see cref="UIElement.RenderTransform"/>；页面切换的位移动画、按钮按压缩放
        /// 都会让玻璃与控件错位。这里每帧计算残差矩阵并写到覆盖层的 RenderTransform。
        /// </para>
        /// </summary>
        private void UpdateRenderTransform()
        {
            if (Layer is null) return;

            try
            {
                var common = FindCommonAncestor(this, _target);
                if (common is null) return;

                var adornerToCommon = ToMatrix(this.TransformToAncestor((Visual)common));
                var targetToCommon = ToMatrix(_target.TransformToAncestor((Visual)common));

                if (!adornerToCommon.HasInverse) return;
                adornerToCommon.Invert();
                adornerToCommon.Append(targetToCommon);

                if (adornerToCommon.Equals(_lastOverlayTransform)) return;
                _lastOverlayTransform = adornerToCommon;
                _overlay.RenderTransform = adornerToCommon.IsIdentity
                    ? null
                    : new MatrixTransform(adornerToCommon);
            }
            catch (Exception)
            {
                // 覆盖层还没接入可视树时忽略，下一帧再试。
            }
        }

        /// <summary>把 2D 仿射变换转成矩阵。GeneralTransform 本身不暴露 Matrix。</summary>
        private static Matrix ToMatrix(GeneralTransform transform)
        {
            var origin = transform.Transform(new Point(0d, 0d));
            var xAxis = transform.Transform(new Point(1d, 0d));
            var yAxis = transform.Transform(new Point(0d, 1d));
            return new Matrix(
                xAxis.X - origin.X, xAxis.Y - origin.Y,
                yAxis.X - origin.X, yAxis.Y - origin.Y,
                origin.X, origin.Y);
        }

        private static DependencyObject? FindCommonAncestor(DependencyObject? first, DependencyObject? second)
        {
            if (first is null || second is null) return null;

            var ancestors = new HashSet<DependencyObject>();
            for (var node = first; node is not null; node = VisualTreeHelper.GetParent(node))
                ancestors.Add(node);

            for (var node = second; node is not null; node = VisualTreeHelper.GetParent(node))
                if (ancestors.Contains(node))
                    return node;

            return null;
        }

        private static bool RectsClose(Rect left, Rect right)
        {
            return Math.Abs(left.X - right.X) < 0.25d && Math.Abs(left.Y - right.Y) < 0.25d &&
                   Math.Abs(left.Width - right.Width) < 0.25d && Math.Abs(left.Height - right.Height) < 0.25d;
        }

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node is not null)
            {
                if (node is T typed) return typed;
                node = VisualTreeHelper.GetParent(node);
            }

            return null;
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _overlay;

        protected override Size MeasureOverride(Size constraint)
        {
            _overlay.Measure(constraint);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _overlay.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }

    #endregion

    #region 批量刷新

    /// <summary>
    /// 配置切换后，刷新所有已打开窗口里的控件。
    /// </summary>
    public static void RefreshAll()
    {
        if (Application.Current is null) return;

        foreach (Window window in Application.Current.Windows)
        {
            try
            {
                Walk(window);
            }
            catch
            {
                // 单个窗口出错不影响其它窗口
            }
        }

        // [修改] 关闭玻璃时把帧钩子一并摘掉。
        if (Live.Count == 0) ReleaseRenderHook();
    }

    private static void Walk(DependencyObject node)
    {
        switch (node)
        {
            case MyButton button:
                Apply(button);
                return;
            case MyRadioButton radio:
                Apply(radio);
                return;
            case MyIconButton icon:
                Apply(icon);
                return;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
    }

    #endregion
}
