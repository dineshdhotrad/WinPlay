// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Dns;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Verifies mDNS response serialisation (Task D3) by round-tripping every record type through
/// <see cref="DnsMessage.Parse"/> — the same parser that reads real receivers' packets. If a
/// record survives write → parse with its fields intact, a conforming peer can read it too.
/// </summary>
public class DnsRecordWriterTests
{
    private static DnsMessage RoundTrip(params DnsResourceRecord[] records)
        => DnsMessage.Parse(DnsRecordWriter.BuildResponse(records));

    [Fact]
    public void Response_Header_Is_An_Authoritative_Answer_With_No_Questions()
    {
        var msg = RoundTrip(new DnsResourceRecord
        {
            Name = "_dacp._tcp.local",
            Type = DnsType.Ptr,
            Class = DnsRecordWriter.ClassIn,
            Ttl = 4500,
            Data = new PtrData("iTunes_Ctrl_AB._dacp._tcp.local"),
        });

        Assert.True(msg.IsResponse);
        Assert.Equal(0, msg.Id);
        Assert.Equal(0x8400, msg.Flags); // QR=1, AA=1
        Assert.Empty(msg.Questions);
        Assert.Single(msg.Answers);
    }

    [Fact]
    public void Ptr_Record_RoundTrips()
    {
        var msg = RoundTrip(new DnsResourceRecord
        {
            Name = "_dacp._tcp.local",
            Type = DnsType.Ptr,
            Class = DnsRecordWriter.ClassIn,
            Ttl = DnsRecordWriter.SharedTtlSeconds,
            Data = new PtrData("iTunes_Ctrl_DEADBEEF._dacp._tcp.local"),
        });

        var record = msg.Answers[0];
        Assert.Equal("_dacp._tcp.local", record.Name);
        Assert.Equal(DnsType.Ptr, record.Type);
        Assert.Equal(4500u, record.Ttl);
        Assert.False(record.CacheFlush); // shared records must NOT set cache-flush
        Assert.Equal("iTunes_Ctrl_DEADBEEF._dacp._tcp.local", Assert.IsType<PtrData>(record.Data).Target);
    }

    [Fact]
    public void Srv_Record_RoundTrips_With_Port_And_Target()
    {
        var msg = RoundTrip(new DnsResourceRecord
        {
            Name = "iTunes_Ctrl_AB._dacp._tcp.local",
            Type = DnsType.Srv,
            Class = DnsRecordWriter.ClassInFlush,
            Ttl = DnsRecordWriter.UniqueTtlSeconds,
            Data = new SrvData(0, 0, 51234, "winplay-1a2b.local"),
        });

        var record = msg.Answers[0];
        Assert.True(record.CacheFlush); // unique record → cache-flush set
        Assert.Equal(120u, record.Ttl);
        var srv = Assert.IsType<SrvData>(record.Data);
        Assert.Equal(51234, srv.Port);
        Assert.Equal("winplay-1a2b.local", srv.Target);
        Assert.Equal(0, srv.Priority);
        Assert.Equal(0, srv.Weight);
    }

    [Fact]
    public void Txt_Record_RoundTrips_Key_Value_Pairs()
    {
        var msg = RoundTrip(new DnsResourceRecord
        {
            Name = "iTunes_Ctrl_AB._dacp._tcp.local",
            Type = DnsType.Txt,
            Class = DnsRecordWriter.ClassInFlush,
            Ttl = 120,
            Data = new TxtData(new Dictionary<string, string> { ["txtvers"] = "1", ["ver"] = "131077" }),
        });

        var txt = Assert.IsType<TxtData>(msg.Answers[0].Data);
        Assert.Equal("1", txt.Pairs["txtvers"]);
        Assert.Equal("131077", txt.Pairs["ver"]);
    }

    [Fact]
    public void Empty_Txt_Record_Emits_One_Zero_Length_String()
    {
        // RFC 6763 §6.1: an empty TXT must still carry a single empty string, never zero bytes.
        byte[] packet = DnsRecordWriter.BuildResponse([new DnsResourceRecord
        {
            Name = "x._dacp._tcp.local",
            Type = DnsType.Txt,
            Class = DnsRecordWriter.ClassInFlush,
            Ttl = 120,
            Data = new TxtData(new Dictionary<string, string>()),
        }]);

        var msg = DnsMessage.Parse(packet);           // must parse, not throw
        Assert.Empty(Assert.IsType<TxtData>(msg.Answers[0].Data).Pairs);
    }

    [Fact]
    public void A_Record_RoundTrips()
    {
        var msg = RoundTrip(new DnsResourceRecord
        {
            Name = "winplay-1a2b.local",
            Type = DnsType.A,
            Class = DnsRecordWriter.ClassInFlush,
            Ttl = 120,
            Data = IPAddress.Parse("192.168.1.162"),
        });

        Assert.Equal(IPAddress.Parse("192.168.1.162"), Assert.IsType<IPAddress>(msg.Answers[0].Data));
    }

    [Fact]
    public void A_Full_Service_Advertisement_RoundTrips_As_One_Message()
    {
        var msg = RoundTrip(
            new DnsResourceRecord { Name = "_dacp._tcp.local", Type = DnsType.Ptr, Class = DnsRecordWriter.ClassIn, Ttl = 4500, Data = new PtrData("iTunes_Ctrl_AB._dacp._tcp.local") },
            new DnsResourceRecord { Name = "iTunes_Ctrl_AB._dacp._tcp.local", Type = DnsType.Srv, Class = DnsRecordWriter.ClassInFlush, Ttl = 120, Data = new SrvData(0, 0, 5000, "h.local") },
            new DnsResourceRecord { Name = "iTunes_Ctrl_AB._dacp._tcp.local", Type = DnsType.Txt, Class = DnsRecordWriter.ClassInFlush, Ttl = 120, Data = new TxtData(new Dictionary<string, string> { ["txtvers"] = "1" }) },
            new DnsResourceRecord { Name = "h.local", Type = DnsType.A, Class = DnsRecordWriter.ClassInFlush, Ttl = 120, Data = IPAddress.Loopback });

        Assert.Equal(4, msg.Answers.Count);
        Assert.Equal(DnsType.Ptr, msg.Answers[0].Type);
        Assert.Equal(DnsType.Srv, msg.Answers[1].Type);
        Assert.Equal(DnsType.Txt, msg.Answers[2].Type);
        Assert.Equal(DnsType.A, msg.Answers[3].Type);
    }

    [Fact]
    public void An_Oversized_Label_Is_Rejected_Rather_Than_Emitting_A_Corrupt_Packet()
    {
        var record = new DnsResourceRecord
        {
            Name = new string('x', 64) + ".local", // labels are limited to 63 bytes
            Type = DnsType.A,
            Class = DnsRecordWriter.ClassInFlush,
            Ttl = 120,
            Data = IPAddress.Loopback,
        };
        // FormatException, not ArgumentException: name encoding now goes through DnsName, which
        // treats a name it cannot represent on the wire as malformed input. That matters because
        // the same code path encodes names taken off the network, where a malformed name is
        // routine data rather than a caller's mistake, and callers there already handle
        // FormatException from the rest of the DNS parser.
        Assert.Throws<FormatException>(() => DnsRecordWriter.BuildResponse([record]));
    }
}
