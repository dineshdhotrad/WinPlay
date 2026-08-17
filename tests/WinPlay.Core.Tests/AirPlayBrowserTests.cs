// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Discovery;
using WinPlay.Core.Dns;
using WinPlay.Core.Mdns;
using Xunit;

namespace WinPlay.Core.Tests;

public class AirPlayBrowserTests
{
    private sealed class FakeTransport : IMdnsTransport
    {
        public event Action<DnsMessage, IPEndPoint>? MessageReceived;
        public List<IReadOnlyList<(string Name, DnsType Type, bool UnicastResponse)>> Queries { get; } = [];

        public List<byte[]> Sent { get; } = [];
        public IReadOnlyList<IPAddress> LocalAddresses => [IPAddress.Loopback];

        public void Start() { }
        public void Query(IReadOnlyList<(string Name, DnsType Type, bool UnicastResponse)> questions) => Queries.Add(questions);
        public void Send(byte[] packet, IPEndPoint? unicastTo = null) => Sent.Add(packet);
        public void Raise(DnsMessage msg) => MessageReceived?.Invoke(msg, new IPEndPoint(IPAddress.Loopback, 5353));
        public void Dispose() { }
    }

    private static DnsMessage Response(params DnsResourceRecord[] records)
    {
        var msg = new DnsMessage { Flags = 0x8400 };
        msg.Answers.AddRange(records);
        return msg;
    }

    private static DnsResourceRecord Ptr(string service, string instance) => new()
    {
        Name = service, Type = DnsType.Ptr, Class = 1, Ttl = 4500, Data = new PtrData(instance),
    };

    private static DnsResourceRecord Srv(string instance, string host, ushort port) => new()
    {
        Name = instance, Type = DnsType.Srv, Class = 0x8001, Ttl = 120, Data = new SrvData(0, 0, port, host),
    };

    private static DnsResourceRecord Txt(string instance, params (string K, string V)[] pairs) => new()
    {
        Name = instance, Type = DnsType.Txt, Class = 0x8001, Ttl = 4500,
        Data = new TxtData(pairs.ToDictionary(p => p.K, p => p.V)),
    };

    private static DnsResourceRecord A(string host, string ip) => new()
    {
        Name = host, Type = DnsType.A, Class = 0x8001, Ttl = 120, Data = IPAddress.Parse(ip),
    };

    [Fact]
    public void Merges_AirPlay_And_Raop_Advertisements_Into_One_Device()
    {
        var transport = new FakeTransport();
        using var browser = new AirPlayBrowser(transport);

        transport.Raise(Response(
            Ptr("_airplay._tcp.local", "Living Room._airplay._tcp.local"),
            Srv("Living Room._airplay._tcp.local", "Living-Room.local", 7000),
            Txt("Living Room._airplay._tcp.local",
                ("deviceid", "AA:BB:CC:DD:EE:FF"),
                ("model", "AudioAccessory5,1"),
                ("features", "0x00000A00,0x00080200"),
                ("gid", "11111111-2222-3333-4444-555555555555"),
                ("gpn", "Living Room"),
                ("igl", "1"),
                ("tsid", "99999999-8888-7777-6666-555555555555")),
            A("Living-Room.local", "192.168.1.50")));

        transport.Raise(Response(
            Ptr("_raop._tcp.local", "AABBCCDDEEFF@Living Room._raop._tcp.local"),
            Srv("AABBCCDDEEFF@Living Room._raop._tcp.local", "Living-Room.local", 7000),
            Txt("AABBCCDDEEFF@Living Room._raop._tcp.local", ("cn", "0,1,2"), ("et", "0,3,5"), ("tp", "UDP"))));

        var device = Assert.Single(browser.Snapshot());
        Assert.Equal("AABBCCDDEEFF", device.DeviceId);
        Assert.Equal("Living Room", device.Name);
        Assert.Equal(AirPlayDeviceSubtype.HomePod, device.Subtype);
        Assert.Equal(7000, device.AirPlayPort);
        Assert.Equal(7000, device.RaopPort);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), Assert.Single(device.Addresses));
        Assert.True(device.IsGroupLeader);
        Assert.Equal("11111111-2222-3333-4444-555555555555", device.GroupId);
        Assert.Equal("99999999-8888-7777-6666-555555555555", device.TightSyncId);
        Assert.Equal("0,1,2", device.RaopTxt["cn"]);
        Assert.True(device.SupportsAudio);
        Assert.False(device.IsMirroringCandidate);
    }

    [Fact]
    public void Goodbye_Ttl_Zero_Removes_Instance()
    {
        var transport = new FakeTransport();
        using var browser = new AirPlayBrowser(transport);

        transport.Raise(Response(
            Ptr("_airplay._tcp.local", "Den._airplay._tcp.local"),
            Srv("Den._airplay._tcp.local", "Den.local", 7000),
            Txt("Den._airplay._tcp.local", ("deviceid", "11:22:33:44:55:66"), ("model", "AppleTV14,1"))));
        Assert.Single(browser.Snapshot());

        var goodbye = Srv("Den._airplay._tcp.local", "Den.local", 7000);
        transport.Raise(Response(new DnsResourceRecord
        {
            Name = goodbye.Name, Type = DnsType.Srv, Class = 0x8001, Ttl = 0, Data = goodbye.Data,
        }));
        Assert.Empty(browser.Snapshot());
    }

    [Fact]
    public void Raop_Only_Device_Still_Appears()
    {
        var transport = new FakeTransport();
        using var browser = new AirPlayBrowser(transport);

        transport.Raise(Response(
            Ptr("_raop._tcp.local", "112233445566@Old Speaker._raop._tcp.local"),
            Srv("112233445566@Old Speaker._raop._tcp.local", "old-speaker.local", 5000),
            Txt("112233445566@Old Speaker._raop._tcp.local", ("cn", "0,1"), ("am", "ShairportSync"))));

        var device = Assert.Single(browser.Snapshot());
        Assert.Equal("112233445566", device.DeviceId);
        Assert.Equal("Old Speaker", device.Name);
        Assert.Equal("ShairportSync", device.Model);
        Assert.Equal(5000, device.RaopPort);
        Assert.Null(device.AirPlayPort);
        Assert.True(device.SupportsAudio);
    }
}
