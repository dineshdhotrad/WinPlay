// SPDX-License-Identifier: GPL-3.0-or-later
// Ported from owntone/libairptp (MIT); see THIRD_PARTY_NOTICES.md.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WinPlay.Core.Ptp;

/// <summary>
/// Monotonic nanosecond clock shared by the PTP master and all PTP-timed sessions.
/// The epoch is arbitrary (process start) — receivers follow the master clock
/// relatively, but every timestamp we emit (PTP Sync/Follow_Up, 0xD7 time announce)
/// MUST come from this one clock or receivers see two contradicting timelines.
/// </summary>
public static class MonotonicClock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();

    public static ulong NowNanoseconds
    {
        get
        {
            long ticks = Sw.ElapsedTicks;
            long freq = Stopwatch.Frequency;
            long sec = ticks / freq;
            long nanos = (ticks % freq) * 1_000_000_000L / freq;
            return (ulong)sec * 1_000_000_000UL + (ulong)nanos;
        }
    }

    public static (ulong Seconds, uint Nanoseconds) Now
    {
        get
        {
            ulong ns = NowNanoseconds;
            return (ns / 1_000_000_000UL, (uint)(ns % 1_000_000_000UL));
        }
    }
}

/// <summary>
/// AirPlay-profile PTP (IEEE 1588) grandmaster — the sender is always the clock
/// master; receivers are added as unicast peers and slave to us (they yield BMCA
/// because we announce clockClass 6 / GPS). Ported from owntone's libairptp, which
/// mirrors captured iOS sender traffic:
///   Announce 1 Hz (general :320), Sync+Follow_Up two-step 8 Hz (event :319 /
///   general :320), Signaling 1 Hz, Delay_Req answered with Delay_Resp.
/// Process-wide singleton — one clock serves every session (that is the point of
/// multi-room). Windows has no privileged-port concept, so binding 319/320 only
/// fails if another PTP daemon is running.
/// </summary>
public sealed class PtpMaster : IDisposable
{
    private const int EventPort = 319;
    private const int GeneralPort = 320;
    private static readonly TimeSpan PeerStaleAfter = TimeSpan.FromSeconds(15);

    private static PtpMaster? _instance;
    private static readonly object InstanceLock = new();

    /// <summary>
    /// Process-wide master, created on first use. Deliberately not Lazy&lt;T&gt; — a
    /// failed port bind (e.g. another PTP daemon exiting soon) must be retryable on
    /// the next session, not cached forever.
    /// </summary>
    public static PtpMaster Shared
    {
        get
        {
            lock (InstanceLock)
            {
                return _instance ??= new PtpMaster();
            }
        }
    }

    /// <summary>Random non-EUI-64 clock identity (IEEE 1588-2019 §7.5.2.2.3, owntone style).</summary>
    public ulong ClockId { get; }

    /// <summary>UUID advertised as timingPeerInfo.ID (iOS sends one; origin unknown).</summary>
    public string ClockUuid { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    private sealed class PeerState
    {
        public int RefCount;
        public DateTime LastSeen;
    }

    private readonly Socket _eventSocket;
    private readonly Socket _generalSocket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<IPAddress, PeerState> _peers = [];
    private readonly object _peersLock = new();
    private ushort _announceSeq;
    private ushort _signalingSeq;
    private ushort _syncSeq;
    private long _delayReqsAnswered;

    public event Action<string>? Diagnostic;

    public long DelayRequestsAnswered => Interlocked.Read(ref _delayReqsAnswered);

    private PtpMaster()
    {
        ClockId = (BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8))
                   & 0x0000FFFFFFFFFFFFUL) | 0xFFFF000000000000UL;
        try
        {
            _eventSocket = BindUdp(EventPort);
            _generalSocket = BindUdp(GeneralPort);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"cannot bind PTP ports {EventPort}/{GeneralPort} (another PTP service running?): {ex.SocketErrorCode}", ex);
        }

        // Every one of these is supervised. This clock is the timing backbone for buffered audio
        // and for keeping a stereo pair or a multi-room group in step; if one of these loops dies
        // there is no sound of it failing, only audio that gradually drifts apart or stops, and
        // nothing anywhere to say why.
        _ = Task.Run(() => SuperviseAsync("ptp event receive", ct => ReceiveLoopAsync(_eventSocket, ct), _cts.Token));
        _ = Task.Run(() => SuperviseAsync("ptp general receive", ct => ReceiveLoopAsync(_generalSocket, ct), _cts.Token));
        _ = Task.Run(() => SuperviseAsync("ptp announce", AnnounceLoopAsync, _cts.Token));
        _ = Task.Run(() => SuperviseAsync("ptp sync", SyncLoopAsync, _cts.Token));
    }

    /// <summary>Reference-counted: concurrent sessions may share a peer address.</summary>
    public void AddPeer(IPAddress address)
    {
        lock (_peersLock)
        {
            if (_peers.TryGetValue(address, out var state))
            {
                state.RefCount++;
                state.LastSeen = DateTime.UtcNow;
            }
            else
            {
                _peers[address] = new PeerState { RefCount = 1, LastSeen = DateTime.UtcNow };
            }
        }
        Diagnostic?.Invoke($"ptp: peer {address} added (clock 0x{ClockId:X16})");
    }

    public void RemovePeer(IPAddress address)
    {
        lock (_peersLock)
        {
            if (!_peers.TryGetValue(address, out var state)) return;
            if (--state.RefCount > 0) return;
            _peers.Remove(address);
        }
        Diagnostic?.Invoke($"ptp: peer {address} removed");
    }

    // ------------------------------------------------------------ send loops

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SendToPeers(_generalSocket, GeneralPort, BuildAnnounce(ClockId, _announceSeq++));
            SendToPeers(_generalSocket, GeneralPort, BuildSignaling(ClockId, _signalingSeq++));
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Two-step: Sync carries a zero timestamp, the Follow_Up carries the
            // precise time the Sync left (owntone samples it between the sends).
            ushort seq = _syncSeq++;
            SendToPeers(_eventSocket, EventPort, BuildSync(ClockId, seq));
            var ts = MonotonicClock.Now;
            SendToPeers(_generalSocket, GeneralPort, BuildFollowUp(ClockId, seq, ts));
            try { await Task.Delay(125, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SendToPeers(Socket socket, int port, byte[] message)
    {
        DateTime now = DateTime.UtcNow;
        List<IPAddress> targets;
        lock (_peersLock)
        {
            targets = _peers
                .Where(p => now - p.Value.LastSeen <= PeerStaleAfter) // reactivated on next receive
                .Select(p => p.Key)
                .ToList();
        }
        foreach (var address in targets)
        {
            try
            {
                socket.SendTo(message, new IPEndPoint(address, port));
            }
            catch (SocketException) { }
        }
    }

    // ------------------------------------------------------------ receive

    /// <summary>
    /// Runs a clock loop so that its failure is reported instead of vanishing. A bare
    /// <c>Task.Run</c> whose task nobody awaits discards the exception that ended it, which for
    /// this class means the grandmaster silently stops serving time.
    /// </summary>
    private async Task SuperviseAsync(string what, Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        try { await loop(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }   // torn down underneath us
        catch (Exception ex) { Diagnostic?.Invoke($"{what} stopped: {ex.Message}"); }
    }

    /// <summary>Longest pause between retries after repeated socket failures.</summary>
    private static readonly TimeSpan MaxReceiveBackoff = TimeSpan.FromSeconds(2);

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        byte[] buf = new byte[512];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false);
                backoff = TimeSpan.Zero;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                // Retrying immediately was a busy-wait whenever the socket entered a persistently
                // failing state — the bound interface going away on a network change, which is
                // precisely when it happens. A spinning core is not something a background tray
                // app can afford, least of all while it is also pumping audio.
                backoff = backoff == TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(20)
                    : (backoff < MaxReceiveBackoff ? backoff + backoff : MaxReceiveBackoff);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            if (r.ReceivedBytes < 34) continue;

            var from = ((IPEndPoint)r.RemoteEndPoint).Address;
            lock (_peersLock)
            {
                if (_peers.TryGetValue(from, out var state))
                    state.LastSeen = DateTime.UtcNow;
            }

            // Answering one peer must never end the loop. These sends go straight back to a
            // device on the LAN, so they throw for entirely ordinary reasons — a speaker
            // switched off mid-exchange, a Wi-Fi roam, a route that vanished. Unguarded, the
            // first such throw killed this receive loop for good, and with it every future
            // Delay_Req from EVERY receiver: the remaining speakers could no longer measure
            // their offset to our clock, so they drifted apart with nothing reported.
            try
            {
                switch (buf[0] & 0x0F)
                {
                    case 0x01: // Delay_Req → Delay_Resp on the general port
                        if (r.ReceivedBytes < 44) break;
                        _generalSocket.SendTo(BuildDelayResp(ClockId, buf.AsSpan(0, r.ReceivedBytes), MonotonicClock.Now),
                            new IPEndPoint(from, GeneralPort));
                        if (Interlocked.Increment(ref _delayReqsAnswered) == 1)
                            Diagnostic?.Invoke($"ptp: first Delay_Req from {from} — receiver is slaving to our clock");
                        break;
                    case 0x02: // PDelay_Req → PDelay_Resp (event) + PDelay_Resp_Follow_Up (general)
                        if (r.ReceivedBytes < 44) break;
                        _eventSocket.SendTo(BuildPDelayResp(ClockId, 0x03, buf.AsSpan(0, r.ReceivedBytes)),
                            new IPEndPoint(from, EventPort));
                        _generalSocket.SendTo(BuildPDelayResp(ClockId, 0x0A, buf.AsSpan(0, r.ReceivedBytes)),
                            new IPEndPoint(from, GeneralPort));
                        break;
                    // Announce/Sync/Follow_Up/Signaling from others: ignored. We claim
                    // clockClass 6 (GPS) so every AirPlay device yields BMCA to us.
                }
            }
            catch (SocketException) { }        // that peer is unreachable this instant; others are not
            catch (ObjectDisposedException) { return; }
        }
    }

    // ------------------------------------------------------------ message builders

    private const ushort FlagsUnicastTimescale = 0x0408;         // iOS: Announce, Follow_Up, Signaling
    private const ushort FlagsUnicastTimescaleTwoStep = 0x0608;  // iOS: Sync, Delay_Resp

    private static byte[] BuildHeader(ulong clockId, int totalLength, byte type, ushort flags,
        ushort sequence, sbyte logInterval, byte control = 0)
    {
        byte[] m = new byte[totalLength];
        m[0] = (byte)(type | 0x10); // transportSpecific=1 nibble, expected by nqptp/Apple
        m[1] = 0x02;                // PTPv2
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)totalLength);
        // domain 0, reserved, correctionField 0, reserved already zero
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(20), clockId);
        m[28] = 0x80; m[29] = 0x05; // port number, same as iOS
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(30), sequence);
        m[32] = control;
        m[33] = unchecked((byte)logInterval);
        return m;
    }

    private static void WriteTimestamp(Span<byte> dst, ulong seconds, uint nanoseconds)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)(seconds >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(dst[2..], (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(dst[6..], nanoseconds);
    }

    internal static byte[] BuildAnnounce(ulong clockId, ushort sequence)
    {
        byte[] m = BuildHeader(clockId, 76, 0x0B, FlagsUnicastTimescale, sequence, 0);
        // originTimestamp zero (iOS does the same); currentUtcOffset 0
        m[47] = 128; // grandmasterPriority1
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(48), 0x0621436A); // class 6 (GPS), acc 0x21 (100ns), var 0x436A — Apple's values
        m[52] = 128; // grandmasterPriority2
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(53), clockId);
        // stepsRemoved 0
        m[63] = 0x20; // timeSource GPS
        // PATH_TRACE TLV with the clock ID (Apple quirk)
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(64), 0x0008);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(66), 8);
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(68), clockId);
        return m;
    }

    internal static byte[] BuildSync(ulong clockId, ushort sequence) =>
        BuildHeader(clockId, 44, 0x00, FlagsUnicastTimescaleTwoStep, sequence, -3);

    internal static byte[] BuildFollowUp(ulong clockId, ushort sequence, (ulong Seconds, uint Nanoseconds) ts)
    {
        byte[] m = BuildHeader(clockId, 96, 0x08, FlagsUnicastTimescale, sequence, -3);
        WriteTimestamp(m.AsSpan(34), ts.Seconds, ts.Nanoseconds);
        // TLV 1: IEEE 802.1 Follow_Up information (cumulativeScaledRateOffset etc. all
        // zero — we don't know our rate error and iOS receivers don't seem to care)
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(44), 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(46), 28);
        m[48] = 0x00; m[49] = 0x80; m[50] = 0xC2; // IEEE org
        m[51] = 0x00; m[52] = 0x00; m[53] = 0x01; // Follow_Up info subtype
        // TLV 2: Apple TLV carrying the clock ID again
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(76), 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(78), 16);
        m[80] = 0x00; m[81] = 0x0D; m[82] = 0x93; // Apple org
        m[83] = 0x00; m[84] = 0x00; m[85] = 0x04; // clock-ID subtype
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(86), clockId);
        return m;
    }

    internal static byte[] BuildSignaling(ulong clockId, ushort sequence)
    {
        byte[] m = BuildHeader(clockId, 106, 0x0C, FlagsUnicastTimescale, sequence, -128, control: 0x05);
        // targetPortIdentity zero. Two Apple TLVs with a fixed unknown value 00 00 03 01
        // (subtypes 1 and 5) — captured from iOS, meaning unknown, sent for parity.
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(44), 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(46), 22);
        m[48] = 0x00; m[49] = 0x0D; m[50] = 0x93;
        m[51] = 0x00; m[52] = 0x00; m[53] = 0x01;
        m[54] = 0x00; m[55] = 0x00; m[56] = 0x03; m[57] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(70), 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(72), 32);
        m[74] = 0x00; m[75] = 0x0D; m[76] = 0x93;
        m[77] = 0x00; m[78] = 0x00; m[79] = 0x05;
        m[80] = 0x00; m[81] = 0x00; m[82] = 0x03; m[83] = 0x01;
        return m;
    }

    internal static byte[] BuildDelayResp(ulong clockId, ReadOnlySpan<byte> request,
        (ulong Seconds, uint Nanoseconds) receiveTime)
    {
        byte[] m = BuildHeader(clockId, 54, 0x09, FlagsUnicastTimescaleTwoStep,
            BinaryPrimitives.ReadUInt16BigEndian(request[30..]), -3);
        WriteTimestamp(m.AsSpan(34), receiveTime.Seconds, receiveTime.Nanoseconds);
        request[20..30].CopyTo(m.AsSpan(44)); // requestingPortIdentity = requester's identity
        return m;
    }

    private static byte[] BuildPDelayResp(ulong clockId, byte type, ReadOnlySpan<byte> request)
    {
        ushort flags = type == 0x03 ? FlagsUnicastTimescaleTwoStep : FlagsUnicastTimescale;
        byte[] m = BuildHeader(clockId, 54, type, flags,
            BinaryPrimitives.ReadUInt16BigEndian(request[30..]), -3);
        var ts = MonotonicClock.Now;
        WriteTimestamp(m.AsSpan(34), ts.Seconds, ts.Nanoseconds);
        request[20..30].CopyTo(m.AsSpan(44));
        return m;
    }

    private static Socket BindUdp(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Any, port));
        return s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _eventSocket.Dispose();
        _generalSocket.Dispose();
        _cts.Dispose();
    }
}
