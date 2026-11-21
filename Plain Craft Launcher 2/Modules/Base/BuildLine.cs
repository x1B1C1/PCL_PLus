using System;
using PCL.Core.App;

namespace PCL.Modules.Base;

/// <summary>
/// 当前构建所属的发布线。
/// <para>
/// BF 线是本地工程调试线（metadata.json 的 suffix 为 <c>bf</c>），
/// 它的 GPU 使用率与工程控制台只在 BF 构建里出现，正式线 / 测试线的行为完全不变。
/// </para>
/// </summary>
internal static class BuildLine
{
    /// <summary>
    /// 是否为 BF 线构建。
    /// </summary>
    public static bool IsBf =>
        string.Equals(Basics.Metadata.Version.Suffix, "bf", StringComparison.OrdinalIgnoreCase);
}
