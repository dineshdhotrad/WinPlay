// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using WinPlay.Core.Dns;
using WinPlay.Core.Mdns;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// The mDNS receive loop is the single reader for every packet WinPlay sees — browsing, and the
/// responses its own advertiser depends on. If it ever stops, device discovery and the DACP
/// endpoint both go dark for the rest of the session, silently, and every symptom the user
/// notices points somewhere else. These tests pin that it cannot be stopped by anything short of
/// disposal.
///
/// <para>They use the real socket on the real loopback interface rather than a fake, because the
/// property under test is a property of the loop as it actually runs — a fake transport would
/// prove nothing about the loop that matters. That dependency is why they carry
/// <see cref="RequiresMulticastFactAttribute"/>: a machine with no multicast-capable interface
/// cannot construct an MdnsClient at all, and reporting that as a product failure would be
/// wrong.</para>
/// </summary>
public class MdnsClientResilienceTests
{
    /// <summary>
    /// Sends one datagram to the mDNS multicast group, exactly as a device on the LAN does.
    ///
    /// <para>To the GROUP, deliberately, not to loopback. Port 5353 is shared — every mDNS
    /// listener on the machine binds it with SO_REUSEADDR, and Bonjour is commonly one of them —
    /// and Windows hands a UNICAST datagram to only one of those sockets. Addressing loopback
    /// therefore delivered to whichever listener happened to win, so the test passed or failed
    /// depending on what else was installed. Multicast is delivered to every joined socket, which
    /// is both deterministic and what the code under test is actually built to receive.</para>
    /// </summary>
    private static void SendToMdnsGroup(byte[] payload)
    {
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        sender.SendTo(payload, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
    }

    /// <summary>A minimal, well-formed DNS response carrying one PTR answer.</summary>
    private static byte[] ValidResponse()
    {
        List<byte> buf = [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0];   // header: response, ANCOUNT=1
        void Name(IEnumerable<string> labels)
        {
            foreach (string l in labels) { buf.Add((byte)l.Length); buf.AddRange(System.Text.Encoding.ASCII.GetBytes(l)); }
            buf.Add(0);
        }
        Name(["_airplay", "_tcp", "local"]);
        buf.AddRange([0, (byte)DnsType.Ptr, 0, 1, 0, 0, 0x11, 0x94]);  // TYPE, CLASS, TTL 4500
        var rdata = new List<byte>();
        foreach (string l in new[] { "Probe", "_airplay", "_tcp", "local" })
        {
            rdata.Add((byte)l.Length);
            rdata.AddRange(System.Text.Encoding.ASCII.GetBytes(l));
        }
        rdata.Add(0);
        buf.AddRange([(byte)(rdata.Count >> 8), (byte)rdata.Count]);
        buf.AddRange(rdata);
        return [.. buf];
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
            await Task.Delay(5);
        return condition();
    }

    [RequiresMulticastFact]
    public async Task A_Throwing_Subscriber_Does_Not_Kill_Reception()
    {
        // The exact regression: subscribers do real work — correlating records, expiring caches,
        // answering queries — and one unexpected throw used to escape the loop and take ALL mDNS
        // reception down with it, permanently and without a word.
        using var client = new MdnsClient();
        int delivered = 0;
        var errors = new List<Exception>();

        client.ReceiveError += ex => { lock (errors) errors.Add(ex); };
        client.MessageReceived += (_, _) =>
        {
            Interlocked.Increment(ref delivered);
            throw new InvalidOperationException("subscriber blew up");
        };
        client.Start();

        SendToMdnsGroup(ValidResponse());
        Assert.True(await WaitForAsync(() => Volatile.Read(ref delivered) >= 1), "first packet never arrived");

        // The loop must still be reading. This is the assertion that matters: delivery #2 only
        // happens if the throw from delivery #1 did not end the loop.
        SendToMdnsGroup(ValidResponse());
        Assert.True(await WaitForAsync(() => Volatile.Read(ref delivered) >= 2), "reception stopped after a subscriber threw");

        lock (errors)
            Assert.Contains(errors, e => e is InvalidOperationException);
    }

    [RequiresMulticastFact]
    public async Task A_Malformed_Datagram_Does_Not_Kill_Reception()
    {
        // Port 5353 carries whatever anyone on the network decides to send, including packets
        // that are not DNS at all.
        using var client = new MdnsClient();
        int delivered = 0;
        client.MessageReceived += (_, _) => Interlocked.Increment(ref delivered);
        client.Start();

        SendToMdnsGroup([0xFF, 0xFE, 0xFD]);                       // truncated header
        SendToMdnsGroup([.. Enumerable.Repeat((byte)0xAA, 400)]);  // structured garbage
        SendToMdnsGroup(ValidResponse());

        Assert.True(await WaitForAsync(() => Volatile.Read(ref delivered) >= 1),
            "a malformed datagram stopped the loop before the valid one arrived");
    }

    [RequiresMulticastFact]
    public async Task Disposing_Stops_The_Loop_Without_Faulting()
    {
        // Shutdown and RestartDiscovery both dispose the transport while the loop is parked in a
        // receive. That has to be an orderly stop, not an unobserved exception.
        var client = new MdnsClient();
        client.Start();
        await Task.Delay(50);
        client.Dispose();

        // Nothing to assert beyond "no exception escaped"; an unobserved faulted task would be
        // torn down on the finalizer thread and never reach this test, so give it the chance.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [RequiresMulticastFact]
    public async Task A_Throwing_Subscriber_Does_Not_Starve_The_Ones_After_It()
    {
        // A multicast delegate stops at the first target that throws. Both the browser and the
        // DACP advertiser subscribe here, so without per-subscriber isolation one of them failing
        // silently stopped the other from ever seeing that message — and which one survived
        // depended purely on registration order, which nothing enforces.
        using var client = new MdnsClient();
        int second = 0;
        client.MessageReceived += (_, _) => throw new InvalidOperationException("first subscriber");
        client.MessageReceived += (_, _) => Interlocked.Increment(ref second);
        client.Start();

        SendToMdnsGroup(ValidResponse());

        Assert.True(await WaitForAsync(() => Volatile.Read(ref second) >= 1),
            "the second subscriber never ran because the first one threw");
    }
}
