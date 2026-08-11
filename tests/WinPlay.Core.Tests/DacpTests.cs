// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinPlay.Core.Dns;
using WinPlay.Core.Mdns;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Covers the sender-side DACP endpoint (Task D3): request parsing for every command a receiver
/// can send, the Active-Remote authorisation gate, and a full end-to-end exchange over a real
/// TCP socket — the same path a HomePod takes when the user presses pause on it.
/// </summary>
public class DacpTests
{
    private static string Request(string target, string? activeRemote = "12345") =>
        $"GET {target} HTTP/1.1\r\nHost: 192.168.1.10\r\n"
        + (activeRemote is null ? "" : $"Active-Remote: {activeRemote}\r\n")
        + "Viewer-Only: 1\r\n\r\n";

    [Theory]
    [InlineData("playpause", DacpCommand.PlayPause)]
    [InlineData("play", DacpCommand.Play)]
    [InlineData("playresume", DacpCommand.Play)]
    [InlineData("pause", DacpCommand.Pause)]
    [InlineData("stop", DacpCommand.Stop)]
    [InlineData("nextitem", DacpCommand.Next)]
    [InlineData("previtem", DacpCommand.Previous)]
    [InlineData("volumeup", DacpCommand.VolumeUp)]
    [InlineData("volumedown", DacpCommand.VolumeDown)]
    [InlineData("mutetoggle", DacpCommand.MuteToggle)]
    [InlineData("shufflesongs", DacpCommand.ShuffleToggle)]
    [InlineData("repeatadvance", DacpCommand.RepeatToggle)]
    public void Every_Transport_Command_Is_Recognised(string verb, DacpCommand expected)
    {
        var parsed = DacpRequest.Parse(Request($"/ctrl-int/1/{verb}"));
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Command);
        Assert.Equal("12345", parsed.ActiveRemote);
    }

    [Fact]
    public void An_Unknown_Verb_Parses_But_Maps_To_Unknown()
    {
        var parsed = DacpRequest.Parse(Request("/ctrl-int/1/somethingelse"));
        Assert.NotNull(parsed);
        Assert.Equal(DacpCommand.Unknown, parsed.Command);
    }

    [Fact]
    public void SetProperty_Extracts_The_Requested_Volume_In_Db()
    {
        var parsed = DacpRequest.Parse(Request("/ctrl-int/1/setproperty?dmcp.device-volume=-14.5"));
        Assert.NotNull(parsed);
        Assert.Equal(-14.5, parsed.Volume!.Value, 3);
    }

    [Fact]
    public void SetProperty_Volume_Parses_Independently_Of_Locale()
    {
        // A comma-decimal culture must not turn "-14.5" into -145.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var parsed = DacpRequest.Parse(Request("/ctrl-int/1/setproperty?dmcp.device-volume=-14.5"));
            Assert.Equal(-14.5, parsed!.Volume!.Value, 3);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void SetProperty_Without_A_Volume_Key_Yields_No_Volume()
    {
        var parsed = DacpRequest.Parse(Request("/ctrl-int/1/setproperty?dmcp.something=1"));
        Assert.NotNull(parsed);
        Assert.Null(parsed.Volume);
    }

    [Fact]
    public void A_Missing_Active_Remote_Header_Is_Reported_As_Null()
    {
        var parsed = DacpRequest.Parse(Request("/ctrl-int/1/playpause", activeRemote: null));
        Assert.NotNull(parsed);
        Assert.Null(parsed.ActiveRemote);
    }

    [Theory]
    [InlineData("POST /ctrl-int/1/playpause HTTP/1.1\r\n\r\n")]  // wrong method
    [InlineData("GET /other/path HTTP/1.1\r\n\r\n")]             // not a control request
    [InlineData("garbage")]                                       // not HTTP at all
    [InlineData("")]                                              // empty
    public void Malformed_Requests_Are_Rejected(string head)
        => Assert.Null(DacpRequest.Parse(head));

    // ---------------------------------------------------------------- live socket exchange

    [Fact]
    public async Task A_Valid_Command_Over_A_Real_Socket_Is_Accepted_And_Raised()
    {
        using var server = new DacpServer(dacpId: "AABBCCDD11223344", activeRemote: "987654321");
        DacpCommand? received = null;
        using var gate = new SemaphoreSlim(0, 1);
        server.CommandReceived += cmd => { received = cmd; gate.Release(); };
        server.Start();

        string status = await SendAsync(server.Port, Request("/ctrl-int/1/nextitem", "987654321"));

        Assert.Contains("204", status);
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)), "command was not raised");
        Assert.Equal(DacpCommand.Next, received);
    }

    [Fact]
    public async Task A_Wrong_Active_Remote_Is_Rejected_With_403_And_Raises_Nothing()
    {
        using var server = new DacpServer(activeRemote: "correct-token");
        bool raised = false;
        server.CommandReceived += _ => raised = true;
        server.Start();

        string status = await SendAsync(server.Port, Request("/ctrl-int/1/playpause", "wrong-token"));

        Assert.Contains("403", status);
        await Task.Delay(200);
        Assert.False(raised, "a command with a bad token must never reach the app");
    }

    [Fact]
    public async Task A_Volume_Request_Over_A_Real_Socket_Raises_The_Requested_Db()
    {
        using var server = new DacpServer(activeRemote: "tok");
        double? db = null;
        using var gate = new SemaphoreSlim(0, 1);
        server.VolumeRequested += v => { db = v; gate.Release(); };
        server.Start();

        await SendAsync(server.Port, Request("/ctrl-int/1/setproperty?dmcp.device-volume=-9.25", "tok"));

        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)), "volume was not raised");
        Assert.Equal(-9.25, db!.Value, 3);
    }

    [Fact]
    public void The_Service_Instance_Name_Follows_The_iTunes_Ctrl_Convention()
    {
        using var server = new DacpServer(dacpId: "0011223344556677");
        Assert.Equal("iTunes_Ctrl_0011223344556677", server.ServiceInstanceName);
    }

    private static async Task<string> SendAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[512];
        int read = await stream.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}

/// <summary>
/// Covers the DNS-SD responder that makes the DACP endpoint discoverable (Task D3): which
/// questions it answers, and the exact record set it publishes.
/// </summary>
public class MdnsServiceAdvertiserTests
{
    private sealed class FakeTransport : IMdnsTransport
    {
        public readonly List<(byte[] Packet, IPEndPoint? To)> Sent = [];
        public event Action<DnsMessage, IPEndPoint>? MessageReceived;
        public IReadOnlyList<IPAddress> LocalAddresses { get; set; } = [IPAddress.Parse("192.168.1.162")];
        public void Start() { }
        public void Query(IReadOnlyList<(string Name, DnsType Type, bool UnicastResponse)> questions) { }
        public void Send(byte[] packet, IPEndPoint? unicastTo = null) => Sent.Add((packet, unicastTo));
        public void Dispose() { }
        public void Deliver(DnsMessage m, IPEndPoint from) => MessageReceived?.Invoke(m, from);
    }

    private static MdnsServiceAdvertiser NewAdvertiser(FakeTransport transport) =>
        new(transport, "_dacp._tcp.local", "iTunes_Ctrl_AABB", 51000,
            new Dictionary<string, string> { ["txtvers"] = "1" }, "winplay-test.local");

    private static DnsMessage Query(string name, DnsType type, bool unicast = false)
    {
        var msg = new DnsMessage();
        msg.Questions.Add(new DnsQuestion(name, type, (ushort)(unicast ? 0x8001 : 0x0001)));
        return msg;
    }

    [Theory]
    [InlineData("_dacp._tcp.local", DnsType.Ptr, true)]
    [InlineData("iTunes_Ctrl_AABB._dacp._tcp.local", DnsType.Srv, true)]
    [InlineData("iTunes_Ctrl_AABB._dacp._tcp.local", DnsType.Txt, true)]
    [InlineData("winplay-test.local", DnsType.A, true)]
    [InlineData("iTunes_Ctrl_AABB._dacp._tcp.local", DnsType.Any, true)]
    [InlineData("_airplay._tcp.local", DnsType.Ptr, false)]   // someone else's service
    [InlineData("other-host.local", DnsType.A, false)]        // someone else's host
    [InlineData("_dacp._tcp.local", DnsType.Srv, false)]      // wrong type for that name
    public void Matches_Only_Questions_For_Records_We_Own(string name, DnsType type, bool expected)
    {
        var advertiser = NewAdvertiser(new FakeTransport());
        Assert.Equal(expected, advertiser.Matches(new DnsQuestion(name, type, 0x0001)));
    }

    [Fact]
    public void Start_Announces_The_Full_Record_Set()
    {
        var transport = new FakeTransport();
        using var advertiser = NewAdvertiser(transport);
        advertiser.Start();

        var msg = DnsMessage.Parse(Assert.Single(transport.Sent).Packet);
        Assert.Equal(4, msg.Answers.Count); // PTR + SRV + TXT + one A per local address

        var srv = Assert.IsType<SrvData>(msg.Answers.First(r => r.Type == DnsType.Srv).Data);
        Assert.Equal(51000, srv.Port);
        Assert.Equal("winplay-test.local", srv.Target);
        Assert.Equal("iTunes_Ctrl_AABB._dacp._tcp.local",
            Assert.IsType<PtrData>(msg.Answers.First(r => r.Type == DnsType.Ptr).Data).Target);
        Assert.Equal(IPAddress.Parse("192.168.1.162"),
            Assert.IsType<IPAddress>(msg.Answers.First(r => r.Type == DnsType.A).Data));
    }

    [Fact]
    public void A_Matching_Query_Is_Answered()
    {
        var transport = new FakeTransport();
        using var advertiser = NewAdvertiser(transport);
        advertiser.Start();
        transport.Sent.Clear();

        transport.Deliver(Query("_dacp._tcp.local", DnsType.Ptr), new IPEndPoint(IPAddress.Parse("192.168.1.3"), 5353));

        var (packet, to) = Assert.Single(transport.Sent);
        Assert.Null(to); // QM question → multicast answer
        Assert.Contains(DnsMessage.Parse(packet).Answers, r => r.Type == DnsType.Srv);
    }

    [Fact]
    public void A_Unicast_Question_Is_Answered_Directly_To_The_Asker()
    {
        var transport = new FakeTransport();
        using var advertiser = NewAdvertiser(transport);
        advertiser.Start();
        transport.Sent.Clear();

        var from = new IPEndPoint(IPAddress.Parse("192.168.1.3"), 5353);
        transport.Deliver(Query("_dacp._tcp.local", DnsType.Ptr, unicast: true), from);

        Assert.Equal(from, Assert.Single(transport.Sent).To);
    }

    [Fact]
    public void Unrelated_Queries_And_Responses_Are_Ignored()
    {
        var transport = new FakeTransport();
        using var advertiser = NewAdvertiser(transport);
        advertiser.Start();
        transport.Sent.Clear();

        var from = new IPEndPoint(IPAddress.Parse("192.168.1.3"), 5353);
        transport.Deliver(Query("_airplay._tcp.local", DnsType.Ptr), from);

        var response = Query("_dacp._tcp.local", DnsType.Ptr);
        response.Flags = 0x8400; // a response, not a question — must never be answered
        transport.Deliver(response, from);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Repeated_Multicast_Queries_Are_Rate_Limited_To_One_Answer_Per_Second()
    {
        // RFC 6762 §6 — five HomePods polling _dacp must not turn us into a multicast storm
        // on the same Wi-Fi airtime the audio streams use.
        var transport = new FakeTransport();
        using var advertiser = NewAdvertiser(transport);
        advertiser.Start();
        transport.Sent.Clear();

        var from = new IPEndPoint(IPAddress.Parse("192.168.1.3"), 5353);
        for (int i = 0; i < 5; i++)
            transport.Deliver(Query("_dacp._tcp.local", DnsType.Ptr), from);

        Assert.Single(transport.Sent);
    }

    [Fact]
    public void A_Query_That_Already_Knows_Us_Is_Suppressed()
    {
        // RFC 6762 §7.1 known-answer suppression: the asker includes records it has cached;
        // if ours is there with at least half its TTL, answering again is pure noise.
        var transport = new FakeTransport();
        using var advertiser = NewAdvertiser(transport);
        advertiser.Start();
        transport.Sent.Clear();

        var query = Query("_dacp._tcp.local", DnsType.Ptr);
        query.Answers.Add(new DnsResourceRecord
        {
            Name = "_dacp._tcp.local",
            Type = DnsType.Ptr,
            Class = 0x0001,
            Ttl = DnsRecordWriter.SharedTtlSeconds, // fresh
            Data = new PtrData("iTunes_Ctrl_AABB._dacp._tcp.local"),
        });
        transport.Deliver(query, new IPEndPoint(IPAddress.Parse("192.168.1.3"), 5353));

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void A_Stale_Known_Answer_Does_Not_Suppress()
    {
        var transport = new FakeTransport();
        using var advertiser = NewAdvertiser(transport);
        advertiser.Start();
        transport.Sent.Clear();

        var query = Query("_dacp._tcp.local", DnsType.Ptr);
        query.Answers.Add(new DnsResourceRecord
        {
            Name = "_dacp._tcp.local",
            Type = DnsType.Ptr,
            Class = 0x0001,
            Ttl = 10, // nearly expired — the asker needs a refresh
            Data = new PtrData("iTunes_Ctrl_AABB._dacp._tcp.local"),
        });
        transport.Deliver(query, new IPEndPoint(IPAddress.Parse("192.168.1.3"), 5353));

        Assert.Single(transport.Sent);
    }

    [Fact]
    public void Dispose_Sends_A_Goodbye_With_Ttl_Zero()
    {
        var transport = new FakeTransport();
        var advertiser = NewAdvertiser(transport);
        advertiser.Start();
        transport.Sent.Clear();

        advertiser.Dispose();

        var msg = DnsMessage.Parse(Assert.Single(transport.Sent).Packet);
        Assert.All(msg.Answers, r => Assert.Equal(0u, r.Ttl));
    }
}
