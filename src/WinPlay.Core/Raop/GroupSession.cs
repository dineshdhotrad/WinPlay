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
    /// </summary>
    public sealed record Member(string Name, IPAddress Address, int Port, bool UsePtp,
        HapPairingCredentials? Credentials = null, IReadOnlyList<IPAddress>? ExtraPeers = null,
        string? DeviceId = null, string? PublicKey = null);

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
    ///  - Stereo pairs / plain groups: one coordinated session per member (their "leader"
    ///    does not relay — every member needs its own clock-synced session).
    ///  - Apple-TV-led home theatre: an Apple TV does NOT play a standalone realtime audio
    ///    session (only audio inside a screen-mirroring session). So for AUDIO we stream
    ///    directly to the ATV's paired speakers (the other members) and leave the ATV as
    ///    the video/mirror target. If the ATV has no separate speakers, we fall back to it.
    /// </summary>
    public static IReadOnlyList<Member> MembersOf(PickerEntry entry, CredentialStore? credentialStore = null)
    {
        static IPAddress? Ip(AirPlayDevice d) =>
            d.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

        bool appleTvGroup = entry.Kind == PickerEntryKind.Group
                            && entry.Leader.Subtype == AirPlayDeviceSubtype.AppleTv;

        IEnumerable<AirPlayDevice> devices = entry.Members;
        if (appleTvGroup)
        {
            var speakers = entry.Members
                .Where(m => !ReferenceEquals(m, entry.Leader) && m.SupportsAudio && Ip(m) is not null)
                .ToList();
            devices = speakers.Count > 0 ? speakers : new[] { entry.Leader };
        }

        var members = new List<Member>();
        foreach (var device in devices)
        {
            if (Ip(device) is not { } address) continue;
            members.Add(new Member(device.Name, address, device.AirPlayPort ?? 7000,
                device.Features.HasFlag(AirPlayFeatures.SupportsPtp),
                credentialStore?.Load(device.DeviceId),
                DeviceId: device.DeviceId, PublicKey: device.PublicKey));
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
    /// one BEFORE any audio flows (G1). A mismatch fails that member with
    /// <see cref="ReceiverIdentityChangedException"/> rather than streaming to an impostor; the
    /// pin is (re)written only after the connection is accepted, so a rejected impostor can never
    /// overwrite a good pin.
    /// </param>
    public static async Task<GroupSession> ConnectAsync(IReadOnlyList<Member> members,
        Action<string, string>? stageChanged = null, CancellationToken ct = default, bool buffered = false,
        ReceiverIdentityStore? identities = null)
    {
        if (members.Count == 0)
            throw new ArgumentException("group has no connectable members", nameof(members));

        var connected = new List<(Member, RaopSession)>();
        var failures = new List<Exception>();
        var allAddresses = members.Select(m => m.Address).ToList();
        // A buffered group shares ONE start timestamp, so the same audio sample carries the same
        // RTP time on every member and (with the shared stream-start anchor) plays in lock-step.
        uint? sharedStart = buffered ? (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue) : null;

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
                // Identity gate (G1): refuse BEFORE connecting, so an impostor never receives
                // audio, metadata, or a pairing attempt.
                if (identities is not null && member.DeviceId is { Length: > 0 } deviceId)
                {
                    var check = identities.Check(deviceId, member.PublicKey);
                    if (check.Trust == IdentityTrust.Mismatch)
                        throw new ReceiverIdentityChangedException(member.Name, check.PinnedKey, check.PresentedKey);
                    if (check.Trust == IdentityTrust.FirstUse)
                        stageChanged?.Invoke(member.Name, "new receiver — trusting its identity on first use");
                }

                var session = await RaopSession.ConnectAsync(member.Address, member.Port,
                    member.UsePtp, peers, stage => stageChanged?.Invoke(member.Name, stage), ct,
                    member.Credentials, buffered && member.UsePtp, sharedStart).ConfigureAwait(false);
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

        var group = new GroupSession(connected);
        if (stageChanged is not null)
            group.StageChanged += stageChanged;
        foreach (var (_, session) in connected)
            session.Faulted += group.OnMemberFaulted;
        return group;
    }

    /// <summary>Tees <paramref name="source"/> to every member and starts streaming. Takes ownership of the source.</summary>
    public async Task StartStreamingAsync(IAudioSource source, double volumeDb = -18)
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
        //   1. ALL preparatory RTSP work (volume, crypto, keep-alive) completes first;
        //   2. the capture flushes to the live edge (stale buffer is pure latency);
        //   3. ONE shared anchor is computed and sent to every buffered member concurrently —
        //      shared start timestamp + shared anchor ⇒ every speaker plays the same sample at
        //      the same PTP instant (the group-echo fix);
        //   4. pumps start with zero awaits after the anchor.
        // Result: headroom = lead − anchor-RTT, run after run, not "lead minus whatever setup ate".
        await Task.WhenAll(streams.Select(async s =>
        {
            try { await s.Session.PrepareStreamingAsync(volumeDb).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StageChanged?.Invoke(s.Member.Name, $"stream prepare failed: {ex.Message}");
            }
        })).ConfigureAwait(false);

        (source as IFlushableAudioSource)?.FlushToLive();

        var buffered = streams.Where(s => s.Session.IsBuffered).ToList();
        if (buffered.Count > 0)
        {
            ulong anchorNanos = MonotonicClock.NowNanoseconds + RaopSession.BufferedLeadNanos;
            await Task.WhenAll(buffered.Select(async s =>
            {
                try { await s.Session.SendBufferedAnchorAsync(anchorNanos, default).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StageChanged?.Invoke(s.Member.Name, $"anchor failed: {ex.Message}");
                }
            })).ConfigureAwait(false);
        }

        foreach (var (member, session, branch) in streams)
        {
            try { session.StartPump(branch); }
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
