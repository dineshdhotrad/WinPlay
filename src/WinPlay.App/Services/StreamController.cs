// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.Sockets;
using WinPlay.Capture;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;
using WinPlay.Core.Mirror;
using WinPlay.Core.Raop;

namespace WinPlay.App.Services;

/// <summary>
/// The two independent things WinPlay can send to one destination. They are separate AirPlay
/// sessions with separate lifetimes — a device can be playing audio while mirroring is starting,
/// failing, or off — so anything that reports or tracks per-destination state has to say which.
/// </summary>
public enum StreamChannel
{
    Audio,
    Mirror,
}

/// <summary>
/// Owns the active streaming sessions — one coordinated <see cref="GroupSession"/> per
/// audio destination and one <see cref="MirrorSession"/> per mirroring destination.
///
/// Each destination is a self-contained lifecycle guarded by its own cancellation token:
/// starting connects and streams; a dropped connection reconnects with backoff; and
/// <see cref="StopAudioAsync"/> cancels that token so a stop is <em>authoritative</em> —
/// no in-flight reconnect can bring a torn-down destination back to life.
/// </summary>
public sealed class StreamController : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PinEntryTimeout = TimeSpan.FromSeconds(120);

    private readonly ConcurrentDictionary<string, AudioDestination> _audio = new();
    private readonly ConcurrentDictionary<string, MirrorDestination> _mirrors = new();
    private readonly CredentialStore _credentials = new();
    private readonly ReceiverIdentityStore _identities = new();
    private readonly SystemAudioMover _mover = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DiagnosticsLog _diagnostics = new();

    public event Action<string, string>? SessionStage;   // key, message

    /// <summary>
    /// A live session for a destination faulted. The channel is part of the signal: audio and
    /// screen mirroring to the same device are independent sessions that fail independently, and a
    /// fault report that cannot say which one it means forces the UI to assume both. That is how
    /// an audio dropout ended up switching the mirroring toggle off while the TV was still
    /// receiving the screen — the toggle no longer matched what was actually running.
    /// </summary>
    public event Action<string, StreamChannel, Exception>? SessionFailed;

    /// <summary>Rolling in-memory diagnostics log of recent session events (for a status view).</summary>
    public DiagnosticsLog Diagnostics => _diagnostics;

    public StreamController()
    {
        SessionStage += (key, msg) => _diagnostics.Add(key, msg);
        SessionFailed += (key, channel, ex) => _diagnostics.Add(key, $"{channel} failed: {ex.Message}");
    }

    /// <summary>
    /// Prompts the user for the PIN a receiver is displaying (argument: receiver name).
    /// Return null to cancel. When unset, PIN-protected receivers simply fail.
    /// </summary>
    public Func<string, Task<string?>>? PinPrompt { get; set; }

    /// <summary>
    /// Drops the pinned identity for every member of a picker row, so the next connection trusts
    /// the device afresh (G1). The explicit recovery path after a receiver is genuinely reset or
    /// replaced — deliberately a user action, never automatic, since silently re-trusting would
    /// defeat the pin.
    /// </summary>
    public void ForgetIdentity(PickerEntry entry)
    {
        foreach (var member in entry.Members)
        {
            _identities.Forget(member.DeviceId);
            // Forget the PAIRING too, not just the pinned identity. A device the user is
            // deliberately forgetting has usually been reset or replaced, in which case its
            // stored credentials are dead as well; leaving them behind means the next attempt
            // fails pair-verify instead of pairing cleanly.
            _credentials.Remove(member.DeviceId);
        }
    }

    public bool IsAudioActive(string key) => _audio.ContainsKey(key);
    public bool IsMirroring(string key) => _mirrors.ContainsKey(key);
    public int ActiveCount => _audio.Count + _mirrors.Count;

    // ------------------------------------------------------------ audio

    private sealed class AudioDestination(PickerEntry entry, double volumeDb, CancellationTokenSource cts)
    {
        public PickerEntry Entry { get; } = entry;
        public double VolumeDb { get; set; } = volumeDb;
        public CancellationTokenSource Cts { get; } = cts;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public GroupSession? Session { get; set; }
        public volatile bool Stopped;
    }

    public async Task StartAudioAsync(PickerEntry entry, double volumeDb, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        var dest = new AudioDestination(entry, volumeDb, cts);
        if (!_audio.TryAdd(entry.Key, dest))
        {
            cts.Dispose();
            return; // already active
        }

        try
        {
            // Hold the same gate the reconnect loop uses. StopAudioAsync cancels, waits on this
            // gate, and then DISPOSES the token source — so without holding it here, a stop
            // landing during the first connect could dispose the source this connect is still
            // linking against. That surfaces as ObjectDisposedException rather than
            // OperationCanceledException, misses every cancellation handler on the way up, and is
            // discarded as an unrecognised failure with no trace. The gate makes "a connect is in
            // progress" mean the same thing on the first attempt as on every reconnect.
            //
            // Released before the catch below runs, because the error path calls StopAudioAsync,
            // which waits on this very gate.
            await dest.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var members = ResolveMembers(entry);
                var session = await ConnectAudioAsync(entry, members, dest.Cts.Token).ConfigureAwait(false);

                // Stop can land while the connect is still finishing — cancellation is
                // cooperative, so a connect past its last check still returns a live session.
                // Committing it blindly created a session unreachable through _audio (the key was
                // already removed): it streamed on, reconnected itself forever, and its unmatched
                // EnterStreaming could leave the PC's speakers silenced with nothing shown as
                // active. Re-check before committing, exactly as the reconnect path does.
                if (dest.Stopped || dest.Cts.IsCancellationRequested)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                dest.Session = session;
                session.Faulted += _ => OnAudioFaulted(dest);

                // "Move" the audio like AirPlay on a Mac: capture the system mix and silence the
                // local speakers so only the receiver plays (avoids a ~2 s echo). The silence is
                // derived from active reception and restored automatically (even after a crash).
                _mover.EnterStreaming(entry.Key);
                await session.StartStreamingAsync(_mover.CreateCaptureSource(), volumeDb).ConfigureAwait(false);
            }
            finally
            {
                dest.Gate.Release();
            }
        }
        catch (Exception ex)
        {
            // A stop that raced this attempt is not a failure worth reporting: the row has
            // already moved on, and raising SessionFailed here would unconditionally uncheck a
            // NEWER attempt for the same device that may already be streaming.
            bool superseded = dest.Stopped || dest.Cts.IsCancellationRequested;
            if (!superseded) SessionFailed?.Invoke(entry.Key, StreamChannel.Audio, ex);
            await StopAudioAsync(entry.Key).ConfigureAwait(false);
            if (superseded && ex is OperationCanceledException) return;
            throw;
        }
    }

    private async Task<GroupSession> ConnectAudioAsync(PickerEntry entry,
        IReadOnlyList<GroupSession.Member> members, CancellationToken ct)
    {
        try
        {
            return await ConnectAsync(entry, members, ct).ConfigureAwait(false);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.OfType<StalePairingException>().Any())
        {
            // The receiver forgot us (reset / restored / re-paired elsewhere). Discard the dead
            // credentials so this device is usable again — otherwise every future attempt fails
            // the same way forever, which previously required deleting credentials.dat by hand.
            foreach (var member in entry.Members)
                _credentials.Remove(member.DeviceId);
            ReportStage(entry.Key, "stored pairing was stale — forgotten, pairing again", ct);

            // Retry from scratch: transient receivers just reconnect; PIN receivers re-pair.
            if (PinPrompt is not null && entry.Leader.CanDisplayPairingPin)
                await PairLeaderAsync(entry, ct).ConfigureAwait(false);
            return await ConnectAsync(entry, ResolveMembers(entry), ct).ConfigureAwait(false);
        }
        catch (AggregateException ex) when (
            PinPrompt is not null && ex.InnerExceptions.OfType<PairingRequiredException>().Any())
        {
            await PairLeaderAsync(entry, ct).ConfigureAwait(false);
            return await ConnectAsync(entry, ResolveMembers(entry), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Handles a dropped connection: reconnect with backoff unless the destination was stopped.</summary>
    private void OnAudioFaulted(AudioDestination dest)
    {
        if (dest.Stopped || dest.Cts.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            await dest.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (dest.Stopped || dest.Cts.IsCancellationRequested) return;
                if (dest.Session is { } dead)
                {
                    dest.Session = null;
                    // Disposing a faulted session can itself throw (e.g. the capture device was
                    // unplugged). Unguarded, that exception escapes this fire-and-forget task and
                    // the reconnect loop below never runs — leaving the destination in _audio,
                    // shown as connected in the UI, permanently silent and never retried.
                    try { await dead.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { SessionStage?.Invoke(dest.Entry.Key, $"cleanup after fault: {ex.Message}"); }
                }

                TimeSpan delay = TimeSpan.FromSeconds(2);
                while (!dest.Stopped && !dest.Cts.IsCancellationRequested)
                {
                    SessionStage?.Invoke(dest.Entry.Key, $"connection lost — reconnecting in {delay.TotalSeconds:F0}s");
                    try { await Task.Delay(delay, dest.Cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        var members = ResolveMembers(dest.Entry);
                        var session = await ConnectAudioAsync(dest.Entry, members, dest.Cts.Token).ConfigureAwait(false);
                        if (dest.Stopped || dest.Cts.IsCancellationRequested)
                        {
                            await session.DisposeAsync().ConfigureAwait(false);
                            return;
                        }
                        dest.Session = session;
                        session.Faulted += _ => OnAudioFaulted(dest);
                        await session.StartStreamingAsync(_mover.CreateCaptureSource(), dest.VolumeDb).ConfigureAwait(false);
                        SessionStage?.Invoke(dest.Entry.Key, "reconnected");
                        return;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        SessionStage?.Invoke(dest.Entry.Key, $"reconnect failed: {ex.Message}");
                        delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                    }
                }
            }
            finally { dest.Gate.Release(); }
        });
    }

    public async Task StopAudioAsync(string key)
    {
        if (!_audio.TryRemove(key, out var dest)) return;
        dest.Stopped = true;
        dest.Cts.Cancel();                                  // kills any in-flight reconnect
        await dest.Gate.WaitAsync().ConfigureAwait(false);  // wait out a reconnect mid-flight
        try
        {
            if (dest.Session is { } s) { dest.Session = null; await s.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            dest.Gate.Release();
            dest.Cts.Dispose();
            // In a finally, unconditionally. This is the call that removes the destination from
            // the active set, and the PC's speakers stay silenced while that set is non-empty.
            // Reached only on the success path, a session whose teardown threw — entirely
            // plausible across a suspend, where sockets straddle a network transition — left the
            // entry behind forever and the user's speakers muted with nothing playing and nothing
            // shown as active. Releasing the mute must not be conditional on a clean goodbye.
            _mover.ExitStreaming(key);
        }
    }

    /// <summary>
    /// Reports connect progress for an attempt that is still wanted.
    ///
    /// <para>Progress messages outlive the attempt that produced them: they cross to the UI
    /// thread, and by the time one arrives the user may have switched off and on again. A stale
    /// "Pairing…" or "Enter the PIN shown on your device" landing on top of a newer attempt tells
    /// the user to do something no one is waiting for. Cancellation is what separates the two, so
    /// a superseded attempt simply stops narrating.</para>
    /// </summary>
    private void ReportStage(string key, string message, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        SessionStage?.Invoke(key, message);
    }

    /// <summary>
    /// Raised when a volume change originates outside the app — a receiver's own volume keys
    /// arriving over DACP (D3) — so the picker's sliders track it.
    /// </summary>
    public event Action<string, double>? VolumeChangedExternally;

    /// <summary>The loudest active destination's volume, in AirPlay dB; full scale when idle.</summary>
    public double CurrentVolumeDb =>
        _audio.IsEmpty ? VolumeControl.MaxDb : _audio.Values.Max(d => d.VolumeDb);

    /// <summary>
    /// Applies one volume to every active audio destination — used for receiver-initiated volume
    /// (DACP), which addresses "what is playing" rather than a single picker row.
    /// </summary>
    public async Task SetAllVolumesAsync(double volumeDb)
    {
        foreach (string key in _audio.Keys.ToArray())
        {
            await SetVolumeAsync(key, volumeDb).ConfigureAwait(false);
            VolumeChangedExternally?.Invoke(key, volumeDb);
        }
    }

    public async Task SetVolumeAsync(string key, double volumeDb)
    {
        if (_audio.TryGetValue(key, out var dest))
        {
            dest.VolumeDb = volumeDb;
            if (dest.Session is { } s)
            {
                try { await s.SetVolumeAsync(volumeDb).ConfigureAwait(false); }
                catch (Exception ex) { SessionFailed?.Invoke(key, StreamChannel.Audio, ex); }
            }
        }
    }

    /// <summary>Pushes now-playing metadata + optional artwork to every active audio destination.</summary>
    public async Task PushNowPlayingAsync(string? title, string? artist, string? album, byte[]? artwork)
    {
        foreach (var dest in _audio.Values)
        {
            if (dest.Session is not { } s) continue;
            try
            {
                await s.SendMetadataAsync(title, artist, album).ConfigureAwait(false);
                if (artwork is { Length: > 0 })
                    await s.SendArtworkAsync(artwork).ConfigureAwait(false);
            }
            catch (Exception) { /* metadata is best-effort */ }
        }
    }

    /// <summary>Pushes playback position to every active audio destination's progress bar.</summary>
    public async Task PushProgressAsync(TimeSpan position, TimeSpan duration)
    {
        foreach (var dest in _audio.Values)
        {
            if (dest.Session is not { } s) continue;
            try { await s.SendProgressAsync(position, duration).ConfigureAwait(false); }
            catch (Exception) { /* progress is best-effort */ }
        }
    }

    // ------------------------------------------------------------ screen mirroring

    /// <summary>
    /// A mirroring destination. Registered BEFORE the connect starts, holding only its
    /// cancellation source; the session and capture source are filled in once connected.
    /// Tracking it from the first moment is what makes an in-flight mirror stoppable.
    /// </summary>
    private sealed class MirrorDestination(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public MirrorSession? Session { get; set; }
        public IH264VideoSource? Source { get; set; }
        public volatile bool Stopped;
    }

    /// <summary>Starts mirroring the desktop to an Apple TV (pairs first if needed).</summary>
    public async Task StartMirrorAsync(PickerEntry entry, CancellationToken ct)
    {
        var leader = entry.Leader;
        if (leader.Subtype is not AirPlayDeviceSubtype.AppleTv)
            throw new InvalidOperationException($"{leader.Name} does not support screen mirroring (Apple TV / AirPlay 2 TV only)");
        var address = leader.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new ReceiverUnreachableException(leader.Name);

        // Register BEFORE connecting. Mirroring an Apple TV can take many seconds (first-time
        // PIN pairing especially), and previously the destination only appeared in _mirrors
        // after the connect returned — so tapping Mirror off during that window found nothing
        // to stop, silently discarded the request, and the TV started mirroring the user's
        // screen anyway, with the toggle showing OFF.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var dest = new MirrorDestination(cts);
        if (!_mirrors.TryAdd(entry.Key, dest))
        {
            cts.Dispose();
            return; // already mirroring or connecting
        }

        try
        {
            var credentials = _credentials.Load(leader.DeviceId);
            if (credentials is null)
            {
                if (PinPrompt is null) throw new PairingRequiredException(leader.Name);
                await PairLeaderAsync(entry, cts.Token).ConfigureAwait(false);
                credentials = _credentials.Load(leader.DeviceId)
                    ?? throw new InvalidOperationException("pairing did not produce credentials");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(ConnectTimeout);
            MirrorSession session;
            try
            {
                session = await MirrorSession.ConnectAsync(address, leader.AirPlayPort ?? 7000, credentials,
                    stage => SessionStage?.Invoke(entry.Key, stage), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cts.IsCancellationRequested)
            {
                // The deadline fired, not the user — report it as a timeout, not "Cancelled".
                throw new TimeoutException(
                    $"{leader.Name} did not respond within {ConnectTimeout.TotalSeconds:F0} seconds");
            }

            // Stop may have landed while connecting; cancellation is cooperative, so a connect
            // past its last check still returns a live session. Never start streaming one the
            // user has already turned off.
            if (dest.Stopped || cts.IsCancellationRequested)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            // Capture/encode runs in a supervised child process (A4): a native GPU/encoder crash
            // kills only the child, which the supervisor restarts while this session stays alive.
            var source = new SupervisedMirrorSource();
            source.Diagnostic += d => SessionStage?.Invoke(entry.Key, $"capture: {d}");
            dest.Session = session;
            dest.Source = source;

            // Unlike audio, mirroring has no reconnect loop — a mirror that dies is over. Saying
            // so is what lets the row stop claiming to be mirroring a screen nothing is receiving.
            session.Faulted += ex => OnMirrorFaulted(entry.Key, dest, ex);

            // Mirror carries audio in the same session (Apple TV syncs A/V itself). Mute the PC
            // and feed system audio, unless audio-only to this destination is already running.
            WinPlay.Core.Audio.IAudioSource? mirrorAudio = null;
            if (session.HasAudio && !_audio.ContainsKey(entry.Key))
            {
                _mover.EnterStreaming(entry.Key);
                mirrorAudio = _mover.CreateCaptureSource();
            }
            // Observed, not fire-and-forget: this starts the capture pipeline and the pumps, and
            // it can fail outright (no encoder, GPU gone, receiver closes immediately). Discarding
            // the task discarded that failure with it, leaving the row switched on and streaming
            // nothing.
            _ = session.StartStreamingAsync(source, mirrorAudio)
                .ContinueWith(t => OnMirrorFaulted(entry.Key, dest, t.Exception!.GetBaseException()),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            await StopMirrorAsync(entry.Key).ConfigureAwait(false);
            // A user-initiated stop is not a failure to report.
            if ((dest.Stopped || cts.IsCancellationRequested) && ex is OperationCanceledException) return;
            throw;
        }
    }

    /// <summary>
    /// A live mirror session died. Mirroring has no reconnect path — an Apple TV that drops the
    /// session has usually gone to sleep or switched input, and silently redialling it would put
    /// the user's screen back on a TV they just walked away from. So it is torn down and reported,
    /// leaving the user in control of restarting it.
    /// </summary>
    /// <param name="source">
    /// The destination the fault belongs to. Identity, not the key — a session can still raise a
    /// genuine fault for seconds after it was removed from <c>_mirrors</c>, while its TEARDOWN is
    /// in flight. Matched by key alone, a stop-then-restart to the same device within that window
    /// let the OLD session's dying fault resolve to the NEW, healthy one and tear it down: the
    /// mirror the user had just started would vanish for no visible reason.
    /// </param>
    private void OnMirrorFaulted(string key, MirrorDestination source, Exception ex)
    {
        if (source.Stopped || source.Cts.IsCancellationRequested)
            return;   // the user stopped it; not a failure to report
        if (!_mirrors.TryGetValue(key, out var current) || !ReferenceEquals(current, source))
            return;   // superseded — this fault belongs to a session that is already gone
        SessionFailed?.Invoke(key, StreamChannel.Mirror, ex);
        _ = StopMirrorAsync(key);
    }

    public async Task StopMirrorAsync(string key)
    {
        if (_mirrors.TryRemove(key, out var m))
        {
            // Cancel first: this aborts a connect that is still in flight, which is the only
            // way to stop a mirror the user turned off before it finished connecting.
            m.Stopped = true;
            try { m.Cts.Cancel(); } catch (ObjectDisposedException) { }

            if (m.Session is { } session)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* already tearing down */ }
            }
            if (m.Source is { } source)
            {
                try { await source.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* already tearing down */ }
            }
            m.Cts.Dispose();
        }
        // This destination stopped capturing; the state model unmutes once none remain.
        _mover.ExitStreaming(key);
    }

    // ------------------------------------------------------------ shared helpers

    private IReadOnlyList<GroupSession.Member> ResolveMembers(PickerEntry entry)
    {
        var members = GroupSession.MembersOf(entry, _credentials);
        if (members.Count == 0)
            throw new InvalidOperationException($"{entry.DisplayName} has no member with an IPv4 address yet");
        return members;
    }

    private async Task<GroupSession> ConnectAsync(PickerEntry entry,
        IReadOnlyList<GroupSession.Member> members, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            // Buffered AirPlay 2 audio (~0.5 s) is the default. GroupSession downgrades any
            // member that cannot do it (no PTP) to the classic realtime path automatically, so
            // this is safe for every receiver — third-party speakers included.
            return await GroupSession.ConnectAsync(members,
                (memberName, stage) => ReportStage(entry.Key,
                    members.Count > 1 ? $"{memberName}: {stage}" : stage, ct),
                timeout.Token, buffered: true, identities: _identities).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The internal deadline fired, not the user. CancelAfter always surfaces as
            // OperationCanceledException, so this previously reported "Cancelled" — reading as
            // though the user had cancelled a connection they were waiting on.
            throw new TimeoutException(
                $"{entry.DisplayName} did not respond within {ConnectTimeout.TotalSeconds:F0} seconds");
        }
    }

    /// <summary>
    /// Runs the on-screen-PIN pairing flow against the entry's leader and stores the result.
    ///
    /// <para>Only devices that can actually DISPLAY a code may take this path. A HomePod has no
    /// screen and never shows a pairing PIN, so asking one for a code produced the reported
    /// "flashes through connecting, asks for a PIN, then fails instantly" behaviour. Screenless
    /// speakers are refused here with an explanation instead.</para>
    /// </summary>
    private async Task PairLeaderAsync(PickerEntry entry, CancellationToken ct)
    {
        var leader = entry.Leader;
        if (!leader.CanDisplayPairingPin)
            throw new ReceiverAccessDeniedException(leader.Name);

        var address = leader.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new ReceiverUnreachableException(leader.Name);

        ReportStage(entry.Key, $"{leader.Name} is showing a PIN — waiting for input", ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PinEntryTimeout);

        using var pairing = await ReceiverPairing.BeginAsync(address, leader.AirPlayPort ?? 7000, timeout.Token)
            .ConfigureAwait(false);
        string? pin = await PinPrompt!(leader.Name).WaitAsync(timeout.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pin))
            throw new OperationCanceledException("pairing cancelled — no PIN entered");

        var credentials = await pairing.FinishAsync(pin.Trim(), timeout.Token).ConfigureAwait(false);
        _credentials.Save(leader.DeviceId, credentials);
        ReportStage(entry.Key, $"paired with {leader.Name} — credentials stored", ct);
    }

    /// <summary>
    /// Stops every destination and restores local audio, WITHOUT tearing the controller down —
    /// used when the machine is about to sleep, log off, or hand the audio endpoint to another
    /// user. Receivers are released while the network still works, instead of being left to
    /// time out on a sender that has gone away.
    /// </summary>
    /// <summary>
    /// Stops every destination, concurrently and independently.
    ///
    /// <para>Concurrently because this runs against a deadline that is not ours: the suspend and
    /// session-ending handlers get about two seconds before Windows stops waiting, and tearing a
    /// session down politely — TEARDOWN over RTSP, then draining its pumps — is seconds of work on
    /// its own. Done one after another, a stereo pair plus a second room could not possibly finish
    /// in time, and the remainder was left to be frozen mid-flight by the suspend and thawed
    /// afterwards against sockets whose interfaces no longer exist. Run together, the wall clock is
    /// the slowest single destination rather than the sum of all of them.</para>
    ///
    /// <para>Independently because one destination's failure is not the others' business. A single
    /// throw used to abandon the whole loop, so every destination after it was never stopped at
    /// all — and since releasing the local audio mute is part of stopping, that could leave the
    /// PC silent with nothing playing and nothing shown as active.</para>
    /// </summary>
    public async Task StopAllAsync()
    {
        var stops = new List<Task>();
        foreach (string key in _audio.Keys.ToArray())
            stops.Add(IsolatedAsync(() => StopAudioAsync(key), key, StreamChannel.Audio));
        foreach (string key in _mirrors.Keys.ToArray())
            stops.Add(IsolatedAsync(() => StopMirrorAsync(key), key, StreamChannel.Mirror));
        await Task.WhenAll(stops).ConfigureAwait(false);
    }

    /// <summary>Runs one teardown so that its failure is recorded and contained, never propagated.</summary>
    private async Task IsolatedAsync(Func<Task> stop, string key, StreamChannel channel)
    {
        try { await stop().ConfigureAwait(false); }
        catch (Exception ex) { _diagnostics.Add(key, $"{channel} teardown failed: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await StopAllAsync().ConfigureAwait(false);
        _mover.Dispose();
        _lifetime.Dispose();
    }
}
