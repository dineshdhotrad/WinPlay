// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Hap;

/// <summary>HomeKit pairing TLV type tags (HAP spec table 4-6).</summary>
public static class TlvType
{
    public const byte Method = 0x00;
    public const byte Identifier = 0x01;
    public const byte Salt = 0x02;
    public const byte PublicKey = 0x03;
    public const byte Proof = 0x04;
    public const byte EncryptedData = 0x05;
    public const byte State = 0x06;
    public const byte Error = 0x07;
    public const byte RetryDelay = 0x08;
    public const byte Certificate = 0x09;
    public const byte Signature = 0x0A;
    public const byte Permissions = 0x0B;
    public const byte FragmentData = 0x0C;
    public const byte FragmentLast = 0x0D;
    public const byte Flags = 0x13;
    public const byte Separator = 0xFF;
}

public static class TlvError
{
    public const byte Unknown = 0x01;
    public const byte Authentication = 0x02;
    public const byte Backoff = 0x03;
    public const byte MaxPeers = 0x04;
    public const byte MaxTries = 0x05;
    public const byte Unavailable = 0x06;
    public const byte Busy = 0x07;

    public static string Describe(byte code) => code switch
    {
        Unknown => "unknown error",
        Authentication => "authentication failed (setup code or signature error)",
        Backoff => "backoff requested",
        MaxPeers => "max peers reached",
        MaxTries => "max authentication attempts reached",
        Unavailable => "pairing method unavailable",
        Busy => "busy with another pairing",
        _ => $"error 0x{code:X2}",
    };
}

/// <summary>
/// TLV8 codec (HAP): [1-byte type][1-byte length][value], values longer than 255 bytes
/// fragmented into consecutive records with the same type.
/// </summary>
public static class Tlv8
{
    public static byte[] Encode(IReadOnlyList<(byte Type, byte[] Value)> items)
    {
        var ms = new MemoryStream();
        foreach (var (type, value) in items)
        {
            if (value.Length == 0)
            {
                ms.WriteByte(type);
                ms.WriteByte(0);
                continue;
            }
            int offset = 0;
            while (offset < value.Length)
            {
                int chunk = Math.Min(255, value.Length - offset);
                ms.WriteByte(type);
                ms.WriteByte((byte)chunk);
                ms.Write(value, offset, chunk);
                offset += chunk;
            }
        }
        return ms.ToArray();
    }

    /// <summary>Decodes, coalescing consecutive same-type fragments.</summary>
    public static List<(byte Type, byte[] Value)> Decode(ReadOnlySpan<byte> data)
    {
        List<(byte, byte[])> items = [];
        int p = 0;
        while (p < data.Length)
        {
            if (p + 2 > data.Length) throw new FormatException("truncated TLV header");
            byte type = data[p];
            int len = data[p + 1];
            p += 2;
            if (p + len > data.Length) throw new FormatException("truncated TLV value");
            byte[] value = data.Slice(p, len).ToArray();
            p += len;

            if (items.Count > 0 && items[^1].Item1 == type && items[^1].Item2.Length % 255 == 0
                && items[^1].Item2.Length > 0)
            {
                items[^1] = (type, [.. items[^1].Item2, .. value]);
            }
            else
            {
                items.Add((type, value));
            }
        }
        return items;
    }

    public static byte[]? Find(List<(byte Type, byte[] Value)> items, byte type)
    {
        foreach (var (t, v) in items)
            if (t == type) return v;
        return null;
    }
}
