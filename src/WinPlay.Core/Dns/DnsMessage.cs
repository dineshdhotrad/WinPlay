// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;

namespace WinPlay.Core.Dns;

public enum DnsType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Opt = 41,
    Nsec = 47,
    Any = 255,
}

public sealed record DnsQuestion(string Name, DnsType Type, ushort Class)
{
    /// <summary>mDNS "QU" bit: request a unicast response.</summary>
    public bool UnicastResponse => (Class & 0x8000) != 0;
}

public sealed record PtrData(string Target);

public sealed record SrvData(ushort Priority, ushort Weight, ushort Port, string Target);

public sealed record TxtData(IReadOnlyDictionary<string, string> Pairs);

public sealed class DnsResourceRecord
{
    public required string Name { get; init; }
    public DnsType Type { get; init; }
    public ushort Class { get; init; }
    public uint Ttl { get; init; }

    /// <summary>
    /// Typed RDATA: <see cref="PtrData"/>, <see cref="SrvData"/>, <see cref="TxtData"/>,
    /// <see cref="IPAddress"/> (A/AAAA), or raw <c>byte[]</c> for unhandled types.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>mDNS cache-flush bit (top bit of the class field).</summary>
    public bool CacheFlush => (Class & 0x8000) != 0;
}

/// <summary>
/// Minimal DNS message model + wire-format parser sufficient for mDNS/DNS-SD browsing
/// (RFC 1035 name compression, PTR/SRV/TXT/A/AAAA records). Bundled deliberately —
/// WinPlay must not depend on Apple Bonjour being installed.
/// </summary>
public sealed class DnsMessage
{
    public ushort Id { get; set; }
    public ushort Flags { get; set; }
    public bool IsResponse => (Flags & 0x8000) != 0;

    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsResourceRecord> Answers { get; } = [];
    public List<DnsResourceRecord> Authorities { get; } = [];
    public List<DnsResourceRecord> Additionals { get; } = [];

    /// <summary>Answers and additionals combined — mDNS responders put related records in either section.</summary>
    public IEnumerable<DnsResourceRecord> AllRecords => Answers.Concat(Authorities).Concat(Additionals);

    public static DnsMessage Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 12)
            throw new FormatException("DNS message shorter than header");

        var msg = new DnsMessage
        {
            Id = ReadU16(buf, 0),
            Flags = ReadU16(buf, 2),
        };
        int qd = ReadU16(buf, 4);
        int an = ReadU16(buf, 6);
        int ns = ReadU16(buf, 8);
        int ar = ReadU16(buf, 10);

        int off = 12;
        for (int i = 0; i < qd; i++)
        {
            string name = ReadName(buf, ref off);
            if (off + 4 > buf.Length) throw new FormatException("question overruns buffer");
            var type = (DnsType)ReadU16(buf, off);
            ushort cls = ReadU16(buf, off + 2);
            off += 4;
            msg.Questions.Add(new DnsQuestion(name, type, cls));
        }
        ReadRecords(buf, ref off, an, msg.Answers);
        ReadRecords(buf, ref off, ns, msg.Authorities);
        ReadRecords(buf, ref off, ar, msg.Additionals);
        return msg;
    }

    private static void ReadRecords(ReadOnlySpan<byte> buf, ref int off, int count, List<DnsResourceRecord> into)
    {
        for (int i = 0; i < count; i++)
            into.Add(ReadRecord(buf, ref off));
    }

    private static DnsResourceRecord ReadRecord(ReadOnlySpan<byte> buf, ref int off)
    {
        string name = ReadName(buf, ref off);
        if (off + 10 > buf.Length) throw new FormatException("record header overruns buffer");
        var type = (DnsType)ReadU16(buf, off);
        ushort cls = ReadU16(buf, off + 2);
        uint ttl = ReadU32(buf, off + 4);
        int rdLen = ReadU16(buf, off + 8);
        off += 10;
        if (off + rdLen > buf.Length) throw new FormatException("RDATA overruns buffer");
        int rdEnd = off + rdLen;

        object? data;
        switch (type)
        {
            case DnsType.Ptr:
            {
                int p = off;
                data = new PtrData(ReadName(buf, ref p));
                break;
            }
            case DnsType.Srv:
            {
                if (rdLen < 7) throw new FormatException("SRV RDATA too short");
                int p = off + 6;
                data = new SrvData(ReadU16(buf, off), ReadU16(buf, off + 2), ReadU16(buf, off + 4), ReadName(buf, ref p));
                break;
            }
            case DnsType.Txt:
                data = ParseTxt(buf.Slice(off, rdLen));
                break;
            case DnsType.A:
                if (rdLen != 4) throw new FormatException("A RDATA must be 4 bytes");
                data = new IPAddress(buf.Slice(off, 4));
                break;
            case DnsType.Aaaa:
                if (rdLen != 16) throw new FormatException("AAAA RDATA must be 16 bytes");
                data = new IPAddress(buf.Slice(off, 16));
                break;
            default:
                data = buf.Slice(off, rdLen).ToArray();
                break;
        }

        off = rdEnd;
        return new DnsResourceRecord { Name = name, Type = type, Class = cls, Ttl = ttl, Data = data };
    }

    private static TxtData ParseTxt(ReadOnlySpan<byte> rdata)
    {
        // TXT keys are case-insensitive per DNS-SD (RFC 6763 §6.4); normalize to lowercase.
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        int p = 0;
        while (p < rdata.Length)
        {
            int len = rdata[p++];
            if (len == 0) continue;
            if (p + len > rdata.Length) throw new FormatException("TXT string overruns RDATA");
            string s = Encoding.UTF8.GetString(rdata.Slice(p, len));
            p += len;
            int eq = s.IndexOf('=');
            string key = (eq < 0 ? s : s[..eq]).ToLowerInvariant();
            string value = eq < 0 ? "" : s[(eq + 1)..];
            if (key.Length > 0)
                pairs[key] = value; // last occurrence wins, per RFC 6763 first-wins is for readers; duplicates are rare
        }
        return new TxtData(pairs);
    }

    internal static string ReadName(ReadOnlySpan<byte> buf, ref int offset)
    {
        var sb = new StringBuilder();
        int pos = offset;
        int afterFirstPointer = -1;
        int jumps = 0;

        while (true)
        {
            if (pos >= buf.Length) throw new FormatException("name overruns buffer");
            byte len = buf[pos];
            if (len == 0)
            {
                pos++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= buf.Length) throw new FormatException("truncated compression pointer");
                int target = ((len & 0x3F) << 8) | buf[pos + 1];
                if (afterFirstPointer < 0) afterFirstPointer = pos + 2;
                if (target >= pos) throw new FormatException("forward compression pointer");
                pos = target;
                if (++jumps > 64) throw new FormatException("compression pointer loop");
                continue;
            }
            if ((len & 0xC0) != 0) throw new FormatException("unsupported label type");
            if (pos + 1 + len > buf.Length) throw new FormatException("label overruns buffer");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(buf.Slice(pos + 1, len)));
            if (sb.Length > 1024) throw new FormatException("name too long");
            pos += 1 + len;
        }

        offset = afterFirstPointer >= 0 ? afterFirstPointer : pos;
        return sb.ToString();
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, int o) =>
        o + 2 <= b.Length ? (ushort)((b[o] << 8) | b[o + 1]) : throw new FormatException("u16 overruns buffer");

    private static uint ReadU32(ReadOnlySpan<byte> b, int o) =>
        o + 4 <= b.Length ? ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3] : throw new FormatException("u32 overruns buffer");
}
