using System;
using System.Numerics;
using System.Text;

namespace PCL.Core.Link.Ipv6Direct;

/// <summary>
/// 把定长字节串压成 base62 文本，用于邀请码的载荷段。
/// 38 字节大约压成 51 个字符，比汉字方案短，且不含任何需要输入法的东西。
/// </summary>
internal static class Base62Codec
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        if (value.IsZero) return "0";
        var builder = new StringBuilder(64);
        while (value > BigInteger.Zero)
        {
            value = BigInteger.DivRem(value, 62, out var remainder);
            builder.Insert(0, Alphabet[(int)remainder]);
        }
        return builder.ToString();
    }

    public static bool TryDecode(string text, int expectedLength, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(text) || text.Length > 96) return false;
        var value = BigInteger.Zero;
        foreach (var ch in text)
        {
            var index = Alphabet.IndexOf(ch);
            if (index < 0) return false;
            value = value * 62 + index;
        }
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > expectedLength) return false;
        data = new byte[expectedLength];
        bytes.CopyTo(data, expectedLength - bytes.Length);
        return true;
    }
}
