using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PCL.Core.Minecraft.Options;

/// <summary>
/// 个人设置的纯转换规则：值归一、逐行保真合并与版本差异转换。
/// 本类不读写文件、不访问全局状态，所有输入均由调用者提供，便于单独测试。
/// </summary>
public static class PersonalOptionsRules
{
    /// <summary>1.13 起按键绑定改用字符串形式的 <c>key.keyboard.*</c>，键名也从 <c>swapHands</c> 改为 <c>swapOffhand</c>。</summary>
    public const int ModernVanillaMajor = 13;

    /// <summary>1.19 起 <c>fov</c> 以角度制写入，而不是 -1..1 的归一值。</summary>
    public const int FovDegreesVanillaMajor = 19;

    /// <summary>单个设置值的最大长度，超过即视为异常数据。</summary>
    public const int MaxValueLength = 4096;

    /// <summary>旧版数字键码到 <c>key.keyboard.*</c> 名称的映射。</summary>
    private static readonly Dictionary<int, string> _LegacyKeys = CreateLegacyKeys();

    /// <summary>上述映射的反向表，用于把字符串绑定转回旧版数字码。</summary>
    private static readonly Dictionary<string, int> _LegacyNames = _CreateLegacyNames(_LegacyKeys);

    /// <summary>
    /// 归一键名：1.13 之前使用 <c>key_key.swapHands</c>，统一按 <c>key_key.swapOffhand</c> 处理。
    /// </summary>
    /// <param name="key">原始键名。</param>
    /// <returns>归一后的键名。</returns>
    public static string CanonicalKey(string key) =>
        key == "key_key.swapHands" ? "key_key.swapOffhand" : key;

    /// <summary>
    /// 判断是否属于原版固有按键绑定，这类键允许写入目标文件中不存在的键。
    /// </summary>
    /// <param name="key">原始键名。</param>
    /// <returns>属于原版按键绑定则返回 <see langword="true"/>。</returns>
    public static bool VanillaBinding(string key) =>
        PersonalOptionsCategories.BasicKeys.Contains(CanonicalKey(key));

    /// <summary>
    /// 把 <c>options.txt</c> 文本解析为键值字典。只按第一个冒号切分，
    /// 因此值里可以继续包含冒号（例如带修饰键的绑定）；无法解析的行会被忽略。
    /// </summary>
    /// <param name="text">已解码的文本。</param>
    /// <returns>键已归一的字典，顺序与原文本一致。</returns>
    public static Dictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0) continue;
            string key = CanonicalKey(line[..separator]);
            values[key] = line[(separator + 1)..];
        }
        return values;
    }

    /// <summary>
    /// 以严格 UTF-8 解码字节内容，并去掉可能存在的 BOM。
    /// </summary>
    /// <param name="bytes">原始字节。</param>
    /// <returns>解码后的文本。</returns>
    /// <exception cref="ArgumentException">字节内容不是合法的 UTF-8 时抛出。</exception>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }

    /// <summary>
    /// 把一个原始值归一为档案中保存的形式：
    /// 过长或含换行的值直接拒绝；<c>fov</c> 统一为角度制；按键绑定统一为
    /// <c>key.keyboard.*</c> / <c>key.mouse.*</c> 字符串，其余设置原样保留。
    /// </summary>
    /// <param name="key">键名。</param>
    /// <param name="raw">原始值。</param>
    /// <returns>归一后的值；无法归一时返回 <see langword="null"/>。</returns>
    public static string? CanonicalValue(string key, string raw)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.Length > MaxValueLength || raw.IndexOfAny(['\0', '\n', '\r']) >= 0) return null;

        if (key == "fov")
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double fovNumber) ||
                double.IsNaN(fovNumber) || double.IsInfinity(fovNumber)) return null;
            // -1..1 是归一值，30..110 是角度制，两者都接受并统一成角度制。
            if (fovNumber >= -1 && fovNumber <= 1) fovNumber = 70 + fovNumber * 40;
            return fovNumber >= 30 && fovNumber <= 110
                ? fovNumber.ToString("0.########", CultureInfo.InvariantCulture)
                : null;
        }

        if (!key.StartsWith("key_", StringComparison.Ordinal)) return raw;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return raw.StartsWith("key.keyboard.", StringComparison.Ordinal) ||
                   raw.StartsWith("key.mouse.", StringComparison.Ordinal)
                ? raw
                : null;

        // -100..-1 是旧版鼠标键码：-100 左键、-99 右键、-98 中键、-97 起为其余侧键。
        if (number is <= -1 and >= -100)
        {
            int button = number + 100;
            string name = button switch
            {
                0 => "left",
                1 => "right",
                2 => "middle",
                _ => (button + 1).ToString(CultureInfo.InvariantCulture)
            };
            return "key.mouse." + name;
        }

        return _LegacyKeys.TryGetValue(number, out string? legacyName) ? "key.keyboard." + legacyName : null;
    }

    /// <summary>
    /// 目标版本应写入的键名：1.13 之前把 <c>swapOffhand</c> 写回 <c>swapHands</c>。
    /// </summary>
    /// <param name="key">键名。</param>
    /// <param name="isModern">目标版本是否使用现代键名。</param>
    /// <param name="vanillaMajor">原版主版本号，0 表示未知。</param>
    /// <returns>目标版本使用的键名。</returns>
    public static string KeyForVersion(string key, bool isModern, int vanillaMajor = 0)
    {
        ArgumentNullException.ThrowIfNull(key);

        string canonical = CanonicalKey(key);
        if (canonical == "key_key.swapOffhand" && !_UsesModernNames(isModern, vanillaMajor))
            return "key_key.swapHands";
        return canonical;
    }

    /// <summary>
    /// 把归一后的值转换为目标版本可读的形式：
    /// 现代版本写 <c>key.keyboard.*</c> / <c>key.mouse.*</c> 字符串，
    /// 旧版本写数字键码；非按键设置原样返回。
    /// </summary>
    /// <param name="key">键名。</param>
    /// <param name="value">原始值。</param>
    /// <param name="isModern">目标版本是否使用现代绑定形式。</param>
    /// <param name="vanillaMajor">原版主版本号；不小于 13 时同样视为现代版本。</param>
    /// <returns>适合目标版本的值；无法转换时返回 <see langword="null"/>。</returns>
    public static string? ForVersion(string key, string value, bool isModern, int vanillaMajor = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        string canonicalKey = CanonicalKey(key);
        string? converted = CanonicalValue(canonicalKey, value);
        if (converted is null) return null;
        if (!canonicalKey.StartsWith("key_", StringComparison.Ordinal)) return converted;
        if (_UsesModernNames(isModern, vanillaMajor)) return converted;

        if (converted.StartsWith("key.mouse.", StringComparison.Ordinal))
        {
            string button = converted[10..];
            return button switch
            {
                "left" => "-100",
                "right" => "-99",
                "middle" => "-98",
                _ => int.TryParse(button, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mouseNumber) &&
                     mouseNumber is >= 4 and <= 100
                    ? (mouseNumber - 101).ToString(CultureInfo.InvariantCulture)
                    : null
            };
        }

        return _LegacyNames.TryGetValue(converted, out int legacyCode)
            ? legacyCode.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// 逐行保真地把档案值合并进 <c>options.txt</c>：保留原有行序、换行风格与末尾换行，
    /// 不注入目标文件中不存在、且不属于原版固有集合的键。
    /// </summary>
    /// <remarks>
    /// 允许注入的例外键为 <see cref="PersonalOptionsCategories.BasicKeys"/> 以及
    /// <c>mouseSensitivity</c>、<c>invertYMouse</c>、<c>fov</c>、<c>showSubtitles</c>，
    /// 与参考实现保持一致。
    /// </remarks>
    /// <param name="original">原始 <c>options.txt</c> 文本。</param>
    /// <param name="values">档案中的键值，键可以未归一。</param>
    /// <param name="isModern">目标版本是否使用现代绑定形式。</param>
    /// <param name="keys">本次允许同步的键集合（已按类别过滤）。</param>
    /// <param name="selected">按键查询是否仍在勾选状态，调用时即时求值。</param>
    /// <param name="fovDegrees">目标版本是否使用角度制 <c>fov</c>；目标已有 <c>fov</c> 时以已有值为准。</param>
    /// <returns>合并后的完整文本。</returns>
    public static string MergeText(
        string original,
        IReadOnlyDictionary<string, string> values,
        bool isModern,
        IReadOnlySet<string> keys,
        Func<string, bool> selected,
        bool fovDegrees = true)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(selected);

        string newline = original.Contains("\r\n") ? "\r\n" : "\n";
        bool finalNewline = original.EndsWith('\n');
        List<string> lines = original.Replace("\r\n", "\n").Split('\n').ToList();
        Dictionary<string, string> present = Parse(original);
        HashSet<string> applied = new(StringComparer.Ordinal);
        Dictionary<string, string> replacements = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> pair in values)
        {
            string key = CanonicalKey(pair.Key);
            if (!keys.Contains(key) || !selected(key)) continue;
            // 版本专用设置只在目标已存在该键时同步，不向 Mod 或未知配置注入新行。
            if (!present.ContainsKey(key) && !PersonalOptionsCategories.BasicKeys.Contains(key) &&
                key is not ("mouseSensitivity" or "invertYMouse" or "fov" or "showSubtitles")) continue;

            string? converted = ForVersion(key, pair.Value, isModern);
            if (key == "fov" && converted is not null)
            {
                bool usesDegrees = fovDegrees;
                // 目标文件已有的 fov 写法优先：绝对值大于 1 视为角度制。
                if (present.TryGetValue(key, out string? existing) && _TryParseFov(existing, out double numeric))
                    usesDegrees = Math.Abs(numeric) > 1;

                double degrees = double.Parse(converted, CultureInfo.InvariantCulture);
                converted = usesDegrees
                    ? Math.Round(degrees).ToString(CultureInfo.InvariantCulture)
                    : ((degrees - 70) / 40).ToString("0.########", CultureInfo.InvariantCulture);
            }

            if (converted is not null) replacements[key] = converted;
        }

        for (int index = 0; index < lines.Count; index++)
        {
            int separator = lines[index].IndexOf(':');
            if (separator <= 0) continue;

            string key = CanonicalKey(lines[index][..separator]);
            if (!replacements.TryGetValue(key, out string? value)) continue;

            lines[index] = lines[index][..(separator + 1)] + value;
            applied.Add(key);
        }

        foreach (KeyValuePair<string, string> pair in replacements.Where(item => !applied.Contains(item.Key)))
        {
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(KeyForVersion(pair.Key, isModern) + ":" + pair.Value);
        }

        string result = string.Join(newline, lines);
        return result + (finalNewline && !result.EndsWith(newline, StringComparison.Ordinal) ? newline : "");
    }

    /// <summary>
    /// 归一单个绑定值（可带 <c>:修饰键</c> 后缀），用于界面读取与写回档案。
    /// </summary>
    /// <param name="value">原始绑定值，例如 <c>-100:LEFT_SHIFT</c>。</param>
    /// <returns>归一后的绑定；无法归一时返回 <see langword="null"/>。</returns>
    public static string? NormalizeBinding(string? value)
    {
        if (value is null) return null;

        string[] parts = value.Split(':');
        string? main = CanonicalValue("key_key.forward", parts[0]);
        if (main is null) return null;
        return main + (parts.Length > 1 ? ":" + string.Join(":", parts.Skip(1)).ToUpperInvariant() : "");
    }

    /// <summary>
    /// 把归一后的绑定转换为目标版本可读的形式，修饰键后缀保持原样。
    /// </summary>
    /// <param name="value">归一后的绑定值。</param>
    /// <param name="isModern">目标版本是否使用现代绑定形式。</param>
    /// <returns>目标版本可读的绑定；无法转换时返回 <see langword="null"/>。</returns>
    public static string? BindingForVersion(string? value, bool isModern)
    {
        if (value is null) return null;

        string[] parts = value.Split(':');
        string? main = ForVersion("key_key.forward", parts[0], isModern);
        return main is null ? null : main + (parts.Length > 1 ? ":" + string.Join(":", parts.Skip(1)) : "");
    }

    /// <summary>
    /// 创建旧版数字键码到 <c>key.keyboard.*</c> 名称的映射表。
    /// </summary>
    /// <returns>键码到名称的字典。</returns>
    public static Dictionary<int, string> CreateLegacyKeys()
    {
        Dictionary<int, string> values = new()
        {
            { 0, "unknown" }, { 1, "escape" }, { 14, "backspace" }, { 15, "tab" }, { 28, "enter" },
            { 29, "left.control" }, { 42, "left.shift" }, { 54, "right.shift" }, { 56, "left.alt" },
            { 57, "space" }, { 58, "caps.lock" }, { 69, "num.lock" }, { 70, "scroll.lock" },
            { 87, "f11" }, { 88, "f12" }, { 156, "keypad.enter" }, { 157, "right.control" },
            { 181, "keypad.divide" }, { 183, "print.screen" }, { 184, "right.alt" }, { 197, "pause" },
            { 199, "home" }, { 200, "up" }, { 201, "page.up" }, { 203, "left" }, { 205, "right" },
            { 207, "end" }, { 208, "down" }, { 209, "page.down" }, { 210, "insert" }, { 211, "delete" },
            { 219, "left.win" }, { 220, "right.win" }, { 221, "menu" }, { 12, "minus" }, { 13, "equal" },
            { 26, "left.bracket" }, { 27, "right.bracket" }, { 43, "backslash" }, { 39, "semicolon" },
            { 40, "apostrophe" }, { 41, "grave.accent" }, { 51, "comma" }, { 52, "period" },
            { 53, "slash" }, { 55, "keypad.multiply" }, { 74, "keypad.subtract" }, { 78, "keypad.add" },
            { 83, "keypad.decimal" }
        };

        for (int index = 0; index <= 9; index++)
            values[2 + index] = index == 9 ? "0" : (index + 1).ToString(CultureInfo.InvariantCulture);

        foreach ((int start, string row) in new[] { (16, "qwertyuiop"), (30, "asdfghjkl"), (44, "zxcvbnm") })
        {
            for (int index = 0; index < row.Length; index++)
                values[start + index] = row[index].ToString();
        }

        for (int index = 1; index <= 10; index++)
            values[58 + index] = "f" + index.ToString(CultureInfo.InvariantCulture);

        foreach (KeyValuePair<int, string> pair in new Dictionary<int, string>
                 {
                     { 71, "7" }, { 72, "8" }, { 73, "9" }, { 75, "4" }, { 76, "5" },
                     { 77, "6" }, { 79, "1" }, { 80, "2" }, { 81, "3" }, { 82, "0" }
                 })
            values[pair.Key] = "keypad." + pair.Value;

        return values;
    }

    /// <summary>目标版本是否使用现代（1.13+）键名与字符串绑定。</summary>
    private static bool _UsesModernNames(bool isModern, int vanillaMajor) =>
        isModern || vanillaMajor >= ModernVanillaMajor;

    private static Dictionary<string, int> _CreateLegacyNames(Dictionary<int, string> legacyKeys)
    {
        Dictionary<string, int> names = new(StringComparer.Ordinal);
        foreach (KeyValuePair<int, string> pair in legacyKeys)
            names["key.keyboard." + pair.Value] = pair.Key;
        return names;
    }

    /// <summary>
    /// 解析目标文件里已有的 <c>fov</c> 值，用于判断该文件使用角度制还是 -1..1 归一值。
    /// 参考实现会额外识别 <c>Infinity</c>、<c>NaN</c> 等字面量，这里保持一致。
    /// </summary>
    private static bool _TryParseFov(string? value, out double numeric)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
        {
            if (!double.IsInfinity(numeric) && !double.IsNaN(numeric)) return true;
        }

        NumberFormatInfo format = CultureInfo.InvariantCulture.NumberFormat;
        string? trimmed = value?.Trim();
        return string.Equals(trimmed, format.PositiveInfinitySymbol, StringComparison.Ordinal) ||
               string.Equals(trimmed, format.NegativeInfinitySymbol, StringComparison.Ordinal) ||
               string.Equals(trimmed, format.NaNSymbol, StringComparison.Ordinal);
    }
}
