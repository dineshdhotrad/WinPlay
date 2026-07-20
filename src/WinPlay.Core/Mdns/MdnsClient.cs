// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinPlay.Core.Dns;

namespace WinPlay.Core.Mdns;

/// <summary>
/// Bundled mDNS transport (IPv4): binds 0.0.0.0:5353 with address reuse (coexists with the
/// Windows mDNS responder), joins 224.0.0.251 on every eligible interface, sends QM
/// (multicast-response) queries, and raises an event per parsed message.
///
/// QM rather than QU questions are used deliberately: with several sockets sharing port
/// 5353 on Windows, unicast responses may be delivered to another process's socket,
/// whereas multicast responses reach every group member.
/// </summary>
public sealed class MdnsClient : IMdnsTransport
{
    public static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    public const int MdnsPort = 5353;

    private readonly Socket _socket;
    private readonly List<int> _interfaceIndexes = [];
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private bool _disposed;

    public event Action<DnsMessage, IPEndPoint>? MessageReceived;
    public event Action<Exception>? ReceiveError;

    public MdnsClient()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.ExclusiveAddressUse = false;
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        _socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

        foreach (var (_, index) in EnumerateEligibleInterfaces())
        {
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MdnsGroup, index));
                _interfaceIndexes.Add(index);
            }
            catch (SocketException)
            {
                // Interface refused the join (e.g. transient/virtual adapter) — skip it.
            }
        }
        if (_interfaceIndexes.Count == 0)
            throw new InvalidOperationException("No network interface accepted the mDNS multicast join.");
    }

    public void Start()
    {
        _receiveLoop ??= Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Sends one query datagram out of every joined interface.</summary>
    public void Query(IReadOnlyList<(string Name, DnsType Type, bool UnicastResponse)> questions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] packet = DnsQueryWriter.BuildQuery(questions);
        var dest = new IPEndPoint(MdnsGroup, MdnsPort);
        foreach (int index in _interfaceIndexes)
        {
            try
            {
                // IP_MULTICAST_IF takes the interface index in network byte order when < 2^24.
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    IPAddress.HostToNetworkOrder(index));
                _socket.SendTo(packet, dest);
            }
            catch (SocketException)
            {
                // Interface went away mid-session; keep trying the others.
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[9000];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                ReceiveError?.Invoke(ex);
                continue;
            }

            try
            {
                var msg = DnsMessage.Parse(buffer.AsSpan(0, result.ReceivedBytes));
                MessageReceived?.Invoke(msg, (IPEndPoint)result.RemoteEndPoint);
            }
            catch (FormatException)
            {
                // Malformed or non-DNS datagram on 5353 — ignore.
            }
        }
    }

    private static IEnumerable<(IPAddress Address, int Index)> EnumerateEligibleInterfaces()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            int index;
            try { index = props.GetIPv4Properties()?.Index ?? -1; }
            catch (NetworkInformationException) { continue; }
            if (index < 0) continue;

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return (ua.Address, index);
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
    }
}
