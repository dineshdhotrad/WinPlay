// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinPlay.Core.Fairplay;
using WinPlay.Core.Hap;
using WinPlay.Core.Net;
using WinPlay.Core.Plist;
using WinPlay.Core.Rtsp;

namespace WinPlay.Core.Mirror;

/// <summary>
/// AirPlay 2 screen-mirroring session to an Apple TV / AirPlay-2 TV. Flow:
/// GET /info → pair-verify (stored credentials) → encrypted RTSP → FairPlay SAP
/// (/fp-setup, yields ekey/eiv) → SETUP audio (type 96, creates the session) → SETUP
/// video (type 110) → connect the TCP data channel → RECORD → NTP timing responder +
/// H.264 frames (128-byte header, ChaCha20-Poly1305). Video is keyed by HKDF-SHA512
/// from the pair-verify shared secret. HomePods cannot mirror; only call this for an
/// Apple TV / TV subtype.
/// </summary>
public sealed class MirrorSession : IAsyncDisposable
{
    private const int SampleRate = 44100;

    private readonly RtspConnection _rtsp = new();
    private readonly MirrorClock _clock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private readonly string _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
    private readonly long _sessionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private readonly long _videoStreamConnectionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);

    private HapSession? _hap;
    private FairplaySession? _fairplay;
    private Socket? _timingSocket;
    private TcpClient? _dataChannel;
    private NetworkStream? _dataStream;
    private TcpClient? _eventChannel;
    private MirrorVideoCipher? _videoCipher;
    private IH264VideoSource? _source;

    // Optional audio-in-mirror: the Apple TV plays this (routing to its home-theatre
    // speakers) in lock-step with the video because both live in one session on one clock.
    private readonly bool _withAudio;
    private readonly byte[] _audioChachaKey = RandomNumberGenerator.GetBytes(32);
    private readonly long _audioStreamConnectionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private Socket? _audioDataSocket;
    private Socket? _audioControlSocket;
    private IPEndPoint? _audioDataRemote;
    private IPEndPoint? _audioControlRemote;
    private ChaCha20Poly1305? _audioCipher;
    private Audio.IAudioSource? _audioSource;
    private Thread? _audioPumpThread;
    private ushort _audioSeq;
    private uint _audioRtpBase;
    private ulong _audioNonceCounter;

    // How far behind "now" the audio source's newest sample actually is (WASAPI capture margin +
    // callback period), in frames at SampleRate — see ICaptureLatency. Zero when the source cannot
    // report it, which reproduces the old (biased-late) behaviour exactly.
    private long _captureLatencyFrames;

    // Video packets are built (and encrypted, in frame order) on the encoder thread, then
    // handed to a dedicated sender so TCP writes never stall the encoder. Bounded + drop-
    // oldest: for live mirroring a fresh frame beats a backlog.
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _sendQueue
        = new(new System.Collections.Concurrent.ConcurrentQueue<byte[]>(), boundedCapacity: 16);

    private bool _codecSent;

    /// <summary>
    /// Completes once the first video packet has actually been written to the data channel.
    ///
    /// <para>The Apple TV decides whether an audio RTP flow belongs to a live mirroring session at
    /// the moment the audio starts arriving, and audio that shows up before there is any video
    /// stream to attach it to is discarded — for the whole session, not just until video appears.
    /// WinPlay started the audio pump before <c>StartAsync</c> had even spun the encoder up, so
    /// hundreds of milliseconds of audio-without-video went out every time and the TV ignored the
    /// audio from then on: picture perfect, sound silent, forever. The reference receiver
    /// implementation vendored under resources/prev_impls gates on exactly this signal, for
    /// exactly this reason.</para>
    /// </summary>
    private readonly TaskCompletionSource _firstVideoPacketSent =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _sps;
    private byte[]? _pps;
    private long _framesSent;
    private int _receiverDisplayW;
    private int _receiverDisplayH;

    public event Action<string>? StageChanged;

    /// <summary>
    /// The session stopped working on its own — the receiver went away, the network dropped, or a
    /// send failed. Every one of the pumps below runs on its own task or thread, so a failure in
    /// one had nowhere to go: the task faulted, nobody awaited it, and the exception was discarded
    /// by the runtime. Mirroring simply stopped while the app went on reporting it as live, and
    /// the toggle stayed on with nothing behind it. This is how that failure reaches the UI.
    /// </summary>
    public event Action<Exception>? Faulted;

    private int _faultRaised;

    /// <summary>
    /// Reports a fault exactly once. A cancelled session is an orderly stop, not a failure, and
    /// one dead socket typically trips several pumps at the same moment — the user should not be
    /// told about it three times.
    /// </summary>
    private void RaiseFaulted(Exception ex)
    {
        if (_cts.IsCancellationRequested) return;
        if (Interlocked.Exchange(ref _faultRaised, 1) != 0) return;
        Faulted?.Invoke(ex);
    }

    /// <summary>Runs a background pump so that its failure is reported instead of swallowed.</summary>
    private async Task SuperviseAsync(Func<CancellationToken, Task> pump, CancellationToken ct)
    {
        try { await pump(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RaiseFaulted(ex); }
    }

    public long FramesSent => Interlocked.Read(ref _framesSent);
    public string RtspSessionUri => $"rtsp://{_rtsp.RemoteAddress}/{_sessionId}";

    /// <summary>The receiver's advertised display size in pixels (0 if not advertised).</summary>
    public int ReceiverDisplayWidth => _receiverDisplayW;
    public int ReceiverDisplayHeight => _receiverDisplayH;

    private void Stage(string message) => StageChanged?.Invoke(message);

    private MirrorSession(bool withAudio) => _withAudio = withAudio;

    /// <param name="withAudio">
    /// When true, the session also carries a system-audio stream the Apple TV plays in sync
    /// with the video (routed to its home-theatre speakers) — this is how Apple keeps A/V
    /// perfectly in lock-step, in one session on one clock.
    /// </param>
    public static async Task<MirrorSession> ConnectAsync(IPAddress address, int port,
        HapPairingCredentials credentials, Action<string>? stageChanged = null,
        CancellationToken ct = default, bool withAudio = true)
    {
        var s = new MirrorSession(withAudio);
        if (stageChanged is not null) s.StageChanged += stageChanged;
        try
        {
            await s.HandshakeAsync(address, port, credentials, ct).ConfigureAwait(false);
            return s;
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandshakeAsync(IPAddress address, int port, HapPairingCredentials credentials, CancellationToken ct)
    {
        Stage($"connecting to {address}:{port}");
        await _rtsp.ConnectAsync(address, port, ct).ConfigureAwait(false);

        var info = await _rtsp.RequestAsync(new RtspRequest { Method = "GET", Uri = "/info" }, ct).ConfigureAwait(false);
        info.EnsureSuccess("GET /info");
        var infoDict = BinaryPlist.ReadDictionary(info.Body);
        (_receiverDisplayW, _receiverDisplayH) = ParseDisplaySize(infoDict);
        Stage($"receiver: {infoDict.GetValueOrDefault("name")} ({infoDict.GetValueOrDefault("model")})"
            + (_receiverDisplayW > 0 ? $", display {_receiverDisplayW}x{_receiverDisplayH}" : ", display size not advertised"));

        Stage("pair-verify (stored credentials)");
        _hap = await HapVerifiedPairing.PairVerifyAsync(ReceiverPairing.MakePost(_rtsp), credentials, ct).ConfigureAwait(false);
        _rtsp.EnableEncryption(new ChannelCrypto(_hap.ControlWriteKey, _hap.ControlReadKey));

        Stage("FairPlay SAP handshake (/fp-setup)");
        _fairplay = await FairplayHandshake.PerformAsync(async (body, token) =>
        {
            var resp = await _rtsp.RequestAsync(new RtspRequest
            {
                Method = "POST",
                Uri = "/fp-setup",
                Body = body,
                ContentType = "application/octet-stream",
                Headers = { ["X-Apple-ET"] = "32" },
            }, token).ConfigureAwait(false);
            resp.EnsureSuccess("POST /fp-setup");
            return resp.Body;
        }, _hap.SharedSecret, ct).ConfigureAwait(false);
        Stage("FairPlay complete — ekey/eiv established");

        _timingSocket = BindUdp();
        int timingPort = ((IPEndPoint)_timingSocket.LocalEndPoint!).Port;
        _loops.Add(Task.Run(() => TimingLoopAsync(_cts.Token)));

        string mac = LocalMac();

        // Phase 1: session-level SETUP (NO streams). Establishes the session + event
        // channel and carries the FairPlay ekey/eiv (the receiver builds its FairPlayAES
        // from these). AirPlay 2 requires this before any stream SETUP — a streams-first
        // SETUP is rejected by modern tvOS with 400.
        Stage("SETUP (session)");
        var sessionSetup = new Dictionary<string, object?>
        {
            ["deviceID"] = mac,
            ["macAddress"] = mac,
            ["sessionUUID"] = _sessionUuid,
            ["sourceVersion"] = "280.33",
            ["timingProtocol"] = "NTP",
            ["timingPort"] = (long)timingPort,
            ["osName"] = "Windows",
            ["osVersion"] = "10.0",
            ["osBuildVersion"] = "13F69",
            ["model"] = "WinPlay1,1",
            ["name"] = "WinPlay",
            ["isScreenMirroringSession"] = true,
            ["et"] = 32L,
            ["ekey"] = _fairplay.Ekey,
            ["eiv"] = _fairplay.StreamIv,
        };
        var sessionResp = await SetupAsync(RtspSessionUri, sessionSetup, ct).ConfigureAwait(false);
        long eventPort = sessionResp.GetValueOrDefault("eventPort") as long? ?? 0;
        Stage($"session created (eventPort={eventPort})");
        if (eventPort > 0)
            StartEventChannel(_rtsp.RemoteAddress, (int)eventPort);

        // Phase 2: stream-level SETUP — the H.264 video stream (type 110) and, when audio
        // is enabled, a realtime ALAC audio stream (type 96) in the SAME session so the
        // Apple TV keeps them in lock-step.
        Stage(_withAudio ? "SETUP (video + audio streams)" : "SETUP (video stream)");
        byte[] shk = RandomNumberGenerator.GetBytes(16);
        byte[] shiv = RandomNumberGenerator.GetBytes(16);

        var streams = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = 110L,
                ["streamConnectionID"] = _videoStreamConnectionId,
                ["shk"] = shk,
                ["shiv"] = shiv,
                // Same budget the audio stream is pinned to — see MirrorLatency.
                ["latencyMs"] = MirrorLatency.Milliseconds,
                ["timestampInfo"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["name"] = "SubSu" },
                    new Dictionary<string, object?> { ["name"] = "BePxT" },
                    new Dictionary<string, object?> { ["name"] = "AfPxT" },
                    new Dictionary<string, object?> { ["name"] = "BefEn" },
                    new Dictionary<string, object?> { ["name"] = "EmEnc" },
                },
            },
        };
        int audioControlPort = 0;
        if (_withAudio)
        {
            _audioControlSocket = BindUdp();
            audioControlPort = ((IPEndPoint)_audioControlSocket.LocalEndPoint!).Port;
            streams.Add(new Dictionary<string, object?>
            {
                ["type"] = 96L,
                ["streamConnectionID"] = _audioStreamConnectionId,
                ["ct"] = 2L,                    // ALAC
                ["spf"] = 352L,
                ["sr"] = (long)SampleRate,
                ["audioFormat"] = 0x40000L,     // ALAC/44100/16/2
                ["audioFormatIndex"] = 0x12L,
                ["controlPort"] = (long)audioControlPort,
                ["audioMode"] = "default",
                ["usingScreen"] = true,
                // PINNED: min == max == the shared budget. A range here would let the receiver's
                // jitter buffer ratchet up under Wi-Fi contention (which the bulk video stream
                // causes) and never come back — the cause of audio drifting seconds behind video.
                ["latencyMin"] = MirrorLatency.Frames(SampleRate),
                ["latencyMax"] = MirrorLatency.Frames(SampleRate),
                ["shk"] = _audioChachaKey,
                ["isMedia"] = true,
                ["supportsDynamicStreamID"] = true,
            });
        }

        var videoResp = await SetupAsync(RtspSessionUri, new Dictionary<string, object?> { ["streams"] = streams }, ct).ConfigureAwait(false);
        var respStreams = (videoResp.GetValueOrDefault("streams") as List<object?>)?.OfType<Dictionary<string, object?>>().ToList()
            ?? throw new RtspException("video SETUP: no streams in response");

        var videoStream = respStreams.FirstOrDefault(s => (s.GetValueOrDefault("type") as long?) == 110L) ?? respStreams[0];
        long dataPort = videoStream.GetValueOrDefault("dataPort") as long?
            ?? throw new RtspException("video SETUP: no dataPort");
        Stage($"video data port {dataPort}");

        if (_withAudio)
        {
            var audioStream = respStreams.FirstOrDefault(s => (s.GetValueOrDefault("type") as long?) == 96L);
            if (audioStream?.GetValueOrDefault("dataPort") is long audioDataPort)
            {
                long audioTheirControl = audioStream.GetValueOrDefault("controlPort") as long? ?? audioDataPort;
                _audioDataSocket = BindUdp();
                _audioDataRemote = new IPEndPoint(_rtsp.RemoteAddress, (int)audioDataPort);
                _audioControlRemote = new IPEndPoint(_rtsp.RemoteAddress, (int)audioTheirControl);
                _audioCipher = new ChaCha20Poly1305(_audioChachaKey);
                Stage($"audio stream ready (data={audioDataPort} control={audioTheirControl})");
            }
            else
            {
                Stage("receiver did not grant an audio stream — mirroring video only");
            }
        }

        _dataChannel = new TcpClient();
        await _dataChannel.ConnectAsync(_rtsp.RemoteAddress, (int)dataPort, ct).ConfigureAwait(false);
        _dataChannel.NoDelay = true;
        _dataChannel.SendBufferSize = 64 * 1024;
        _dataStream = _dataChannel.GetStream();

        Stage("RECORD");
        var record = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "RECORD",
            Uri = RtspSessionUri,
            Headers =
            {
                ["Session"] = _sessionUuid,
                ["Range"] = "npt=0-",
                ["RTP-Info"] = "seq=0;rtptime=0",
            },
        }, ct).ConfigureAwait(false);
        record.EnsureSuccess("RECORD");

        // Video frames are ChaCha20-Poly1305, keyed by HKDF-SHA512 from the pair-verify
        // shared secret (the Apple TV "DataStream-Output-Encryption-Key" direction).
        byte[] videoKey = MirrorVideoStream.DeriveDataStreamKey(_hap.SharedSecret, _videoStreamConnectionId);
        _videoCipher = new MirrorVideoCipher(videoKey);
        Stage("session live — ready to stream video");
    }

    private async Task<Dictionary<string, object?>> SetupAsync(string uri, Dictionary<string, object?> body, CancellationToken ct)
    {
        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "SETUP",
            Uri = uri,
            Body = BinaryPlist.Write(body),
            ContentType = "application/x-apple-binary-plist",
        }, ct).ConfigureAwait(false);
        if (resp.StatusCode != 200)
            Stage($"SETUP {uri} → {resp.StatusCode} {resp.ReasonPhrase}; headers=[{string.Join(", ", resp.Headers.Select(h => $"{h.Key}: {h.Value}"))}]"
                + (resp.Body.Length > 0 ? $"; body={Convert.ToHexString(resp.Body.AsSpan(0, Math.Min(resp.Body.Length, 96)))}" : "; no body"));
        resp.EnsureSuccess("SETUP");
        return resp.Body.Length > 0 ? BinaryPlist.ReadDictionary(resp.Body) : [];
    }

    // ------------------------------------------------------------ streaming

    /// <summary>Whether an audio stream was granted by the receiver (mirror carries audio).</summary>
    public bool HasAudio => _audioCipher is not null;

    /// <summary>
    /// Sets the receiver's volume on the AirPlay dB scale, exactly as the audio-only path does.
    ///
    /// <para>A mirror session that carries audio owns that audio, so it has to own its volume too.
    /// Without this the slider in Screen+Audio mode was bound to nothing at all: the controller
    /// looked the destination up among the AUDIO sessions, found nothing there because a mirroring
    /// destination lives elsewhere, and silently did nothing. A control that moves and has no
    /// effect is worse than one that isn't offered.</para>
    /// </summary>
    public async Task SetVolumeAsync(double db, CancellationToken ct = default)
    {
        if (!HasAudio) return;   // video-only session: there is nothing to set
        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "SET_PARAMETER",
            Uri = RtspSessionUri,
            Body = System.Text.Encoding.ASCII.GetBytes(Raop.VolumeControl.FormatVolumeBody(db)),
            ContentType = "text/parameters",
        }, ct).ConfigureAwait(false);
        resp.EnsureSuccess("SET_PARAMETER volume");
    }

    public async Task StartStreamingAsync(IH264VideoSource source, Audio.IAudioSource? audioSource = null)
    {
        _source = source;
        source.Configure(_receiverDisplayW, _receiverDisplayH);
        source.FrameEncoded += OnFrameEncoded;
        // A source that has stopped producing frames ends the session. Without this the pumps
        // kept running happily over an empty stream and the app went on reporting a live mirror.
        source.Failed += RaiseFaulted;
        _loops.Add(Task.Run(() => SuperviseAsync(SendLoopAsync, _cts.Token)));
        _loops.Add(Task.Run(() => SuperviseAsync(DataHeartbeatLoopAsync, _cts.Token)));
        _loops.Add(Task.Run(() => SuperviseAsync(FeedbackLoopAsync, _cts.Token)));

        // Audio-in-mirror: an ALAC RTP pump + 1 Hz sync, sharing the video clock so the
        // Apple TV keeps audio and picture in lock-step.
        if (_audioCipher is not null && audioSource is not null)
        {
            _audioSource = audioSource;
            // Reported in seconds (a fixed device/margin property); converted to frames once, here,
            // rather than on every sync packet.
            double captureLatencySeconds = (audioSource as Audio.ICaptureLatency)?.CaptureLatencySeconds ?? 0;
            _captureLatencyFrames = (long)(captureLatencySeconds * SampleRate);
            _audioRtpBase = (uint)RandomNumberGenerator.GetInt32(0, int.MaxValue);

            // Seed the "where is audio now" value before anything can read it. The sync loop
            // publishes rtpNow minus the latency budget; starting from zero underflowed the
            // unsigned subtraction and put a nonsense playout point in the first sync packet.
            Interlocked.Exchange(ref _audioRtpNow, _audioRtpBase);

            // Discard whatever the capture ring accumulated between opening the source and video
            // going live — the same flush the RAOP path does — so audio starts at "now" rather
            // than replaying a backlog it would then spend the session catching up from.
            (audioSource as Audio.IFlushableAudioSource)?.FlushToLive();

            _loops.Add(Task.Run(() => StartAudioWhenVideoIsLiveAsync(_cts.Token)));
        }

        source.RequestKeyframe();
        await source.StartAsync(_cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for video to actually be on the wire, then starts the audio pump and its sync loop.
    /// Bounded: if video never starts there is nothing for audio to attach to anyway, and the
    /// session's own failure handling covers it.
    /// </summary>
    private async Task StartAudioWhenVideoIsLiveAsync(CancellationToken ct)
    {
        try
        {
            await _firstVideoPacketSent.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Stage("no video within 10s — audio not started");
            return;
        }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        // Re-flush: the wait may have taken a moment, and audio must begin at the live edge.
        (_audioSource as Audio.IFlushableAudioSource)?.FlushToLive();

        _audioPumpThread = new Thread(AudioPump)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "WinPlay-MirrorAudio",
        };
        _audioPumpThread.Start();
        Stage("audio started (video is live)");

        await SuperviseAsync(AudioSyncLoopAsync, ct).ConfigureAwait(false);
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    /// <summary>
    /// Realtime ALAC audio pump: captured PCM → ALAC verbatim frame → RTP (PT 96, SSRC 0)
    /// → ChaCha20-Poly1305 with an 8-byte LE counter nonce (mirror audio convention), the
    /// counter appended as an 8-byte trailer. Paced by wall clock against frames sent.
    /// </summary>
    private void AudioPump()
    {
        timeBeginPeriod(1);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Span<short> samples = stackalloc short[352 * 2];
            Span<byte> nonce = stackalloc byte[12]; // reused each frame (never inside the loop)
            long framesSent = 0;
            while (!_cts.IsCancellationRequested)
            {
                double dueMs = framesSent * 352_000.0 / SampleRate;
                double nowMs = sw.Elapsed.TotalMilliseconds;
                if (nowMs < dueMs) { int sleep = (int)(dueMs - nowMs); if (sleep >= 2) Thread.Sleep(sleep - 1); continue; }

                _audioSource!.Read(samples);
                byte[] alac = Raop.AlacFramer.WrapPcmFrame(samples);
                uint rtpTime = (uint)(_audioRtpBase + (ulong)framesSent * 352);

                byte[] packet = new byte[12 + alac.Length + 16 + 8];
                packet[0] = 0x80;
                packet[1] = 0x60; // PT 96, no marker (Apple mirror audio)
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _audioSeq);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTime);
                // SSRC = 0 (bytes 8..12 already zero)

                ulong counter = _audioNonceCounter++;
                nonce.Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
                _audioCipher!.Encrypt(nonce, alac, packet.AsSpan(12, alac.Length),
                    packet.AsSpan(12 + alac.Length, 16), packet.AsSpan(4, 8)); // AAD = rtpTime + SSRC
                BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(12 + alac.Length + 16), counter);

                try { _audioDataSocket!.SendTo(packet, _audioDataRemote!); }
                catch (SocketException) { }

                _audioSeq++;
                framesSent++;
                Interlocked.Exchange(ref _audioRtpNow, rtpTime);
            }
        }
        catch (Exception ex)
        {
            // Swallowing this meant the mirror's audio could stop dead while the picture kept
            // going and the app reported everything as fine. The pump owns its own thread, so the
            // only place this can be reported from is here.
            RaiseFaulted(ex);
        }
        finally { timeEndPeriod(1); }
    }

    private long _audioRtpNow;

    /// <summary>1 Hz audio sync packet (0xD4) mapping the audio RTP timeline onto boot NTP time.</summary>
    private async Task AudioSyncLoopAsync(CancellationToken ct)
    {
        // Computed once: the shared 250 ms budget minus whatever the audio source's newest sample
        // is ALREADY behind "now" (PositionedCaptureRing's jitter margin + capture callback period,
        // via ICaptureLatency — zero if the source cannot report it). Promising the receiver the
        // full raw budget behind a sample that was itself already stale schedules audio exactly
        // that much later than video, which carries no equivalent capture lag — the dominant term
        // in mirroring's audio-lags-video offset. Floored at 1 frame by MirrorLatency.AnchorFrames.
        long anchorFrames = MirrorLatency.AnchorFrames(SampleRate, _captureLatencyFrames);

        bool first = true;
        byte[] pkt = new byte[20];
        while (!ct.IsCancellationRequested)
        {
            uint rtpNow = (uint)Interlocked.Read(ref _audioRtpNow);
            pkt[0] = first ? (byte)0x90 : (byte)0x80;
            pkt[1] = 0xD4;
            // 0x0004, exactly as captured from real Apple senders (UxPlay lib/raop_rtp.c documents
            // the observed packet: "packet[2:3] = 0x00 0x04"). This carried 0x0007 — the value
            // from a different RAOP generation's documentation — and a strict receiver treats a
            // malformed control packet as a protocol violation worth ending the session over.
            BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), 0x0004);
            // The playout point is the (capture-latency-adjusted) budget behind the newest sample
            // sent — close to the value SETUP pinned latencyMin/Max to, so the receiver is told a
            // consistent story and has no elastic room to drift into.
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), rtpNow - (uint)anchorFrames);
            BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), _clock.BootTimestamp);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), rtpNow);
            try { await _audioControlSocket!.SendToAsync(pkt, SocketFlags.None, _audioControlRemote!, ct).ConfigureAwait(false); }
            catch (SocketException) { }
            catch (OperationCanceledException) { return; }
            first = false;
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnFrameEncoded(ReadOnlyMemory<byte> annexB, bool isKeyframe, long captureTicks)
    {
        try
        {
            var nals = H264.SplitAnnexB(annexB.Span);
            if (nals.Count == 0) return;

            foreach (byte[] nal in nals)
            {
                byte type = H264.NalType(nal);
                if (type == H264.NalSps) _sps = nal;
                else if (type == H264.NalPps) _pps = nal;
            }

            // Every AirPlay/RTP timestamp this frame carries is derived from the moment it was
            // CAPTURED, not the moment this handler happens to run — pipe/queueing delay between
            // capture and here (the supervised capture-host case routes frames over a named pipe)
            // must not silently become part of the A/V sync error. See MirrorClock.FrameTimestampAt
            // and MirrorPipeProtocol's doc comment on CaptureTicks.
            ulong frameTimestamp = _clock.FrameTimestampAt(captureTicks);

            // Send the SPS/PPS codec packet once, before the first VCL frame. The header
            // carries the encoded content size and the receiver's display size so the TV
            // can pillarbox/letterbox content whose aspect differs from the panel.
            if (!_codecSent && _sps is not null && _pps is not null && _source is not null)
            {
                int displayW = _receiverDisplayW > 0 ? _receiverDisplayW : _source.Width;
                int displayH = _receiverDisplayH > 0 ? _receiverDisplayH : _source.Height;
                byte[] avcc = H264.BuildAvcCConfig(_sps, _pps);
                byte[] codecHeader = MirrorVideoStream.BuildCodecHeader(avcc.Length, frameTimestamp,
                    _source.Width, _source.Height, displayW, displayH);
                SendPacket(codecHeader, avcc);
                _codecSent = true;
                Stage($"codec packet sent (encode {_source.Width}x{_source.Height}, display {displayW}x{displayH})");
                _firstVideoPacketSent.TrySetResult();   // audio may now start — see the field's note
            }
            if (!_codecSent) return; // wait for SPS/PPS

            // VCL NAL units (coded slices) go in the encrypted video packet as AVCC.
            var vcl = nals.Where(n => H264.NalType(n) is 1 or 5).ToList();
            if (vcl.Count == 0) return;
            byte[] payload = H264.ToAvcc(vcl);

            byte[] header = MirrorVideoStream.BuildVideoHeader(payload.Length + 16, isKeyframe, frameTimestamp);
            byte[] encrypted = _videoCipher!.Seal(header, payload);
            SendPacket(header, encrypted);

            long n = Interlocked.Increment(ref _framesSent);
            if (n == 1) Stage("first video frame sent");
            // "encoded", not "sent": this counts frames handed to the send queue. Whether they
            // reach the wire is SendLoopAsync's truth, and a dead send loop now faults the
            // session — this counter must not imply delivery it cannot see.
            else if (n % 120 == 0) Stage($"{n} frames encoded");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (_cts.IsCancellationRequested) return;
            Stage($"data channel closed: {ex.Message}");
            RaiseFaulted(ex);   // a dead video channel is a dead session — see SendLoopAsync
        }
    }

    /// <summary>Enqueues a header+payload packet for the sender loop (drops the oldest if the link is saturated).</summary>
    private void SendPacket(byte[] header, byte[] payload)
    {
        byte[] packet = new byte[header.Length + payload.Length];
        header.CopyTo(packet, 0);
        payload.CopyTo(packet, header.Length);
        while (!_sendQueue.TryAdd(packet) && !_cts.IsCancellationRequested)
        {
            if (_sendQueue.TryTake(out _)) continue; // make room by dropping the oldest
            break;
        }
    }

    /// <summary>Drains the send queue to the TCP data channel, off the encoder thread.</summary>
    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            foreach (byte[] packet in _sendQueue.GetConsumingEnumerable(ct))
            {
                if (_dataStream is null) continue;
                await _dataStream.WriteAsync(packet, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) return;
            Stage($"data channel closed: {ex.Message}");
            // The video TCP channel is the session. When the receiver closes it, nothing sent
            // after this line reaches a screen — yet the encoder kept running, the frame counter
            // kept climbing, and the app reported a healthy mirror while the TV sat on a frozen
            // frame. A dead send loop IS a dead session and must say so.
            RaiseFaulted(ex);
        }
    }

    /// <summary>
    /// Connects the reverse event channel the receiver announced (TCP, HAP-encrypted with
    /// the Events keys) and drains it, answering each complete request with 200 OK. A
    /// desynced event channel gets the whole session torn down by the receiver.
    /// </summary>
    private void StartEventChannel(IPAddress address, int port)
    {
        _eventChannel = new TcpClient();
        _loops.Add(Task.Run(async () =>
        {
            try
            {
                await _eventChannel.ConnectAsync(address, port, _cts.Token).ConfigureAwait(false);
                var stream = _eventChannel.GetStream();
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
                        triedSwap = true;
                        crypto.Dispose();
                        crypto = new ChannelCrypto(_hap.EventsReadKey, _hap.EventsWriteKey);
                        continue;
                    }

                    while (TryTakeEventRequest(plain, out string requestLine, out string? cseq))
                    {
                        Stage($"event: {requestLine}");
                        string headers = "RTSP/1.0 200 OK\r\n"
                            + (cseq is not null ? $"CSeq: {cseq}\r\n" : "")
                            + "Server: AirTunes/366.0\r\nContent-Length: 0\r\n\r\n";
                        await stream.WriteAsync(crypto.Encrypt(System.Text.Encoding.ASCII.GetBytes(headers)), _cts.Token).ConfigureAwait(false);
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

    private static bool TryTakeEventRequest(MemoryStream buffer, out string requestLine, out string? cseq)
    {
        requestLine = "";
        cseq = null;
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
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(value, out contentLength);
            else if (name.Equals("CSeq", StringComparison.OrdinalIgnoreCase)) cseq = value;
        }
        int total = headerEnd + 4 + contentLength;
        if (buf.Length < total) return false;

        requestLine = lines[0];
        byte[] rest = buf[total..].ToArray();
        buffer.SetLength(0);
        buffer.Write(rest);
        return true;
    }

    /// <summary>Answers the receiver's NTP timing requests (boot-relative, epoch-adjusted).</summary>
    private async Task TimingLoopAsync(CancellationToken ct)
    {
        byte[] buf = new byte[128];
        byte[] reply = new byte[32];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await _timingSocket!.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (r.ReceivedBytes < 32) continue;

            buf.AsSpan(0, 32).CopyTo(reply);
            reply[0] = 0x80;
            reply[1] = 0xD3;
            ulong now = _clock.BootTimestamp;
            buf.AsSpan(24, 8).CopyTo(reply.AsSpan(8));                    // reference = sender transmit time
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), now); // receive
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), now); // transmit
            try { await _timingSocket!.SendToAsync(reply, SocketFlags.None, r.RemoteEndPoint, ct).ConfigureAwait(false); }
            catch (SocketException) { }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Data-channel keep-alive: a header-only heartbeat packet (type 0x02).</summary>
    private async Task DataHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!_codecSent) continue;
            try
            {
                byte[] header = new byte[MirrorVideoStream.HeaderLength];
                header[4] = 0x02; // heartbeat payload type
                header[6] = 0x1e;
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), _clock.FrameTimestamp);
                SendPacket(header, []);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task FeedbackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                await _rtsp.RequestAsync(new RtspRequest { Method = "POST", Uri = "/feedback" }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RtspException or IOException or SocketException or ObjectDisposedException)
            {
                if (!ct.IsCancellationRequested) Stage($"feedback ended: {ex.Message}");
                return;
            }
        }
    }

    public async Task StopAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _rtsp.RequestAsync(new RtspRequest
            {
                Method = "TEARDOWN",
                Uri = RtspSessionUri,
                Headers = { ["Session"] = _sessionUuid },
            }, cts.Token).ConfigureAwait(false);
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
        if (_source is not null) _source.FrameEncoded -= OnFrameEncoded;
        await StopAsync().ConfigureAwait(false);
        _sendQueue.CompleteAdding();
        _audioPumpThread?.Join(2000);
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { }
        _videoCipher?.Dispose();
        _audioCipher?.Dispose();
        _audioSource?.Dispose();
        _audioDataSocket?.Dispose();
        _audioControlSocket?.Dispose();
        _timingSocket?.Dispose();
        _dataStream?.Dispose();
        _dataChannel?.Dispose();
        _eventChannel?.Dispose();
        _rtsp.Dispose();
        _sendQueue.Dispose();
        _cts.Dispose();
    }

    /// <summary>Reads the receiver's primary display size (widthPixels/heightPixels, else width/height) from /info.</summary>
    private static (int Width, int Height) ParseDisplaySize(Dictionary<string, object?> info)
    {
        if (info.GetValueOrDefault("displays") is not List<object?> displays || displays.Count == 0
            || displays[0] is not Dictionary<string, object?> d)
            return (0, 0);
        int Get(string k) => d.GetValueOrDefault(k) is long v ? (int)v : 0;
        int w = Get("widthPixels"), h = Get("heightPixels");
        if (w <= 0 || h <= 0) { w = Get("width"); h = Get("height"); }
        return (w, h);
    }

    private static Socket BindUdp()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return s;
    }

    private static string LocalMac()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length == 6) return string.Join(":", mac.Select(b => b.ToString("X2")));
        }
        return "02:00:00:00:00:01";
    }
}

/// <summary>
/// The ONE presentation-latency budget a mirroring session promises its receiver, shared by
/// the video and audio streams (A-V sync).
///
/// <para><b>Why this must be a single value.</b> A mirror carries two streams that have to
/// land on the same instant. Previously each invented its own number: video biased its frame
/// timestamps by 100 ms while audio anchored its sync packets 250 ms back — a permanent 150 ms
/// skew before anything else went wrong. Worse, the audio SETUP advertised
/// <c>latencyMin</c>=250 ms with <c>latencyMax</c>=2000 ms, granting the receiver an 8×
/// elastic jitter buffer while telling it to target the floor. Real AirPlay receivers ratchet
/// that buffer UP under jitter — which the bursty video TCP stream on the same Wi-Fi reliably
/// causes — and never ratchet it back down. The result was audio drifting to seconds behind
/// video and staying there, while video (whose send queue is drop-oldest, so it cannot
/// accumulate) stayed flat.</para>
///
/// <para>Fixing this by subtracting an offset would be treating the symptom. The cause is the
/// receiver having permission to buffer more than we promise, so the fix removes the
/// permission: <c>latencyMin == latencyMax == </c> this value, and every timestamp either
/// stream emits is derived from it. With no elastic range there is nothing to creep into.
/// (Same invariant as doubletake's <c>latMin = latMax = audioLatencySamples</c> with the video
/// timestamp bias taken from the same session latency.)</para>
///
/// <para>250 ms is chosen to sit above Wi-Fi jitter for a link shared with bulk video, while
/// remaining in the range Apple's own mirroring presents at — low enough that lip-sync reads
/// as immediate.</para>
/// </summary>
internal static class MirrorLatency
{
    /// <summary>The shared presentation delay for both mirror streams.</summary>
    public static readonly TimeSpan Target = TimeSpan.FromMilliseconds(250);

    /// <summary>The same budget in audio frames at 44.1 kHz (11025 = 250 ms).</summary>
    public static long Frames(int sampleRate) => (long)(Target.TotalSeconds * sampleRate);

    /// <summary>The same budget in whole milliseconds, for the video stream's SETUP.</summary>
    public static long Milliseconds => (long)Target.TotalMilliseconds;

    /// <summary>
    /// The playout anchor the audio sync loop actually promises the receiver: the shared budget
    /// minus however many frames the audio source's newest sample is ALREADY behind "now" (its
    /// <see cref="Audio.ICaptureLatency.CaptureLatencySeconds"/>, converted to frames — zero when
    /// the source cannot report one). Floored at 1 frame so the sync packet's playout-point field
    /// (offset 4) is never at or past the newest sample sent, which would tell the receiver to play
    /// audio at/after a sample that has not gone out yet.
    /// </summary>
    public static long AnchorFrames(int sampleRate, long captureLatencyFrames) =>
        Math.Max(1, Frames(sampleRate) - captureLatencyFrames);
}

/// <summary>
/// Boot-relative NTP clock for mirroring. Frame headers use raw boot-relative fixed-point
/// (plus a forward bias = the session's playout latency); timing replies add the 1900→1970
/// epoch, matching what Apple senders emit (doubletake mirror.go).
///
/// <para>The bias is <see cref="MirrorLatency.Target"/> — the SAME budget the audio stream is
/// pinned to. Video and audio must promise the receiver one shared presentation delay or they
/// cannot be in sync by construction.</para>
/// </summary>
internal sealed class MirrorClock
{
    private const ulong EpochOffsetSeconds = 2208988800UL;

    // Raw QueryPerformanceCounter tick this clock started at. Kept as a plain tick count (rather
    // than only a Stopwatch instance) so a frame's OWN capture-time tick — from this process or a
    // capture-host CHILD process, since QPC is machine-wide on Windows — can be converted against
    // the exact same baseline as "now", in FrameTimestampAt.
    private readonly long _startTicks = Stopwatch.GetTimestamp();

    public ulong FrameTimestamp => FrameTimestampAt(Stopwatch.GetTimestamp());

    /// <summary>
    /// Converts a frame's OWN capture-time tick (a <see cref="Stopwatch.GetTimestamp"/> value taken
    /// when the frame was captured/encoded — not whenever this method happens to run) into the
    /// boot-relative NTP timestamp the receiver expects, still biased forward by
    /// <see cref="MirrorLatency.Target"/>. Using the real capture tick, instead of the moment this
    /// runs, is what keeps constant delay between capture and here (pipe transit for a supervised
    /// capture-host child, scheduling jitter, …) from silently becoming part of the A/V sync error —
    /// see <c>MirrorPipeProtocol</c>'s doc comment on why <c>CaptureTicks</c> exists.
    /// </summary>
    public ulong FrameTimestampAt(long captureTicks)
    {
        double elapsedSeconds = Math.Max(0.0, (captureTicks - _startTicks) / (double)Stopwatch.Frequency);
        return ToNtp(TimeSpan.FromSeconds(elapsedSeconds) + MirrorLatency.Target, 0);
    }

    public ulong BootTimestamp =>
        ToNtp(TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _startTicks) / (double)Stopwatch.Frequency), EpochOffsetSeconds);

    private static ulong ToNtp(TimeSpan elapsed, ulong epochOffset)
    {
        double total = elapsed.TotalSeconds;
        ulong seconds = (ulong)total + epochOffset;
        ulong fraction = (ulong)((total - Math.Floor(total)) * 4294967296.0);
        return (seconds << 32) | (fraction & 0xFFFFFFFF);
    }
}
