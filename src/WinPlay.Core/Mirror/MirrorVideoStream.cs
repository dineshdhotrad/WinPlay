// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinPlay.Core.Mirror;

/// <summary>
/// Screen-mirroring video wire format and per-frame encryption (doubletake mirror.go
/// parity). Frames go over the TCP data channel as a fixed 128-byte little-endian
/// header followed by the payload. Apple TV sessions (pair-verify + FairPlay) encrypt
/// video with ChaCha20-Poly1305 keyed by HKDF-SHA512; the 128-byte header is the AEAD
/// associated data. The SPS/PPS "codec" packet is sent once, unencrypted, in avcC form.
/// </summary>
public static class MirrorVideoStream
{
    public const int HeaderLength = 128;

    // Payload types (header[4]).
    private const byte PayloadVideo = 0x00;
    private const byte PayloadCodec = 0x01;

    /// <summary>
    /// HKDF-SHA512 data-stream key for encrypted screen video (Apple's
    /// _GetDataStreamSecurityKeys). IKM is the pair-verify ECDH shared secret when
    /// available, else the raw FairPlay AES key.
    /// </summary>
    public static byte[] DeriveDataStreamKey(byte[] ikm, long streamConnectionId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, ikm, outputLength: 32,
            salt: Encoding.ASCII.GetBytes($"DataStream-Salt{(ulong)streamConnectionId}"),
            info: "DataStream-Output-Encryption-Key"u8.ToArray());

    /// <summary>
    /// Legacy AES-128-CTR key/IV (UxPlay path): SHA-512("AirPlayStreamKey&lt;id&gt;" ‖ shk)[..16]
    /// and SHA-512("AirPlayStreamIV&lt;id&gt;" ‖ shk)[..16].
    /// </summary>
    public static (byte[] Key, byte[] Iv) DeriveAesCtrKeys(byte[] shk, long streamConnectionId)
    {
        byte[] key = Sha512Prefixed($"AirPlayStreamKey{(ulong)streamConnectionId}", shk)[..16];
        byte[] iv = Sha512Prefixed($"AirPlayStreamIV{(ulong)streamConnectionId}", shk)[..16];
        return (key, iv);
    }

    private static byte[] Sha512Prefixed(string ascii, byte[] tail)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        sha.AppendData(Encoding.ASCII.GetBytes(ascii));
        sha.AppendData(tail);
        return sha.GetHashAndReset();
    }

    /// <summary>Builds the 128-byte header for an encrypted H.264 access unit (AVCC).</summary>
    public static byte[] BuildVideoHeader(int payloadLength, bool keyframe, ulong ntpTimestamp)
    {
        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        header[4] = PayloadVideo;
        header[5] = keyframe ? (byte)0x10 : (byte)0x00;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ntpTimestamp);
        return header;
    }

    /// <summary>Builds the 128-byte header for the unencrypted SPS/PPS codec packet.</summary>
    public static byte[] BuildCodecHeader(int payloadLength, ulong ntpTimestamp,
        float sourceWidth, float sourceHeight, float displayWidth, float displayHeight)
    {
        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        header[4] = PayloadCodec;
        header[5] = 0x00;
        header[6] = 0x16; // H.264 SPS+PPS option
        header[7] = 0x01;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ntpTimestamp);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(16), sourceWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(20), sourceHeight);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(40), sourceWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(44), sourceHeight);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(56), displayWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(60), displayHeight);
        return header;
    }
}

/// <summary>
/// Per-frame ChaCha20-Poly1305 sealer for mirror video. Uses a 64-bit little-endian
/// frame counter placed at nonce bytes 4..12 (nonce[0..4]=0), matching the receiver's
/// 64×64 ChaCha state layout, with the 128-byte packet header as AEAD associated data.
/// </summary>
public sealed class MirrorVideoCipher(byte[] key) : IDisposable
{
    private readonly ChaCha20Poly1305 _cipher = new(key);
    private ulong _nonceCounter;

    /// <summary>Encrypts <paramref name="plaintext"/>, returning ciphertext followed by the 16-byte tag.</summary>
    public byte[] Seal(ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _nonceCounter++);

        byte[] output = new byte[plaintext.Length + 16];
        _cipher.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length, 16), header);
        return output;
    }

    public void Dispose() => _cipher.Dispose();
}

/// <summary>
/// Stateful AirPlay mirroring video framer: guarantees that the unencrypted 0x01 SPS/PPS
/// codec packet and its accompanying encrypted 0x00 IDR frame share the exact same NTP
/// timestamp, always advertise a valid full-screen TV viewport, and never advance the
/// ChaCha20-Poly1305 AEAD nonce when non-IDR frames are dropped or waiting on SPS/PPS.
/// </summary>
internal sealed class MirrorVideoPacketizer
{
    private bool _codecSent;
    private byte[]? _sps;
    private byte[]? _pps;

    public bool IsCodecSent => _codecSent;

    public void Reset() => _codecSent = false;

    public bool TryPacketize(
        ReadOnlySpan<byte> annexB,
        bool isKeyframe,
        int sourceWidth,
        int sourceHeight,
        int receiverDisplayWidth,
        int receiverDisplayHeight,
        ulong ntpTimestamp,
        MirrorVideoCipher cipher,
        out byte[]? codecPacket,
        out byte[]? videoPacket,
        out bool needsKeyframe)
    {
        codecPacket = null;
        videoPacket = null;
        needsKeyframe = false;

        var nals = H264.SplitAnnexB(annexB);
        if (nals.Count == 0) return false;

        bool spsPpsChanged = false;
        foreach (byte[] nal in nals)
        {
            byte type = H264.NalType(nal);
            if (type == H264.NalSps)
            {
                if (_sps is null || !_sps.AsSpan().SequenceEqual(nal)) spsPpsChanged = true;
                _sps = nal;
            }
            else if (type == H264.NalPps)
            {
                if (_pps is null || !_pps.AsSpan().SequenceEqual(nal)) spsPpsChanged = true;
                _pps = nal;
            }
        }

        var vcl = nals.Where(n => H264.NalType(n) is 1 or 5).ToList();
        if (vcl.Count == 0) return false;

        if (!_codecSent && (!isKeyframe || _sps is null || _pps is null))
        {
            needsKeyframe = true;
            return false;
        }

        if ((!_codecSent || (isKeyframe && spsPpsChanged)) && _sps is not null && _pps is not null)
        {
            int displayW = receiverDisplayWidth > 0 ? receiverDisplayWidth : 1920;
            int displayH = receiverDisplayHeight > 0 ? receiverDisplayHeight : 1080;
            byte[] avcc = H264.BuildAvcCConfig(_sps, _pps);
            byte[] codecHeader = MirrorVideoStream.BuildCodecHeader(
                avcc.Length, ntpTimestamp, sourceWidth, sourceHeight, displayW, displayH);

            codecPacket = new byte[codecHeader.Length + avcc.Length];
            codecHeader.CopyTo(codecPacket, 0);
            avcc.CopyTo(codecPacket, codecHeader.Length);
            _codecSent = true;
        }

        byte[] payload = H264.ToAvcc(vcl);
        byte[] videoHeader = MirrorVideoStream.BuildVideoHeader(payload.Length + 16, isKeyframe, ntpTimestamp);
        byte[] encrypted = cipher.Seal(videoHeader, payload);

        videoPacket = new byte[videoHeader.Length + encrypted.Length];
        videoHeader.CopyTo(videoPacket, 0);
        encrypted.CopyTo(videoPacket, videoHeader.Length);
        return true;
    }
}

/// <summary>
/// Thread-safe congestion controller for AirPlay TCP video delivery:
/// <list type="bullet">
/// <item>Absorbs normal Wi-Fi bursts (<c>queueDepth &lt; 24</c>, ~400 ms) via closed-loop ABR
/// without dropping H.264 P-frames or breaking inter-frame reference chains.</item>
/// <item>On severe link stalls (<c>queueDepth &gt;= 24</c>), cleanly drops non-IDR frames
/// before AEAD encryption, requests at most one recovery IDR per 1500 ms (preventing
/// back-to-back IDR keyframe congestion collapse), and resumes normal P-frame forwarding
/// only once a fresh recovery IDR is accepted.</item>
/// </list>
/// </summary>
internal sealed class MirrorCongestionController
{
    public const int SevereDropThreshold = 24;
    public const long KeyframeCooldownMs = 1500;
    private const long AbrStepDownCooldownMs = 350;
    private const long AbrStepUpCooldownMs = 4000;

    public readonly record struct FrameDecision(bool DropFrame, bool RequestKeyframe, double? BitrateScale);

    private readonly object _gate = new();
    private bool _awaitingRecoveryKeyframe;
    private long _lastKeyframeRequestMs = -KeyframeCooldownMs;
    private long _lastStepDownMs = -AbrStepDownCooldownMs;
    private long _lastStepUpMs;

    public void Reset()
    {
        lock (_gate)
        {
            _awaitingRecoveryKeyframe = false;
            _lastKeyframeRequestMs = -KeyframeCooldownMs;
            _lastStepDownMs = -AbrStepDownCooldownMs;
            _lastStepUpMs = 0;
        }
    }

    public FrameDecision EvaluateIncomingFrame(bool isKeyframe, int queueDepth, long nowMs)
    {
        lock (_gate)
        {
            if (isKeyframe)
            {
                // If the TCP queue is nearly full (>= 48 frames), even an IDR cannot fit safely;
                // drop it and wait for queue drain before requesting another recovery IDR.
                if (queueDepth >= 48)
                {
                    _awaitingRecoveryKeyframe = true;
                    return new FrameDecision(DropFrame: true, RequestKeyframe: false, BitrateScale: 0.60);
                }

                _awaitingRecoveryKeyframe = false;
                return new FrameDecision(DropFrame: false, RequestKeyframe: false, BitrateScale: null);
            }

            if (queueDepth >= SevereDropThreshold || _awaitingRecoveryKeyframe)
            {
                _awaitingRecoveryKeyframe = true;
                bool canRequestIdr = (nowMs - _lastKeyframeRequestMs) >= KeyframeCooldownMs;
                double? scale = null;
                if (canRequestIdr)
                {
                    _lastKeyframeRequestMs = nowMs;
                    _lastStepDownMs = nowMs;
                    _lastStepUpMs = nowMs;
                    scale = 0.65;
                }
                return new FrameDecision(DropFrame: true, RequestKeyframe: canRequestIdr, BitrateScale: scale);
            }

            return new FrameDecision(DropFrame: false, RequestKeyframe: false, BitrateScale: null);
        }
    }

    public double? EvaluateSendCompletion(int queueDepth, double writeMs, long nowMs)
    {
        lock (_gate)
        {
            if ((queueDepth >= 8 || writeMs > 45.0) && (nowMs - _lastStepDownMs) >= AbrStepDownCooldownMs)
            {
                _lastStepDownMs = nowMs;
                _lastStepUpMs = nowMs;
                return 0.68;
            }
            if ((queueDepth >= 3 || writeMs > 22.0) && (nowMs - _lastStepDownMs) >= AbrStepDownCooldownMs)
            {
                _lastStepDownMs = nowMs;
                _lastStepUpMs = nowMs;
                return 0.82;
            }
            if (queueDepth == 0 && writeMs < 8.0 &&
                (nowMs - _lastStepDownMs) >= AbrStepUpCooldownMs &&
                (nowMs - _lastStepUpMs) >= AbrStepUpCooldownMs)
            {
                _lastStepUpMs = nowMs;
                return 1.10;
            }
            return null;
        }
    }
}
