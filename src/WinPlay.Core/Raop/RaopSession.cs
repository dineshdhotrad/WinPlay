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

    private readonly RtspConnection _rtsp = new();
    private readonly NtpClock _clock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private readonly string _sessionUri;
    private readonly ulong _streamConnectionId;
    private readonly uint _ssrc;
    private readonly bool _usePtp;
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

    private RaopSession(bool usePtp)
    {
        // One session id serves as RTSP URI number and streamConnectionID — receivers
        // correlate the RTP flow with the announced stream through it. The RTP SSRC is
        // the same id in NTP mode but ZERO in PTP mode (owntone parity — iOS senders
        // leave it zero for PTP-timed realtime streams).
        uint sessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _usePtp = usePtp;
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
        CancellationToken ct = default, HapPairingCredentials? credentials = null)
    {
        var s = new RaopSession(usePtp) { _credentials = credentials };
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
            _hap = await HapVerifiedPairing.PairVerifyAsync(
                ReceiverPairing.MakePost(_rtsp), _credentials, ct).ConfigureAwait(false);
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
            catch (RtspException ex) when (ex.StatusCode is 470 or 401)
            {
                throw new PairingRequiredException($"{address}");
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

        Stage("SETUP (stream: realtime ALAC 44.1/16/2)");
        var streamSetup = await PlistRequestAsync("SETUP", new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 0x60L,
                    ["ct"] = 2L,                    // ALAC
                    ["spf"] = 352L,
                    ["sr"] = (long)SampleRate,
                    ["audioFormat"] = 0x40000L,     // ALAC/44100/16/2
                    ["audioMode"] = "default",
                    ["controlPort"] = (long)LocalPort(_controlSocket),
                    ["latencyMax"] = (long)LatencyFrames,
                    ["latencyMin"] = 11025L,
                    ["isMedia"] = true,
                    ["shk"] = _hap.AudioKey,
                    ["supportsDynamicStreamID"] = false,
                    ["streamConnectionID"] = unchecked((long)_streamConnectionId),
                },
            },
        }, ct).ConfigureAwait(false);

        var stream = (streamSetup.GetValueOrDefault("streams") as List<object?>)?.FirstOrDefault()
            as Dictionary<string, object?> ?? throw new RtspException("stream SETUP: no streams in response");
        long dataPort = stream.GetValueOrDefault("dataPort") as long? ?? throw new RtspException("stream SETUP: no dataPort");
        long theirControl = stream.GetValueOrDefault("controlPort") as long? ?? dataPort;
        _receiverData = new IPEndPoint(_rtsp.RemoteAddress, (int)dataPort);
        _receiverControl = new IPEndPoint(_rtsp.RemoteAddress, (int)theirControl);
        Stage($"stream ports: data={dataPort} control={theirControl} — session live");
        // No SETRATEANCHORTIME for realtime streams: playback is driven by the sync
        // packets alone (owntone parity). Anchoring rtpTime=start at "now" would
        // contradict the sync timeline by the full latency window.
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

    public async Task StartStreamingAsync(IAudioSource source, double volumeDb = -18)
    {
        await SetVolumeAsync(volumeDb, _cts.Token).ConfigureAwait(false);

        _audioCrypto = new AudioPacketCrypto(_hap!.AudioKey);
        _loops.Add(Task.Run(() => SyncLoopAsync(_cts.Token)));
        _loops.Add(Task.Run(() => FeedbackLoopAsync(_cts.Token)));

        _pumpThread = new Thread(() => AudioPump(source)) { IsBackground = true, Priority = ThreadPriority.Highest };
        _pumpThread.Start();
        Stage("streaming started");
    }

    public async Task SetVolumeAsync(double db, CancellationToken ct)
    {
        var resp = await _rtsp.RequestAsync(new RtspRequest
        {
            Method = "SET_PARAMETER",
            Uri = RtspUri,
            Body = System.Text.Encoding.ASCII.GetBytes($"volume: {db:F6}\r\n"),
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
                    if (Interlocked.Increment(ref _audioSendFailures) == 1)
                        Stage($"AUDIO SEND FAILING: {ex.SocketErrorCode} → {_receiverData}");
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
