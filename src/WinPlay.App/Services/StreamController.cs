// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.Sockets;
using WinPlay.Capture;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;
using WinPlay.Core.Mirror;
using WinPlay.Core.Raop;
using WinPlay.Diagnostics;

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
        // AAC-LC encoder for Apple-TV-targeted buffered audio (Media Foundation lives above Core).
        GroupSession.AacEncoderFactory = () => new WinPlay.Capture.MediaFoundationAacEncoder();

        // The PC's volume keys are the only volume control the user can still reach once local
        // output is silenced, so they drive the receiver — the same thing a Mac does while playing
        // to an AirPlay speaker. Without this they did nothing except unmute the PC.
        // Capture-layer reports go to the same session log — a capture rebuilding or dying is
        // session-relevant even though no single session owns it.
        _mover.CaptureDiagnostic += msg => WinPlayLog.For("Capture").Warning("{Message}", msg);

        _mover.SystemVolumeChanged += scalar =>
            _ = SetAllVolumesAsync(VolumeControl.PercentToDb(Math.Clamp(scalar, 0f, 1f) * 100));

        // To the LOG FILE as well as the in-memory buffer.
        //
        // Everything a session says about itself — every connect stage, every failure, every
        // protocol diagnostic — used to go only to a rolling in-memory list and the flyout's
        // subtitle. So when streaming failed on a user's machine there was nothing on disk to
        // read afterwards: the log showed the app starting and trimming memory, and not one word
        // about the receiver it had just failed to play to. A bug you cannot see is a bug you
        // cannot fix, and this is the single most useful thing the log can contain.
        SessionStage += (key, msg) =>
        {
            _diagnostics.Add(key, msg);
            WinPlayLog.For("Session").Information("{Key}: {Stage}", key, msg);
        };
        SessionFailed += (key, channel, ex) =>
        {
            _diagnostics.Add(key, $"{channel} failed: {ex.Message}");
            WinPlayLog.For("Session").Warning(ex, "{Key}: {Channel} failed", key, channel);
        };
    }

    /// <summary>
    /// Prompts the user for the PIN a receiver is displaying (argument: receiver name).
    /// Return null to cancel. When unset, PIN-protected receivers simply fail.
    /// </summary>
    public Func<string, Task<string?>>? PinPrompt { get; set; }

    /// <summary>
    /// Drops the pinned identity for every member of a picker row, so the next connection trusts
    /// the device afresh. The explicit recovery path after a receiver is genuinely reset or
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

    /// <summary>
    /// Mints the per-destination <c>Active-Remote</c> token for a destination key, and retires it.
    /// Supplied by <see cref="RemoteControlService"/>, which owns the DACP server — set as
    /// callbacks rather than a reference because that service already depends on this one, and a
    /// cycle between them would be a worse answer than two delegates.
    ///
    /// <para>The token is what lets a volume change from one speaker be told apart from another
    /// room's. Unset (as in tests, or before remote control starts), sessions fall back to the
    /// shared identity and behave exactly as before.</para>
    /// </summary>
    public Func<string, string>? IssueRemoteToken { get; set; }

    public Action<string>? RevokeRemoteToken { get; set; }

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

        /// <summary>This destination's Active-Remote token, retired when the session ends.</summary>
        public string? RemoteToken { get; set; }
    }

    public async Task StartAudioAsync(PickerEntry entry, double volumeDb, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        var dest = new AudioDestination(entry, volumeDb, cts);

        if (!_audio.TryAdd(entry.Key, dest))
        {
            // Something is already registered under this key. Returning here reported SUCCESS,
            // which is only true if that something is genuinely playing. When it was a destination
            // stuck in the reconnect loop — or one whose stop had not finished unwinding — the
            // user's tap did nothing at all: no connect, no error, no log line, and a row that
            // settled on "Streaming system audio" because this method returned without throwing.
            // That is the "it will not even connect, and nothing tells me why" case, and it is
            // self-inflicted: the app decided it was already doing something it was not doing.
            if (_audio.TryGetValue(entry.Key, out var existing) && IsGenuinelyStreaming(existing))
            {
                cts.Dispose();
                return;   // really is playing; nothing to do
            }

            // Stale. Clear it out and take the key, so the user's request actually happens.
            SessionStage?.Invoke(entry.Key, "replacing a stale session for this device");
            await StopAudioAsync(entry.Key).ConfigureAwait(false);
            if (!_audio.TryAdd(entry.Key, dest))
            {
                cts.Dispose();
                throw new InvalidOperationException(
                    $"{entry.DisplayName} is busy finishing a previous connection — try again in a moment");
            }
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
                dest.RemoteToken = IssueRemoteToken?.Invoke(entry.Key);
                var members = ResolveMembers(entry);
                // Nothing dialable. The usual cause is a device that has published only an IPv6
                // address: WinPlay's RTSP, RTP and PTP paths are all IPv4, so it is discovered and
                // listed but cannot be connected to. Left to GroupSession this surfaced as "group
                // has no connectable members", which the UI rendered as "Device not ready yet —
                // try again in a moment", inviting the user to retry something that will never
                // work. Say what is actually wrong instead.
                if (members.Count == 0) throw new ReceiverUnreachableException(entry.DisplayName);
                uint sharedStart = ReserveTimelineStart();
                var session = await ConnectAudioAsync(entry, members, dest.Cts.Token, dest.RemoteToken,
                    sharedStart).ConfigureAwait(false);

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

                await session.StartStreamingAsync(_mover.CreateCaptureSource(), volumeDb, TakeTimelineSlot)
                    .ConfigureAwait(false);

                // "Move" the audio like AirPlay on a Mac: capture the system mix and silence the
                // local speakers so only the receiver plays. Silenced at the CROSSOVER — after the
                // start sequence above has sent the anchor and started the pumps — never at the
                // click. The start sequence legitimately takes seconds (handshake, plus waiting
                // for the receiver's clock servo to converge before the anchor may be sent), and
                // muting up front turned all of it into a hole in the user's listening: local
                // audio gone at the click, remote audio not due yet, and the music that played
                // meanwhile skipped entirely. Local playback covers the whole connect instead;
                // the capture reads the pre-mute mix, so the moment of silencing has no effect on
                // what the receiver is sent. The silence is derived from active reception and
                // restored automatically (even after a crash).
                //
                // Guarded: a stop that landed during the start sequence has already run its
                // ExitStreaming, and silencing after it would hold the user's speakers muted with
                // nothing playing.
                if (!dest.Stopped && !dest.Cts.IsCancellationRequested)
                    _mover.EnterStreaming(entry.Key);
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
            await StopAudioAsync(entry.Key, only: dest).ConfigureAwait(false);
            if (superseded && ex is OperationCanceledException) return;
            throw;
        }
    }

    private async Task<GroupSession> ConnectAudioAsync(PickerEntry entry,
        IReadOnlyList<GroupSession.Member> members, CancellationToken ct, string? activeRemote = null,
        uint? sharedStartTimestamp = null)
    {
        try
        {
            return await ConnectAsync(entry, members, ct, activeRemote, sharedStartTimestamp).ConfigureAwait(false);
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
            return await ConnectAsync(entry, ResolveMembers(entry), ct, activeRemote, sharedStartTimestamp).ConfigureAwait(false);
        }
        catch (AggregateException ex) when (
            PinPrompt is not null && ex.InnerExceptions.OfType<PairingRequiredException>().Any())
        {
            await PairLeaderAsync(entry, ct).ConfigureAwait(false);
            return await ConnectAsync(entry, ResolveMembers(entry), ct, activeRemote, sharedStartTimestamp).ConfigureAwait(false);
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

                // Reconnection is bounded. A receiver that is coming back — a brief Wi-Fi blip,
                // a router reboot — returns well inside this. One that is switched off is not
                // coming back, and retrying it forever was the worst outcome available: the
                // destination stayed registered, so the PC's speakers stayed MUTED with nothing
                // playing anywhere, the row went on saying "Streaming system audio", and every
                // "reconnecting…" message was dropped before it reached the UI. Silence, a lie,
                // and no way out except noticing and toggling it off by hand.
                var giveUpAfter = TimeSpan.FromSeconds(90);
                long startedTicks = Environment.TickCount64;

                TimeSpan delay = TimeSpan.FromSeconds(2);
                while (!dest.Stopped && !dest.Cts.IsCancellationRequested)
                {
                    if (Environment.TickCount64 - startedTicks > (long)giveUpAfter.TotalMilliseconds)
                    {
                        // Report it and let go. Raising SessionFailed is what un-checks the row,
                        // and releasing the destination is what un-mutes the PC.
                        SessionFailed?.Invoke(dest.Entry.Key, StreamChannel.Audio,
                            new TimeoutException(
                                $"{dest.Entry.DisplayName} did not come back after "
                                + $"{giveUpAfter.TotalSeconds:F0} seconds — it may be switched off"));
                        await StopAudioAsync(dest.Entry.Key, only: dest).ConfigureAwait(false);
                        return;
                    }

                    SessionStage?.Invoke(dest.Entry.Key, $"connection lost — reconnecting in {delay.TotalSeconds:F0}s");
                    try { await Task.Delay(delay, dest.Cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        var members = ResolveMembers(dest.Entry);
                        // Rejoin the SHARED timeline, exactly as a first connect does. Reconnecting
                        // onto a fresh one would put this room back on a line of its own and the
                        // echo would return the first time a speaker blipped — the bug fixed once,
                        // reappearing through the recovery path nobody re-checked.
                        var session = await ConnectAudioAsync(dest.Entry, members, dest.Cts.Token,
                            dest.RemoteToken, ReserveTimelineStart()).ConfigureAwait(false);
                        if (dest.Stopped || dest.Cts.IsCancellationRequested)
                        {
                            await session.DisposeAsync().ConfigureAwait(false);
                            return;
                        }
                        dest.Session = session;
                        session.Faulted += _ => OnAudioFaulted(dest);
                        await session.StartStreamingAsync(_mover.CreateCaptureSource(), dest.VolumeDb,
                            TakeTimelineSlot).ConfigureAwait(false);
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

    // ---- one playback timeline for every destination ----
    //
    // All destinations render the SAME PC audio, so they must all sit on the same timeline: one
    // rtp base, one anchor instant. Each session used to invent its own, which meant two rooms
    // switched on a few seconds apart rendered the same sample a few seconds apart — the echo the
    // user hears standing between them.
    //
    // The timeline is WALL-LOCKED and lives exactly as long as the capture it describes — minted
    // once, never released, never reference-counted. Its origin is a wall instant; every session's
    // start offset is (flush instant − origin) converted to frames. That derivation cannot go
    // stale, because wall time has no lifecycle: it does not freeze between sessions, does not
    // accumulate across them, and cannot be released out from under a concurrent start. Two prior
    // designs failed here, each by deriving the timeline from state with a lifecycle of its own:
    // release-on-idle was a check-then-act race (two rooms, two timelines — the echo), and
    // reference counting fixed the race but left the START OFFSET coming from a demand-driven
    // frame counter — which only advances while a pump reads, so a session starting after an idle
    // gap inherited every previously-pumped frame as a phantom future offset, and the receiver
    // held dead air for exactly that long before shedding the rest ("plays once per launch, then
    // cuts, then silence, worse each session"). The wall clock is the one time base that is
    // correct for every session shape — first, repeat, late-join, and reconnect — by the same
    // arithmetic: a packet stamped (R + wallFrames(now) + n·352) against the anchor (R at
    // T₀ = origin + lead) is due at now + lead + n·352/rate, always.
    private readonly object _timelineLock = new();
    private uint? _timelineStart;
    private ulong? _timelineOriginNanos;
    private long _timelineCaptureGeneration;

    /// <summary>
    /// The shared rtp base, minted on first use per capture generation. Reserved BEFORE
    /// connecting — it rides in SETUP/RECORD — so whoever gets there first defines it and
    /// everyone else joins it, with no window for a second timeline.
    /// </summary>
    private uint ReserveTimelineStart()
    {
        lock (_timelineLock)
        {
            DiscardTimelineIfCaptureReplaced();
            _timelineStart ??= (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
            return _timelineStart.Value;
        }
    }

    /// <summary>
    /// Drops a timeline whose capture no longer exists — the positions it describes are meaningless
    /// for a rebuilt capture. The capture currently lives for the process lifetime, so this fires
    /// at most once; it exists so that invariant is enforced rather than assumed. Must be called
    /// under <see cref="_timelineLock"/>.
    /// </summary>
    private void DiscardTimelineIfCaptureReplaced()
    {
        long generation = _mover.CaptureGeneration;
        if (generation == _timelineCaptureGeneration) return;
        _timelineCaptureGeneration = generation;
        _timelineStart = null;
        _timelineOriginNanos = null;
    }

    /// <summary>
    /// Takes this session's slot on the shared timeline. Called by the session at the one instant
    /// the answer is valid: immediately after its capture branch flushed to live, just before the
    /// anchor is sent — so "the audio at the branch cursor" and "wall now" are the same moment.
    ///
    /// <para>First caller per capture generation fixes the origin (now) and the anchor
    /// (now + lead); every caller — including that first one — gets its start offset as wall time
    /// since the origin, in frames. The anchor is identical for every session that ever joins,
    /// which is what keeps every room rendering the same sample at the same instant.</para>
    /// </summary>
    private (ulong AnchorNanos, long StartPositionFrames) TakeTimelineSlot()
    {
        lock (_timelineLock)
        {
            DiscardTimelineIfCaptureReplaced();
            ulong now = WinPlay.Core.Ptp.MonotonicClock.NowNanoseconds;
            if (_timelineOriginNanos is not { } origin)
            {
                _timelineOriginNanos = origin = now;
            }
            // 44100 / 1e9 reduced to 441 / 1e7: exact same value, and the intermediate product
            // stays inside ulong for ~484 days of timeline age instead of overflowing after ~4.7.
            long spf = (long)((now - origin) * 441UL / 10_000_000UL);
            return (origin + RaopSession.BufferedLeadNanos, spf);
        }
    }

    /// <summary>
    /// Whether a registered destination is actually playing, as opposed to merely present.
    /// A destination in the reconnect loop has no live session; one that has been stopped is on
    /// its way out. Neither should make a fresh request silently do nothing.
    /// </summary>
    private static bool IsGenuinelyStreaming(AudioDestination dest) =>
        !dest.Stopped && !dest.Cts.IsCancellationRequested && dest.Session is not null;

    public Task StopAudioAsync(string key) => StopAudioAsync(key, only: null);

    /// <param name="only">
    /// When given, stop this destination and no other. A failed attempt cleaning up after itself
    /// must not remove whatever is registered under its key NOW: toggle a speaker off and quickly
    /// on again and the first attempt's own cleanup would tear down the second, live session — the
    /// row settled on "streaming", nothing played, and nothing was reported, because the dead
    /// attempt considered itself superseded and the stray stop raised no event at all. Identity,
    /// not the key, decides. (OnMirrorFaulted already reasons this way; the catch blocks did not.)
    /// </param>
    private async Task StopAudioAsync(string key, AudioDestination? only)
    {
        AudioDestination? dest;
        if (only is null)
        {
            if (!_audio.TryRemove(key, out dest)) return;
        }
        else
        {
            if (!_audio.TryGetValue(key, out dest) || !ReferenceEquals(dest, only)) return;
            if (!_audio.TryRemove(new KeyValuePair<string, AudioDestination>(key, only))) return;
        }
        WinPlayLog.For("Session").Information("{Key}: stopping audio session", key);
        dest.Stopped = true;
        dest.Cts.Cancel();                                  // kills any in-flight reconnect
        // BOUNDED wait for a reconnect mid-flight. Unbounded, a holder that is itself stuck
        // (a connect wedged inside a slow RTSP exchange) wedged this stop, and with it every
        // later start to the same destination — observed as "one stream broke, now nothing
        // streams anywhere". After the cancel above the holder is already doomed; five seconds
        // is courtesy, not correctness, and proceeding without the gate is the lesser evil.
        bool gated = await dest.Gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!gated)
            WinPlayLog.For("Session").Warning("{Key}: stop proceeding without the gate (holder stuck)", key);
        try
        {
            if (dest.Session is { } s) { dest.Session = null; await s.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            if (gated)
            {
                dest.Gate.Release();
                // Disposed only on the clean path. On the ungated path a wedged holder still owns
                // the source: disposing under it turns its next Token read into an
                // ObjectDisposedException that escapes paths which only expect cancellation —
                // observed as a silently dead reconnect loop. An undisposed CTS is a leak the GC
                // absorbs; a disposed-in-use one is a crash.
                dest.Cts.Dispose();
                dest.Gate.Dispose();
            }
            if (dest.RemoteToken is { } token) RevokeRemoteToken?.Invoke(token);
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
    /// arriving over DACP — so the picker's sliders track it.
    /// </summary>
    public event Action<string, double>? VolumeChangedExternally;

    /// <summary>
    /// One destination's current volume in AirPlay dB, or full scale if it is not streaming.
    /// Relative volume keys need the level of the speaker that pressed them, not the loudest one
    /// in the house.
    /// </summary>
    public double VolumeOf(string key) =>
        _audio.TryGetValue(key, out var dest) ? dest.VolumeDb
        : _mirrors.TryGetValue(key, out var mirror) ? mirror.VolumeDb
        : VolumeControl.MaxDb;

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

    /// <summary>
    /// Applies a volume to whichever session is actually carrying this destination's audio.
    ///
    /// <para>That is not always the audio session. In Screen+Audio the sound travels inside the
    /// MIRROR session, and this method used to look only among the audio destinations — so the
    /// slider moved and nothing happened, silently, because the destination it wanted was in the
    /// other dictionary.</para>
    /// </summary>
    /// <summary>
    /// Records a volume a receiver ANNOUNCED about itself: the desired level is remembered (so
    /// reconnects and the UI slider agree with the device) and the UI is synced — and nothing is
    /// written to any session, because the announcing device already has this volume.
    /// </summary>
    public void RecordExternalVolume(string key, double volumeDb)
    {
        if (_audio.TryGetValue(key, out var dest))
        {
            // A multi-member destination's announcements are MEMBER volumes arriving through one
            // shared origin token: a stereo pair's two speakers report two different levels, and
            // both land here indistinguishably. Recording them into the destination's single
            // volume made the row whipsaw between the speakers' truths — and worse, the next
            // write-back (slider touch, reconnect apply) pushed one speaker's level onto the
            // other, which is exactly "I set the knob to 100% and it reduced itself". Ambiguous
            // data is not applied: the destination volume stays what the USER last set. (The
            // faithful model — per-member tokens and per-member volume with the row as a group
            // master — is the follow-up; correctness first.)
            if (dest.Entry.Members.Count > 1)
            {
                WinPlayLog.For("Dacp").Debug(
                    "{Key}: member-level announcement ({Db:F1} dB) not applied to a {Count}-member destination",
                    key, volumeDb, dest.Entry.Members.Count);
                return;
            }
            dest.VolumeDb = volumeDb;
        }
        else if (_mirrors.TryGetValue(key, out var mirror)) mirror.VolumeDb = volumeDb;
        else return;
        VolumeChangedExternally?.Invoke(key, volumeDb);
    }

    public async Task SetVolumeAsync(string key, double volumeDb)
    {
        // A failed volume write is a WARNING, never a session fault. Raising SessionFailed here
        // handed the volume slider a self-destruct: one transient RTSP hiccup on a SET_PARAMETER
        // — a keep-alive holding the connection's request lock, a busy receiver — and the UI's
        // fault handling tore down a stream that was playing perfectly. Death detection belongs
        // to the /feedback keep-alive, which probes every 2 s and faults the session honestly
        // when the connection is actually gone; the volume path only needs to leave the desired
        // level recorded (above) so the next write or reconnect applies it.
        if (_audio.TryGetValue(key, out var dest))
        {
            dest.VolumeDb = volumeDb;
            if (dest.Session is { } s)
            {
                try { await s.SetVolumeAsync(volumeDb).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    WinPlayLog.For("Session").Warning(ex, "{Key}: volume write failed (stream unaffected)", key);
                }
            }
            return;
        }

        if (_mirrors.TryGetValue(key, out var mirror))
        {
            mirror.VolumeDb = volumeDb;
            if (mirror.Session is { } ms)
            {
                try { await ms.SetVolumeAsync(volumeDb).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    WinPlayLog.For("Session").Warning(ex, "{Key}: volume write failed (stream unaffected)", key);
                }
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

        /// <summary>Last volume applied, so Screen+Audio remembers it across a reconnect.</summary>
        public double VolumeDb { get; set; } = VolumeControl.MaxDb;
    }

    /// <summary>Starts mirroring the desktop to an Apple TV (pairs first if needed).</summary>
    /// <param name="includeAudio">
    /// Whether this mirror session also carries the PC's audio. Carrying it INSIDE the mirror
    /// session is the only way picture and sound stay locked: one session, one clock. Running a
    /// separate audio session to the same device alongside a mirror puts them on two clocks and
    /// they drift apart by construction, which is why the picker no longer lets that happen.
    /// </param>
    public async Task StartMirrorAsync(PickerEntry entry, bool includeAudio, CancellationToken ct)
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

            // Capture/encode runs in a supervised child process: a native GPU/encoder crash
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
            if (includeAudio && session.HasAudio && !_audio.ContainsKey(entry.Key))
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
            await StopMirrorAsync(entry.Key, only: dest).ConfigureAwait(false);
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

    public Task StopMirrorAsync(string key) => StopMirrorAsync(key, only: null);

    /// <param name="only">Stop only this destination — see the note on <see cref="StopAudioAsync"/>.</param>
    private async Task StopMirrorAsync(string key, MirrorDestination? only)
    {
        bool removed;
        MirrorDestination? m;
        if (only is null)
        {
            removed = _mirrors.TryRemove(key, out m);
        }
        else
        {
            removed = _mirrors.TryGetValue(key, out m) && ReferenceEquals(m, only)
                      && _mirrors.TryRemove(new KeyValuePair<string, MirrorDestination>(key, only));
        }
        if (removed && m is not null)
        {
            WinPlayLog.For("Session").Information("{Key}: stopping mirror session", key);
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
        IReadOnlyList<GroupSession.Member> members, CancellationToken ct, string? activeRemote = null,
        uint? sharedStartTimestamp = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            // BUFFERED AirPlay 2 audio (type 103 over TCP) is the transport;
            // GroupSession automatically downgrades any member without PTP to classic realtime.
            //
            // Getting here took three root causes, each proven on real hardware and each required:
            // 1. FORMAT — the buffered stream accepts a DIFFERENT format set than realtime.
            // Receivers publish it in GET /info: `audioStream` carries bit 18
            // (ALAC/44100/16/2), `bufferStream` does not — it carries bit 19
            // (ALAC/44100/24/2). Sending 16-bit on type 103 was offering a format the
            // stream never advertised. (RaopSession.AudioFormatAlac44100S24Stereo)
            // 2. CLOCK IDENTITY — the PTP grandmaster id was random per process, so every run
            // was a stranger's clock and receivers re-elected/converged from scratch.
            // Now a stable MAC-derived EUI-64, per IEEE 1588. (PtpMaster.StableClockIdentity)
            // 3. ANCHOR TIMING — a receiver discards a SETRATEANCHORTIME anchor that arrives
            // before its servo trusts the timeline (~6 s on first contact, measured), and it
            // is sent exactly once. The settle gate waits, event-driven, on the receiver's
            // own Delay_Req flow. (RaopSession.WaitForClockSettleAsync)
            //
            // A receiver failing any of these renders NOTHING while reporting NOTHING — every
            // handshake succeeds, keep-alives flow, `streams=[]` either way. That is why each had
            // to be established with controlled A/B listening tests rather than log reading.
            // Buffered AAC-LC wherever a room's speakers answer to nobody; REALTIME
            // for an Apple-TV-led room, whose speakers are the ATV's own outputs. Both boundaries
            // are measured on real hardware, not assumed: buffered to home-theatre speakers is cut
            // by their owner within seconds (audio died at ~3–5 s with both servos still slaved to
            // our clock), and audio-only THROUGH the ATV is accepted but never rendered on either
            // timing domain (PTP and NTP both tested; matches pyatv's four-year-open silent-audio
            // bug on this model, postlund/pyatv#1666 — the same ATV renders our mirror session's
            // audio, so the gate is tvOS policy, not session shape). Realtime to the speakers is
            // the one route with months of verified daily use — and on the shared wall-locked
            // grid it renders at the same declared lead as every buffered room, so cross-room
            // sync does not depend on which transport a room rides.
            bool appleTvLed = entry.Leader.Subtype == AirPlayDeviceSubtype.AppleTv;
            return await GroupSession.ConnectAsync(members,
                (memberName, stage) => ReportStage(entry.Key,
                    members.Count > 1 ? $"{memberName}: {stage}" : stage, ct),
                timeout.Token, buffered: !appleTvLed, identities: _identities,
                activeRemote: activeRemote, sharedStartTimestamp: sharedStartTimestamp).ConfigureAwait(false);
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
