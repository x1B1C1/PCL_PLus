// ============================================================================
// [新增文件] TerracottaInviteCode.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本文件全部为新增代码。邀请码格式校验，避免把明显错误的字符串发给陶瓦。
// ============================================================================

using System.Text.RegularExpressions;

namespace PCL.Modules.Terracotta;

/// <summary>
/// 陶瓦邀请码工具。
/// <para>
/// 陶瓦邀请码形如 <c>U/XXXX-XXXX-XXXX-XXXX</c>。这里只做“看起来是否像邀请码”的校验，
/// 真正的有效性（校验和、房间是否存在）仍由陶瓦自己判断。
/// </para>
/// </summary>
internal static partial class TerracottaInviteCode
{
    [GeneratedRegex(@"^U/[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>
    /// 规范化用户输入：去空格、转大写、补 <c>U/</c> 前缀，然后校验格式。
    /// </summary>
    public static bool TryNormalize(string? input, out string code)
    {
        code = (input ?? "").Trim().ToUpperInvariant()
            .Replace(" ", "")
            .Replace("\t", "")
            .Replace("－", "-");

        // 用户经常只复制后半段（XXXX-XXXX-XXXX-XXXX），这里补上陶瓦要求的前缀。
        if (code.Length == 19 && !code.StartsWith("U/", StringComparison.Ordinal)) code = "U/" + code;

        if (!Pattern().IsMatch(code))
        {
            code = "";
            return false;
        }

        return true;
    }
}
