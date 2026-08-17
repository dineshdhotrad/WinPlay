// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;
using WinPlay.Core.Net;
using WinPlay.Core.Plist;
using WinPlay.Core.Ptp;
using WinPlay.Core.Rtsp;

namespace WinPlay.Core.Raop;

/// <summary>
/// One AirPlay 2 realtime audio session to a single receiver (owntone-parity order):
/// GET /info → transient pair-setup → encrypted RTSP → SETUP (session) → event channel →
/// RECORD → SETPEERS (PTP only) → SETUP (stream) → RTP/UDP audio + sync + /feedback
/// keep-alive → SET_PARAMETER volume → TEARDOWN.
/// Timing: PTP mode (HomePods/Apple TV, feature bit 41) makes us grandmaster via the
/// shared <see cref="PtpMaster"/> — required for stereo pairs and multi-room, where the
/// leader relays audio to members clocked against our PTP time. NTP mode (third-party
/// receivers) uses the classic timing-port request/response exchange.
/// </summary>
public sealed class RaopSession : IAsyncDisposable
{
    private const int LatencyFrames = 88200; // ~2 s at 44.1 kHz (classic realtime latency)
    private const int SampleRate = 44100;

    private readonly RtspConnection _rtsp;
    private readonly NtpClock _clock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private readonly string _sessionUri;
    private readonly ulong _streamConnectionId;
    private uint _ssrc;   // buffered: the codec tag; adjusted when AAC attaches post-construction
    private readonly bool _usePtp;
    private readonly bool _buffered;
    private TcpClient? _audioTcp;
    private NetworkStream? _audioTcpStream;
    private BufferedAudioPacket? _bufferedCrypto;
    // Shared playout lead: 1.75 s (77175 frames at 44.1 kHz) — Apple's own operating point,
    // straight from captured Apple senders (next_rtp − sync_rtp = 77175 = 441 × 175 in UxPlay's
    // documented capture of the ALAC stream). Every room — buffered anchor and realtime 0xD7
    // declaration alike — renders at this one lead on the one wall-locked grid, which is what
    // keeps the whole home phase-locked regardless of transport.
    //
    // The history of this constant is a ladder of measured failures at smaller values: 0.35 s
    // sounded perfect on an idle network and produced audible cuts under ordinary load; 0.75 s
    // (a reference receiver's decode-buffer default, not Apple's number) ran clean in steady
    // state and collapsed at the first disturbance — a volume SET_PARAMETER, a second room's
    // handshake — with the session protocol-healthy throughout, because every stall longer than
    // the cushion lands in the audible band and a receiver pushed under its design margin stops
    // rendering rather than limping. Apple ships 1.75 s because that is what survives real rooms
    // on real Wi-Fi; matching it is engineering, not taste.
    private const int BufferedLeadFrames = 77175;

    /// <summary>The shared playout lead in nanoseconds (1.75 s at 44.1 kHz).</summary>
    public static ulong BufferedLeadNanos => (ulong)((double)BufferedLeadFrames / SampleRate * 1_000_000_000);

    /// <summary>True if this session negotiated a buffered (type 103) audio stream.</summary>
    public bool IsBuffered => _buffered;
    private readonly List<IPAddress> _groupPeers = [];
    private HapPairingCredentials? _credentials;
    // The group identity this SENDER is forming: one UUID per playback session, identical across
    // every member so they synchronise as one group. Never derived from what the receiver
    // advertises about itself — see ResolveGroupUuid.
    private string? _sessionGroupUuid;
    private PtpMaster? _ptp;
    private readonly List<IPAddress> _ptpPeers = [];

    private HapSession? _hap;
    private Socket? _timingSocket;
    private Socket? _controlSocket;
    private Socket? _audioSocket;
    private TcpClient? _eventTcp;
    private IPEndPoint? _receiverData;
    private IPEndPoint? _receiverControl;
    private AudioPacketCrypto? _audioCrypto;
    private Thread? _pumpThread;
    private volatile bool _stopped;

    private ushort _sequence;
    private uint _startTimestamp;

    // This session's absolute offset (in frames) on the SHARED capture timeline — see
    // WinPlay.Core.Audio.IPositionedAudioSource. Zero for the ordinary case (this session's
    // capture branch began at the shared timeline's own frame 0); non-zero when the branch is a
    // LATER join onto a machine capture already feeding another destination.
    //
    // _startTimestamp alone answers "what rtp anchors this session's timeline" — declared to the
    // receiver ONCE via RECORD and (buffered) SendBufferedAnchorAsync, and identical for every
    // destination sharing this timeline. It must NOT be shifted by _startPositionFrames: the
    // anchor's promise is "rtp _startTimestamp plays at the anchor instant", and that promise is
    // about the SHARED timeline's origin, not about whatever sample this particular session
    // happens to send first.
    //
    // _startPositionFrames answers the different question "what rtp is THIS session's own first
    // emitted frame" — _startTimestamp + _startPositionFrames, because that frame is not the
    // timeline's sample 0, it is the shared capture's sample _startPositionFrames (that's what its
    // branch actually starts producing). Conflating the two is exactly the multi-destination echo:
    // a session joining a capture already in progress would otherwise stamp its own (unrelated)
    // first sample as if it were the timeline's origin sample, and a receiver rendering that
    // against the anchor plays the wrong instant of audio.
    //
    // Set once by StartPump, before the pump thread starts; every per-packet rtp computation goes
    // through FrameTimestamp so the two values can never be accidentally conflated at a call site.
    private long _startPositionFrames;
    private long _framesSent;
    private long _audioSendFailures;

    // Recent-packet history for receiver retransmit requests (PT 0xD5 → reply 0xD6).
    private const int ResendRingSize = 1024;
    private readonly (ushort Seq, byte[]? Packet)[] _resendRing = new (ushort, byte[]?)[ResendRingSize];
    private readonly object _resendLock = new();

    public event Action<string>? StageChanged;

    /// <summary>
    /// SETUP(stream) <c>audioFormat</c> for ALAC 44.1 kHz / 16-bit / stereo — bit 18 of the
    /// AirPlay 2 audio-format bitfield (owntone <c>airplay.c</c>'s format table).
    /// </summary>
    private const long AudioFormatAlac44100S16Stereo = 0x40000L;

    /// <summary>
    /// ALAC 44.1 kHz / 24-bit / stereo — bit 19. The BUFFERED stream's format.
    ///
    /// <para>The receivers publish their accepted formats per stream: `audioStream` (realtime) is
    /// 0x1440800, which contains bit 18; `bufferStream` is 0xF7FE018E00E80000, which does NOT
    /// contain bit 18 but does contain bit 19 — the same 44.1 kHz stereo audio at 24 bits. Bit 18
    /// is a realtime-only format, so sending it on type 103 offered the buffered stream something
    /// it never advertised, and the receiver silently rendered nothing.</para>
    /// </summary>
    private const long AudioFormatAlac44100S24Stereo = 0x80000L;   // 1 << 19

    private const long AudioFormatIndexAlac44100S24Stereo = 19L;

    /// <summary>Reverse-DNS client id, as real senders send on the buffered path.</summary>
    private const string ClientBundleId = "com.dineshdhotrad.winplay";

    /// <summary>
    /// The SSRC carried by every BUFFERED audio packet. It is not a synchronisation source in the
    /// RTP sense at all — on stream type 103 this field is the CODEC TAG, and the receiver uses it
    /// to decide how to decode the payload.
    ///
    /// <para>Encoding, cross-derived from two references that each hold half of it: owntone lists
    /// the <c>audioFormat</c> bit indices (18 = ALAC/44100/16/2, 21 = ALAC/48000/24/2,
    /// 22 = AAC-LC/44100/2, 23 = AAC-LC/48000/2) and shairport-sync lists the SSRCs it observes on
    /// the wire for the latter three (0x15000000, 0x16000000, 0x17000000). Those line up exactly as
    /// <c>bit index &lt;&lt; 24</c>, so bit 18 gives 0x12000000.</para>
    ///
    /// <para><b>Why this was a silent failure.</b> This field was 0, which shairport-sync names
    /// <c>SSRC_NONE</c> and treats as unrecognised: the packet is read, counted, and discarded
    /// WITHOUT being decrypted or queued — no error, no teardown, no log, the receiver simply never
    /// makes a sound. The realtime path is immune because it hard-codes the codec and never reads
    /// this offset, which is precisely why a synthetic tone played over realtime and was silent
    /// over buffered on the same speaker, minutes apart. Of every field in the session, this is the
    /// only one with that asymmetry.</para>
    /// </summary>
    private const uint BufferedCodecSsrc = 0x13000000;   // 19 << 24, ALAC/44100/24/2

    /// <summary>AAC-LC 44.1 kHz stereo — bit 22, the buffered format every captured Apple sender uses.</summary>
    private const long AudioFormatAacLc44100Stereo = 0x400000L;   // 1 << 22
    private const long AudioFormatIndexAacLc44100Stereo = 22L;
    private const uint BufferedAacSsrc = 0x16000000;              // 22 << 24

    /// <summary>Samples per packet: 1024 for AAC access units, 352 for ALAC/realtime.</summary>
    private int FramesPerPacket => _aacEncoder is not null ? 1024 : 352;

    // Non-null ⇒ this buffered session carries AAC-LC access units instead of ALAC frames — the
    // stream shape Apple's own senders use, and the only one an Apple TV renders for audio-only.
    private IAacFrameEncoder? _aacEncoder;
    private readonly Queue<uint> _aacRtpFifo = new();
    private ulong _nonceCounter;

    /// <summary>
    /// Human-readable name for an <c>audioFormat</c> bit, per the AirPlay 2 audio-format bitfield.
    /// </summary>
    private static string AudioFormatName(int bit) => bit switch
    {
        18 => "ALAC/44100/16/2",
        19 => "ALAC/44100/24/2",
        20 => "ALAC/48000/16/2",
        21 => "ALAC/48000/24/2",
        22 => "AAC-LC/44100/2",
        23 => "AAC-LC/48000/2",
        _ => $"bit {bit}",
    };

    /// <summary>
    /// Logs which audio formats the receiver actually accepts, per stream type, from its own
    /// <c>GET /info</c>.
    ///
    /// <para>The receiver advertises an <c>audioFormats</c> array of dicts, each carrying a
    /// <c>type</c> (96 = realtime, 103 = buffered) and <c>audioInputFormats</c> / </c>
    /// <c>audioOutputFormats</c> bitmasks over the audio-format bitfield. This codebase never read
    /// it and instead hardcoded ALAC/44100/16/2 for both stream types — so a receiver that does
    /// not accept that format on type 103 had no way to say so, and simply held the session open
    /// and rendered nothing. Reading it turns "which format does this speaker want" from a guess
    /// into a fact the device itself supplies.</para>
    /// </summary>
    private void ReportAudioFormats(Dictionary<string, object?> infoDict)
    {
        if (infoDict.GetValueOrDefault("supportedFormats") is not Dictionary<string, object?> legacy) return;
        long audio = ToLong(legacy.GetValueOrDefault("audioStream"));
        long buffer = ToLong(legacy.GetValueOrDefault("bufferStream"));
        Stage($"receiver formats: audioStream=0x{audio:X} [{Bits(audio)}] bufferStream=0x{buffer:X} [{Bits(buffer)}]");
    }

    private static long ToLong(object? v) => v is null ? 0L : Convert.ToInt64(v);

    private static string Bits(long mask)
    {
        var names = new List<string>();
        for (int bit = 0; bit < 48; bit++)
            if ((mask & (1L << bit)) != 0 && bit is 18 or 19 or 20 or 21 or 22 or 23)
                names.Add($"{bit}={AudioFormatName(bit)}");
        return names.Count > 0 ? string.Join(", ", names) : "none of the known audio bits";
    }


    public long FramesSent => Interlocked.Read(ref _framesSent);
    public TimeSpan Elapsed => TimeSpan.FromSeconds(FramesSent * 352.0 / SampleRate);

    /// <summary>
    /// Capture-health counters for the source this session is pumping, or null when the source
    /// cannot report them. Exposed so a caller can log audible damage as it happens: clicks and
    /// dropouts are the one class of fault that leaves no trace in the RTSP exchange, so a session
    /// that reports a clean handshake, a live stream and steady keep-alives can still be producing
    /// audio the user describes as broken — with nothing anywhere to confirm or deny it.
    /// </summary>
    public (long UnderrunFrames, long LateFrames, long GapJumps)? CaptureStats =>
        (Volatile.Read(ref _pumpSource) as ICaptureDiagnostics)?.CaptureStats;

    private IAudioSource? _pumpSource;
    private long _lastReportedLateFrames;

    // ---------------------------------------------------------------- timeline grid
    //
    // Wall instant (MonotonicClock) at which the shared timeline's rtp base plays "frame zero".
    // Zero ⇒ no shared timeline (test harness, mirror-internal audio): the pump paces off its own
    // stopwatch, exactly as before.
    //
    // WHY A GRID: with per-pump stopwatch pacing, a session's stamps lag wall time by however
    // long its own pump took to start after its capture flushed — anchor round-trip plus thread
    // start, tens of milliseconds, DIFFERENT for every room. Each room therefore rendered the
    // shared audio at lead + itsOwnSkew: rooms played the same music 30–80 ms apart, an audible
    // flam no receiver-side clock discipline can remove, because the error is baked into which
    // sample carries which stamp. On the grid, packet k — in every session, both transports —
    // carries exactly rtp (base + k·352) and is read and sent when the wall clock crosses that
    // very frame. Per-session skew is not reduced; it is unrepresentable.
    private ulong _timelineOriginNanos;

    private const ulong NanosPerFrameNum = 10_000_000UL;   // 1e9 / 44100 reduced: 10_000_000 / 441
    private const ulong NanosPerFrameDen = 441UL;

    private long WallFramesNow() =>
        (long)((MonotonicClock.NowNanoseconds - _timelineOriginNanos) * NanosPerFrameDen / NanosPerFrameNum);

    private ulong GridDueNanos(long packetIndex) =>
        _timelineOriginNanos + (ulong)packetIndex * (ulong)FramesPerPacket * NanosPerFrameNum / NanosPerFrameDen;

    /// <summary>
    /// Aligns a pump to the timeline grid at startup: re-aims the session's capture cursor to the
    /// live edge — so the backlog between the session's earlier flush and this thread actually
    /// running is skipped, not played as a permanent per-room offset — and returns the first grid
    /// slot safely in the future.
    ///
    /// <para>Alignment is a CURSOR MOVE, never a read. An earlier version read-and-discarded the
    /// backlog, and those reads travel through the shared capture tee: the burst dragged the one
    /// shared device-ring reader half a second ahead of real time for EVERY room at once — a room
    /// already playing had ~230 ms torn out of its live audio the moment another room joined
    /// (caught by the capture-health counters as LateFrames). A flush moves only this session's
    /// own cursor; sessions may never consume shared media to align themselves.</para>
    /// </summary>
    private long StartOnGrid(IAudioSource source, Span<short> scratch)
    {
        _ = scratch; // alignment must not read — see above
        (source as IFlushableAudioSource)?.FlushToLive();
        // The stamp base is the CAPTURE POSITION at the flush instant, not a slot index. The
        // cursor now sits at live = wall frame W, so packet n's content is exactly the audio
        // captured at W + n·spf — and that, not "the slot we happened to send it in", is what
        // its rtp stamp must say. Slot-based stamps carried each pump's private send offset
        // (+2 slots × its packet size) into the timestamps themselves: rooms with different
        // packet sizes rendered the same captured instant tens of milliseconds apart — a
        // structural cross-room flam no receiver could correct. Capture-based stamps make
        // "content captured at t renders at t + lead" an identity in every room, on every
        // transport, at any packet size, through any encoder latency.
        _gridStampFrames = WallFramesNow();
        return _gridStampFrames / FramesPerPacket + 2;   // first SEND slot: ~2 slots of lead
    }

    private long _gridStampFrames;   // capture-true rtp offset of the NEXT packet's content

    /// <summary>Sleeps (1 ms granularity via timeBeginPeriod, final spin) until the wall instant.</summary>
    private void WaitUntilNanos(ulong dueNanos)
    {
        while (!_stopped)
        {
            ulong now = MonotonicClock.NowNanoseconds;
            if (now >= dueNanos) return;
            ulong remainingMs = (dueNanos - now) / 1_000_000UL;
            if (remainingMs >= 3) Thread.Sleep((int)remainingMs - 1);
            else if (remainingMs >= 1) Thread.Sleep(1);
            else Thread.SpinWait(50);
        }
    }
    private long _silentPackets;
    private bool _silenceReported;

    /// <summary>
    /// Reports a capture that is delivering nothing but digital silence for long enough that it
    /// cannot be explained by the user simply not playing anything.
    ///
    /// <para>Silence is the one failure that leaves no trace anywhere else. A dead capture still
    /// paces correctly, still encodes, still sends well-formed packets, still holds PTP, still
    /// answers keep-alives, and drops no audio — so every counter reads healthy while the room is
    /// quiet. That is precisely how a capture that failed to re-initialise stayed invisible across
    /// an entire evening of debugging, with attention going to the protocol instead. Ten seconds
    /// of unbroken zeroes is reported once per session, at a level a normal run records.</para>
    /// </summary>
    private void ReportIfCaptureIsSilent(ReadOnlySpan<short> samples)
    {
        if (_silenceReported) return;

        bool silent = true;
        foreach (short s in samples)
            if (s != 0) { silent = false; break; }

        if (!silent) { _silentPackets = 0; return; }

        // 10 seconds of unbroken zeros, independent of packet size (352-frame ALAC ≈ 8 ms,
        // 1024-frame AAC ≈ 23 ms per packet).
        if (++_silentPackets < 10 * SampleRate / FramesPerPacket) return;
        _silenceReported = true;
        Stage("capture is delivering silence — the PC is producing no audio, or the capture failed to start");
    }

    private RaopSession(bool usePtp, bool buffered, string? activeRemote)
    {
        // Carried into every RTSP request, and echoed back by the receiver on each DACP command —
        // which is how a volume change from THIS speaker is told apart from any other room's.
        _rtsp = new RtspConnection(activeRemote);

        // One session id serves as RTSP URI number and streamConnectionID — receivers
        // correlate the RTP flow with the announced stream through it. The RTP SSRC is
        // the same id in NTP mode but ZERO in PTP mode (owntone parity — iOS senders
        // leave it zero for PTP-timed realtime streams).
        uint sessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _usePtp = usePtp;
        _buffered = buffered && usePtp; // buffered audio requires the PTP timeline
        _streamConnectionId = sessionId;
        _ssrc = _buffered ? BufferedCodecSsrc : usePtp ? 0 : sessionId;
        // (Adjusted after construction when AAC is attached — see ConnectAsync.)
        _sequence = (ushort)RandomNumberGenerator.GetInt32(0, ushort.MaxValue);
        _startTimestamp = (uint)RandomNumberGenerator.GetInt32(0, int.MaxValue);
        _sessionUri = sessionId.ToString();
    }

    /// <param name="groupPeers">
    /// Addresses of the OTHER members of the receiver's group/stereo pair. They join
    /// the SETPEERS timing group and are served by our PTP grandmaster — a stereo-pair
    /// partner plays nothing without a clock, even though the leader relays the audio.
    /// </param>
    /// <param name="credentials">
    /// Stored PIN-pairing credentials for this receiver. When present, authentication
    /// uses the fast pair-verify handshake; otherwise transient pair-setup, which
    /// PIN-protected receivers reject with <see cref="PairingRequiredException"/>.
    /// </param>
    /// <param name="sessionGroupUuid">
    /// The group identity THIS SENDER is forming — one UUID per playback session, passed
    /// unchanged to every member so they synchronise as one group. Null mints a fresh one.
    /// Never the receiver's advertised `gid`; see <see cref="ResolveGroupUuid"/>.
    /// </param>
    public static async Task<RaopSession> ConnectAsync(IPAddress address, int port, bool usePtp,
        IReadOnlyList<IPAddress>? groupPeers = null, Action<string>? stageChanged = null,
        CancellationToken ct = default, HapPairingCredentials? credentials = null, bool buffered = false,
        uint? sharedStartTimestamp = null, string? activeRemote = null,
        string? sessionGroupUuid = null, IAacFrameEncoder? aacEncoder = null)
    {
        var s = new RaopSession(usePtp, buffered, activeRemote)
        {
            _credentials = credentials,
            _sessionGroupUuid = sessionGroupUuid,
        };
        if (buffered && usePtp && aacEncoder is not null)
        {
            s._aacEncoder = aacEncoder;
            s._ssrc = BufferedAacSsrc;   // the type-103 codec tag names the payload codec
        }
        // A buffered group shares ONE start timestamp across all members so the same audio
        // sample carries the same RTP time everywhere and (with the shared anchor) plays in sync.
        if (sharedStartTimestamp is { } sts) s._startTimestamp = sts;
        if (groupPeers is not null)
            s._groupPeers.AddRange(groupPeers.Where(p => !p.Equals(address)));
        if (stageChanged is not null) s.StageChanged += stageChanged;
        try
        {
            await s.HandshakeAsync(address, port, ct).ConfigureAwait(false);
            return s;
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Whether an RTSP failure during pair-verify means "I do not know you" rather than a
    /// transport problem. 401 and 403 are the receiver refusing this controller's identity; 470
    /// is its request to pair afresh. Anything else — a timeout, a 5xx, a dropped connection — is
    /// not evidence the stored credentials are dead, and must not cause them to be discarded.
    /// </summary>
    private static bool IsPairingRejection(RtspException ex) =>
        ex.StatusCode is 401 or 403 or 470;

    private void Stage(string message) => StageChanged?.Invoke(message);

    private async Task HandshakeAsync(IPAddress address, int port, CancellationToken ct)
    {
        Stage($"connecting to {address}:{port}");
        await _rtsp.ConnectAsync(address, port, ct).ConfigureAwait(false);

        Stage("GET /info");
        var info = await _rtsp.RequestAsync(new RtspRequest { Method = "GET", Uri = "/info" }, ct)
            .ConfigureAwait(false);
        info.EnsureSuccess("GET /info");
        var infoDict = BinaryPlist.ReadDictionary(info.Body);
        Stage($"receiver: {infoDict.GetValueOrDefault("name")} ({infoDict.GetValueOrDefault("model")})");
        ReportAudioFormats(infoDict);

        if (_credentials is not null)
        {
            Stage("pair-verify (stored credentials, X25519 + Ed25519)");
            try
            {
                _hap = await HapVerifiedPairing.PairVerifyAsync(
                    ReceiverPairing.MakePost(_rtsp), _credentials, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HapPairingException
                                       || (ex is RtspException rtsp && IsPairingRejection(rtsp)))
            {
                // Stored credentials no longer work — the receiver was reset, restored, or
                // re-paired elsewhere, so it has forgotten this controller. Previously this
                // threw straight out with a technical message and NOTHING cleared the stale
                // credentials, so the device failed every future attempt forever and the only
                // fix was deleting credentials.dat by hand. Report it as re-pairing required so
                // the caller can discard them and pair again.
                //
                // Both shapes count. A receiver can reject a forgotten pairing with a TLV error
                // inside a 200 (HapPairingException) OR by refusing the request outright at the
                // RTSP status line (RtspException). Catching only the first left the second on
                // exactly the dead-end path this was written to remove — the user saw a raw
                // "POST /pair-verify: 401" and the device never worked again.
                Stage($"stored pairing rejected ({ex.Message}) — re-pairing required");
                throw new StalePairingException($"{address}", ex);
            }
        }
        else
        {
            Stage("transient pair-setup (SRP-6a, PIN 3939)");
            try
            {
                _hap = await HapTransientPairing.PairAsync(async (tlv, token) =>
                {
                    var resp = await _rtsp.RequestAsync(new RtspRequest
                    {
                        Method = "POST",
                        Uri = "/pair-setup",
                        Body = tlv,
                        ContentType = "application/octet-stream",
                        Headers = { ["X-Apple-HKP"] = "4" },
                    }, token).ConfigureAwait(false);
                    resp.EnsureSuccess("POST /pair-setup");
                    return resp.Body;
                }, ct).ConfigureAwait(false);
            }
            // 470 and 401 are NOT the same thing and must not be conflated:
            // 470 Connection Authorization Required — the receiver wants on-screen PIN
            // pairing (Apple TV). Answering with the PIN flow is correct.
            // 401 Unauthorized — the receiver's access control refused this sender. A
            // HomePod has no screen and never shows a PIN, so starting the PIN flow here
            // fails instantly and tells the user nothing. Surfaced separately so the app
            // can name the exact setting to change.
            catch (RtspException ex) when (ex.StatusCode == 470)
            {
                throw new PairingRequiredException($"{address}");
            }
            catch (RtspException ex) when (ex.StatusCode == 401)
            {
                throw new ReceiverAccessDeniedException($"{address}");
            }
        }

        Stage("control channel encrypted (ChaCha20-Poly1305)");
        _rtsp.EnableEncryption(new ChannelCrypto(_hap.ControlWriteKey, _hap.ControlReadKey));

        // UDP sockets: timing responder, control (sync out / resend in), audio data out.
        _timingSocket = BindUdp();
        _controlSocket = BindUdp();
        _audioSocket = BindUdp();

        // The receiver probes the announced timingPort DURING session SETUP and answers
        // 400 after ~30 s if nothing responds — these listeners must run before SETUP.
        lock (_loops) _loops.Add(Task.Run(() => TimingLoopAsync(_cts.Token)));
        lock (_loops) _loops.Add(Task.Run(() => ControlReceiveLoopAsync(_cts.Token)));

        string mac = LocalMacAddress();
        string sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        Dictionary<string, object?> sessionPayload;
        if (_usePtp)
        {
            _ptp = PtpMaster.Shared;
            _ptp.Diagnostic += Stage;
            string groupUuid = ResolveGroupUuid(_sessionGroupUuid);
            Stage($"SETUP (session, PTP grandmaster clock 0x{_ptp.ClockId:X16}, group {groupUuid})");
            string localAddress = _rtsp.LocalAddress.ToString();
            // timingPeerInfo appears twice (dict + single-element list); build separate
            // instances so the plist writer doesn't have to handle a shared reference.
            Dictionary<string, object?> PeerInfo() => new()
            {
                ["ID"] = _ptp.ClockUuid,
                ["DeviceType"] = 0L,
                ["ClockID"] = unchecked((long)_ptp.ClockId),
                ["SupportsClockPortMatchingOverride"] = false,
                ["Addresses"] = new List<object?> { localAddress },
            };
            sessionPayload = new Dictionary<string, object?>
            {
                ["name"] = "WinPlay",
                ["deviceID"] = mac,
                ["sessionUUID"] = sessionUuid,
                ["timingProtocol"] = "PTP",
                ["macAddress"] = mac,
                ["groupUUID"] = groupUuid,
                // ALWAYS false, for every topology WinPlay ever streams to (solo, stereo pair,
                // multi-room group, ATV-led home theatre) — never derived from the receiver's own
                // mDNS `gcgl` self-report. Every reference sender examined agrees on this:
                // owntone-server (src/outputs/airplay.c, payload_make_setup_session_ptp) hardcodes
                // `false` unconditionally with the comment "iOS Music app sets this to false, let's
                // roll with that", and pyatv (protocols/raop/protocols/airplayv2.py) does the same.
                // Neither varies it by destination topology, because groupContainsGroupLeader is
                // not how AirPlay-2 native multi-speaker groups are formed by a THIRD-PARTY sender
                // in the first place — real grouped playback (a HomePod relaying to its stereo-pair
                // partner) is Apple's own controller-to-receiver group-management flow, which no
                // sender in this codebase's reference set implements or needs to. WinPlay's actual
                // multi-member sync (stereo pairs, multi-room groups) is carried entirely by PTP +
                // SETPEERS + a shared start timestamp/anchor (see GroupSession), independent of this
                // field — confirmed on real hardware: the stereo pair and 3-member group this
                // project is verified against already play correctly with this hardcoded false.
                // The one receiver that needed different treatment (a HomePod mini, "Guest
                // Bedroom", stuck believing — via mDNS igl=1/gcgl=1 — that it already belongs to a
                // group with a leader) turned out to need its group IDENTITY honoured (groupUuid,
                // above), not a false promise that this one-member session has a second, leader
                // role to fill — sending `groupContainsGroupLeader: true` for a session with no
                // second member is exactly the "connects, streams, plays no audio" bug this
                // superseded: the receiver waited for a group participant that would never arrive.
                ["groupContainsGroupLeader"] = false,
                ["timingPeerInfo"] = PeerInfo(),
                ["timingPeerList"] = new List<object?> { PeerInfo() },
            };
        }
        else
        {
            // The full identity set the WORKING NTP path sends — WinPlay's own mirror session
            // renders audio through an Apple TV with exactly this shape, and pyatv sends the same
            // four capability booleans. The four-key minimal payload was a shape no working sender
            // uses, and an Apple TV that accepts it never renders the audio.
            Stage("SETUP (session, NTP)");
            sessionPayload = new Dictionary<string, object?>
            {
                ["deviceID"] = mac,
                ["macAddress"] = mac,
                ["sessionUUID"] = sessionUuid,
                ["timingPort"] = (long)LocalPort(_timingSocket),
                ["timingProtocol"] = "NTP",
                ["name"] = _rtsp.ClientName,
                ["model"] = "PC1,1",
                ["osName"] = "Windows",
                ["osVersion"] = Environment.OSVersion.Version.ToString(2),
                ["osBuildVersion"] = Environment.OSVersion.Version.Build.ToString(),
                ["sourceVersion"] = "550.10",
                ["isMultiSelectAirPlay"] = true,
                ["groupContainsGroupLeader"] = false,
                ["senderSupportsRelay"] = false,
                ["statsCollectionEnabled"] = false,
            };
        }
        var sessionSetup = await PlistRequestAsync("SETUP", sessionPayload, ct).ConfigureAwait(false);

        long eventPort = sessionSetup.TryGetValue("eventPort", out object? ep) && ep is long e ? e : 0;
        Stage($"event channel → port {eventPort}");
        if (eventPort > 0)
            StartEventChannel(_rtsp.RemoteAddress, (int)eventPort);

        if (_usePtp)
        {
            // The receiver's SETUP reply tells us which of its addresses wants to be
            // clock-slaved (timingPeerInfo.Addresses); group/pair members also need
            // our clock — a partner with no clock stays silent even with relayed audio.
            var timingPeer = ExtractTimingPeer(sessionSetup) ?? _rtsp.RemoteAddress;
            foreach (var peer in new[] { timingPeer }.Concat(_groupPeers).Distinct())
            {
                _ptpPeers.Add(peer);
                _ptp!.AddPeer(peer);
            }
        }

        Stage("RECORD");
        var record = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "RECORD",
            Uri = RtspUri,
            Headers =
            {
                ["Range"] = "npt=0-",
                ["RTP-Info"] = $"seq={_sequence};rtptime={_startTimestamp}",
            },
        }, ct).ConfigureAwait(false);
        record.EnsureSuccess("RECORD");

        if (_usePtp)
        {
            // Declare the timing group: [receiver, other members…, sender]. Without the
            // members listed, a stereo-pair leader plays alone and never brings its
            // partner into the session.
            List<object?> peerList = [_rtsp.RemoteAddress.ToString()];
            peerList.AddRange(_groupPeers.Select(p => (object?)p.ToString()));
            peerList.Add(_rtsp.LocalAddress.ToString());
            Stage($"SETPEERS [{string.Join(", ", peerList)}]");
            var peers = await _rtsp.RequestAsync(new RtspRequest
            {
                Method = "SETPEERS",
                Uri = RtspUri,
                Body = BinaryPlist.Write(peerList),
                ContentType = "/peer-list-changed",
            }, ct).ConfigureAwait(false);
            peers.EnsureSuccess("SETPEERS");
        }

        var streamDict = new Dictionary<string, object?>
        {
            ["type"] = _buffered ? 0x67L : 0x60L,   // 103 buffered vs 96 realtime
            ["ct"] = _aacEncoder is not null ? 4L : 2L,             // 4 = AAC-LC, 2 = ALAC
            ["spf"] = _aacEncoder is not null ? 1024L : 352L,
            ["audioFormat"] = _aacEncoder is not null ? AudioFormatAacLc44100Stereo
                : _buffered ? AudioFormatAlac44100S24Stereo : AudioFormatAlac44100S16Stereo,
            ["audioMode"] = "default",
            ["shk"] = _hap.AudioKey,
            // True for buffered, per both decrypted Apple captures — the per-packet SSRC is the
            // authoritative codec tag and this key is what declares the stream honours it.
            ["supportsDynamicStreamID"] = _buffered,
            ["streamConnectionID"] = unchecked((long)_streamConnectionId),
        };
        // NOTE: no `audioBufferSize` here, deliberately.
        //
        // It was sent for buffered streams to bound receiver-side buffering and cut latency. It is
        // a RECEIVER-TO-SENDER field: no reference sender emits it (owntone's
        // payload_make_setup_stream and pyatv both omit it), and both reference receivers return it
        // in their SETUP RESPONSE to declare their own ring size — shairport-sync reports 8 MiB, in
        // BYTES. Sending 88200 told the receiver its buffer was ~0.5 s where it expects to choose,
        // which is at or below our own 350 ms anchor lead and leaves nothing to ride out jitter.
        // The comment that introduced it recorded that buffered had been PLAYING beforehand, so it
        // was the change, not the baseline.

        // Per-stream-type key sets, taken from decrypted SETUP plists of REAL Apple senders
        // (shairport-sync issues #1876 / #1942 for buffered, #1807 for realtime):
        //
        // realtime (96): sr, controlPort, isMedia, latencyMin, latencyMax
        // buffered (103): audioFormatIndex, clientID — and NONE of the above
        //
        // Both sets used to go out on both types, because the dictionary was shared. Sending
        // realtime-only keys on a buffered stream is not additive: `controlPort` in particular
        // offers a control channel the buffered path does not use, and `isMedia`/`sr` are absent
        // from every captured Apple buffered SETUP.
        if (_buffered)
        {
            // The BIT INDEX of audioFormat, not its mask — real captures pair
            // audioFormat=4194304 (1<<22) with audioFormatIndex=22.
            streamDict["audioFormatIndex"] = _aacEncoder is not null
                ? AudioFormatIndexAacLc44100Stereo : AudioFormatIndexAlac44100S24Stereo;
            streamDict["clientID"] = ClientBundleId;
        }
        else
        {
            streamDict["sr"] = (long)SampleRate;
            streamDict["controlPort"] = (long)LocalPort(_controlSocket);
            streamDict["isMedia"] = true;
            streamDict["latencyMax"] = (long)LatencyFrames;
            streamDict["latencyMin"] = 11025L;
        }

        Stage($"SETUP (stream: {(_buffered ? "buffered" : "realtime")} " +
              $"{(_aacEncoder is not null ? "AAC-LC 44.1/2" : "ALAC 44.1")})");
        var streamSetup = await PlistRequestAsync("SETUP", new Dictionary<string, object?>
        {
            ["streams"] = new List<object?> { streamDict },
        }, ct).ConfigureAwait(false);

        var stream = (streamSetup.GetValueOrDefault("streams") as List<object?>)?.FirstOrDefault()
            as Dictionary<string, object?> ?? throw new RtspException("stream SETUP: no streams in response");
        long dataPort = stream.GetValueOrDefault("dataPort") as long? ?? throw new RtspException("stream SETUP: no dataPort");
        long theirControl = stream.GetValueOrDefault("controlPort") as long? ?? dataPort;
        _receiverData = new IPEndPoint(_rtsp.RemoteAddress, (int)dataPort);
        _receiverControl = new IPEndPoint(_rtsp.RemoteAddress, (int)theirControl);
        Stage($"stream ports: data={dataPort} control={theirControl} — session live");

        if (_buffered)
        {
            // Buffered audio flows over a TCP connection to the receiver's data port. The
            // receiver buffers ahead and plays from the shared timeline anchored at stream start
            // (SendBufferedAnchorAsync) — NOT here, because connect finishes seconds before audio
            // flows, and for a group every member must share ONE anchor to play in sync.
            _audioTcp = new TcpClient();
            await _audioTcp.ConnectAsync(_rtsp.RemoteAddress, (int)dataPort, ct).ConfigureAwait(false);
            _audioTcp.NoDelay = true;
            _audioTcpStream = _audioTcp.GetStream();
            Stage($"buffered audio TCP connected → {_rtsp.RemoteAddress}:{dataPort}");
        }
        // Realtime streams need no SETRATEANCHORTIME: playback is driven by the sync packets
        // alone (owntone parity).
    }

    /// <summary>
    /// Sends SETRATEANCHORTIME to start buffered playback anchored to <paramref name="anchorNanos"/>
    /// (a PTP grandmaster time). Called once at stream start; for a group the SAME anchorNanos and
    /// the shared start timestamp are used for every member, so all speakers render the same sample
    /// at the same instant. The sample with RTP time <see cref="_startTimestamp"/> plays at the anchor.
    /// </summary>
    /// <summary>
    /// Waits until this session's receiver has demonstrably converged on our PTP timeline —
    /// measured by its own Delay_Req traffic — before any buffered anchor is sent.
    ///
    /// <para>An anchor the receiver gets before its servo trusts the timeline is silently
    /// discarded, and since the anchor is sent exactly once, the session then plays nothing
    /// forever while every health signal reads clean. Verified on real hardware: anchor at
    /// ~1.5 s after first contact → silence; identical session with the anchor at ~6 s → plays.
    /// 48 Delay_Reqs at the 8 Hz Apple sync cadence ≈ the 6 s that measurement proved out.
    /// On timeout the anchor is sent anyway: a late anchor is recoverable by reconnecting,
    /// a never-sent one is not.</para>
    /// </summary>
    public async Task WaitForClockSettleAsync(CancellationToken ct)
    {
        if (!_buffered || _ptp is null || _ptpPeers.Count == 0) return;
        var sw = Stopwatch.StartNew();
        bool settled = await _ptp.WaitForPeerSettleAsync(
            _ptpPeers[0], delayReqTarget: 48, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        Stage(settled
            ? $"clock settled ({sw.ElapsedMilliseconds} ms of servo tracking)"
            : $"clock settle timed out after {sw.ElapsedMilliseconds} ms — anchoring anyway");
    }

    public async Task SendBufferedAnchorAsync(ulong anchorNanos, CancellationToken ct)
    {
        // Real Apple senders POST /audioMode immediately before SETRATEANCHORTIME on the buffered
        // path (observed in a decrypted capture, shairport-sync issue #1876). WinPlay never sent
        // it. Best-effort: a receiver that does not implement it answers 4xx, which is not a
        // reason to abandon a session that is otherwise ready to play.
        try
        {
            var modeResp = await _rtsp.RequestAsync(new RtspRequest
            {
                Method = "POST",
                Uri = "/audioMode",
                Body = BinaryPlist.Write(new Dictionary<string, object?> { ["audioMode"] = "default" }),
                ContentType = "application/x-apple-binary-plist",
            }, ct).ConfigureAwait(false);
            modeResp.EnsureSuccess("/audioMode");
        }
        catch (RtspException ex)
        {
            Stage($"/audioMode declined ({ex.Message}) — continuing");
        }

        var (secs, frac) = AnchorNetworkTime(anchorNanos);
        var payload = new Dictionary<string, object?>
        {
            ["rate"] = 1L,
            ["rtpTime"] = (long)_startTimestamp,
            ["networkTimeTimelineID"] = unchecked((long)(_ptp?.ClockId ?? 0)),
            ["networkTimeSecs"] = unchecked((long)secs),
            ["networkTimeFrac"] = unchecked((long)frac),
            ["networkTimeFlags"] = 0L,
        };
        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "SETRATEANCHORTIME",
            Uri = RtspUri,
            Body = BinaryPlist.Write(payload),
            ContentType = "application/x-apple-binary-plist",
        }, ct).ConfigureAwait(false);
        resp.EnsureSuccess("SETRATEANCHORTIME");
        Stage($"anchored buffered timeline (rtpTime={_startTimestamp})");
    }

    /// <summary>
    /// Splits a PTP time in nanoseconds into the SETRATEANCHORTIME <c>networkTimeSecs</c> and
    /// <c>networkTimeFrac</c> fields. The fraction is 2^-64 fixed-point (NOT nanoseconds): the
    /// receiver recovers nanoseconds as <c>(frac * 1e9) &gt;&gt; 64</c> (shairport-sync
    /// <c>handle_setrateanchori</c>). Emitting raw nanoseconds anchors the stream in the past and
    /// the receiver plays nothing — the bug this encoding fixes.
    /// </summary>
    internal static (ulong Secs, ulong Frac) AnchorNetworkTime(ulong nanos)
    {
        ulong secs = nanos / 1_000_000_000UL;
        ulong frac = (ulong)(((UInt128)(nanos % 1_000_000_000UL) << 64) / 1_000_000_000UL);
        return (secs, frac);
    }

    /// <summary>
    /// The rtp timestamp for a session's packet number <paramref name="packetsSent"/> (packets of
    /// 352 frames each): the timeline's anchor rtp, plus this session's own absolute start offset
    /// on the shared capture, plus how far this session has streamed since. Pure and static — see
    /// <see cref="_startPositionFrames"/> for why the anchor rtp and this value are deliberately
    /// NOT the same thing — so the split is verifiable without a live RTSP session.
    /// </summary>
    internal static uint FrameTimestamp(uint anchorRtpTimestamp, long startPositionFrames, long packetsSent) =>
        unchecked((uint)(anchorRtpTimestamp + (ulong)startPositionFrames + (ulong)packetsSent * 352));

    /// <summary>
    /// Resolves the SETUP(session) <c>groupUUID</c>: the identity of the group THIS SENDER is
    /// forming, not anything the receiver told us about itself.
    ///
    /// <para>One playback session mints one UUID and sends the same value to every member, which
    /// is what ties a multi-room set together — the receivers learn they belong to one synchronised
    /// group because the sender said so. <c>owntone-server</c> stores it as
    /// <c>char group_uuid[37]</c> (one UUID plus terminator) filled by <c>uuid_make()</c>; pyatv
    /// mints one per session too. Neither derives it from the receiver.</para>
    ///
    /// <para><b>Why this is a validating parse and not a passthrough.</b> An earlier revision
    /// echoed the receiver's advertised mDNS <c>gid</c> here. That field is a membership
    /// advertisement and is legitimately compound — a HomePod that belongs to a home group
    /// advertises <c>gid=&lt;uuid&gt;+&lt;uuid&gt;</c>, which <see cref="Discovery.DevicePicker"/>
    /// already splits on <c>'+'</c> for exactly that reason. Echoed into SETUP it produced a
    /// 73-character <c>groupUUID</c>; the receiver accepted every request and then rendered
    /// nothing. Captured off the wire against a HomePod mini, side by side with the working
    /// build — that one field was the whole difference. Parsing rather than trusting means no
    /// value that is not a single well-formed UUID can ever reach the wire again, whatever its
    /// source.</para>
    /// </summary>
    internal static string ResolveGroupUuid(string? sessionGroupUuid) =>
        Guid.TryParse(sessionGroupUuid, out var g)
            ? g.ToString().ToUpperInvariant()
            : Guid.NewGuid().ToString().ToUpperInvariant();

    private static IPAddress? ExtractTimingPeer(Dictionary<string, object?> sessionSetup)
    {
        if (sessionSetup.GetValueOrDefault("timingPeerInfo") is not Dictionary<string, object?> info
            || info.GetValueOrDefault("Addresses") is not List<object?> addresses)
            return null;
        foreach (object? entry in addresses)
        {
            if (entry is string s && IPAddress.TryParse(s, out var a)
                && a.AddressFamily == AddressFamily.InterNetwork)
                return a;
        }
        return null;
    }

    private string RtspUri => $"rtsp://{_rtsp.LocalAddress}/{_sessionUri}";

    private async Task<Dictionary<string, object?>> PlistRequestAsync(string method,
        Dictionary<string, object?> body, CancellationToken ct)
    {
        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = method,
            Uri = RtspUri,
            Body = BinaryPlist.Write(body),
            ContentType = "application/x-apple-binary-plist",
        }, ct).ConfigureAwait(false);
        resp.EnsureSuccess(method);
        return resp.Body.Length > 0 ? BinaryPlist.ReadDictionary(resp.Body) : [];
    }

    // ------------------------------------------------------------ streaming

    /// <summary>
    /// Convenience for a lone session: prepare, (buffered) anchor at now + lead, start the pump.
    /// Groups use the split API so every member shares one anchor computed at the last instant.
    /// </summary>
    public async Task StartStreamingAsync(IAudioSource source, double volumeDb = -18, long startPositionFrames = 0)
    {
        await PrepareStreamingAsync(volumeDb).ConfigureAwait(false);
        if (_buffered)
            await WaitForClockSettleAsync(_cts.Token).ConfigureAwait(false);
        // The lone session is its own timeline: origin is now, so grid frame zero is this instant
        // and the anchor (buffered) promises rendering at origin + lead — the same relationship
        // GroupSession establishes for shared timelines.
        ulong originNanos = MonotonicClock.NowNanoseconds;
        if (_buffered)
            await SendBufferedAnchorAsync(originNanos + BufferedLeadNanos, _cts.Token).ConfigureAwait(false);
        StartPump(source, startPositionFrames, originNanos);
    }

    /// <summary>
    /// Completes every RTSP round-trip needed before audio can flow (volume, crypto, keep-alive)
    /// — deliberately separated from <see cref="StartPump"/>. The buffered anchor promises the
    /// receiver "sample 0 plays at anchor + lead", so every await that sits between computing the
    /// anchor and sending the first packet is stolen jitter headroom. Sequencing all RTSP work
    /// first makes the receiver's headroom deterministic: headroom = lead − anchor-RTT.
    /// </summary>
    public async Task PrepareStreamingAsync(double volumeDb = -18)
    {
        await SetVolumeAsync(volumeDb, _cts.Token).ConfigureAwait(false);
        lock (_loops) _loops.Add(Task.Run(() => FeedbackLoopAsync(_cts.Token)));
        if (_buffered)
        {
            // Buffered mode: the anchor + PTP clock drive playback, so no per-second RTP sync
            // packets are sent. Audio flows over the TCP data channel.
            _bufferedCrypto = new BufferedAudioPacket(_hap!.AudioKey);
        }
        else
        {
            _audioCrypto = new AudioPacketCrypto(_hap!.AudioKey);
        }
    }

    /// <summary>Starts the audio pump — no network awaits, so a buffered pump begins sending
    /// within milliseconds of its anchor. Call after <see cref="PrepareStreamingAsync"/> (and,
    /// for buffered, after <see cref="SendBufferedAnchorAsync"/>).</summary>
    /// <param name="startPositionFrames">
    /// This session's absolute offset (in frames) on the shared capture timeline that
    /// <paramref name="source"/> begins reading from — 0 unless <paramref name="source"/> is a
    /// branch that joined a capture already in progress (see
    /// <see cref="WinPlay.Core.Audio.IPositionedAudioSource"/>). Folded into every frame this
    /// session emits (see <see cref="_startPositionFrames"/>), never into the anchor.
    /// </param>
    public void StartPump(IAudioSource source, long startPositionFrames = 0, ulong timelineOriginNanos = 0)
    {
        _startPositionFrames = startPositionFrames;
        _timelineOriginNanos = timelineOriginNanos;
        Volatile.Write(ref _pumpSource, source);
        if (_buffered)
        {
            _pumpThread = new Thread(() => BufferedAudioPump(source)) { IsBackground = true, Priority = ThreadPriority.Highest };
        }
        else
        {
            lock (_loops) _loops.Add(Task.Run(() => SyncLoopAsync(_cts.Token)));
            _pumpThread = new Thread(() => AudioPump(source)) { IsBackground = true, Priority = ThreadPriority.Highest };
        }
        _pumpThread.Start();
        Stage($"streaming started ({(_buffered ? "buffered" : "realtime")})");
    }

    /// <summary>
    /// Buffered audio pump: paces PCM at real time, ALAC-frames it, and writes length-prefixed
    /// buffered packets to the TCP data channel. Playback timing is set once by the anchor, so
    /// real-time pacing keeps the receiver's buffer at the shared 1.75 s lead established there.
    /// </summary>
    private void BufferedAudioPump(IAudioSource source)
    {
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            var writeSw = new Stopwatch();
            Span<short> samples = stackalloc short[FramesPerPacket * 2];
            uint seq = 0;
            long gridIndex = _timelineOriginNanos != 0 ? StartOnGrid(source, samples) : 0;
            // TCP send-health window (~15 s): a stalled Write means Wi-Fi backpressure — the
            // receiver is starving no matter how healthy the capture side is. Reported via
            // Stage so live runs can attribute dropouts to the correct pipeline stage.
            long windowMaxStallMs = 0, windowTotalStallMs = 0;
            const int WindowPackets = 1880;
            while (!_stopped)
            {
                if (_timelineOriginNanos != 0)
                {
                    WaitUntilNanos(GridDueNanos(gridIndex));
                    if (_stopped) return;
                }
                else
                {
                    double dueMs = _framesSent * 352000.0 / SampleRate;
                    double nowMs = sw.Elapsed.TotalMilliseconds;
                    if (nowMs < dueMs)
                    {
                        int sleep = (int)(dueMs - nowMs);
                        if (sleep >= 2) Thread.Sleep(sleep - 1);
                        continue;
                    }
                }

                source.Read(samples);
                ReportIfCaptureIsSilent(samples);
                uint ts;
                if (_timelineOriginNanos != 0)
                {
                    ts = unchecked(_startTimestamp + (uint)_gridStampFrames);
                    _gridStampFrames += FramesPerPacket;
                }
                else
                {
                    ts = FrameTimestamp(_startTimestamp, _startPositionFrames, _framesSent);
                }
                gridIndex++;
                byte[] payload;
                if (_aacEncoder is not null)
                {
                    // One raw AAC-LC access unit per packet, with the timestamp travelling in a
                    // FIFO alongside the content: the MF encoder holds ~2 frames of pipeline
                    // latency (measured), so the AU that emerges at slot k carries the AUDIO of
                    // slot k−2 and must be stamped with slot k−2's rtp. Stamping it with the
                    // current slot shifted every AAC room 46 ms off the shared grid — a constant
                    // cross-room flam. The FIFO keeps content↔timestamp exact whatever the
                    // encoder's (build-dependent) latency is.
                    _aacRtpFifo.Enqueue(ts);
                    // Bounded: an encoder that stops producing (post-fault) would otherwise grow
                    // this queue forever while the pump spun in silence with a clean log — the
                    // exact invisible failure mode this release exists to eliminate.
                    if (_aacRtpFifo.Count > 64)
                        throw new InvalidOperationException("AAC encoder stopped producing output");
                    byte[]? au = _aacEncoder.EncodeFrame(samples);
                    if (au is null) continue;   // priming: content retained, its rtp stays queued
                    payload = au;
                    ts = _aacRtpFifo.Dequeue();
                }
                else
                {
                    payload = AlacFramer.WrapPcmFrame24(samples);
                }
                // 23-bit sequence space — receivers mask the leading word with 0x7FFFFF. The
                // AEAD nonce is a separate, never-wrapping 64-bit counter: deriving it from the
                // sequence would repeat a nonce under the same key after 2²³ packets (~19 h of
                // ALAC), which voids ChaCha20-Poly1305's guarantees. Receivers take the nonce
                // from the packet trailer, so the two need not be related.
                byte[] frame = _bufferedCrypto!.BuildFrame(seq & 0x7FFFFF, ts, _ssrc, payload, _nonceCounter++);
                try
                {
                    writeSw.Restart();
                    _audioTcpStream!.Write(frame, 0, frame.Length);
                    long stallMs = writeSw.ElapsedMilliseconds;
                    windowTotalStallMs += stallMs;
                    if (stallMs > windowMaxStallMs) windowMaxStallMs = stallMs;
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    if (_stopped) return;
                    if (Interlocked.Increment(ref _audioSendFailures) == 1)
                        Stage($"BUFFERED AUDIO SEND FAILING: {ex.Message}");
                    RaiseFaulted(ex);
                    return;
                }
                seq++;
                Interlocked.Increment(ref _framesSent);
                if (seq % WindowPackets == 0)
                {
                    if (windowMaxStallMs > 20)
                        Stage($"tcp send health: max stall {windowMaxStallMs}ms, total {windowTotalStallMs}ms in ~15s window");
                    windowMaxStallMs = windowTotalStallMs = 0;
                }
            }
        }
        catch (Exception ex)
        {
            // A pump owns its own thread, so this is the only frame that can report its death.
            // Without it, any throw — an MF encoder fault, a cipher disposed by a racing
            // teardown, a capture failure — was an unhandled thread exception and KILLED THE
            // PROCESS, with the local speakers still muted. Faulting the session instead hands
            // recovery to the reconnect loop, which is its job.
            if (!_stopped) { Stage($"audio pump stopped: {ex.Message}"); RaiseFaulted(ex); }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    public async Task SetVolumeAsync(double db, CancellationToken ct)
    {
        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "SET_PARAMETER",
            Uri = RtspUri,
            Body = System.Text.Encoding.ASCII.GetBytes(VolumeControl.FormatVolumeBody(db)),
            ContentType = "text/parameters",
        }, ct).ConfigureAwait(false);
        resp.EnsureSuccess("SET_PARAMETER volume");
    }

    // Grid sessions derive "now" from the wall — the same mapping the sync declarations use —
    // because the packet-counter formula assumes 352-frame packets and a session-local origin,
    // neither of which holds on the shared timeline (AAC packets are 1024 frames): a receiver's
    // scrubber fed the counter formula ran up to ~3× off.
    private uint CurrentRtpTimestamp => _timelineOriginNanos != 0
        ? unchecked(_startTimestamp + (uint)WallFramesNow())
        : FrameTimestamp(_startTimestamp, _startPositionFrames, Interlocked.Read(ref _framesSent));

    /// <summary>Sends now-playing metadata (title/artist/album) to the receiver's Now Playing UI.</summary>
    public async Task SendMetadataAsync(string? title, string? artist, string? album, CancellationToken ct = default)
    {
        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "SET_PARAMETER",
            Uri = RtspUri,
            Body = DaapMetadata.Encode(title, artist, album),
            ContentType = "application/x-dmap-tagged",
            Headers = { ["RTP-Info"] = $"rtptime={CurrentRtpTimestamp}" },
        }, ct).ConfigureAwait(false);
        resp.EnsureSuccess("SET_PARAMETER metadata");
    }

    /// <summary>
    /// Reports playback position so the receiver can draw its progress bar / scrubber.
    /// The RAOP <c>progress</c> parameter is three RTP timestamps — <c>start/current/end</c> —
    /// on the session's own audio timeline, so they are derived from <see cref="_startTimestamp"/>
    /// rather than wall time.
    /// </summary>
    public async Task SendProgressAsync(TimeSpan position, TimeSpan duration, CancellationToken ct = default)
    {
        uint now = CurrentRtpTimestamp;
        // "start" is where the current track began on our timeline: now − elapsed.
        uint start = (uint)(now - (long)(position.TotalSeconds * SampleRate));
        uint end = duration > TimeSpan.Zero
            ? (uint)(start + (long)(duration.TotalSeconds * SampleRate))
            : now;

        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "SET_PARAMETER",
            Uri = RtspUri,
            Body = System.Text.Encoding.ASCII.GetBytes(
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"progress: {start}/{now}/{end}\r\n")),
            ContentType = "text/parameters",
        }, ct).ConfigureAwait(false);
        resp.EnsureSuccess("SET_PARAMETER progress");
    }

    /// <summary>Sends cover artwork (JPEG or PNG) to the receiver's Now Playing UI.</summary>
    public async Task SendArtworkAsync(byte[] image, string contentType = "image/jpeg", CancellationToken ct = default)
    {
        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "SET_PARAMETER",
            Uri = RtspUri,
            Body = image,
            ContentType = contentType,
            Headers = { ["RTP-Info"] = $"rtptime={CurrentRtpTimestamp}" },
        }, ct).ConfigureAwait(false);
        resp.EnsureSuccess("SET_PARAMETER artwork");
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private void AudioPump(IAudioSource source)
    {
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            Span<short> samples = stackalloc short[352 * 2];
            bool first = true;
            long gridIndex = _timelineOriginNanos != 0 ? StartOnGrid(source, samples) : 0;
            while (!_stopped)
            {
                if (_timelineOriginNanos != 0)
                {
                    WaitUntilNanos(GridDueNanos(gridIndex));
                    if (_stopped) return;
                }
                else
                {
                    double dueMs = _framesSent * 352000.0 / SampleRate;
                    double nowMs = sw.Elapsed.TotalMilliseconds;
                    if (nowMs < dueMs)
                    {
                        int sleep = (int)(dueMs - nowMs);
                        if (sleep >= 2) Thread.Sleep(sleep - 1);
                        continue;
                    }
                }

                source.Read(samples);
                ReportIfCaptureIsSilent(samples);
                byte[] alac = AlacFramer.WrapPcmFrame(samples);
                uint ts;
                if (_timelineOriginNanos != 0)
                {
                    ts = unchecked(_startTimestamp + (uint)_gridStampFrames);
                    _gridStampFrames += FramesPerPacket;
                }
                else
                {
                    ts = FrameTimestamp(_startTimestamp, _startPositionFrames, _framesSent);
                }
                gridIndex++;
                byte[] packet = _audioCrypto!.BuildPacket(_sequence, ts, _ssrc, first, alac);
                lock (_resendLock)
                    _resendRing[_sequence % ResendRingSize] = (_sequence, packet);
                try
                {
                    _audioSocket!.SendTo(packet, _receiverData!);
                }
                catch (SocketException ex)
                {
                    if (_stopped) return;
                    // A UDP send failure here is terminal for this session — the receiver has
                    // gone (ICMP port-unreachable after it tore its session down, adapter
                    // disabled, Wi-Fi dropped). Previously the loop swallowed this and kept
                    // pumping forever: Faulted never fired, so StreamController never
                    // reconnected, and the destination sat silent while the UI still showed it
                    // streaming. Report and stop, exactly as the buffered pump does.
                    Interlocked.Increment(ref _audioSendFailures);
                    Stage($"audio send failed: {ex.SocketErrorCode} → {_receiverData}");
                    RaiseFaulted(ex);
                    return;
                }
                _sequence++;
                Interlocked.Increment(ref _framesSent);
                first = false;
            }
        }
        catch (Exception ex)
        {
            // A pump owns its own thread, so this is the only frame that can report its death.
            // Without it, any throw — an MF encoder fault, a cipher disposed by a racing
            // teardown, a capture failure — was an unhandled thread exception and KILLED THE
            // PROCESS, with the local speakers still muted. Faulting the session instead hands
            // recovery to the reconnect loop, which is its job.
            if (!_stopped) { Stage($"audio pump stopped: {ex.Message}"); RaiseFaulted(ex); }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    /// <summary>
    /// Sync packets on the control port map RTP time onto the timing clock (1 Hz).
    /// NTP mode: 20-byte 0xD4 with an NTP wall timestamp. PTP mode: 28-byte 0xD7
    /// "time announce" with raw monotonic nanoseconds of the grandmaster clock plus
    /// its clock ID (owntone rtp_common.c parity).
    /// </summary>
    private async Task SyncLoopAsync(CancellationToken ct)
    {
        bool first = true;
        byte[] pkt = new byte[_usePtp ? 28 : 20];
        while (!ct.IsCancellationRequested)
        {
            // From the WALL, not the pump counter: the declaration "this rtp renders now" must be
            // exact even when the pump jitters, or the receiver inherits the jitter as offset.
            uint nowTs = _timelineOriginNanos != 0
                ? unchecked(_startTimestamp + (uint)WallFramesNow())
                : FrameTimestamp(_startTimestamp, _startPositionFrames, Interlocked.Read(ref _framesSent));
            pkt[0] = first ? (byte)0x90 : (byte)0x80;
            if (_usePtp)
            {
                pkt[1] = 0xD7; // PT 215 time announce
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), 0x0006);
                // Playout point = the SAME lead buffered uses, not the classic 2 s. The sender
                // declares realtime's render offset in this packet, so this line is where
                // cross-transport sync lives: every PTP room — realtime or buffered — renders the
                // sample captured at (now − lead), on the one wall-locked timeline. With realtime
                // still declaring 2 s while buffered rooms ran at 0.75 s, the same music played
                // 1.25 s apart between rooms — a designed-in echo. The receiver keeps the same
                // jitter cushion buffered gets: every declared-due packet was sent lead-ms ago.
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), nowTs - BufferedLeadFrames);
                BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), MonotonicClock.NowNanoseconds);
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), nowTs);
                BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(20), _ptp!.ClockId);
            }
            else
            {
                pkt[1] = 0xD4;
                // 0x0004: the field value WinPlay's own mirror session sends to this very Apple TV
                // with rendered audio to show for it (0x0007 is documented ending sessions there).
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), 0x0004);
                // Declared playout on the SHARED grid lead when a timeline exists: the receiver
                // renders the sample captured at (now − lead), the same wall instants every
                // buffered/PTP room renders — the mapping is declared each second at the wall, so
                // it is independent of the NTP epoch. Legacy 2 s only without a timeline.
                uint playoutLead = _timelineOriginNanos != 0 ? (uint)BufferedLeadFrames : LatencyFrames;
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), nowTs - playoutLead);
                BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), _clock.NowNtp);
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), nowTs);
            }
            try
            {
                await _controlSocket!.SendToAsync(pkt, SocketFlags.None, _receiverControl!, ct).ConfigureAwait(false);
                if (first) Stage($"first sync packet sent to {_receiverControl}");
            }
            catch (SocketException ex) { Stage($"sync send failed: {ex.SocketErrorCode}"); }
            catch (OperationCanceledException) { return; }
            first = false;
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private long _timingRequests;

    /// <summary>Answers the receiver's NTP timing requests (RTP PT 82/83).</summary>
    private async Task TimingLoopAsync(CancellationToken ct)
    {
        byte[] buf = new byte[64];
        byte[] resp = new byte[32];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _timingSocket!.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (r.ReceivedBytes < 32) continue;

            ulong recvTime = _clock.NowNtp;
            resp[0] = 0x80;
            resp[1] = 0xD3; // timing response
            resp[2] = buf[2]; // echo request sequence
            resp[3] = buf[3];
            resp.AsSpan(4, 4).Clear();
            buf.AsSpan(24, 8).CopyTo(resp.AsSpan(8));   // echo request send time as reference
            BinaryPrimitives.WriteUInt64BigEndian(resp.AsSpan(16), recvTime);
            BinaryPrimitives.WriteUInt64BigEndian(resp.AsSpan(24), _clock.NowNtp);
            try
            {
                await _timingSocket!.SendToAsync(resp, SocketFlags.None, r.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (SocketException) { }
            catch (OperationCanceledException) { return; }

            long n = Interlocked.Increment(ref _timingRequests);
            if (n == 1)
                Stage($"timing request answered — receiver is slaving to our clock ({r.RemoteEndPoint})");
            else if (n % 60 == 0)
                Stage($"timing request #{n} answered");
        }
    }

    /// <summary>Drains receiver→sender control traffic (retransmit requests; logged only).</summary>
    private async Task ControlReceiveLoopAsync(CancellationToken ct)
    {
        byte[] buf = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _controlSocket!.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false);
                if (r.ReceivedBytes >= 8 && (buf[1] & 0x7F) == 0x55)
                {
                    ushort firstMissing = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4));
                    ushort count = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
                    int sent = await ResendAsync(firstMissing, count, r.RemoteEndPoint, ct).ConfigureAwait(false);
                    long n = Interlocked.Increment(ref _resendRequests);
                    if (n <= 5 || n % 50 == 0)
                        Stage($"retransmit req #{n}: seq {firstMissing} ×{count} → resent {sent}");
                }
                else
                {
                    Stage($"control packet in: {r.ReceivedBytes}B pt=0x{buf[1]:X2}");
                }
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { }
        }
    }

    /// <summary>POST /feedback every 2 s — receivers drop the session after ~30 s without it.</summary>
    private async Task FeedbackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                var resp = await _rtsp.RequestAsync(new RtspRequest { Method = "POST", Uri = "/feedback" }, ct)
                    .ConfigureAwait(false);
                long n = Interlocked.Increment(ref _feedbackCount);

                // Report capture damage the moment it changes, not on the keep-alive's sampling
                // schedule. Clicks and dropouts leave NO trace in the RTSP exchange — a session
                // can hand back a clean handshake and steady keep-alives while the user hears the
                // audio breaking up — so this is the only place the two can be told apart after
                // the fact, from an ordinary log.
                if (CaptureStats is { } stats)
                {
                    // Report on ANY counter movement, not only lost frames. Underruns during
                    // active playback are the signature of the reader overtaking the writer —
                    // audio that arrives as cuts and then permanent silence at the receiver while
                    // every other instrument reads healthy — and they were counted but never
                    // surfaced, which left exactly that failure invisible in an ordinary log.
                    long fingerprint = stats.LateFrames ^ (stats.UnderrunFrames << 1) ^ (stats.GapJumps << 2);
                    if (fingerprint != Interlocked.Exchange(ref _lastReportedLateFrames, fingerprint))
                        Stage($"capture health: late {stats.LateFrames} " +
                              $"({stats.LateFrames * 1000.0 / SampleRate:F0} ms lost), " +
                              $"underrun {stats.UnderrunFrames} ({stats.UnderrunFrames * 1000.0 / SampleRate:F0} ms), " +
                              $"gaps {stats.GapJumps}");
                }

                if (resp.Body.Length > 0 && (n <= 5 || n % 15 == 0))
                {
                    try
                    {
                        Stage($"feedback #{n} stats: {DescribePlist(BinaryPlist.Read(resp.Body))}");
                    }
                    catch (FormatException)
                    {
                        Stage($"feedback #{n} body unparsed ({resp.Body.Length}B)");
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is RtspException or IOException or SocketException or ObjectDisposedException)
            {
                if (!ct.IsCancellationRequested)
                {
                    Stage($"feedback failed: {ex.Message}");
                    RaiseFaulted(ex);
                }
                return;
            }
        }
    }

    private int _faultRaised;

    /// <summary>Raised once when the session detects the connection has dropped (not on intentional stop).</summary>
    public event Action<Exception>? Faulted;

    // _loops is appended from the connect, prepare and pump-start paths and drained by
    // DisposeAsync — different threads; a torn Add would lose a loop from the drain.
    private Task[] SnapshotLoops() { lock (_loops) return [.. _loops]; }

    private void RaiseFaulted(Exception ex)
    {
        if (_stopped) return;
        if (Interlocked.Exchange(ref _faultRaised, 1) == 0)
            Faulted?.Invoke(ex);
    }

    // ------------------------------------------------------------ event channel

    private void StartEventChannel(IPAddress address, int port)
    {
        _eventTcp = new TcpClient();
        lock (_loops) _loops.Add(Task.Run(async () =>
        {
            try
            {
                await _eventTcp.ConnectAsync(address, port, _cts.Token).ConfigureAwait(false);
                var stream = _eventTcp.GetStream();
                var crypto = new ChannelCrypto(_hap!.EventsWriteKey, _hap.EventsReadKey);
                bool triedSwap = false;
                var raw = new MemoryStream();
                var plain = new MemoryStream();
                byte[] buf = new byte[8192];
                while (!_cts.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, _cts.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    raw.Write(buf, 0, n);
                    try
                    {
                        int consumed = crypto.DecryptFrames(raw.GetBuffer().AsSpan(0, (int)raw.Length), plain);
                        if (consumed > 0)
                        {
                            byte[] tail = raw.GetBuffer().AsSpan(consumed, (int)raw.Length - consumed).ToArray();
                            raw.SetLength(0);
                            raw.Write(tail);
                        }
                    }
                    catch (CryptographicException) when (!triedSwap && plain.Length == 0)
                    {
                        // Direction convention differs between receivers — retry with swapped keys.
                        triedSwap = true;
                        crypto.Dispose();
                        crypto = new ChannelCrypto(_hap.EventsReadKey, _hap.EventsWriteKey);
                        continue;
                    }

                    // Answer every COMPLETE request (headers + full body); keep partial
                    // bytes buffered — a desynced event channel gets the whole session
                    // torn down by the receiver ~30 s later.
                    while (TryTakeEventRequest(plain, out string requestLine, out string? cseq,
                               out byte[] body))
                    {
                        Stage($"event: {requestLine} ({DescribeEventBodyBytes(body)})");
                        string headers = "RTSP/1.0 200 OK\r\n"
                            + (cseq is not null ? $"CSeq: {cseq}\r\n" : "")
                            + "Server: AirTunes/366.0\r\nContent-Length: 0\r\n\r\n";
                        byte[] ok = System.Text.Encoding.ASCII.GetBytes(headers);
                        await stream.WriteAsync(crypto.Encrypt(ok), _cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                if (!_cts.IsCancellationRequested) Stage($"event channel closed: {ex.Message}");
            }
        }));
    }

    private long _feedbackCount;
    private long _resendRequests;

    /// <summary>
    /// Replies to a 0xD5 retransmit request: 0xD6 packets on the control port, a 6-byte
    /// prefix followed by the original RTP packet minus its first 2 bytes (receivers
    /// parse the embedded packet starting at its sequence-number field).
    /// </summary>
    private async Task<int> ResendAsync(ushort firstMissing, ushort count, EndPoint to, CancellationToken ct)
    {
        int sent = 0;
        for (int i = 0; i < count; i++)
        {
            ushort seq = (ushort)(firstMissing + i);
            byte[]? packet;
            lock (_resendLock)
            {
                var slot = _resendRing[seq % ResendRingSize];
                packet = slot.Seq == seq ? slot.Packet : null;
            }
            if (packet is null) continue;

            byte[] reply = new byte[6 + packet.Length - 2];
            reply[0] = 0x80;
            reply[1] = 0xD6;
            packet.AsSpan(2).CopyTo(reply.AsSpan(6));
            try
            {
                await _controlSocket!.SendToAsync(reply, SocketFlags.None, to, ct).ConfigureAwait(false);
                sent++;
            }
            catch (SocketException) { }
        }
        return sent;
    }

    internal static string DescribePlist(object? value) => value switch
    {
        null => "null",
        string s => s.Length > 40 ? s[..40] + "…" : s,
        byte[] b => $"<{b.Length}B>",
        Dictionary<string, object?> d =>
            "{" + string.Join(", ", d.Select(kv => $"{kv.Key}={DescribePlist(kv.Value)}")) + "}",
        List<object?> l => "[" + string.Join(", ", l.Select(DescribePlist)) + "]",
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Extracts one complete RTSP request (request line, CSeq, body) from the front of
    /// <paramref name="buffer"/>, compacting it. Returns false while incomplete.
    /// </summary>
    private static bool TryTakeEventRequest(MemoryStream buffer, out string requestLine,
        out string? cseq, out byte[] body)
    {
        requestLine = "";
        cseq = null;
        body = [];

        ReadOnlySpan<byte> buf = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        int headerEnd = buf.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return false;

        string head = System.Text.Encoding.ASCII.GetString(buf[..headerEnd]);
        string[] lines = head.Split("\r\n");
        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                _ = int.TryParse(value, out contentLength);
            else if (name.Equals("CSeq", StringComparison.OrdinalIgnoreCase))
                cseq = value;
        }

        int total = headerEnd + 4 + contentLength;
        if (buf.Length < total) return false;

        requestLine = lines[0];
        body = buf.Slice(headerEnd + 4, contentLength).ToArray();

        byte[] rest = buf[total..].ToArray();
        buffer.SetLength(0);
        buffer.Write(rest);
        return true;
    }

    private static string DescribeEventBodyBytes(byte[] body)
    {
        if (body.Length == 0) return "no body";
        try
        {
            return DescribePlist(BinaryPlist.Read(body));
        }
        catch (FormatException)
        {
            return $"non-plist body ({body.Length}B)";
        }
    }

    // ------------------------------------------------------------ teardown & helpers

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _rtsp.RequestAsync(new RtspRequest { Method = "TEARDOWN", Uri = RtspUri }, cts.Token)
                .ConfigureAwait(false);
            Stage("TEARDOWN sent");
        }
        catch (Exception ex) when (ex is RtspException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Stage($"teardown: {ex.Message}");
        }
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_ptp is not null)
        {
            _ptp.Diagnostic -= Stage;
            foreach (var peer in _ptpPeers) _ptp.RemovePeer(peer);
        }
        _pumpThread?.Join(2000);
        try { await Task.WhenAll(SnapshotLoops()).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { /* loops end on cancellation/socket close */ }
        _audioCrypto?.Dispose();
        _bufferedCrypto?.Dispose();
        _aacEncoder?.Dispose();
        _audioTcpStream?.Dispose();
        _audioTcp?.Dispose();
        _timingSocket?.Dispose();
        _controlSocket?.Dispose();
        _audioSocket?.Dispose();
        _eventTcp?.Dispose();
        _rtsp.Dispose();
        _cts.Dispose();
    }

    private static Socket BindUdp()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return s;
    }

    private static int LocalPort(Socket s) => ((IPEndPoint)s.LocalEndPoint!).Port;

    private static string LocalMacAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length == 6)
                return string.Join(":", mac.Select(b => b.ToString("X2")));
        }
        return "02:00:00:00:00:01"; // locally-administered fallback
    }
}
