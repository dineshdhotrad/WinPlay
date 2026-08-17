// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;

namespace WinPlay.Core.Dns;

/// <summary>
/// Serialises mDNS <em>responses</em> (RFC 6762 §18, RFC 6763 §4-6) — the counterpart to
/// <see cref="DnsQueryWriter"/>. WinPlay needs this to advertise its own DNS-SD service so
/// receivers can find WinPlay's DACP endpoint and send transport commands back.
///
/// <para>Responses use ID 0 with QR=1 and AA=1. Name compression is deliberately not applied:
/// it is optional for responders, our packets are far below the MTU, and uncompressed names
/// cannot desynchronise a peer's parser.</para>
/// </summary>
public static class DnsRecordWriter
{
    /// <summary>Class IN.</summary>
    public const ushort ClassIn = 0x0001;

    /// <summary>Class IN with the mDNS cache-flush bit — for records unique to this host.</summary>
    public const ushort ClassInFlush = 0x8001;

    /// <summary>RFC 6763 §10: 75 minutes for shared PTR records.</summary>
    public const uint SharedTtlSeconds = 4500;

    /// <summary>RFC 6763 §10: 120 seconds for the host's unique SRV/TXT/A records.</summary>
    public const uint UniqueTtlSeconds = 120;

    /// <summary>Builds an authoritative mDNS response carrying <paramref name="answers"/>.</summary>
    public static byte[] BuildResponse(IReadOnlyList<DnsResourceRecord> answers)
    {
        var buf = new List<byte>(256);
        WriteU16(buf, 0);                     // ID: always 0 for multicast DNS
        WriteU16(buf, 0x8400);                // QR=1 (response), AA=1 (authoritative)
        WriteU16(buf, 0);                     // QDCOUNT: responses echo no questions
        WriteU16(buf, (ushort)answers.Count); // ANCOUNT
        WriteU16(buf, 0);                     // NSCOUNT
        WriteU16(buf, 0);                     // ARCOUNT

        foreach (var record in answers)
            WriteRecord(buf, record);
        return [.. buf];
    }

    private static void WriteRecord(List<byte> buf, DnsResourceRecord record)
    {
        WriteName(buf, record.Name);
        WriteU16(buf, (ushort)record.Type);
        WriteU16(buf, record.Class);
        WriteU32(buf, record.Ttl);

        byte[] rdata = EncodeRdata(record);
        WriteU16(buf, (ushort)rdata.Length);
        buf.AddRange(rdata);
    }

    private static byte[] EncodeRdata(DnsResourceRecord record) => record.Data switch
    {
        PtrData ptr => EncodeName(ptr.Target),
        SrvData srv => EncodeSrv(srv),
        TxtData txt => EncodeTxt(txt),
        IPAddress ip => ip.GetAddressBytes(),
        byte[] raw => raw,
        null => [],
        _ => throw new ArgumentException($"unsupported RDATA type {record.Data.GetType().Name}", nameof(record)),
    };

    private static byte[] EncodeSrv(SrvData srv)
    {
        var buf = new List<byte>(16);
        WriteU16(buf, srv.Priority);
        WriteU16(buf, srv.Weight);
        WriteU16(buf, srv.Port);
        buf.AddRange(EncodeName(srv.Target));
        return [.. buf];
    }

    private static byte[] EncodeTxt(TxtData txt)
    {
        var buf = new List<byte>(64);
        foreach (var (key, value) in txt.Pairs)
        {
            byte[] entry = Encoding.UTF8.GetBytes(value.Length == 0 ? key : $"{key}={value}");
            if (entry.Length > 255)
                throw new ArgumentException($"TXT entry '{key}' exceeds 255 bytes", nameof(txt));
            buf.Add((byte)entry.Length);
            buf.AddRange(entry);
        }
        // RFC 6763 §6.1: an empty TXT record must still carry one zero-length string.
        if (buf.Count == 0) buf.Add(0);
        return [.. buf];
    }

    private static byte[] EncodeName(string name)
    {
        var buf = new List<byte>(name.Length + 2);
        WriteName(buf, name);
        return [.. buf];
    }

    /// <summary>
    /// Encodes a presentation-format name, honouring RFC 1035 §5.1 escapes so a label containing a
    /// literal dot survives as one label. See <see cref="DnsName"/> for why that matters.
    /// </summary>
    private static void WriteName(List<byte> buf, string name)
    {
        foreach (string label in DnsName.SplitLabels(name))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
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

    private static void WriteU32(List<byte> buf, uint v)
    {
        buf.Add((byte)(v >> 24));
        buf.Add((byte)(v >> 16));
        buf.Add((byte)(v >> 8));
        buf.Add((byte)v);
    }
}
