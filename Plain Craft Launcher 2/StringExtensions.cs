using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PCL;

/// <summary>
///     字符串扩展方法。
/// </summary>
public static class StringExtensions
{
    /// <summary>
    ///     将第一个字符转换为大写，其余字符转换为小写。
    /// </summary>
    public static string Capitalize(this string word)
    {
        if (string.IsNullOrEmpty(word))
            return word;
        return word.Substring(0, 1).ToUpperInvariant() + word.Substring(1).ToLowerInvariant();
    }

    /// <summary>
    ///     分割字符串。
    /// </summary>
    public static string[] Split(this string fullStr, string splitStr)
    {
        if (splitStr.Length == 1) return fullStr.Split(splitStr[0]);

        return fullStr.Split(new[] { splitStr }, StringSplitOptions.None);
    }

    /// <summary>
    ///     检查字符串中的字符是否均为 ASCII 字符。
    /// </summary>
    public static bool IsASCII(this string input)
    {
        return input.All(c => c < 128);
    }

    /// <summary>
    ///     获取在子字符串第一次出现之前的部分，例如对 2024/11/08 拆切 / 会得到 2024。
    ///     如果未找到子字符串则不裁切。
    /// </summary>
    public static string BeforeFirst(this string str, string text, bool ignoreCase = false)
    {
        var pos = string.IsNullOrEmpty(text) ? -1 : str.IndexOfF(text, ignoreCase);
        if (pos >= 0) return str.Substring(0, pos);

        return str;
    }

    /// <summary>
    ///     获取在子字符串最后一次出现之前的部分，例如对 2024/11/08 拆切 / 会得到 2024/11。
    ///     如果未找到子字符串则不裁切。
    /// </summary>
    public static string BeforeLast(this string str, string text, bool ignoreCase = false)
    {
        var pos = string.IsNullOrEmpty(text) ? -1 : str.LastIndexOfF(text, ignoreCase);
        if (pos >= 0) return str.Substring(0, pos);

        return str;
    }

    /// <summary>
    ///     获取在子字符串第一次出现之后的部分，例如对 2024/11/08 拆切 / 会得到 11/08。
    ///     如果未找到子字符串则不裁切。
    /// </summary>
    public static string AfterFirst(this string str, string text, bool ignoreCase = false)
    {
        var pos = string.IsNullOrEmpty(text) ? -1 : str.IndexOfF(text, ignoreCase);
        if (pos >= 0) return str.Substring(pos + text.Length);

        return str;
    }

    /// <summary>
    ///     获取在子字符串最后一次出现之后的部分，例如对 2024/11/08 拆切 / 会得到 08。
    ///     如果未找到子字符串则不裁切。
    /// </summary>
    public static string AfterLast(this string str, string text, bool ignoreCase = false)
    {
        var pos = string.IsNullOrEmpty(text) ? -1 : str.LastIndexOfF(text, ignoreCase);
        if (pos >= 0) return str.Substring(pos + text.Length);

        return str;
    }

    /// <summary>
    ///     获取处于两个子字符串之间的部分，裁切尽可能多的内容。
    ///     等效于 AfterLast 后接 BeforeFirst。
    ///     如果未找到子字符串则不裁切。
    /// </summary>
    public static string Between(this string str, string after, string before, bool ignoreCase = false)
    {
        var startPos = string.IsNullOrEmpty(after) ? -1 : str.LastIndexOfF(after, ignoreCase);
        if (startPos >= 0)
            startPos += after.Length;
        else
            startPos = 0;
        var endPos = string.IsNullOrEmpty(before) ? -1 : str.IndexOfF(before, startPos, ignoreCase);
        if (endPos >= 0) return str.Substring(startPos, endPos - startPos);

        if (startPos > 0) return str.Substring(startPos);

        return str;
    }

    /// <summary>
    ///     高速的 StartsWith。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool StartsWithF(this string str, string prefix, bool ignoreCase = false)
    {
        return str.StartsWith(prefix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    ///     高速的 EndsWith。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool EndsWithF(this string str, string suffix, bool ignoreCase = false)
    {
        return str.EndsWith(suffix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    ///     支持可变大小写判断的 Contains。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsF(this string str, string subStr, bool ignoreCase = false)
    {
        return str.IndexOf(subStr, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    ///     高速的 IndexOf。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOfF(this string str, string subStr, bool ignoreCase = false)
    {
        return str.IndexOf(subStr, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    ///     高速的 IndexOf。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOfF(this string str, string subStr, int startIndex, bool ignoreCase = false)
    {
        return str.IndexOf(subStr, startIndex,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    ///     高速的 LastIndexOf。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LastIndexOfF(this string str, string subStr, bool ignoreCase = false)
    {
        return str.LastIndexOf(subStr, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    ///     高速的 LastIndexOf。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LastIndexOfF(this string str, string subStr, int startIndex, bool ignoreCase = false)
    {
        return str.LastIndexOf(subStr, startIndex,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // 正则
    /// <summary>
    ///     搜索字符串中的所有正则匹配项。
    /// </summary>
    public static List<string> RegexSearch(this string str, string regex, RegexOptions options = RegexOptions.None)
    {
        List<string> regexSearchRet = default;
        try
        {
            regexSearchRet = new List<string>();
            var regexSearchRes = new Regex(regex, options).Matches(str);
            if (regexSearchRes is null)
                return regexSearchRet;
            foreach (Match item in regexSearchRes)
                regexSearchRet.Add(item.Value);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "正则匹配全部项出错");
            return new List<string>();
        }

        return regexSearchRet;
    }

    /// <summary>
    /// 搜索字符串中的所有正则匹配项。
    /// </summary>
    /// <param name="str">要搜索的字符串</param>
    /// <param name="regex">正则表达式对象</param>
    /// <returns>所有匹配项的列表</returns>
    public static List<string> RegexSearch(this string str, Regex regex)
    {
        try
        {
            var result = new List<string>();
            foreach (Match item in regex.Matches(str))
            {
                result.Add(item.Value);
            }
            return result;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "正则匹配全部项出错");
            return new List<string>();
        }
    }

    /// <summary>
    ///     获取字符串中的第一个正则匹配项，若无匹配则返回 Nothing。
    /// </summary>
    public static string RegexSeek(this string str, string regex, RegexOptions options = RegexOptions.None)
    {
        try
        {
            var result = Regex.Match(str, regex, options).Value;
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "正则匹配第一项出错");
            return null;
        }
    }

    /// <summary>
    ///     获取字符串中的第一个正则匹配项，若无匹配则返回 Nothing。
    /// </summary>
    public static string RegexSeek(this string str, Regex regex, RegexOptions options = RegexOptions.None)
    {
        try
        {
            var result = regex.Match(str, (int)options).Value;
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "正则匹配第一项出错");
            return null;
        }
    }

    /// <summary>
    ///     检查字符串是否匹配某正则模式。
    /// </summary>
    public static bool RegexCheck(this string str, string regex, RegexOptions options = RegexOptions.None)
    {
        try
        {
            return Regex.IsMatch(str, regex, options);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "正则检查出错");
            return false;
        }
    }

    /// <summary>
    ///     进行正则替换，会抛出错误。
    /// </summary>
    public static string RegexReplace(this string allContents, string searchRegex, string replaceTo,
        RegexOptions options = RegexOptions.None)
    {
        return Regex.Replace(allContents, searchRegex, replaceTo, options);
    }

    /// <summary>
    ///     对每个正则匹配分别进行替换，会抛出错误。
    /// </summary>
    public static string RegexReplaceEach(this string allContents, string searchRegex, MatchEvaluator replaceTo,
        RegexOptions options = RegexOptions.None)
    {
        return Regex.Replace(allContents, searchRegex, replaceTo, options);
    }
}
