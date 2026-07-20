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
}
