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
/// AirPlay 2 screen-mirroring session to an Apple TV / AirPlay-2 TV (plan §3.7). Flow:
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

    private RtspConnection _rtsp = new();
    private readonly MirrorClock _clock = new();
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _connCts;
    private readonly List<Task> _loops = [];
    private string _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
    private long _sessionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private long _videoStreamConnectionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private IPAddress? _remoteAddress;
    private int _remotePort;
    private HapPairingCredentials? _credentials;
    private int _reconnecting;

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
    private byte[] _audioChachaKey = RandomNumberGenerator.GetBytes(32);
    private long _audioStreamConnectionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private Socket? _audioDataSocket;
    private Socket? _audioControlSocket;
    private IPEndPoint? _audioDataRemote;
    private IPEndPoint? _audioControlRemote;
    private ChaCha20Poly1305? _audioCipher;
    private Audio.IAudioSource? _audioSource;
    private Audio.SystemAudioMover? _fallbackAudioMover;
    private Thread? _audioPumpThread;
    private ushort _audioSeq;
    private uint _audioRtpBase;
    private ulong _audioNonceCounter;

    // Video packets are built (and encrypted, in frame order) on the encoder thread, then
    // handed to a dedicated sender so TCP writes never stall the encoder. Bounded + drop-
    // oldest: for live mirroring a fresh frame beats a backlog.
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _sendQueue
        = new(new System.Collections.Concurrent.ConcurrentQueue<byte[]>(), boundedCapacity: 64);

    private readonly MirrorVideoPacketizer _packetizer = new();
    private readonly MirrorCongestionController _congestion = new();
    private readonly Stopwatch _sessionClock = Stopwatch.StartNew();
    private long _framesSent;
    private int _receiverDisplayW;
    private int _receiverDisplayH;
    private string? _receiverModel;

    public event Action<string>? StageChanged;

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
        var s = new MirrorSession(withAudio)
        {
            _remoteAddress = address,
            _remotePort = port,
            _credentials = credentials,
        };
        if (stageChanged is not null) s.StageChanged += stageChanged;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await s.HandshakeAsync(address, port, credentials, ct).ConfigureAwait(false);
                    return s;
                }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested
                    && ex is SocketException or IOException or RtspException)
                {
                    s.Stage($"initial handshake interrupted while receiver wakes up ({ex.Message}) — retrying ({attempt}/3)...");
                    s.DisposeConnectionResources();
                    s._rtsp = new RtspConnection();
                    s._sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
                    s._sessionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                    s._videoStreamConnectionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                    s._audioStreamConnectionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                    s._audioChachaKey = RandomNumberGenerator.GetBytes(32);
                    await Task.Delay(600, ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandshakeAsync(IPAddress address, int port, HapPairingCredentials credentials, CancellationToken ct)
    {
        try { _connCts?.Cancel(); } catch { }
        try { _connCts?.Dispose(); } catch { }
        _connCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        CancellationToken connCt = _connCts.Token;

        Stage($"connecting to {address}:{port}");
        await _rtsp.ConnectAsync(address, port, ct).ConfigureAwait(false);

        var info = await _rtsp.RequestAsync(new RtspRequest { Method = "GET", Uri = "/info" }, ct).ConfigureAwait(false);
        info.EnsureSuccess("GET /info");
        var infoDict = BinaryPlist.ReadDictionary(info.Body);
        (_receiverDisplayW, _receiverDisplayH) = ParseDisplaySize(infoDict);
        _receiverModel = infoDict.GetValueOrDefault("model") as string;
        Stage($"receiver: {infoDict.GetValueOrDefault("name")} ({_receiverModel})"
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
        _loops.Add(Task.Run(() => TimingLoopAsync(_timingSocket, connCt)));

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
            StartEventChannel(_rtsp.RemoteAddress, (int)eventPort, connCt);

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
                ["latencyMs"] = 65L,
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
                ["latencyMin"] = 2205L,
                ["latencyMax"] = 11025L,
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
        _dataChannel.SendBufferSize = 256 * 1024;
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

    /// <summary>Whether an audio stream is enabled/granted (mirror carries audio).</summary>
    public bool HasAudio => _withAudio || _audioCipher is not null;

    public async Task StartStreamingAsync(IH264VideoSource source, Audio.IAudioSource? audioSource = null)
    {
        _source = source;
        if (audioSource is not null)
        {
            _audioSource = audioSource;
        }
        else if (_withAudio && _audioSource is null)
        {
            try
            {
                _fallbackAudioMover = new Audio.SystemAudioMover();
                _fallbackAudioMover.EnterStreaming();
                _audioSource = _fallbackAudioMover.CreateCaptureSource();
            }
            catch { }
        }

        source.ConfigureReceiverProfile(_receiverModel, _receiverDisplayW, _receiverDisplayH);
        source.FrameEncoded += OnFrameEncoded;

        StartConnectionLoops();

        source.RequestKeyframe();
        await source.StartAsync(_cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts all per-connection sender, heartbeat, feedback, and audio (ALAC RTP + 1 Hz sync)
    /// loops for the current active RTSP session. Called both on initial start and after every
    /// auto-reconnect (e.g. when Apple TV wakes from standby and resets the initial session).
    /// </summary>
    private void StartConnectionLoops()
    {
        CancellationToken connCt = _connCts?.Token ?? _cts.Token;
        _loops.Add(Task.Run(() => SendLoopAsync(connCt)));
        _loops.Add(Task.Run(() => DataHeartbeatLoopAsync(connCt)));
        _loops.Add(Task.Run(() => FeedbackLoopAsync(connCt)));

        // Audio-in-mirror: an ALAC RTP pump + 1 Hz sync, sharing the video clock so the
        // Apple TV keeps audio and picture in lock-step.
        if (_audioCipher is not null && _audioSource is not null && _audioDataSocket is not null && _audioDataRemote is not null)
        {
            _audioSeq = 0;
            _audioNonceCounter = 0;
            _audioRtpBase = (uint)RandomNumberGenerator.GetInt32(11025, int.MaxValue);
            Interlocked.Exchange(ref _audioRtpNow, _audioRtpBase);

            var audioSrc = _audioSource;
            var cipher = _audioCipher;
            var dataSock = _audioDataSocket;
            var dataRemote = _audioDataRemote;
            var ctrlSock = _audioControlSocket;
            var ctrlRemote = _audioControlRemote;

            if (ctrlSock is not null && ctrlRemote is not null)
                _loops.Add(Task.Run(() => AudioSyncLoopAsync(connCt, ctrlSock, ctrlRemote)));

            _audioPumpThread = new Thread(() => AudioPump(connCt, audioSrc, cipher, dataSock, dataRemote))
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "WinPlay-MirrorAudio",
            };
            _audioPumpThread.Start();
            Stage("audio pump + sync active");
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    /// <summary>
    /// Realtime ALAC audio pump: captured PCM → ALAC verbatim frame → RTP (PT 96, SSRC 0)
    /// → ChaCha20-Poly1305 with an 8-byte LE counter nonce (mirror audio convention), the
    /// counter appended as an 8-byte trailer. Paced by wall clock against frames sent.
    /// </summary>
    private void AudioPump(CancellationToken connCt, Audio.IAudioSource audioSource,
        ChaCha20Poly1305 audioCipher, Socket audioDataSocket, IPEndPoint audioDataRemote)
    {
        timeBeginPeriod(1);
        try
        {
            // Flush any stale audio samples accumulated in the capture ring buffer during
            // Apple TV wake-up / reconnect so audio resumes with 0 ms lag in lock-step with video.
            Span<short> drain = stackalloc short[2048];
            for (int i = 0; i < 180; i++)
                audioSource.Read(drain);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Span<short> samples = stackalloc short[352 * 2];
            Span<byte> nonce = stackalloc byte[12]; // reused each frame (never inside the loop)
            long framesSent = 0;
            uint rtpBase = _audioRtpBase;

            while (!connCt.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                double dueMs = framesSent * 352_000.0 / SampleRate;
                double nowMs = sw.Elapsed.TotalMilliseconds;
                if (nowMs < dueMs)
                {
                    int sleep = (int)(dueMs - nowMs);
                    if (sleep >= 2) Thread.Sleep(sleep - 1);
                    continue;
                }

                try
                {
                    audioSource.Read(samples);
                    byte[] alac = Raop.AlacFramer.WrapPcmFrame(samples);
                    uint rtpTime = (uint)(rtpBase + (ulong)framesSent * 352);

                    byte[] packet = new byte[12 + alac.Length + 16 + 8];
                    packet[0] = 0x80;
                    packet[1] = 0x60; // PT 96, no marker (Apple mirror audio)
                    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _audioSeq);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTime);
                    // SSRC = 0 (bytes 8..12 already zero)

                    ulong counter = _audioNonceCounter++;
                    nonce.Clear();
                    BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
                    audioCipher.Encrypt(nonce, alac, packet.AsSpan(12, alac.Length),
                        packet.AsSpan(12 + alac.Length, 16), packet.AsSpan(4, 8)); // AAD = rtpTime + SSRC
                    BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(12 + alac.Length + 16), counter);

                    try { audioDataSocket.SendTo(packet, audioDataRemote); }
                    catch (SocketException) { }

                    _audioSeq++;
                    framesSent++;
                    Interlocked.Exchange(ref _audioRtpNow, rtpTime);
                }
                catch (ObjectDisposedException) when (connCt.IsCancellationRequested || _cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    if (connCt.IsCancellationRequested || _cts.IsCancellationRequested)
                        break;
                }
            }
        }
        finally { timeEndPeriod(1); }
    }

    private long _audioRtpNow;

    /// <summary>1 Hz audio sync packet (0xD4) mapping the audio RTP timeline onto boot NTP time.</summary>
    private async Task AudioSyncLoopAsync(CancellationToken ct, Socket audioControlSocket, IPEndPoint audioControlRemote)
    {
        bool first = true;
        int syncSent = 0;
        byte[] pkt = new byte[20];
        while (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            uint rtpNow = (uint)Interlocked.Read(ref _audioRtpNow);
            pkt[0] = first ? (byte)0x90 : (byte)0x80;
            pkt[1] = 0xD4;
            BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), 0x0007);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), rtpNow - 2205);
            BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(8), _clock.BootTimestamp);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(16), rtpNow);
            try { await audioControlSocket.SendToAsync(pkt, SocketFlags.None, audioControlRemote, ct).ConfigureAwait(false); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
            catch (OperationCanceledException) { return; }
            first = false;
            syncSent++;
            int delayMs = syncSent < 3 ? 150 : 1000;
            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnFrameEncoded(ReadOnlyMemory<byte> annexB, bool isKeyframe)
    {
        if (Volatile.Read(ref _reconnecting) != 0 || _cts.IsCancellationRequested) return;
        try
        {
            if (_source is null) return;

            var decision = _congestion.EvaluateIncomingFrame(
                isKeyframe, _sendQueue.Count, _sessionClock.ElapsedMilliseconds);
            if (decision.BitrateScale is double emergencyScale)
                _source.AdjustBitrate(emergencyScale);
            if (decision.RequestKeyframe)
                _source.RequestKeyframe();
            if (decision.DropFrame)
                return;

            string? codecLog = null;
            lock (_sendQueue)
            {
                if (_videoCipher is null) return;
                ulong frameTimestamp = _clock.FrameTimestamp;
                if (!_packetizer.TryPacketize(
                        annexB.Span,
                        isKeyframe,
                        _source.Width,
                        _source.Height,
                        _receiverDisplayW,
                        _receiverDisplayH,
                        frameTimestamp,
                        _videoCipher,
                        out byte[]? codecPacket,
                        out byte[]? videoPacket,
                        out bool needsKeyframe))
                {
                    if (needsKeyframe) _source.RequestKeyframe();
                    return;
                }

                if (codecPacket is not null)
                {
                    int displayW = _receiverDisplayW > 0 ? _receiverDisplayW : 1920;
                    int displayH = _receiverDisplayH > 0 ? _receiverDisplayH : 1080;
                    try { _sendQueue.Add(codecPacket, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (InvalidOperationException) { return; }
                    codecLog = $"codec packet sent (encode {_source.Width}x{_source.Height}, display {displayW}x{displayH})";
                }

                if (videoPacket is not null)
                {
                    try { _sendQueue.Add(videoPacket, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (InvalidOperationException) { return; }
                }
            }

            if (codecLog is not null) Stage(codecLog);

            long n = Interlocked.Increment(ref _framesSent);
            if (n == 1) Stage("first video frame sent");
            else if (n % 120 == 0) Stage($"{n} frames sent");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (!_cts.IsCancellationRequested) Stage($"data channel closed: {ex.Message}");
        }
    }

    /// <summary>Enqueues a header+payload packet for the sender loop (drops the oldest if the link is saturated).</summary>
    private void SendPacket(byte[] header, byte[] payload)
    {
        byte[] packet = new byte[header.Length + payload.Length];
        header.CopyTo(packet, 0);
        payload.CopyTo(packet, header.Length);
        try
        {
            _sendQueue.Add(packet, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Drains the send queue to the TCP data channel, off the encoder thread.</summary>
    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            var swWrite = new Stopwatch();

            foreach (byte[] packet in _sendQueue.GetConsumingEnumerable(ct))
            {
                if (_dataStream is null) continue;
                int queueDepth = _sendQueue.Count;
                swWrite.Restart();
                await _dataStream.WriteAsync(packet, ct).ConfigureAwait(false);
                double writeMs = swWrite.Elapsed.TotalMilliseconds;

                if (_congestion.EvaluateSendCompletion(queueDepth, writeMs, _sessionClock.ElapsedMilliseconds) is double abrScale)
                {
                    _source?.AdjustBitrate(abrScale);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                Stage($"data channel closed: {ex.Message}");
                TriggerAutoReconnect();
            }
        }
    }

    /// <summary>
    /// Connects the reverse event channel the receiver announced (TCP, HAP-encrypted with
    /// the Events keys) and drains it, answering each complete request with 200 OK. A
    /// desynced event channel gets the whole session torn down by the receiver.
    /// </summary>
    private void StartEventChannel(IPAddress address, int port, CancellationToken ct)
    {
        var eventClient = new TcpClient();
        _eventChannel = eventClient;
        _loops.Add(Task.Run(async () =>
        {
            try
            {
                await eventClient.ConnectAsync(address, port, ct).ConfigureAwait(false);
                var stream = eventClient.GetStream();
                var crypto = new ChannelCrypto(_hap!.EventsWriteKey, _hap.EventsReadKey);
                bool triedSwap = false;
                var raw = new MemoryStream();
                var plain = new MemoryStream();
                byte[] buf = new byte[8192];
                while (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
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
                        await stream.WriteAsync(crypto.Encrypt(System.Text.Encoding.ASCII.GetBytes(headers)), ct).ConfigureAwait(false);
                        if (requestLine.StartsWith("TEARDOWN", StringComparison.OrdinalIgnoreCase)
                            && !ct.IsCancellationRequested && !_cts.IsCancellationRequested)
                        {
                            Stage("receiver requested TEARDOWN on event channel — reconnecting");
                            TriggerAutoReconnect();
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                if (!ct.IsCancellationRequested && !_cts.IsCancellationRequested) Stage($"event channel closed: {ex.Message}");
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
    private async Task TimingLoopAsync(Socket timingSocket, CancellationToken ct)
    {
        byte[] buf = new byte[128];
        byte[] reply = new byte[32];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await timingSocket.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (r.ReceivedBytes < 32) continue;

            buf.AsSpan(0, 32).CopyTo(reply);
            reply[0] = 0x80;
            reply[1] = 0xD3;
            ulong now = _clock.BootTimestamp;
            buf.AsSpan(24, 8).CopyTo(reply.AsSpan(8));                    // reference = sender transmit time
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), now); // receive
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), now); // transmit
            try { await timingSocket.SendToAsync(reply, SocketFlags.None, r.RemoteEndPoint, ct).ConfigureAwait(false); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Data-channel keep-alive: a header-only heartbeat packet (type 0x02).</summary>
    private async Task DataHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!_packetizer.IsCodecSent) continue;
            try
            {
                byte[] header = new byte[MirrorVideoStream.HeaderLength];
                header[4] = 0x02; // heartbeat payload type
                header[6] = 0x1e;
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), _clock.FrameTimestamp);
                lock (_sendQueue)
                {
                    if (_packetizer.IsCodecSent)
                        SendPacket(header, []);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task FeedbackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                var resp = await _rtsp.RequestAsync(new RtspRequest { Method = "POST", Uri = "/feedback", Headers = { ["Session"] = _sessionUuid } }, ct).ConfigureAwait(false);
                resp.EnsureSuccess("POST /feedback");
            }
            catch (Exception ex) when (ex is RtspException or IOException or SocketException or ObjectDisposedException)
            {
                if (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
                {
                    Stage($"feedback ended: {ex.Message}");
                    TriggerAutoReconnect();
                }
                return;
            }
        }
    }

    private void DisposeConnectionResources()
    {
        try { _connCts?.Cancel(); } catch { }
        try { _audioPumpThread?.Join(500); } catch { }
        _audioPumpThread = null;

        lock (_sendQueue)
        {
            while (_sendQueue.TryTake(out _)) { }
            _packetizer.Reset();
            _congestion.Reset();
        }
        try { _dataStream?.Dispose(); } catch { }
        try { _dataChannel?.Dispose(); } catch { }
        try { _eventChannel?.Dispose(); } catch { }
        try { _timingSocket?.Dispose(); } catch { }
        try { _audioDataSocket?.Dispose(); } catch { }
        try { _audioControlSocket?.Dispose(); } catch { }
        try { _videoCipher?.Dispose(); } catch { }
        try { _audioCipher?.Dispose(); } catch { }
        try { _rtsp.Dispose(); } catch { }
        _dataStream = null;
        _dataChannel = null;
        _eventChannel = null;
        _timingSocket = null;
        _audioDataSocket = null;
        _audioControlSocket = null;
        _videoCipher = null;
        _audioCipher = null;
    }

    private void TriggerAutoReconnect()
    {
        if (_cts.IsCancellationRequested || _remoteAddress is null || _credentials is null)
            return;
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Stop the old connection's loops and join the old audio thread before disposing its resources.
                try { _connCts?.Cancel(); } catch { }
                try { _audioPumpThread?.Join(500); } catch { }
                _audioPumpThread = null;

                for (int attempt = 1; attempt <= 8 && !_cts.IsCancellationRequested; attempt++)
                {
                    Stage($"reconnecting mirror session (attempt {attempt})...");
                    try { await Task.Delay(800, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        DisposeConnectionResources();

                        _rtsp = new RtspConnection();
                        _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
                        _sessionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                        _videoStreamConnectionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                        _audioStreamConnectionId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                        _audioChachaKey = RandomNumberGenerator.GetBytes(32);

                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        attemptCts.CancelAfter(TimeSpan.FromSeconds(12));

                        await HandshakeAsync(_remoteAddress, _remotePort, _credentials, attemptCts.Token).ConfigureAwait(false);
                        _source?.ConfigureReceiverProfile(_receiverModel, _receiverDisplayW, _receiverDisplayH);
                        StartConnectionLoops();
                        Interlocked.Exchange(ref _reconnecting, 0);
                        _source?.RequestKeyframe();
                        Stage("mirror session reconnected");
                        return;
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        try { _connCts?.Cancel(); } catch { }
                        Stage($"reconnect attempt {attempt} failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        });
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
        try { _connCts?.Cancel(); } catch { }
        _sendQueue.CompleteAdding();
        _audioPumpThread?.Join(2000);
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { }
        _videoCipher?.Dispose();
        _audioCipher?.Dispose();
        _audioSource?.Dispose();
        _fallbackAudioMover?.Dispose();
        _audioDataSocket?.Dispose();
        _audioControlSocket?.Dispose();
        _timingSocket?.Dispose();
        _dataStream?.Dispose();
        _dataChannel?.Dispose();
        _eventChannel?.Dispose();
        _rtsp.Dispose();
        _sendQueue.Dispose();
        try { _connCts?.Dispose(); } catch { }
        _cts.Dispose();
    }

    /// <summary>
    /// Resolves the receiver's full-screen display size from /info: prefers explicit
    /// <c>displays[0]</c> dimensions, falls back to <c>receiverHDRCapability</c> (modern tvOS
    /// omits <c>displays</c> when HDMI is waking or negotiated via HDR capability strings such as
    /// <c>"1080p60"</c> or <c>"4k60-dolbyvision"</c>), then model generation, defaulting to 1920x1080.
    /// </summary>
    internal static (int Width, int Height) ParseDisplaySize(Dictionary<string, object?> info)
    {
        if (info.GetValueOrDefault("displays") is List<object?> displays && displays.Count > 0
            && displays[0] is Dictionary<string, object?> d)
        {
            int Get(string k) => d.GetValueOrDefault(k) is long v ? (int)v : 0;
            int w = Get("widthPixels"), h = Get("heightPixels");
            if (w <= 0 || h <= 0) { w = Get("width"); h = Get("height"); }
            if (w > 0 && h > 0) return (w, h);
        }

        if (info.GetValueOrDefault("receiverHDRCapability") is string hdrCap && !string.IsNullOrWhiteSpace(hdrCap))
        {
            if (hdrCap.Contains("4k", StringComparison.OrdinalIgnoreCase) ||
                hdrCap.Contains("2160", StringComparison.OrdinalIgnoreCase))
                return (3840, 2160);
            if (hdrCap.Contains("1440", StringComparison.OrdinalIgnoreCase))
                return (2560, 1440);
            if (hdrCap.Contains("1080", StringComparison.OrdinalIgnoreCase))
                return (1920, 1080);
            if (hdrCap.Contains("720", StringComparison.OrdinalIgnoreCase))
                return (1280, 720);
        }

        if (info.GetValueOrDefault("model") is string model && !string.IsNullOrWhiteSpace(model))
        {
            if (model.StartsWith("AppleTV6", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV11", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV14", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV15", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV16", StringComparison.OrdinalIgnoreCase))
            {
                return (3840, 2160);
            }
        }

        return (1920, 1080);
    }

    private static Socket BindUdp()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            s.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch { }
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
/// Boot-relative NTP clock for mirroring. Frame headers use raw boot-relative fixed-point
/// (plus a small forward bias = playout latency); timing replies add the 1900→1970 epoch,
/// matching what Apple senders emit (doubletake mirror.go).
/// </summary>
internal sealed class MirrorClock
{
    private const ulong EpochOffsetSeconds = 2208988800UL;
    // Aligned with ALAC audio latencyMin (2205 / 44100 = 50 ms) + ~5 ms capture/encode headroom
    // so high-motion sports frames never miss Apple TV's presentation deadline on Wi-Fi jitter.
    private static readonly TimeSpan Bias = TimeSpan.FromMilliseconds(65);
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public ulong FrameTimestamp => ToNtp(_sw.Elapsed + Bias, 0);
    public ulong BootTimestamp => ToNtp(_sw.Elapsed, EpochOffsetSeconds);

    private static ulong ToNtp(TimeSpan elapsed, ulong epochOffset)
    {
        double total = elapsed.TotalSeconds;
        ulong seconds = (ulong)total + epochOffset;
        ulong fraction = (ulong)((total - Math.Floor(total)) * 4294967296.0);
        return (seconds << 32) | (fraction & 0xFFFFFFFF);
    }
}
