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
    private readonly uint _ssrc;
    private readonly bool _usePtp;
    private readonly bool _buffered;
    private TcpClient? _audioTcp;
    private NetworkStream? _audioTcpStream;
    private BufferedAudioPacket? _bufferedCrypto;
    // Buffered send-ahead lead: 0.35 s. The full latency budget, end to end:
    //   perceived delay = lead (350 ms) + capture margin (60 ms) + capture chain (~30 ms)
    //                   ≈ 0.44 s — at parity with Apple's own ~0.5 s buffered figure
    //   receiver jitter headroom = lead − anchor RTT (~50 ms) ≈ 0.3 s
    // Both are DETERMINISTIC because the start sequence performs all other RTSP round-trips
    // before the anchor is computed and starts the pump with zero awaits after it — and the
    // positioned capture ring holds the delay constant instead of letting it creep. Headroom
    // below ~0.2 s risks audible dropouts on Wi-Fi bursts; realtime mode remains ~2 s.
    private const int BufferedLeadFrames = 15435;

    /// <summary>The buffered send-ahead lead in nanoseconds (~0.5 s at 44.1 kHz).</summary>
    public static ulong BufferedLeadNanos => (ulong)((double)BufferedLeadFrames / SampleRate * 1_000_000_000);

    /// <summary>True if this session negotiated a buffered (type 103) audio stream.</summary>
    public bool IsBuffered => _buffered;
    private readonly List<IPAddress> _groupPeers = [];
    private HapPairingCredentials? _credentials;
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
    private long _framesSent;
    private long _audioSendFailures;

    // Recent-packet history for receiver retransmit requests (PT 0xD5 → reply 0xD6).
    private const int ResendRingSize = 1024;
    private readonly (ushort Seq, byte[]? Packet)[] _resendRing = new (ushort, byte[]?)[ResendRingSize];
    private readonly object _resendLock = new();

    public event Action<string>? StageChanged;

    public long FramesSent => Interlocked.Read(ref _framesSent);
    public TimeSpan Elapsed => TimeSpan.FromSeconds(FramesSent * 352.0 / SampleRate);

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
        _ssrc = usePtp ? 0 : sessionId;
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
    public static async Task<RaopSession> ConnectAsync(IPAddress address, int port, bool usePtp,
        IReadOnlyList<IPAddress>? groupPeers = null, Action<string>? stageChanged = null,
        CancellationToken ct = default, HapPairingCredentials? credentials = null, bool buffered = false,
        uint? sharedStartTimestamp = null, string? activeRemote = null)
    {
        var s = new RaopSession(usePtp, buffered, activeRemote) { _credentials = credentials };
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
            //   470 Connection Authorization Required — the receiver wants on-screen PIN
            //       pairing (Apple TV). Answering with the PIN flow is correct.
            //   401 Unauthorized — the receiver's access control refused this sender. A
            //       HomePod has no screen and never shows a PIN, so starting the PIN flow here
            //       fails instantly and tells the user nothing. Surfaced separately so the app
            //       can name the exact setting to change.
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
        _loops.Add(Task.Run(() => TimingLoopAsync(_cts.Token)));
        _loops.Add(Task.Run(() => ControlReceiveLoopAsync(_cts.Token)));

        string mac = LocalMacAddress();
        string sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        Dictionary<string, object?> sessionPayload;
        if (_usePtp)
        {
            _ptp = PtpMaster.Shared;
            _ptp.Diagnostic += Stage;
            Stage($"SETUP (session, PTP grandmaster clock 0x{_ptp.ClockId:X16})");
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
                ["groupUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["groupContainsGroupLeader"] = false,
                ["timingPeerInfo"] = PeerInfo(),
                ["timingPeerList"] = new List<object?> { PeerInfo() },
            };
        }
        else
        {
            Stage("SETUP (session, NTP)");
            // Minimal NTP session payload, owntone parity: extra advertisement keys are
            // omitted deliberately — receivers are pickier than the spec suggests.
            sessionPayload = new Dictionary<string, object?>
            {
                ["deviceID"] = mac,
                ["sessionUUID"] = sessionUuid,
                ["timingPort"] = (long)LocalPort(_timingSocket),
                ["timingProtocol"] = "NTP",
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
            // partner into the session (plan §3.2 point 4).
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
            ["ct"] = 2L,                             // ALAC
            ["spf"] = 352L,
            ["sr"] = (long)SampleRate,
            ["audioFormat"] = 0x40000L,              // ALAC/44100/16/2
            ["audioMode"] = "default",
            ["controlPort"] = (long)LocalPort(_controlSocket),
            ["isMedia"] = true,
            ["shk"] = _hap.AudioKey,
            ["supportsDynamicStreamID"] = false,
            ["streamConnectionID"] = unchecked((long)_streamConnectionId),
        };
        if (_buffered)
            // Bound the receiver-side buffer so it can't over-buffer and inflate latency. In
            // frames this is ~2 s of headroom — enough to ride out jitter, far less than the 8 MiB
            // (~190 s!) that let the HomePod's latency creep toward 2 s.
            streamDict["audioBufferSize"] = 88200L;
        else
        {
            streamDict["latencyMax"] = (long)LatencyFrames;
            streamDict["latencyMin"] = 11025L;
        }

        Stage($"SETUP (stream: {(_buffered ? "buffered" : "realtime")} ALAC 44.1/16/2)");
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
    public async Task SendBufferedAnchorAsync(ulong anchorNanos, CancellationToken ct)
    {
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
    public async Task StartStreamingAsync(IAudioSource source, double volumeDb = -18)
    {
        await PrepareStreamingAsync(volumeDb).ConfigureAwait(false);
        if (_buffered)
            await SendBufferedAnchorAsync(MonotonicClock.NowNanoseconds + BufferedLeadNanos, _cts.Token)
                .ConfigureAwait(false);
        StartPump(source);
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
        _loops.Add(Task.Run(() => FeedbackLoopAsync(_cts.Token)));
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
    public void StartPump(IAudioSource source)
    {
        if (_buffered)
        {
            _pumpThread = new Thread(() => BufferedAudioPump(source)) { IsBackground = true, Priority = ThreadPriority.Highest };
        }
        else
        {
            _loops.Add(Task.Run(() => SyncLoopAsync(_cts.Token)));
            _pumpThread = new Thread(() => AudioPump(source)) { IsBackground = true, Priority = ThreadPriority.Highest };
        }
        _pumpThread.Start();
        Stage($"streaming started ({(_buffered ? "buffered" : "realtime")})");
    }

    /// <summary>
    /// Buffered audio pump: paces PCM at real time, ALAC-frames it, and writes length-prefixed
    /// buffered packets to the TCP data channel. Playback timing is set once by the anchor, so
    /// real-time pacing keeps the receiver's buffer at the ~0.5 s lead established there.
    /// </summary>
    private void BufferedAudioPump(IAudioSource source)
    {
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            var writeSw = new Stopwatch();
            Span<short> samples = stackalloc short[352 * 2];
            uint seq = 0;
            // TCP send-health window (~15 s): a stalled Write means Wi-Fi backpressure — the
            // receiver is starving no matter how healthy the capture side is. Reported via
            // Stage so live runs can attribute dropouts to the correct pipeline stage.
            long windowMaxStallMs = 0, windowTotalStallMs = 0;
            const int WindowPackets = 1880;
            while (!_stopped)
            {
                double dueMs = _framesSent * 352000.0 / SampleRate;
                double nowMs = sw.Elapsed.TotalMilliseconds;
                if (nowMs < dueMs)
                {
                    int sleep = (int)(dueMs - nowMs);
                    if (sleep >= 2) Thread.Sleep(sleep - 1);
                    continue;
                }

                source.Read(samples);
                byte[] alac = AlacFramer.WrapPcmFrame(samples);
                uint ts = (uint)(_startTimestamp + (ulong)_framesSent * 352);
                byte[] frame = _bufferedCrypto!.BuildFrame(seq & 0xFFFFFF, ts, _ssrc, alac);
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

    private uint CurrentRtpTimestamp => (uint)(_startTimestamp + (ulong)Interlocked.Read(ref _framesSent) * 352);

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
    /// Reports playback position so the receiver can draw its progress bar / scrubber (D2).
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
            while (!_stopped)
            {
                double dueMs = _framesSent * 352000.0 / SampleRate;
                double nowMs = sw.Elapsed.TotalMilliseconds;
                if (nowMs < dueMs)
                {
                    int sleep = (int)(dueMs - nowMs);
                    if (sleep >= 2) Thread.Sleep(sleep - 1);
                    continue;
                }

                source.Read(samples);
                byte[] alac = AlacFramer.WrapPcmFrame(samples);
                uint ts = (uint)(_startTimestamp + (ulong)_framesSent * 352);
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
            uint nowTs = (uint)(_startTimestamp + (ulong)Interlocked.Read(ref _framesSent) * 352);
            pkt[0] = first ? (byte)0x90 : (byte)0x80;
            if (_usePtp)
            {
                pkt[1] = 0xD7; // PT 215 time announce
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), 0x0006);
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), nowTs - LatencyFrames);
                BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), MonotonicClock.NowNanoseconds);
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), nowTs - 11025);
                BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(20), _ptp!.ClockId);
            }
            else
            {
                pkt[1] = 0xD4;
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), 0x0007);
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), nowTs - LatencyFrames);
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
        _loops.Add(Task.Run(async () =>
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
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { /* loops end on cancellation/socket close */ }
        _audioCrypto?.Dispose();
        _bufferedCrypto?.Dispose();
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
