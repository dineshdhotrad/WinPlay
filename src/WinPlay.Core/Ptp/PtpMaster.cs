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
/// Announce 1 Hz (general:320), Sync+Follow_Up two-step 8 Hz (event:319 /
/// general:320), Signaling 1 Hz, Delay_Req answered with Delay_Resp.
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

    /// <summary>
    /// Grandmaster clock identity: a stable EUI-64 derived from the primary NIC's MAC
    /// (IEEE 1588-2008 §7.5.2.2.2) — see <see cref="StableClockIdentity"/> for why stability
    /// across launches is load-bearing, not cosmetic.
    /// </summary>
    public ulong ClockId { get; }

    /// <summary>UUID advertised as timingPeerInfo.ID (iOS sends one; origin unknown).</summary>
    public string ClockUuid { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    private sealed class PeerState
    {
        /// <summary>Whether this peer has been reported as slaved, so it is said once each.</summary>
        public bool SlavedLogged;

        public int RefCount;
        public DateTime LastSeen;

        /// <summary>Delay_Reqs answered for THIS peer — the direct measure of its servo activity.</summary>
        public int DelayReqCount;

        /// <summary>Completed (under the peers lock) as <see cref="DelayReqCount"/> passes each target.</summary>
        public readonly List<(int Target, TaskCompletionSource<bool> Tcs)> SettleWaiters = [];
    }

    private readonly Socket _eventSocket;
    private readonly Socket _generalSocket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<IPAddress, PeerState> _peers = [];
    private readonly object _peersLock = new();
    // int + Interlocked, truncated to ushort at use: Announce/Signaling/Sync are sent from the
    // periodic loops AND from the initial burst a connect thread fires for a new peer. A torn
    // ushort++ can hand two different Sync/Follow_Up pairs one sequence number, and a receiver
    // matching Follow_Up to Sync by sequence then computes its offset from the wrong Sync — a
    // silent timing error at the exact moment a speaker joins.
    private int _announceSeq;
    private int _signalingSeq;
    private int _syncSeq;
    private long _delayReqsAnswered;

    public event Action<string>? Diagnostic;

    public long DelayRequestsAnswered => Interlocked.Read(ref _delayReqsAnswered);

    /// <summary>
    /// Completes once <paramref name="peer"/> has sent at least <paramref name="delayReqTarget"/>
    /// Delay_Reqs, or the timeout expires (false). Event-driven — completed by the Delay_Req
    /// handler itself, no polling.
    ///
    /// <para>A receiver introduced to a grandmaster it has never seen slaves quickly but TRUSTS
    /// slowly: it sends Delay_Reqs within milliseconds, yet discards a SETRATEANCHORTIME anchor
    /// that arrives before its servo has converged on the new timeline — silently, with the
    /// session otherwise healthy. Verified on a HomePod mini: an anchor ~1.5 s after first contact
    /// → silence every time; the identical session with the anchor held ~6 s → plays. Grouped
    /// sessions always worked because serial member handshakes delayed the shared anchor past the
    /// convergence window by accident. This wait makes that guarantee explicit and event-driven
    /// instead of an accident of topology.</para>
    /// </summary>
    public Task<bool> WaitForPeerSettleAsync(IPAddress peer, int delayReqTarget, TimeSpan timeout,
        CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        lock (_peersLock)
        {
            if (!_peers.TryGetValue(peer, out var state))
                return Task.FromResult(false);          // unknown peer: nothing to wait for
            if (state.DelayReqCount >= delayReqTarget)
                return Task.FromResult(true);           // already settled
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.SettleWaiters.Add((delayReqTarget, tcs));
        }
        // Timeout/cancel resolve the same task the Delay_Req handler resolves; whichever comes
        // first wins and the rest are no-ops on the completed TCS.
        _ = Task.Delay(timeout, ct).ContinueWith(
            t => tcs.TrySetResult(false), TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>
    /// The grandmaster's clock identity as a MAC-derived EUI-64 (IEEE 1588-2008 §7.5.2.2.2:
    /// MAC-48 with <c>FF FE</c> inserted) — the SAME value on every launch.
    ///
    /// <para>Every Apple device presents one clock identity for its whole life. This was random
    /// per process, so each run of WinPlay appeared to the LAN as a brand-new grandmaster: a
    /// receiver that had slaved to the previous run's clock had to notice that master vanish
    /// (announce timeout), re-elect, and re-converge — per run. Observed consequence on a HomePod
    /// mini: the first buffered session after the receiver rebooted (no prior master state)
    /// rendered fine, while an identical session minutes later — now the receiver's Nth new
    /// master of the evening — stayed silent, because the anchor's
    /// <c>networkTimeTimelineID</c> named a timeline the receiver had not (yet) adopted. A stable
    /// identity makes every WinPlay run the same master RETURNING, which is the situation PTP's
    /// state machines are designed for.</para>
    /// </summary>
    private static ulong StableClockIdentity()
    {
        byte[]? mac = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .Select(n => n.GetPhysicalAddress().GetAddressBytes())
            .FirstOrDefault(b => b.Length == 6);
        // No usable NIC (rare; e.g. everything down): a machine-stable stand-in beats a random
        // one — stability across runs is the property that matters here.
        mac ??= SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName))[..6];

        Span<byte> eui = stackalloc byte[8];
        eui[0] = mac[0]; eui[1] = mac[1]; eui[2] = mac[2];
        eui[3] = 0xFF; eui[4] = 0xFE;
        eui[5] = mac[3]; eui[6] = mac[4]; eui[7] = mac[5];
        return BinaryPrimitives.ReadUInt64BigEndian(eui);
    }

    private PtpMaster()
    {
        ClockId = StableClockIdentity();
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
        bool isNew = false;
        lock (_peersLock)
        {
            if (_peers.TryGetValue(address, out var state))
            {
                state.RefCount++;
                state.LastSeen = DateTime.UtcNow;
            }
            else
            {
                var fresh = new PeerState { RefCount = 1, LastSeen = DateTime.UtcNow };
                // A returning peer resumes from its remembered convergence: the receiver's servo
                // never forgot our (stable) clock, so its settle gate should not pretend it did.
                // The memory is bounded — see WarmPeerMemory — so a receiver that rebooted in the
                // meantime (losing real servo state) eventually re-earns trust from zero.
                if (_warmPeers.TryGetValue(address, out var warm)
                    && Environment.TickCount64 - warm.LastSeenMs < (long)WarmPeerMemory.TotalMilliseconds)
                {
                    fresh.DelayReqCount = warm.DelayReqCount;
                }
                _warmPeers.Remove(address);
                _peers[address] = fresh;
                isNew = true;
            }
        }
        Diagnostic?.Invoke($"ptp: peer {address} added (clock 0x{ClockId:X16})");

        // Announce to a brand-new peer IMMEDIATELY, rather than leaving it to wait for the next
        // scheduled tick.
        //
        // A PTP client discards Sync and Follow_Up that arrive before it has an Announce from that
        // clock — it has not run BMCA and elected us master yet, so the traffic means nothing to
        // it. Announce is only sent once a second, so a peer added just after a tick waited up to
        // a full second while everything we sent it was thrown away.
        //
        // The buffered-audio anchor fires about 350 ms after the peer is added, so on a fast LAN
        // that race was routinely lost — and losing it is silent: every RTSP step still returns
        // 200 OK, the audio channel still connects and still receives frames, but with no elected
        // master the receiver has no clock to render them against and simply plays nothing.
        //
        // A GROUP hid this by accident. Members connect sequentially, and each extra member's
        // handshake padded the wall clock past the one-second gap. A single speaker has no such
        // padding, which is exactly why one lone HomePod stayed silent while a stereo pair and a
        // multi-room group played perfectly.
        //
        // The reference implementation this module is ported from does the same thing on peer-add
        // (owntone libairptp `daemon_peer_add`, which fires the announce and signaling timers
        // immediately); the port simply dropped it.
        if (isNew) SendInitialBurstTo(address);
    }

    public void RemovePeer(IPAddress address)
    {
        lock (_peersLock)
        {
            if (!_peers.TryGetValue(address, out var state)) return;
            if (--state.RefCount > 0) return;
            // The peer leaves OUR bookkeeping, but its servo does not forget our clock: the
            // receiver keeps its converged timeline state, and our grandmaster identity is stable
            // across sessions. Remember how far it got, so the next session does not re-charge a
            // trust the receiver still holds.
            if (state.DelayReqCount > 0)
                _warmPeers[address] = (state.DelayReqCount, Environment.TickCount64);
            _peers.Remove(address);
        }
        Diagnostic?.Invoke($"ptp: peer {address} removed");
    }

    /// <summary>
    /// Delay_Req progress of peers whose sessions have ended, kept for
    /// <see cref="WarmPeerMemory"/> so a returning receiver's settle gate resumes from its
    /// actual convergence state instead of restarting from zero.
    /// </summary>
    private readonly Dictionary<IPAddress, (int DelayReqCount, long LastSeenMs)> _warmPeers = [];

    /// <summary>
    /// How long a departed peer's convergence is trusted to persist. A receiver's servo holds a
    /// known grandmaster's timeline for far longer than this; the bound exists so a receiver that
    /// silently rebooted (losing all servo state) cannot be treated as warm indefinitely.
    /// </summary>
    private static readonly TimeSpan WarmPeerMemory = TimeSpan.FromMinutes(10);

    // ------------------------------------------------------------ send loops

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SendToPeers(_generalSocket, GeneralPort, BuildAnnounce(ClockId, unchecked((ushort)Interlocked.Increment(ref _announceSeq))));
            SendToPeers(_generalSocket, GeneralPort, BuildSignaling(ClockId, unchecked((ushort)Interlocked.Increment(ref _signalingSeq))));
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
            ushort seq = unchecked((ushort)Interlocked.Increment(ref _syncSeq));
            SendToPeers(_eventSocket, EventPort, BuildSync(ClockId, seq));
            var ts = MonotonicClock.Now;
            SendToPeers(_generalSocket, GeneralPort, BuildFollowUp(ClockId, seq, ts));
            try { await Task.Delay(125, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Sends one peer everything it needs to elect us master right now: Announce and Signaling
    /// first, then a Sync/Follow_Up pair so it can start measuring offset without waiting for the
    /// next 125 ms tick. Ordering matters — Announce must precede Sync or the Sync is discarded.
    /// </summary>
    private void SendInitialBurstTo(IPAddress address)
    {
        try
        {
            _generalSocket.SendTo(BuildAnnounce(ClockId, unchecked((ushort)Interlocked.Increment(ref _announceSeq))), new IPEndPoint(address, GeneralPort));
            _generalSocket.SendTo(BuildSignaling(ClockId, unchecked((ushort)Interlocked.Increment(ref _signalingSeq))), new IPEndPoint(address, GeneralPort));

            ushort seq = unchecked((ushort)Interlocked.Increment(ref _syncSeq));
            _eventSocket.SendTo(BuildSync(ClockId, seq), new IPEndPoint(address, EventPort));
            var ts = MonotonicClock.Now;
            _generalSocket.SendTo(BuildFollowUp(ClockId, seq, ts), new IPEndPoint(address, GeneralPort));
        }
        catch (SocketException ex)
        {
            // The scheduled loops will reach it shortly; this is an optimisation, not the only path.
            Diagnostic?.Invoke($"ptp: initial burst to {address} failed ({ex.SocketErrorCode})");
        }
        catch (ObjectDisposedException) { /* shutting down */ }
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
                        Interlocked.Increment(ref _delayReqsAnswered);
                        // Per PEER, not per process. Logging only the very first one made this
                        // diagnostic useless for the question it exists to answer — "did THIS
                        // speaker lock onto our clock?" — because the grandmaster is shared, so
                        // one receiver's success hid every other receiver's silence.
                        lock (_peersLock)
                        {
                            if (_peers.TryGetValue(from, out var counted))
                            {
                                counted.DelayReqCount++;
                                for (int i = counted.SettleWaiters.Count - 1; i >= 0; i--)
                                    if (counted.DelayReqCount >= counted.SettleWaiters[i].Target)
                                    {
                                        counted.SettleWaiters[i].Tcs.TrySetResult(true);
                                        counted.SettleWaiters.RemoveAt(i);
                                    }
                            }
                            if (_peers.TryGetValue(from, out var peer) && !peer.SlavedLogged)
                            {
                                peer.SlavedLogged = true;
                                Diagnostic?.Invoke($"ptp: {from} is slaving to our clock (Delay_Req received)");
                            }
                        }
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
