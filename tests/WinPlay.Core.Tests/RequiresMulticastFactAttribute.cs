// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using WinPlay.Core.Dns;
using WinPlay.Core.Mdns;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself unless this machine can actually run an mDNS
/// loopback exchange.
///
/// <para>A few tests exercise the real receive loop over a real socket, because the property they
/// pin — that nothing short of disposal can stop it — belongs to the loop as it actually runs; a
/// fake transport would prove nothing about it. That makes them dependent on the host, and a
/// hosted CI runner is not guaranteed to oblige: it may have no multicast-capable interface, the
/// firewall may drop inbound UDP to the test host, or something else may already own port
/// 5353.</para>
///
/// <para>The check is a PROBE, not a proxy. An earlier version asked "is there a multicast-capable
/// interface?", which a runner answers yes to while still dropping the datagram at the firewall —
/// so the tests ran and failed anyway, reporting a broken product when the truth was that the
/// environment could not host the test. This sends a datagram and waits for it, which is exactly
/// what the tests need; if it arrives, they run, and any failure after that is a real one.</para>
/// </summary>
public sealed class RequiresMulticastFactAttribute : FactAttribute
{
    public RequiresMulticastFactAttribute()
    {
        if (!Probe.Value)
            Skip = "this machine cannot receive an mDNS loopback datagram (no multicast interface, "
                   + "a firewall, or port 5353 is taken)";
    }

    /// <summary>Run once per test process — it costs a socket and up to a second.</summary>
    private static readonly Lazy<bool> Probe = new(CanExchangeLoopbackDatagram, isThreadSafe: true);

    private static bool CanExchangeLoopbackDatagram()
    {
        try
        {
            using var client = new MdnsClient();
            int received = 0;
            client.MessageReceived += (_, _) => Interlocked.Increment(ref received);
            client.Start();

            // A minimal well-formed query: enough to parse, and harmless on a real network.
            byte[] probe = DnsQueryWriter.BuildQuery([("_winplay-probe._tcp.local", DnsType.Ptr, false)]);
            // To the group, not loopback: port 5353 is shared, and a unicast datagram would be
            // handed to only one of the sockets bound to it.
            using (var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                sender.SendTo(probe, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
            }

            for (int i = 0; i < 100 && Volatile.Read(ref received) == 0; i++)
                Thread.Sleep(10);
            return Volatile.Read(ref received) > 0;
        }
        catch (Exception)
        {
            return false;   // no transport at all on this host
        }
    }
}
