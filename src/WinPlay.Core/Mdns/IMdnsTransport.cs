// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Dns;

namespace WinPlay.Core.Mdns;

/// <summary>mDNS send/receive abstraction — <see cref="MdnsClient"/> in production, fakes in tests.</summary>
public interface IMdnsTransport : IDisposable
{
    event Action<DnsMessage, IPEndPoint>? MessageReceived;

    void Start();

    void Query(IReadOnlyList<(string Name, DnsType Type, bool UnicastResponse)> questions);

    /// <summary>
    /// Sends an already-serialised DNS message. <paramref name="unicastTo"/> targets a single
    /// peer (answering a QU question); when null the message is multicast to every joined
    /// interface. Used by <see cref="MdnsServiceAdvertiser"/> to publish WinPlay's own services.
    /// </summary>
    void Send(byte[] packet, IPEndPoint? unicastTo = null);

    /// <summary>IPv4 addresses of the interfaces this transport is active on (for A records).</summary>
    IReadOnlyList<IPAddress> LocalAddresses { get; }
}
