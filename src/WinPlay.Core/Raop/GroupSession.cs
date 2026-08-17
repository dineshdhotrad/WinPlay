// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;
using WinPlay.Core.Ptp;

namespace WinPlay.Core.Raop;

/// <summary>
/// Coordinated AirPlay 2 session to every member of a picker entry (single device,
/// stereo pair, or group). Live-verified rule: stereo pairs and groups do NOT relay
/// audio from their leader — each member needs its own full session, all slaved to
/// the one process-wide PTP grandmaster, with every member listed in every SETPEERS.
/// Each member then renders its own configured channel from the shared stereo feed.
/// One <see cref="BroadcastAudioSource"/> tees a single capture to all members so the
/// content is sample-identical across the group.
/// </summary>
public sealed class GroupSession : IAsyncDisposable
{
    /// <summary>
    /// One receiver to open a session with. <paramref name="ExtraPeers"/> are additional
    /// timing peers to declare in SETPEERS beyond the other session members — used so an
    /// Apple-TV-led home theatre fans audio out to its paired HomePods (which we do not
    /// open sessions to, but the ATV must know as clock-synced peers or it stays silent).
    /// <paramref name="GroupId"/> carries the device's own mDNS `gid` self-report (see
    /// <see cref="AirPlayDevice"/>) through to <see cref="RaopSession.ConnectAsync"/>, which
    /// echoes it into the SETUP(session) `groupUUID` instead of a random one — required for
    /// receivers that already believe they belong to a specific group identity (confirmed on a
    /// HomePod mini advertising `igl=1`/`gcgl=1`; without this it never elects us as PTP master).
    /// There is deliberately no equivalent "advertised gcgl" member: `groupContainsGroupLeader`
    /// in the SETUP(session) payload is always false, for every member of every topology — see
    /// the comment at that payload's construction site in <see cref="RaopSession"/> for why.
    /// </summary>
    public sealed record Member(string Name, IPAddress Address, int Port, bool UsePtp,
        HapPairingCredentials? Credentials = null, IReadOnlyList<IPAddress>? ExtraPeers = null,
        string? DeviceId = null, string? PublicKey = null, string? GroupId = null,
        bool PreferAacBuffered = false);

    private readonly List<(Member Member, RaopSession Session)> _members;
    private BroadcastAudioSource? _broadcast;

    /// <summary>(member name, stage message) — connection and streaming progress.</summary>
    public event Action<string, string>? StageChanged;

    /// <summary>Raised once when any member's connection drops (candidate for reconnect).</summary>
    public event Action<Exception>? Faulted;
    private int _faultRaised;

    public IReadOnlyList<string> MemberNames => [.. _members.Select(m => m.Member.Name)];
    public long FramesSent => _members.Count > 0 ? _members.Max(m => m.Session.FramesSent) : 0;
    public TimeSpan Elapsed => _members.Count > 0 ? _members.Max(m => m.Session.Elapsed) : TimeSpan.Zero;

    private GroupSession(List<(Member, RaopSession)> members) => _members = members;

    private void OnMemberFaulted(Exception ex)
    {
        if (System.Threading.Interlocked.Exchange(ref _faultRaised, 1) == 0)
            Faulted?.Invoke(ex);
    }

    /// <summary>
    /// Builds the AUDIO member list for a picker entry (IPv4, PTP by feature bit 41). With a
    /// <paramref name="credentialStore"/>, stored PIN-pairing credentials are attached so
    /// PIN-protected members authenticate via pair-verify.
    ///
    /// Topology (live-verified):
    /// - Stereo pairs / plain groups: one coordinated session per member (their "leader"
    /// does not relay — every member needs its own clock-synced session).
    /// - Apple-TV-led home theatre: an Apple TV does NOT play a standalone realtime audio
    /// session (only audio inside a screen-mirroring session). So for AUDIO we stream
    /// directly to the ATV's paired speakers (the other members) and leave the ATV as
    /// the video/mirror target. If the ATV has no separate speakers, we fall back to it.
    /// </summary>
    public static IReadOnlyList<Member> MembersOf(PickerEntry entry, CredentialStore? credentialStore = null)
    {
        static IPAddress? Ip(AirPlayDevice d) =>
            d.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

        bool appleTvGroup = entry.Kind == PickerEntryKind.Group
                            && entry.Leader.Subtype == AirPlayDeviceSubtype.AppleTv;

        // A standalone Apple TV (no home-theatre speakers) cannot be an AUDIO destination at
        // all: tvOS accepts a third-party audio-only session in full — every RTSP stage
        // succeeds — and renders nothing (verified across four session shapes; matches
        // postlund/pyatv#1666 on the same model). Streaming to it would report success while
        // playing silence, so it is refused with the actionable alternative instead.
        if (entry.Kind != PickerEntryKind.Group && entry.Leader.Subtype == AirPlayDeviceSubtype.AppleTv)
            throw new NotSupportedException(
                $"{entry.Leader.Name} plays audio only inside screen mirroring — use Screen + Audio");

        // An Apple-TV-led room streams to its home-theatre SPEAKERS directly, on the realtime
        // transport (forced by the caller). Every alternative is closed BY MEASUREMENT on real
        // hardware, each accepted by the receiver and never rendered or killed by its owner:
        //   • audio-only through the ATV — realtime ALAC over PTP and over NTP (enriched session
        //     payload), buffered ALAC over PTP, and buffered AAC-LC over PTP (the exact shape
        //     captured from real Apple senders, raw 1024-sample access units, SSRC 0x16000000) —
        //     ALL silent. tvOS on this model does not render third-party audio-only sessions
        //     (pyatv's four-year-open postlund/pyatv#1666 is the same wall); mirror-class
        //     sessions render fine, so pairing/FairPlay/keys are not the gate.
        //   • buffered to the speakers directly — their owner cuts rendering within seconds
        //     (measured with servos still slaved).
        // Realtime to the speakers at the shared wall-grid lead is the one route that renders and
        // stays: months of daily use on the classic path, now phase-locked with every other room.
        //
        // Both obvious alternatives are closed, each tested on real hardware:
        //   • Audio-only THROUGH the Apple TV: the session is accepted in full — PTP or NTP
        //     timing, enriched session payload, ALAC — and never rendered. This matches pyatv's
        //     four-year-open silent-audio bug on the same model (postlund/pyatv#1666); the same
        //     ATV renders our mirror session's audio, so the gate is tvOS's audio-only policy,
        //     not the session shape.
        //   • BUFFERED to the speakers directly: they are the ATV's own outputs; the ATV cuts
        //     their rendering within seconds of a foreign grandmaster capturing their clocks
        //     (measured: audio died at ~3 s with both servos still slaved to us).
        // Realtime to the speakers captures nothing the ATV reclaims in practice — it is the one
        // route with months of verified daily use — and on the shared timeline grid it renders at
        // the same declared lead as every buffered room.
        IEnumerable<AirPlayDevice> devices = entry.Members;
        // Declared partners the leader belongs to (per entry.Members, resolved from its own
        // gid/pgid/tsid TXT fields by DevicePicker.Collapse) that end up EXCLUDED from the
        // connectable session-member set below. They get no RaopSession of their own, so
        // unless their addresses are still declared in SETPEERS they never elect our clock and
        // stay silent — see the Member.ExtraPeers doc for the confirmed ATV/HomePod case.
        IReadOnlyList<IPAddress>? extraPeers = null;
        if (appleTvGroup)
        {
            var speakers = entry.Members
                .Where(m => !ReferenceEquals(m, entry.Leader) && m.SupportsAudio && Ip(m) is not null)
                .ToList();
            if (speakers.Count > 0)
                devices = speakers;
            else
            {
                // No standalone-audio speaker resolved: we fall back to streaming to the ATV
                // alone, which is FEWER session members than its own group membership implies
                // (entry.Members still lists its paired HomePods). Their addresses — if any are
                // resolvable — must still ride along as PTP peers of the ATV's session.
                devices = new[] { entry.Leader };
                var declaredPartners = entry.Members
                    .Where(m => !ReferenceEquals(m, entry.Leader))
                    .Select(Ip)
                    .Where(a => a is not null)
                    .Select(a => a!)
                    .Distinct()
                    .ToList();
                if (declaredPartners.Count > 0)
                    extraPeers = declaredPartners;
            }
        }

        var members = new List<Member>();
        foreach (var device in devices)
        {
            if (Ip(device) is not { } address) continue;
            // Buffered streams carry AAC-LC — the codec every captured Apple sender uses on
            // type 103, advertised by every AirPlay 2 receiver (bufferStream bit 22), at
            // ~0.2 Mbit/s. The previous payload was UNCOMPRESSED 24-bit ALAC at ~2.1 Mbit/s per
            // speaker — ten times heavier — and a speaker on a marginal Wi-Fi link starved on it:
            // measured 19 s of cumulative TCP send stalls in one 15-second-of-audio window, the
            // pump running at half real time while every protocol instrument read healthy.
            // Falls back to ALAC automatically when no AAC encoder is available (GroupSession
            // stage-logs the downgrade).
            members.Add(new Member(device.Name, address, device.AirPlayPort ?? 7000,
                device.Features.HasFlag(AirPlayFeatures.SupportsPtp),
                credentialStore?.Load(device.DeviceId), ExtraPeers: extraPeers,
                DeviceId: device.DeviceId, PublicKey: device.PublicKey,
                GroupId: device.GroupId, PreferAacBuffered: true));
        }
        return members;
    }

    /// <summary>
    /// Connects to every member. Partial failures are reported via the stage callback
    /// and tolerated — the group plays on the members that accepted. Throws only when
    /// no member could be connected.
    /// </summary>
    /// <param name="identities">
    /// When supplied, every member's advertised long-term identity is checked against the pinned
    /// one BEFORE any audio flows. A mismatch fails that member with
    /// <see cref="ReceiverIdentityChangedException"/> rather than streaming to an impostor; the
    /// pin is (re)written only after the connection is accepted, so a rejected impostor can never
    /// overwrite a good pin.
    /// </param>
    /// <summary>
    /// Builds the AAC-LC encoder for a member that requested <see cref="Member.PreferAacBuffered"/>.
    /// Injected because the codec lives above this assembly (Media Foundation, WinPlay.Capture);
    /// null ⇒ such members fall back to buffered ALAC.
    /// </summary>
    public static Func<Audio.IAacFrameEncoder>? AacEncoderFactory { get; set; }

    public static async Task<GroupSession> ConnectAsync(IReadOnlyList<Member> members,
        Action<string, string>? stageChanged = null, CancellationToken ct = default, bool buffered = false,
        ReceiverIdentityStore? identities = null, string? activeRemote = null,
        uint? sharedStartTimestamp = null)
    {
        if (members.Count == 0)
            throw new ArgumentException("group has no connectable members", nameof(members));

        var connected = new List<(Member, RaopSession)>();
        var failures = new List<Exception>();
        var allAddresses = members.Select(m => m.Address).ToList();
        // A buffered group shares ONE start timestamp, so the same audio sample carries the same
        // RTP time on every member and (with the shared stream-start anchor) plays in lock-step.
        // One timeline for every speaker that will render this audio. Within a group that was
        // already true; ACROSS destinations it was not, so two rooms started from different rtp
        // bases and anchored at different instants — same clock, different timelines, audible echo
        // between rooms. The caller passes its timeline in when one already exists.
        uint? sharedStart = buffered ? (sharedStartTimestamp ?? (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)) : null;

        // One group identity for this whole playback session, sent unchanged to every member:
        // that is what tells the receivers they form a single synchronised group. It is ours to
        // mint — never derived from any member's advertised `gid`, which is a membership
        // advertisement and may name several groups at once (see RaopSession.ResolveGroupUuid).
        string sessionGroupUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        // Connecting a group is a multi-step operation that can be abandoned partway: the
        // caller's connect timeout can fire, or the user can hit Stop, while member 2 of 3 is
        // still handshaking. Members connected so far are FULLY live — bound UDP sockets, an
        // open encrypted RTSP connection, and running timing/control loops that root the
        // session object — so abandoning them leaks sockets and threads for the process
        // lifetime. Everything already connected is therefore disposed before rethrowing.
        try
        {
        foreach (var member in members)
        {
            var peers = allAddresses.Where(a => !a.Equals(member.Address))
                .Concat(member.ExtraPeers ?? [])
                .Distinct()
                .ToList();
            try
            {
                // Identity gate: refuse BEFORE connecting, so an impostor never receives
                // audio, metadata, or a pairing attempt.
                if (identities is not null && member.DeviceId is { Length: > 0 } deviceId)
                {
                    var check = identities.Check(deviceId, member.PublicKey);
                    if (check.Trust == IdentityTrust.Mismatch)
                        throw new ReceiverIdentityChangedException(member.Name, check.PinnedKey, check.PresentedKey);
                    if (check.Trust == IdentityTrust.FirstUse)
                        stageChanged?.Invoke(member.Name, "new receiver — trusting its identity on first use");
                }

                Audio.IAacFrameEncoder? aac = null;
                if (member.PreferAacBuffered && buffered && member.UsePtp && AacEncoderFactory is { } factory)
                {
                    // Task.Run, deliberately: the first member's connect can run synchronously on
                    // the WinUI STA thread, and an in-proc MFT created there is homed to that
                    // apartment while the audio pump that drives it is MTA — an apartment
                    // violation that surfaces as machine-dependent COM failures mid-stream.
                    // A pool thread is MTA, matching the pump.
                    try { aac = await Task.Run(factory).ConfigureAwait(false); }
                    catch (Exception ex) { stageChanged?.Invoke(member.Name, $"AAC encoder unavailable ({ex.Message}) — using ALAC"); }
                }
                else if (member.PreferAacBuffered && buffered && member.UsePtp)
                {
                    stageChanged?.Invoke(member.Name, "no AAC encoder registered — using ALAC");
                }
                var session = await RaopSession.ConnectAsync(member.Address, member.Port,
                    member.UsePtp, peers, stage => stageChanged?.Invoke(member.Name, stage), ct,
                    member.Credentials, buffered && member.UsePtp, sharedStart, activeRemote,
                    sessionGroupUuid, aac).ConfigureAwait(false);
                connected.Add((member, session));

                // Pin only after the receiver accepted us.
                if (identities is not null && member.DeviceId is { Length: > 0 } acceptedId)
                    identities.Pin(acceptedId, member.PublicKey, member.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                stageChanged?.Invoke(member.Name, $"member failed to connect: {ex.Message}");
            }
        }
        }
        catch
        {
            // Cancellation (or anything else escaping the per-member handler) must not strand
            // the sessions already established.
            foreach (var (_, session) in connected)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* already tearing down */ }
            }
            throw;
        }

        if (connected.Count == 0)
            throw new AggregateException("no group member could be connected", failures);

        var group = new GroupSession(connected) { StartTimestamp = sharedStart };
        if (stageChanged is not null)
            group.StageChanged += stageChanged;
        foreach (var (_, session) in connected)
            session.Faulted += group.OnMemberFaulted;
        return group;
    }

    /// <summary>Tees <paramref name="source"/> to every member and starts streaming. Takes ownership of the source.</summary>
    /// <summary>The rtp base every member of this session renders against.</summary>
    public uint? StartTimestamp { get; private set; }

    /// <summary>The instant this session's timeline was anchored to, for other destinations to join.</summary>
    public ulong? AnchorNanos { get; private set; }

    /// <param name="timelineSlot">
    /// Supplies this session's slot on the machine-wide capture timeline, evaluated at the one
    /// instant it is valid — right after the capture flushes to live, just before the anchor is
    /// sent. Returns the timeline's anchor instant (identical for every session that ever joins,
    /// which is what keeps every room rendering the same sample at the same moment) and this
    /// session's start offset, computed from wall time since the timeline's origin. Null → the
    /// session forms a timeline of its own (protocol test harness).
    /// </param>
    public async Task StartStreamingAsync(IAudioSource source, double volumeDb = -18,
        Func<(ulong AnchorNanos, long StartPositionFrames)>? timelineSlot = null)
    {
        _broadcast = new BroadcastAudioSource(source);

        // Create EVERY branch at the same live position, before any pump reads, so frame index N
        // maps to the identical absolute audio sample on every member — the basis for sample-exact
        // group sync (tightens realtime too, and is required for buffered).
        var streams = _members
            .Select(ms => (ms.Member, ms.Session, Branch: _broadcast.CreateBranch()))
            .ToList();

        // Deterministic start sequence. The buffered anchor promises "sample 0 plays at
        // anchor + lead", so every network round-trip between computing the anchor and the first
        // packet is stolen jitter headroom. Therefore:
        // 1. ALL preparatory RTSP work (volume, crypto, keep-alive) completes first;
        // 2. the capture flushes to the live edge (stale buffer is pure latency);
        // 3. ONE shared anchor is computed and sent to every buffered member concurrently —
        // shared start timestamp + shared anchor ⇒ every speaker plays the same sample at
        // the same PTP instant (the group-echo fix);
        // 4. pumps start with zero awaits after the anchor.
        // Result: headroom = lead − anchor-RTT, run after run, not "lead minus whatever setup ate".
        await Task.WhenAll(streams.Select(async s =>
        {
            try { await s.Session.PrepareStreamingAsync(volumeDb).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StageChanged?.Invoke(s.Member.Name, $"stream prepare failed: {ex.Message}");
            }
        })).ConfigureAwait(false);

        // Every buffered member's receiver must have CONVERGED on our PTP timeline before the
        // anchor exists — an anchor received sooner is silently discarded and never re-sent, so
        // the session plays nothing with a spotless log (see RaopSession.WaitForClockSettleAsync).
        // All members wait concurrently. Deliberately BEFORE the flush below: this can take
        // seconds, and audio captured while waiting must not become backlog the first packet has
        // to wade through.
        await Task.WhenAll(streams.Where(s => s.Session.IsBuffered)
            .Select(s => s.Session.WaitForClockSettleAsync(default))).ConfigureAwait(false);

        (source as IFlushableAudioSource)?.FlushToLive();

        // The timeline slot is taken at THIS instant — immediately after the flush, so "the
        // position this session starts producing from" and "the wall moment the timeline calls
        // now" are the same moment by construction. The caller's slot computes the start offset
        // from its wall-locked timeline origin, NOT from any capture-side counter: a demand-driven
        // counter only advances while somebody is pumping, so a session that started after an idle
        // gap inherited every previously-pumped frame as a phantom future offset — the receiver
        // was told the audio was due minutes from now, held dead air, and shed the rest. Wall time
        // has no such memory. Without a caller-supplied slot (protocol test harness), the session
        // is its own timeline: fresh anchor, offset from the source's own position.
        long startPositionFrames;

        var buffered = streams.Where(s => s.Session.IsBuffered).ToList();
        ulong anchorNanos;
        if (timelineSlot?.Invoke() is { } slot)
        {
            anchorNanos = slot.AnchorNanos;
            startPositionFrames = slot.StartPositionFrames;
        }
        else
        {
            anchorNanos = MonotonicClock.NowNanoseconds + RaopSession.BufferedLeadNanos;
            startPositionFrames = (source as IPositionedAudioSource)?.StartPositionFrames ?? 0;
        }
        if (buffered.Count > 0)
        {
            AnchorNanos = anchorNanos;
            await Task.WhenAll(buffered.Select(async s =>
            {
                try { await s.Session.SendBufferedAnchorAsync(anchorNanos, default).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StageChanged?.Invoke(s.Member.Name, $"anchor failed: {ex.Message}");
                }
            })).ConfigureAwait(false);
        }

        // Every pump — buffered AND realtime — runs on the same timeline grid: origin is the wall
        // instant the timeline calls frame zero (the anchor promises rendering at origin + lead).
        // A pump aligned to that grid stamps every packet from its capture position on the wall
        // timeline (packet-size-independent — ALAC and AAC alike), so every room renders
        // identically by construction; a pump paced off its own stopwatch instead bakes its
        // startup delay into every stamp as a permanent per-room offset — the audible flam.
        ulong timelineOrigin = anchorNanos - RaopSession.BufferedLeadNanos;
        foreach (var (member, session, branch) in streams)
        {
            try { session.StartPump(branch, startPositionFrames, timelineOrigin); }
            catch (Exception ex)
            {
                StageChanged?.Invoke(member.Name, $"streaming start failed: {ex.Message}");
            }
        }
    }

    public async Task SetVolumeAsync(double db, CancellationToken ct = default)
    {
        foreach (var (member, session) in _members)
        {
            try
            {
                await session.SetVolumeAsync(db, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StageChanged?.Invoke(member.Name, $"volume change failed: {ex.Message}");
            }
        }
    }

    /// <summary>Pushes now-playing metadata to every member's Now Playing UI (best effort).</summary>
    public async Task SendMetadataAsync(string? title, string? artist, string? album, CancellationToken ct = default)
    {
        foreach (var (_, session) in _members)
        {
            try { await session.SendMetadataAsync(title, artist, album, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* non-fatal */ }
        }
    }

    /// <summary>Pushes playback position to every member's progress bar (best effort).</summary>
    public async Task SendProgressAsync(TimeSpan position, TimeSpan duration, CancellationToken ct = default)
    {
        foreach (var (_, session) in _members)
        {
            try { await session.SendProgressAsync(position, duration, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* non-fatal */ }
        }
    }

    /// <summary>Pushes cover artwork to every member's Now Playing UI (best effort).</summary>
    public async Task SendArtworkAsync(byte[] image, string contentType = "image/jpeg", CancellationToken ct = default)
    {
        foreach (var (_, session) in _members)
        {
            try { await session.SendArtworkAsync(image, contentType, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* non-fatal */ }
        }
    }

    public async Task StopAsync()
    {
        await Task.WhenAll(_members.Select(m => IsolatedAsync(() => m.Session.StopAsync())))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Tears every member down, together and independently.
    ///
    /// <para>Independently because a member's failure is not the other members' business. Disposed
    /// in a plain loop, one throw — entirely plausible mid-teardown across a sleep or a Wi-Fi
    /// switch — abandoned every member after it, leaving a real speaker in the room still playing
    /// while the app showed everything stopped, until the process exited.</para>
    ///
    /// <para>Together because teardown runs against the suspend deadline, and a polite goodbye
    /// (TEARDOWN, then draining the pumps) takes seconds per member. Sequentially, a stereo pair
    /// alone could not finish in time.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_members.Select(m => IsolatedAsync(() => m.Session.DisposeAsync().AsTask())))
            .ConfigureAwait(false);
        _broadcast?.Dispose();
    }

    /// <summary>Runs one member's teardown so a failure cannot take the others with it.</summary>
    private static async Task IsolatedAsync(Func<Task> teardown)
    {
        try { await teardown().ConfigureAwait(false); }
        catch (Exception) { /* nothing left to salvage for this member; the rest still must stop */ }
    }
}
