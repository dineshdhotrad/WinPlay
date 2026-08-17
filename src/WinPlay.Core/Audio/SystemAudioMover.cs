// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Audio;

/// <summary>
/// Moves the PC's audio to AirPlay the way a Mac does: while a destination is actively capturing
/// the system mix, the local speakers are silenced (so only the receiver plays — no ~2 s echo).
///
/// <para>The mute is now strictly <em>derived</em> from active reception (<see cref="StreamStateModel"/>)
/// and crash-safe (<see cref="AudioStateGuardian"/>): it is never left applied when nothing is
/// streaming, and it is restored on stop, on process exit, on crash (via the persisted state),
/// and on the next launch. This replaces the old unconditional mute that could leave the system
/// muted after a crash.</para>
///
/// <para>Capture uses process-loopback (which taps before the endpoint, so it survives the mute).
/// If that is unavailable, it falls back to endpoint loopback with local silence disabled —
/// endpoint loopback captures <em>after</em> the mute, so muting would record silence.</para>
/// </summary>
public sealed class SystemAudioMover : IDisposable
{
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly StreamStateModel _state;
    private readonly AudioStateGuardian _guardian;
    private readonly Func<IAudioSource>? _innerCaptureFactory;
    private bool _processLoopbackSupported = true;

    // ---- one capture for the whole machine (fixes the multi-destination echo) ----
    //
    // Streaming to two independent destinations used to mean two independent WASAPI captures,
    // each with its own PositionedCaptureRing and its own timeline origin — so at rtp timestamp T,
    // destination A emitted one sample and destination B emitted a different one. Same clock, same
    // timeline, different content: the echo a listener hears standing between two rooms. There is
    // exactly one system audio mix; every destination must branch off ONE capture of it (via
    // BroadcastAudioSource), ref-counted so it survives one destination stopping while another
    // still streams, and is torn down only once none remain.
    private readonly object _captureLock = new();
    private BroadcastAudioSource? _sharedCapture;
    private int _captureRefCount;

    public SystemAudioMover(Action<string>? log = null)
        : this(new AudioStateGuardian(new WasapiEndpointController(), log: log)) { }

    /// <summary>Test seam: inject a guardian built over a fake endpoint.</summary>
    /// <summary>
    /// The user reached for the system volume while WinPlay had the local speakers silenced.
    /// Forwarded so their keys control the receiver, which is the only output they can hear.
    /// </summary>
    public event Action<float>? SystemVolumeChanged
    {
        add => _guardian.SystemVolumeChanged += value;
        remove => _guardian.SystemVolumeChanged -= value;
    }

    public SystemAudioMover(AudioStateGuardian guardian) : this(guardian, innerCaptureFactory: null) { }

    /// <summary>Test seam: inject a fake in place of the real WASAPI capture, so the shared-capture
    /// ref-counting and branch-offset behaviour of <see cref="CreateCaptureSource"/> can be verified
    /// without an audio device.</summary>
    internal SystemAudioMover(AudioStateGuardian guardian, Func<IAudioSource>? innerCaptureFactory)
    {
        _guardian = guardian;
        _state = new StreamStateModel(_guardian);
        _innerCaptureFactory = innerCaptureFactory;
        // Recover audio left muted by a previous session that died while streaming.
        _guardian.RestorePersistedIfPresent();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>Whether to silence local speakers while streaming (default true).</summary>
    public bool LocalSilenceWhileStreaming
    {
        get => _state.LocalSilenceRequested;
        set => _state.LocalSilenceRequested = value;
    }

    /// <summary>Marks a destination as actively capturing system audio → silences local speakers.</summary>
    public void EnterStreaming(string destinationKey)
    {
        if (_processLoopbackSupported) _state.SetActive(destinationKey, true);
    }

    /// <summary>Marks a destination as no longer capturing → unmutes once none remain.</summary>
    public void ExitStreaming(string destinationKey) => _state.SetActive(destinationKey, false);

    /// <summary>
    /// Hands a destination its branch of the ONE machine capture — creating that capture on the
    /// first call and ref-counting every call after it. Prefers process-loopback capture (excludes
    /// WinPlay itself, survives the endpoint mute); falls back to endpoint loopback if process
    /// loopback can't start, disabling local silence for this run so the capture is not itself
    /// muted.
    ///
    /// <para>The returned source's branch begins at whatever the shared capture's live position
    /// already is — zero for the first destination, a non-zero absolute frame offset
    /// (<see cref="IPositionedAudioSource.StartPositionFrames"/>) for one that joins while the
    /// capture is already feeding another destination. Every RTP session MUST fold that offset into
    /// its own timestamps (see RaopSession) — that is what keeps two destinations rendering the same
    /// sample at the same instant instead of each treating its own join moment as "frame 0".</para>
    ///
    /// <para>Dispose the returned source when the destination stops. The underlying capture is torn
    /// down only once every destination has done so.</para>
    /// </summary>
    /// <summary>
    /// Increments every time a NEW shared capture is built. A capture's sample positions start at
    /// zero, so a timeline anchored against a previous capture describes an origin that no longer
    /// exists: after the last destination stops and one reconnects, the anchor would still claim
    /// "the origin sample played N seconds ago" while the fresh capture reports position 0, and the
    /// two disagree permanently — total silence for that session, not a glitch it recovers from.
    /// A caller that caches an anchor must discard it when this changes.
    /// </summary>
    public long CaptureGeneration { get { lock (_captureLock) return _captureGeneration; } }

    private long _captureGeneration;

    public IAudioSource CreateCaptureSource()
    {
        lock (_captureLock)
        {
            if (_sharedCapture is null)
            {
                var inner = CreateInnerSource();

                // Do not hand out a capture that has not started producing yet. WASAPI returns a
                // client immediately but delivers nothing until the engine's first period, and a
                // pump that starts reading inside that window drains an empty ring and streams the
                // result — which arrives at the speaker as harsh clicking for the first second or
                // so. It is audible ONLY on the first stream after this object is built, since
                // every later one reuses a capture that is already running.
                //
                // 400 ms is far beyond any normal engine start (tens of ms) and still short enough
                // that a genuinely broken capture does not stall the connection. On timeout the
                // stream proceeds anyway: silence is a better failure than refusing to play.
                // On timeout this simply proceeds; the session's own silence detector reports a
                // capture that never produces, so nothing is lost by not logging here.
                (inner as IPrimeableCapture)?.WaitUntilPrimed(TimeSpan.FromMilliseconds(400));

                _sharedCapture = new BroadcastAudioSource(inner);
                _captureRefCount = 0;
                _captureGeneration++;
            }
            // No flush here, deliberately. The moment worth flushing at is immediately before
            // streaming begins — after the connect handshake and clock settle, which take seconds
            // — and that is exactly when the session's own FlushToLive runs. Flushing here as
            // well would be the same work done too early to matter: everything captured between
            // this call and the pump's first read would still become backlog.
            // BroadcastAudioSource's idle detection (production recency) makes the late flush
            // safe regardless of how many sessions came before.
            _captureRefCount++;
            return new RefCountedCapture(this, _sharedCapture.CreateBranch());
        }
    }

    /// <summary>Capture-layer recovery/failure reports (rebuilds, terminal death), for the log.</summary>
    public event Action<string>? CaptureDiagnostic;

    private IAudioSource CreateInnerSource()
    {
        if (_innerCaptureFactory is not null) return _innerCaptureFactory();
        if (_processLoopbackSupported)
        {
            try
            {
                var source = new ProcessLoopbackAudioSource(_ownPid);
                source.Diagnostic += d => CaptureDiagnostic?.Invoke(d);
                return source;
            }
            catch (Exception)
            {
                _processLoopbackSupported = false;
                _state.LocalSilenceRequested = false; // endpoint loopback records post-mute; don't mute
                _state.Reset();
            }
        }
        return new LoopbackAudioSource();
    }

    /// <summary>One destination's branch left. Tears the shared capture down once none remain, so
    /// it neither outlives every destination nor dies while any of them still needs it.</summary>
    private void ReleaseCaptureSource()
    {
        lock (_captureLock)
        {
            if (_sharedCapture is null) return; // already torn down (e.g. double-dispose)
            if (--_captureRefCount > 0) return;
            _captureRefCount = 0;
            // The capture is KEPT, not disposed, until the app exits.
            //
            // Tearing it down here meant every destination that was the last one out forced the
            // next connect to construct a fresh WASAPI process-loopback capture. That rebuild is
            // the one code path a SOLO speaker exercises constantly and a speaker kept alongside
            // others never touches at all — which is exactly the shape of the bug: the receivers
            // the user left connected always worked, and the one he switched on and off by itself
            // played on the first connect after launch and silence on the ones after. A rebuilt
            // capture that yields silence is invisible everywhere else: the session is live, the
            // pump is pacing, no audio is DROPPED, so no capture-damage counter moves — silence
            // is not damage, it is just nothing.
            //
            // Holding one capture for the process lifetime removes the re-initialisation entirely
            // rather than trying to make it reliable. It costs one idle loopback client, which is
            // what the app already holds for the whole time anything is streaming, and it is the
            // same object every session shares — so timeline positions stay on one origin too
            // (see StreamController's capture-generation guard, which now never has to fire).
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => _guardian.OnProcessExit();

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _state.Reset();          // unmute — nothing is streaming anymore
        _guardian.OnProcessExit();
        lock (_captureLock)
        {
            // Every destination is expected to have disposed its own branch by now (StreamController
            // stops all of them before disposing this). Guarded here too so a caller that skips that
            // sequence still can't leak the WASAPI client past the mover's own lifetime.
            _sharedCapture?.Dispose();   // disposes the inner capture too
            _sharedCapture = null;
            _captureRefCount = 0;
        }
    }

    /// <summary>
    /// The <see cref="IAudioSource"/> handed to each destination by <see cref="CreateCaptureSource"/>:
    /// a branch of the one machine-wide capture, wrapped so disposing it releases this destination's
    /// share (<see cref="ReleaseCaptureSource"/>) instead of tearing down the underlying WASAPI
    /// client out from under every other destination still streaming from it.
    /// </summary>
    private sealed class RefCountedCapture(SystemAudioMover owner, IAudioSource branch) :
        IAudioSource, IPositionedAudioSource, IFlushableAudioSource, ICaptureLatency, ICaptureDiagnostics,
        ICaptureAheadAudioSource
    {
        /// <inheritdoc />
        public long SamplesAheadOfCursor => (branch as ICaptureAheadAudioSource)?.SamplesAheadOfCursor ?? 0;

        private int _disposed;

        /// <summary>
        /// Forwards the branch's capture-health counters. This wrapper silently dropping the
        /// diagnostics interface meant every session in the app read permanent zeros — "all
        /// counters healthy" while audio audibly failed was this missing forward, not health.
        /// The latency forward below exists for exactly the same reason; the same rule applies to
        /// every measurement interface a wrapper sits in front of.
        /// </summary>
        public (long UnderrunFrames, long LateFrames, long GapJumps) CaptureStats =>
            (branch as ICaptureDiagnostics)?.CaptureStats ?? (0, 0, 0);

        /// <inheritdoc />
        public long StartPositionFrames => (branch as IPositionedAudioSource)?.StartPositionFrames ?? 0;

        /// <summary>Forwards the branch's reported capture latency (0 if it cannot report one) — see
        /// <see cref="ICaptureLatency"/>'s doc on why wrappers must not silently drop this.</summary>
        public double CaptureLatencySeconds => (branch as ICaptureLatency)?.CaptureLatencySeconds ?? 0;

        public void Read(Span<short> interleavedStereo) => branch.Read(interleavedStereo);

        public void FlushToLive() => (branch as IFlushableAudioSource)?.FlushToLive();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent — never double-release
            branch.Dispose();
            owner.ReleaseCaptureSource();
        }
    }
}
