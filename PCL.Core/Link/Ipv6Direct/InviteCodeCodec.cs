using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Core.Link.Ipv6Direct;

public enum InviteKind : byte
{
    HostInvite = 0x11,
    GuestAck = 0x12
}

public readonly record struct InviteCodePayload(
    InviteKind Kind,
    IPAddress Address,
    ushort Port,
    uint Token,
    TunnelSecret? Secret = null,
    byte[]? PeerId = null,
    byte[]? Proof = null,
    bool AllowOffline = true)
{
    public override string ToString() => $"{Kind}: [hide]";
}

/// <summary>
/// 邀请码形如 <c>PCL6A-K7M2-NS-9F3D81C0-&lt;base62 载荷&gt;</c>。
/// 前四段是给人看的：4 位房间指纹、NS（允许离线）/ YS（需正版验证）、8 位校验；
/// 最后一段是 38 字节的地址、端口、房间 token 与通道密钥，用 base62 压过。
/// 校验段由载荷派生，抄错一位就能当场发现，不必等到连不上。
/// </summary>
public static class InviteCodeCodec
{
    public const string HostPrefix = "PCL6A-";
    public const string GuestAckPrefix = "PCL6A-";
    public const int CodeLength = 30;

    private const int KeyLength = 4;
    private const int CheckLength = 8;
    private const string ModeOffline = "NS";
    private const string ModeOnline = "YS";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private static readonly byte[] KeyTag = "pcl6-key"u8.ToArray();
    private static readonly byte[] CheckTag = "pcl6-check"u8.ToArray();

    internal const int AckProofSize = 12;
    private const int EndpointLength = 18;
    private const int SignedAckLength = 2 + EndpointLength + 4 + TunnelAuthentication.PeerIdSize;

    public static string Encode(InviteCodePayload payload)
    {
        Span<byte> buffer = stackalloc byte[CommonHanziCode.PayloadLength];
        WriteEndpoint(buffer, payload);
        if (payload.Kind == InviteKind.HostInvite)
        {
            ArgumentNullException.ThrowIfNull(payload.Secret);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[18..22], payload.Token);
            payload.Secret.CopyTo(buffer[22..]);
        }
        else if (payload.Kind == InviteKind.GuestAck)
        {
            if (payload.PeerId?.Length != TunnelAuthentication.PeerIdSize || payload.Proof?.Length != AckProofSize)
                throw new ArgumentException("Guest ACK requires authenticated session credentials.", nameof(payload));
            payload.PeerId.CopyTo(buffer[18..26]);
            payload.Proof.CopyTo(buffer[26..]);
        }
        else
        {
            throw new ArgumentException("Invalid invite kind.", nameof(payload));
        }

        // 首段第一位兼作类型标记（H 房主 / A 加入者），剩下三位是载荷指纹。
        var kindChar = payload.Kind == InviteKind.HostInvite ? 'H' : 'A';
        var key = kindChar + Digest(buffer, KeyTag, KeyLength - 1);
        var check = Digest(buffer, CheckTag, CheckLength);
        var mode = payload.AllowOffline ? ModeOffline : ModeOnline;
        var body = Base62Codec.Encode(buffer);
        CryptographicOperations.ZeroMemory(buffer);
        return $"{HostPrefix}{key}-{mode}-{check}-{body}";
    }

    public static InviteCodePayload CreateGuestAck(InviteCodePayload hostInvite, IPAddress address, ushort port, ReadOnlySpan<byte> peerId)
    {
        if (hostInvite.Kind != InviteKind.HostInvite || hostInvite.Secret is null || peerId.Length != TunnelAuthentication.PeerIdSize)
            throw new ArgumentException("A current invite and session ID are required.", nameof(hostInvite));
        var ack = new InviteCodePayload(InviteKind.GuestAck, address, port, hostInvite.Token, PeerId: peerId.ToArray());
        Span<byte> signed = stackalloc byte[SignedAckLength];
        WriteSignedAck(signed, ack, hostInvite.Token);
        var signature = hostInvite.Secret.Sign(signed);
        try { return ack with { Proof = signature.AsSpan(0, AckProofSize).ToArray() }; }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    public static bool VerifyGuestAck(InviteCodePayload ack, uint token, TunnelSecret secret)
    {
        // ACK 不重复传输房间标识；必须绑定房主当前房间的 token，不能信任输入中的值。
        if (ack.Kind != InviteKind.GuestAck || ack.Address is null ||
            ack.Address.AddressFamily != AddressFamily.InterNetworkV6 || ack.Port == 0 ||
            ack.PeerId?.Length != TunnelAuthentication.PeerIdSize || ack.Proof?.Length != AckProofSize)
            return false;
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> signed = stackalloc byte[SignedAckLength];
        WriteSignedAck(signed, ack, token);
        var expected = secret.Sign(signed);
        try { return CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, AckProofSize), ack.Proof); }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    private static void WriteSignedAck(Span<byte> buffer, InviteCodePayload ack, uint token)
    {
        buffer[0] = TunnelAuthentication.Version;
        buffer[1] = (byte)InviteKind.GuestAck;
        WriteEndpoint(buffer[2..], ack);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[20..24], token);
        ack.PeerId!.CopyTo(buffer[24..]);
    }

    private static void WriteEndpoint(Span<byte> buffer, InviteCodePayload payload)
    {
        if (payload.Port == 0 || payload.Address is null || payload.Address.AddressFamily != AddressFamily.InterNetworkV6 ||
            !payload.Address.TryWriteBytes(buffer[..16], out var written) || written != 16)
            throw new ArgumentException("Invalid invite endpoint.", nameof(payload));
        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..18], payload.Port);
    }

    public static bool TryDecode(string? code, out InviteCodePayload payload, out string error)
    {
        payload = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(code) || code.Length > 1024)
        {
            error = "请粘贴完整的 PCL6A 邀请码。";
            return false;
        }

        var cleaned = new StringBuilder(code.Length);
        foreach (var ch in code)
        {
            if (!char.IsWhiteSpace(ch)) cleaned.Append(ch);
        }

        var parts = cleaned.ToString().Split('-');
        if (parts.Length != 5 || !parts[0].Equals("PCL6A", StringComparison.OrdinalIgnoreCase))
        {
            error = "邀请码应以 PCL6A- 开头，形如 PCL6A-XXXX-NS-XXXXXXXX-…。";
            return false;
        }

        if (!parts[2].Equals(ModeOffline, StringComparison.OrdinalIgnoreCase) &&
            !parts[2].Equals(ModeOnline, StringComparison.OrdinalIgnoreCase))
        {
            error = "联机模式段无效（应为 NS 或 YS）。";
            return false;
        }

        if (!Base62Codec.TryDecode(parts[4], CommonHanziCode.PayloadLength, out var raw))
        {
            error = "邀请码主体无法解析，可能复制不完整。";
            return false;
        }

        try
        {
            var kindChar = parts[1][0];
            var kind = kindChar is 'H' or 'h' ? InviteKind.HostInvite : InviteKind.GuestAck;
            var key = kindChar + Digest(raw, KeyTag, KeyLength - 1);
            var check = Digest(raw, CheckTag, CheckLength);
            if (!key.Equals(parts[1], StringComparison.OrdinalIgnoreCase) ||
                !check.Equals(parts[3], StringComparison.OrdinalIgnoreCase))
            {
                error = "邀请码校验不通过，请让房主重新复制一份完整的码。";
                return false;
            }

            var port = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(16, 2));
            if (port == 0)
            {
                error = "代码中的端口无效，请重新生成。";
                return false;
            }

            var address = new IPAddress(raw.AsSpan(0, 16));
            var allowOffline = parts[2].Equals(ModeOffline, StringComparison.OrdinalIgnoreCase);
            payload = kind == InviteKind.HostInvite
                ? new InviteCodePayload(kind, address, port, BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(18, 4)),
                    TunnelSecret.Read(raw.AsSpan(22, TunnelSecret.Size)), AllowOffline: allowOffline)
                : new InviteCodePayload(kind, address, port, 0,
                    PeerId: raw.AsSpan(18, TunnelAuthentication.PeerIdSize).ToArray(),
                    Proof: raw.AsSpan(26, AckProofSize).ToArray(), AllowOffline: allowOffline);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    private static string Digest(ReadOnlySpan<byte> payload, byte[] tag, int count)
    {
        Span<byte> material = stackalloc byte[payload.Length + 16];
        payload.CopyTo(material);
        tag.AsSpan().CopyTo(material[payload.Length..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(material[..(payload.Length + tag.Length)], hash);
        return Base32Tail(hash, count);
    }

    private static string Base32Tail(ReadOnlySpan<byte> hash, int count)
    {
        var builder = new StringBuilder(count);
        var bits = 0;
        var available = 0;
        var index = 0;
        while (builder.Length < count && index < hash.Length)
        {
            if (available < 5)
            {
                bits = (bits << 8) | hash[index++];
                available += 8;
            }
            var shift = available - 5;
            builder.Append(Base32Alphabet[(bits >> shift) & 31]);
            available -= 5;
        }
        return builder.ToString();
    }
}
