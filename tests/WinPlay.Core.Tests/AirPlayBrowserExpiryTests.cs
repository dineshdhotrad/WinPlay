// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Microsoft.Extensions.Time.Testing;
using WinPlay.Core.Discovery;
using WinPlay.Core.Dns;
using WinPlay.Core.Mdns;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Pins the browse-cache expiry sweep. Two failure modes matter here and they pull in opposite
/// directions: never expiring leaves unplugged receivers in the picker until the app restarts,
/// and expiring too eagerly deletes a receiver that is sitting right there and answering. Both
/// are tested, at exact round boundaries, on a fake clock — a sweep that only misbehaves on the
/// fifth round is not something a wall-clock test would catch twice in a row.
/// </summary>
public class AirPlayBrowserExpiryTests
{
    private static readonly TimeSpan Round = TimeSpan.FromSeconds(16);

    /// <summary>
    /// The production budget, referenced rather than copied: these tests assert behaviour at the
    /// exact round it changes, so a hardcoded number here would silently stop testing the boundary
    /// the moment the constant is retuned.
    /// </summary>
    private const int Budget = AirPlayBrowser.MaxMissedRounds;

    private sealed class FakeTransport : IMdnsTransport
    {
        public event Action<DnsMessage, IPEndPoint>? MessageReceived;
        public int QueryCount;

        public IReadOnlyList<IPAddress> LocalAddresses => [IPAddress.Loopback];
        public void Start() { }
        public void Query(IReadOnlyList<(string Name, DnsType Type, bool UnicastResponse)> q) => Interlocked.Increment(ref QueryCount);
        public void Send(byte[] packet, IPEndPoint? unicastTo = null) { }
        public void Raise(DnsMessage msg) => MessageReceived?.Invoke(msg, new IPEndPoint(IPAddress.Loopback, 5353));
        public void Dispose() { }
    }

    /// <summary>A complete, connectable instance — PTR + SRV + TXT + A, as a real HomePod sends.</summary>
    private static DnsMessage Announcement(string name, string id, string host, string ip)
    {
        var msg = new DnsMessage { Flags = 0x8400 };
        msg.Answers.AddRange(
        [
            new DnsResourceRecord { Name = AirPlayBrowser.AirPlayService, Type = DnsType.Ptr, Class = 1, Ttl = 4500, Data = new PtrData($"{name}.{AirPlayBrowser.AirPlayService}") },
            new DnsResourceRecord { Name = $"{name}.{AirPlayBrowser.AirPlayService}", Type = DnsType.Srv, Class = 0x8001, Ttl = 120, Data = new SrvData(0, 0, 7000, host) },
            new DnsResourceRecord { Name = $"{name}.{AirPlayBrowser.AirPlayService}", Type = DnsType.Txt, Class = 0x8001, Ttl = 4500, Data = new TxtData(new Dictionary<string, string> { ["deviceid"] = id, ["model"] = "AudioAccessory5,1" }) },
            new DnsResourceRecord { Name = host, Type = DnsType.A, Class = 0x8001, Ttl = 120, Data = IPAddress.Parse(ip) },
        ]);
        return msg;
    }

    /// <summary>
    /// Starts the browser and waits until its loop is actually running.
    ///
    /// <para><see cref="AirPlayBrowser.Start"/> spins the loop up on the thread pool, and the loop
    /// sends its first query IMMEDIATELY rather than after a delay — that query is the start of
    /// browsing, not the end of a round. Without waiting for it, the first
    /// <see cref="AdvanceRoundAsync"/> can mistake it for its own round, and then every
    /// round-boundary assertion in the file is off by one — but only when the thread pool is busy
    /// enough for the race to land that way, which is exactly the kind of test failure that gets
    /// dismissed as flaky and re-run.</para>
    /// </summary>
    private static async Task StartAsync(AirPlayBrowser browser, FakeTransport transport)
    {
        browser.Start();
        for (int i = 0; i < 400 && Volatile.Read(ref transport.QueryCount) == 0; i++)
            await Task.Delay(5);
        Assert.True(Volatile.Read(ref transport.QueryCount) > 0, "the browse loop never started");
    }

    /// <summary>
    /// Drives the browser through exactly one browse round.
    ///
    /// <para>The loop runs on the thread pool, so between finishing a round and awaiting the next
    /// delay there is a window in which it has no timer registered. Advancing once into that
    /// window would move the clock past nothing at all and silently lose the round, which makes a
    /// test that counts rounds intermittently wrong — worse than no test. So the clock is nudged
    /// until the round is observed to happen: the query the loop sends at the top of each round is
    /// the signal. Advances that land while the loop is mid-round have nothing scheduled to fire
    /// and cannot double-count.</para>
    /// </summary>
    private static async Task AdvanceRoundAsync(FakeTimeProvider time, FakeTransport transport)
    {
        int before = Volatile.Read(ref transport.QueryCount);
        for (int i = 0; i < 400 && Volatile.Read(ref transport.QueryCount) == before; i++)
        {
            time.Advance(Round);
            await Task.Delay(5);
        }
        Assert.True(Volatile.Read(ref transport.QueryCount) > before, "the browse loop stopped querying");
    }

    [Fact]
    public async Task A_Receiver_That_Keeps_Answering_Is_Never_Expired()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        using var browser = new AirPlayBrowser(transport, time);
        await StartAsync(browser, transport);

        transport.Raise(Announcement("Kitchen", "AA:BB:CC:DD:EE:01", "kitchen.local", "192.168.1.51"));
        Assert.Single(browser.Snapshot());

        // Far beyond any plausible budget: if the sweep were time-based or off by one, this fails.
        for (int i = 0; i < 40; i++)
        {
            await AdvanceRoundAsync(time, transport);
            transport.Raise(Announcement("Kitchen", "AA:BB:CC:DD:EE:01", "kitchen.local", "192.168.1.51"));
            Assert.Single(browser.Snapshot());
        }
    }

    [Fact]
    public async Task A_Receiver_That_Goes_Silent_Is_Expired_And_Announced()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        using var browser = new AirPlayBrowser(transport, time);

        List<IReadOnlyList<AirPlayDevice>> snapshots = [];
        browser.DevicesChanged += s => { lock (snapshots) snapshots.Add(s); };
        await StartAsync(browser, transport);

        transport.Raise(Announcement("Patio", "AA:BB:CC:DD:EE:02", "patio.local", "192.168.1.52"));
        Assert.Single(browser.Snapshot());

        // Silence. It must survive the whole slack budget — a run of dropped multicast packets on
        // Wi-Fi must not delete a live speaker — and then go on the round after.
        for (int i = 0; i < Budget; i++)
        {
            await AdvanceRoundAsync(time, transport);
            Assert.True(browser.Snapshot().Count == 1, $"evicted after only {i + 1} silent rounds");
        }

        await AdvanceRoundAsync(time, transport);
        Assert.Empty(browser.Snapshot());

        // The UI only learns about it through the event, so the eviction must raise one.
        lock (snapshots)
            Assert.Contains(snapshots, s => s.Count == 0);
    }

    [Fact]
    public async Task Expiring_One_Receiver_Leaves_The_Others_Alone()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        using var browser = new AirPlayBrowser(transport, time);
        await StartAsync(browser, transport);

        transport.Raise(Announcement("Study", "AA:BB:CC:DD:EE:03", "study.local", "192.168.1.53"));
        transport.Raise(Announcement("Den", "AA:BB:CC:DD:EE:04", "den.local", "192.168.1.54"));
        Assert.Equal(2, browser.Snapshot().Count);

        for (int i = 0; i <= Budget; i++)
        {
            await AdvanceRoundAsync(time, transport);
            transport.Raise(Announcement("Den", "AA:BB:CC:DD:EE:04", "den.local", "192.168.1.54"));
        }

        var device = Assert.Single(browser.Snapshot());
        Assert.Equal("Den", device.Name);
        // The survivor must keep its address: pruning orphaned host records must not take a host
        // that another live instance still points at.
        Assert.Equal(IPAddress.Parse("192.168.1.54"), Assert.Single(device.Addresses));
    }

    [Fact]
    public async Task A_Receiver_Discovered_Late_Gets_A_Full_Budget()
    {
        // Regression guard for the obvious off-by-one: an entry created on round N must be judged
        // from round N, not from round 0, or anything found after the first minute dies instantly.
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        using var browser = new AirPlayBrowser(transport, time);
        await StartAsync(browser, transport);

        for (int i = 0; i < 10; i++)
            await AdvanceRoundAsync(time, transport);

        transport.Raise(Announcement("Garage", "AA:BB:CC:DD:EE:05", "garage.local", "192.168.1.55"));
        Assert.Single(browser.Snapshot());

        for (int i = 0; i < Budget; i++)
        {
            await AdvanceRoundAsync(time, transport);
            Assert.True(browser.Snapshot().Count == 1, $"late arrival evicted after {i + 1} silent rounds");
        }
    }

    [Fact]
    public async Task A_Receiver_That_Comes_Back_Is_Rediscovered()
    {
        // Power-cycling a HomePod must land it back in the picker, with no stale state left over
        // from the entry that was swept.
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        using var browser = new AirPlayBrowser(transport, time);
        await StartAsync(browser, transport);

        transport.Raise(Announcement("Bedroom", "AA:BB:CC:DD:EE:06", "bedroom.local", "192.168.1.56"));
        for (int i = 0; i <= Budget; i++)
            await AdvanceRoundAsync(time, transport);
        Assert.Empty(browser.Snapshot());

        // Back on the network, now on a different address — the old A record must not linger.
        transport.Raise(Announcement("Bedroom", "AA:BB:CC:DD:EE:06", "bedroom.local", "192.168.1.77"));
        var device = Assert.Single(browser.Snapshot());
        Assert.Equal(IPAddress.Parse("192.168.1.77"), Assert.Single(device.Addresses));
    }
}
