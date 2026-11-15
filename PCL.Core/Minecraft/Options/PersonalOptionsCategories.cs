using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Minecraft.Options;

/// <summary>
/// 个人设置类别的成员判定：哪些 <c>options.txt</c> 键属于按键、鼠标、视角、辅助功能或声音。
/// 键集合照抄参考实现，音频按 <c>soundCategory_</c> 前缀判定。
/// </summary>
public static class PersonalOptionsCategories
{
    /// <summary>声音类别键的统一前缀。</summary>
    public const string AudioPrefix = "soundCategory_";

    /// <summary>
    /// 原版固有按键绑定。这些键即使目标 <c>options.txt</c> 中不存在也会被写入，
    /// 否则用新版本档案初始化旧版本时会出现按键缺失。
    /// </summary>
    public static IReadOnlySet<string> BasicKeys { get; } = _CreateBasicKeys();

    /// <summary>鼠标与其他输入相关设置。</summary>
    public static IReadOnlySet<string> MouseKeys { get; } = new HashSet<string>(
    [
        "mouseSensitivity", "invertYMouse", "mouseWheelSensitivity", "discrete_mouse_scroll",
        "rawMouseInput", "touchscreen", "toggleSprint", "toggleCrouch"
    ], StringComparer.Ordinal);

    /// <summary>视角相关设置。</summary>
    public static IReadOnlySet<string> ViewKeys { get; } = new HashSet<string>(
    [
        "fov", "fovEffectScale", "screenEffectScale", "bobView"
    ], StringComparer.Ordinal);

    /// <summary>辅助功能相关设置。</summary>
    public static IReadOnlySet<string> AccessibilityKeys { get; } = new HashSet<string>(
    [
        "showSubtitles", "autoJump", "highContrast", "darknessEffectScale",
        "hideLightningFlashes", "notificationDisplayTime"
    ], StringComparer.Ordinal);

    /// <summary>判断是否属于原版固有按键绑定（会先把 <c>swapHands</c> 归一为 <c>swapOffhand</c>）。</summary>
    /// <param name="key">待判断的键。</param>
    /// <returns>属于原版按键绑定则返回 <see langword="true"/>。</returns>
    public static bool IsVanillaBinding(string key) =>
        BasicKeys.Contains(PersonalOptionsRules.CanonicalKey(key));

    /// <summary>判断是否为鼠标设置。</summary>
    /// <param name="key">待判断的键。</param>
    /// <returns>属于鼠标设置则返回 <see langword="true"/>。</returns>
    public static bool IsMouseKey(string key) => MouseKeys.Contains(key);

    /// <summary>判断是否为视角设置。</summary>
    /// <param name="key">待判断的键。</param>
    /// <returns>属于视角设置则返回 <see langword="true"/>。</returns>
    public static bool IsViewKey(string key) => ViewKeys.Contains(key);

    /// <summary>判断是否为辅助功能设置。</summary>
    /// <param name="key">待判断的键。</param>
    /// <returns>属于辅助功能则返回 <see langword="true"/>。</returns>
    public static bool IsAccessibilityKey(string key) => AccessibilityKeys.Contains(key);

    /// <summary>判断是否为声音类别音量。</summary>
    /// <param name="key">待判断的键。</param>
    /// <returns>以 <see cref="AudioPrefix"/> 开头则返回 <see langword="true"/>。</returns>
    public static bool IsAudioKey(string key) => key.StartsWith(AudioPrefix, StringComparison.Ordinal);

    /// <summary>返回指定类别包含的全部固定键（声音类别没有固定键集合，返回空）。</summary>
    /// <param name="category">类别。</param>
    /// <returns>该类别包含的键。</returns>
    public static IReadOnlySet<string> KeysOf(PersonalOptionsCategory category) => category switch
    {
        PersonalOptionsCategory.Keys => BasicKeys,
        PersonalOptionsCategory.Mouse => MouseKeys,
        PersonalOptionsCategory.View => ViewKeys,
        PersonalOptionsCategory.Accessibility => AccessibilityKeys,
        _ => new HashSet<string>(StringComparer.Ordinal)
    };

    /// <summary>
    /// 判断指定键是否落入给定类别集合。判定顺序与参考实现一致：
    /// 按键 → 鼠标 → 视角 → 辅助功能 → 声音。
    /// </summary>
    /// <param name="key">待判断的键（内部会归一 <c>swapHands</c>）。</param>
    /// <param name="categories">已勾选的类别。</param>
    /// <returns>命中任一已勾选类别则返回 <see langword="true"/>。</returns>
    public static bool IsSelected(string key, IReadOnlySet<PersonalOptionsCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (categories.Count == 0) return false;

        string canonical = PersonalOptionsRules.CanonicalKey(key);
        if (canonical.StartsWith("key_", StringComparison.Ordinal))
            return categories.Contains(PersonalOptionsCategory.Keys) && BasicKeys.Contains(canonical);
        if (MouseKeys.Contains(canonical)) return categories.Contains(PersonalOptionsCategory.Mouse);
        if (ViewKeys.Contains(canonical)) return categories.Contains(PersonalOptionsCategory.View);
        if (AccessibilityKeys.Contains(canonical)) return categories.Contains(PersonalOptionsCategory.Accessibility);
        return IsAudioKey(canonical) && categories.Contains(PersonalOptionsCategory.Audio);
    }

    /// <summary>
    /// 判断指定键是否落入给定类别集合的便捷重载，内部会建立哈希集合后委派。
    /// </summary>
    /// <param name="key">待判断的键。</param>
    /// <param name="categories">已勾选的类别。</param>
    /// <returns>命中任一已勾选类别则返回 <see langword="true"/>。</returns>
    public static bool IsSelected(string key, IEnumerable<PersonalOptionsCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return IsSelected(key, ToSet(categories));
    }

    /// <summary>把任意类别序列转换为用于快速判定的集合。</summary>
    /// <param name="categories">类别序列。</param>
    /// <returns>去重后的集合；入参本身就是集合时直接复用。</returns>
    public static IReadOnlySet<PersonalOptionsCategory> ToSet(IEnumerable<PersonalOptionsCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return categories as IReadOnlySet<PersonalOptionsCategory>
               ?? new HashSet<PersonalOptionsCategory>(categories);
    }

    /// <summary>返回指定键所属的类别；不属于任何类别时返回 <see langword="null"/>。</summary>
    /// <param name="key">待判断的键。</param>
    /// <returns>所属类别或 <see langword="null"/>。</returns>
    public static PersonalOptionsCategory? CategoryOf(string key)
    {
        string canonical = PersonalOptionsRules.CanonicalKey(key);
        if (canonical.StartsWith("key_", StringComparison.Ordinal))
            return BasicKeys.Contains(canonical) ? PersonalOptionsCategory.Keys : null;
        if (MouseKeys.Contains(canonical)) return PersonalOptionsCategory.Mouse;
        if (ViewKeys.Contains(canonical)) return PersonalOptionsCategory.View;
        if (AccessibilityKeys.Contains(canonical)) return PersonalOptionsCategory.Accessibility;
        if (IsAudioKey(canonical)) return PersonalOptionsCategory.Audio;
        return null;
    }

    private static HashSet<string> _CreateBasicKeys()
    {
        // 参考实现使用 "key_key." 前缀：options.txt 里按键绑定的键名是 key_key.attack 这种形式。
        string[] names =
        [
            "attack", "use", "pickItem", "forward", "back", "left", "right", "jump", "sneak",
            "sprint", "drop", "inventory", "chat", "playerlist", "command", "screenshot",
            "togglePerspective", "smoothCamera", "fullscreen", "spectatorOutlines", "advancements",
            "swapOffhand", "saveToolbarActivator", "loadToolbarActivator"
        ];

        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string name in names) keys.Add("key_key." + name);
        for (int index = 1; index <= 9; index++) keys.Add("key_key.hotbar." + index);
        return keys;
    }
}
