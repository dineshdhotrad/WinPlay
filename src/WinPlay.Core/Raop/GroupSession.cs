// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;

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
        HapPairingCredentials? Credentials = null, IReadOnlyList<IPAddress>? ExtraPeers = null);

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
                credentialStore?.Load(device.DeviceId)));
        }
        return members;
    }

    /// <summary>
    /// Connects to every member. Partial failures are reported via the stage callback
    /// and tolerated — the group plays on the members that accepted. Throws only when
    /// no member could be connected.
    /// </summary>
    public static async Task<GroupSession> ConnectAsync(IReadOnlyList<Member> members,
        Action<string, string>? stageChanged = null, CancellationToken ct = default)
    {
        if (members.Count == 0)
            throw new ArgumentException("group has no connectable members", nameof(members));

        var connected = new List<(Member, RaopSession)>();
        var failures = new List<Exception>();
        var allAddresses = members.Select(m => m.Address).ToList();
        foreach (var member in members)
        {
            var peers = allAddresses.Where(a => !a.Equals(member.Address))
                .Concat(member.ExtraPeers ?? [])
                .Distinct()
                .ToList();
            try
            {
                var session = await RaopSession.ConnectAsync(member.Address, member.Port,
                    member.UsePtp, peers, stage => stageChanged?.Invoke(member.Name, stage), ct,
                    member.Credentials).ConfigureAwait(false);
                connected.Add((member, session));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                stageChanged?.Invoke(member.Name, $"member failed to connect: {ex.Message}");
            }
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
        foreach (var (member, session) in _members)
        {
            try
            {
                await session.StartStreamingAsync(_broadcast.CreateBranch(), volumeDb).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        foreach (var (_, session) in _members)
            await session.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, session) in _members)
            await session.DisposeAsync().ConfigureAwait(false);
        _broadcast?.Dispose();
    }
}
