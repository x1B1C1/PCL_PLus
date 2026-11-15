// 与 Modules\Base\ModAnimation.cs 同一个分部类：
// 这里是从 Controls 里搬出来的 AniDispose 重载，按目录命名空间归位到 PCL.Modules.Base。

using System.Windows;
using System.Windows.Controls;

namespace PCL.Modules.Base;

public static partial class ModAnimation
{
    public static void AniDispose(MyCard control, bool removeFromChildren, ParameterizedThreadStart callBack = null)
    {
        if (control.IsHitTestVisible)
        {
            control.IsHitTestVisible = false;
            AniStart(new[]
            {
                AaScaleTransform(control, -0.08d, 200, ease: new AniEaseInFluent()),
                AaOpacity(control, -1, 200, ease: new AniEaseOutFluent()),
                AaHeight(control, -control.ActualHeight, 150, 100, new AniEaseOutFluent()),
                AaCode(() =>
                {
                    if (removeFromChildren)
                    {
                        if (control.Parent is null)
                            return;
                        ((Panel)control.Parent).Children.Remove(control);
                    }
                    else
                    {
                        control.Visibility = Visibility.Collapsed;
                    }

                    if (callBack is not null)
                        callBack(control);
                }, after: true)
            }, "MyCard Dispose " + control.uuid);
        }
        else
        {
            if (removeFromChildren)
            {
                if (control.Parent is null)
                    return;
                ((Panel)control.Parent).Children.Remove(control);
            }
            else
            {
                control.Visibility = Visibility.Collapsed;
            }

            if (callBack is not null)
                callBack(control);
        }
    }
}

public static partial class ModAnimation
{
    public static void AniDispose(MyHint control, bool removeFromChildren, ParameterizedThreadStart callBack = null)
    {
        if (!control.IsHitTestVisible)
            return;
        control.IsHitTestVisible = false;
        AniStart(new[]
        {
            AaScaleTransform(control, -0.08d, 200, ease: new AniEaseInFluent()),
            AaOpacity(control, -1, 200, ease: new AniEaseOutFluent()),
            AaHeight(control, -control.ActualHeight, 150, 100, new AniEaseOutFluent()),
            AaCode(() =>
            {
                if (removeFromChildren)
                    ((Panel)control.Parent).Children.Remove(control);
                else
                    control.Visibility = Visibility.Collapsed;
                if (callBack is not null)
                    callBack(control);
            }, after: true)
        }, "MyCard Dispose " + control.Uuid);
    }
}

public static partial class ModAnimation
{
    public static void AniDispose(MyIconButton control, bool removeFromChildren,
        ParameterizedThreadStart callBack = null)
    {
        if (!control.IsHitTestVisible)
            return;
        control.IsHitTestVisible = false;
        AniStart(new[]
        {
            AaScaleTransform(control, -1.5d, 200, ease: new AniEaseInFluent()),
            AaCode(() =>
            {
                if (removeFromChildren)
                    ((Panel)control.Parent).Children.Remove(control);
                else
                    control.Visibility = Visibility.Collapsed;
                if (callBack is not null)
                    callBack(control);
            }, after: true)
        }, "MyIconButton Dispose " + control.Uuid);
    }
}
