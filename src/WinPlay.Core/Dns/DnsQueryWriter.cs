// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace WinPlay.Core.Dns;

public static class DnsQueryWriter
{
    /// <summary>
    /// Builds a standard mDNS query. mDNS queries use ID 0 and QR=0 (RFC 6762 §18).
    /// Name compression is not applied — queries are small and compression is optional.
    /// </summary>
    public static byte[] BuildQuery(IReadOnlyList<(string Name, DnsType Type, bool UnicastResponse)> questions)
    {
        var buf = new List<byte>(64);
        WriteU16(buf, 0);                       // ID: always 0 for multicast
        WriteU16(buf, 0);                       // flags: standard query
        WriteU16(buf, (ushort)questions.Count); // QDCOUNT
        WriteU16(buf, 0);                       // ANCOUNT
        WriteU16(buf, 0);                       // NSCOUNT
        WriteU16(buf, 0);                       // ARCOUNT

        foreach (var (name, type, unicast) in questions)
        {
            WriteName(buf, name);
            WriteU16(buf, (ushort)type);
            ushort cls = 0x0001; // IN
            if (unicast) cls |= 0x8000;
            WriteU16(buf, cls);
        }
        return [.. buf];
    }

    private static void WriteName(List<byte> buf, string name)
    {
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new ArgumentException($"invalid DNS label '{label}'", nameof(name));
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.Add(0);
    }

    private static void WriteU16(List<byte> buf, ushort v)
    {
        buf.Add((byte)(v >> 8));
        buf.Add((byte)v);
    }
}
