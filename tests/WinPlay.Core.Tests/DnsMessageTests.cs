// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using WinPlay.Core.Dns;
using Xunit;

namespace WinPlay.Core.Tests;

public class DnsMessageTests
{
    [Fact]
    public void BuildQuery_RoundTrips_Through_Parser()
    {
        byte[] packet = DnsQueryWriter.BuildQuery(
        [
            ("_airplay._tcp.local", DnsType.Ptr, false),
            ("_raop._tcp.local", DnsType.Ptr, true),
        ]);

        var msg = DnsMessage.Parse(packet);

        Assert.False(msg.IsResponse);
        Assert.Equal(2, msg.Questions.Count);
        Assert.Equal("_airplay._tcp.local", msg.Questions[0].Name);
        Assert.Equal(DnsType.Ptr, msg.Questions[0].Type);
        Assert.False(msg.Questions[0].UnicastResponse);
        Assert.Equal("_raop._tcp.local", msg.Questions[1].Name);
        Assert.True(msg.Questions[1].UnicastResponse);
    }

    [Fact]
    public void Parse_Response_With_Compression_Ptr_Srv_Txt_A()
    {
        var w = new PacketWriter();
        // Header: response, AA; 1 answer, 3 additionals.
        w.U16(0); w.U16(0x8400); w.U16(0); w.U16(1); w.U16(0); w.U16(3);

        // Answer: _airplay._tcp.local PTR "Living Room._airplay._tcp.local"
        int svcNameOff = w.Position;
        w.Name("_airplay._tcp.local");
        w.U16(12); w.U16(0x0001); w.U32(4500);
        int rdlenAt = w.ReserveU16();
        int instanceOff = w.Position;
        w.Label("Living Room");
        w.Pointer(svcNameOff);
        w.PatchU16(rdlenAt, (ushort)(w.Position - rdlenAt - 2));

        // Additional: SRV (cache-flush) → Living-Room.local:7000
        w.Pointer(instanceOff);
        w.U16(33); w.U16(0x8001); w.U32(120);
        rdlenAt = w.ReserveU16();
        w.U16(0); w.U16(0); w.U16(7000);
        int hostOff = w.Position;
        w.Name("Living-Room.local");
        w.PatchU16(rdlenAt, (ushort)(w.Position - rdlenAt - 2));

        // Additional: TXT
        w.Pointer(instanceOff);
        w.U16(16); w.U16(0x8001); w.U32(4500);
        rdlenAt = w.ReserveU16();
        w.TxtString("deviceid=AA:BB:CC:DD:EE:FF");
        w.TxtString("features=0x00000A00,0x00080200");
        w.TxtString("model=AudioAccessory5,1");
        w.TxtString("igl=1");
        w.PatchU16(rdlenAt, (ushort)(w.Position - rdlenAt - 2));

        // Additional: A → 192.168.1.50
        w.Pointer(hostOff);
        w.U16(1); w.U16(0x8001); w.U32(120);
        w.U16(4);
        w.Raw([192, 168, 1, 50]);

        var msg = DnsMessage.Parse(w.ToArray());

        Assert.True(msg.IsResponse);
        var ptr = Assert.IsType<PtrData>(Assert.Single(msg.Answers).Data);
        Assert.Equal("Living Room._airplay._tcp.local", ptr.Target);

        Assert.Equal(3, msg.Additionals.Count);

        Assert.Equal("Living Room._airplay._tcp.local", msg.Additionals[0].Name);
        var srv = Assert.IsType<SrvData>(msg.Additionals[0].Data);
        Assert.Equal(7000, srv.Port);
        Assert.Equal("Living-Room.local", srv.Target);
        Assert.True(msg.Additionals[0].CacheFlush);

        var txt = Assert.IsType<TxtData>(msg.Additionals[1].Data);
        Assert.Equal("AA:BB:CC:DD:EE:FF", txt.Pairs["deviceid"]);
        Assert.Equal("0x00000A00,0x00080200", txt.Pairs["features"]);
        Assert.Equal("AudioAccessory5,1", txt.Pairs["model"]);
        Assert.Equal("1", txt.Pairs["igl"]);

        Assert.Equal("Living-Room.local", msg.Additionals[2].Name);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), msg.Additionals[2].Data);
    }

    [Fact]
    public void Parse_Rejects_Compression_Loop()
    {
        // Header + a question whose name is a pointer to itself.
        var w = new PacketWriter();
        w.U16(0); w.U16(0); w.U16(1); w.U16(0); w.U16(0); w.U16(0);
        w.Pointer(12); // points at itself → forward/self pointer must be rejected
        w.U16(12); w.U16(1);

        Assert.Throws<FormatException>(() => DnsMessage.Parse(w.ToArray()));
    }

    [Fact]
    public void Parse_Rejects_Truncated_Packet()
    {
        byte[] packet = DnsQueryWriter.BuildQuery([("_airplay._tcp.local", DnsType.Ptr, false)]);
        Assert.Throws<FormatException>(() => DnsMessage.Parse(packet.AsSpan(0, packet.Length - 3)));
    }

    private sealed class PacketWriter
    {
        private readonly List<byte> _b = [];
        public int Position => _b.Count;

        public void U16(int v) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); }
        public void U32(uint v) { _b.Add((byte)(v >> 24)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 8)); _b.Add((byte)v); }
        public void Raw(byte[] bytes) => _b.AddRange(bytes);

        public void Label(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            _b.Add((byte)utf8.Length);
            _b.AddRange(utf8);
        }

        public void Name(string dotted)
        {
            foreach (string label in dotted.Split('.')) Label(label);
            _b.Add(0);
        }

        public void Pointer(int offset) { _b.Add((byte)(0xC0 | (offset >> 8))); _b.Add((byte)offset); }

        public void TxtString(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            _b.Add((byte)utf8.Length);
            _b.AddRange(utf8);
        }

        public int ReserveU16() { int at = _b.Count; U16(0); return at; }
        public void PatchU16(int at, ushort v) { _b[at] = (byte)(v >> 8); _b[at + 1] = (byte)v; }

        public byte[] ToArray() => [.. _b];
    }
}
