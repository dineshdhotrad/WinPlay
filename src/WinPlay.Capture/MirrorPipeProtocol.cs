// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace WinPlay.Capture;

/// <summary>Message kinds exchanged between the tray app (parent) and the capture host (child).</summary>
public enum MirrorMessageType : byte
{
    /// <summary>Parent → child: begin capture with the negotiated parameters.</summary>
    Init = 1,
    /// <summary>Child → parent: capture/encode started; carries the negotiated encode size.</summary>
    Ready = 2,
    /// <summary>Child → parent: one encoded H.264 access unit with its capture timestamp.</summary>
    Frame = 3,
    /// <summary>Child → parent: a human-readable diagnostic line.</summary>
    Diagnostic = 4,
    /// <summary>Parent → child: stop and exit cleanly.</summary>
    Stop = 5,
    /// <summary>Child → parent: a fatal error description (child is about to exit).</summary>
    Error = 6,
}

/// <summary>One decoded protocol message. Only the fields relevant to <see cref="Type"/> are set.</summary>
public readonly record struct MirrorMessage(
    MirrorMessageType Type,
    long CaptureTicks = 0,
    bool Keyframe = false,
    byte[]? Payload = null,
    int Fps = 0,
    int BitrateMbps = 0,
    int Width = 0,
    int Height = 0,
    string? Text = null);

/// <summary>
/// Length-framed binary protocol carried over the anonymous pipe between the tray app and the
/// supervised capture-host child process. Each message is
/// <c>[magic:4][type:1][payloadLength:4][payload…]</c>, little-endian (same machine, same
/// endianness on both ends).
///
/// <para>Crucially, every <see cref="MirrorMessageType.Frame"/> carries the frame's
/// <see cref="MirrorMessage.CaptureTicks"/> (a <see cref="System.Diagnostics.Stopwatch"/>
/// timestamp). The parent stamps AirPlay RTP/PTP timing from that capture time — not from when
/// the frame arrived over the pipe — so the constant pipe latency cannot skew A/V sync.</para>
/// </summary>
public static class MirrorPipeProtocol
{
    private const uint Magic = 0x57504D31; // "WPM1"

    public static void WriteInit(Stream s, int fps, int bitrateMbps, int receiverWidth, int receiverHeight)
    {
        Span<byte> payload = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(payload[0..], fps);
        BinaryPrimitives.WriteInt32LittleEndian(payload[4..], bitrateMbps);
        BinaryPrimitives.WriteInt32LittleEndian(payload[8..], receiverWidth);
        BinaryPrimitives.WriteInt32LittleEndian(payload[12..], receiverHeight);
        WriteMessage(s, MirrorMessageType.Init, payload);
    }

    public static void WriteReady(Stream s, int width, int height)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload[0..], width);
        BinaryPrimitives.WriteInt32LittleEndian(payload[4..], height);
        WriteMessage(s, MirrorMessageType.Ready, payload);
    }

    public static void WriteFrame(Stream s, long captureTicks, bool keyframe, ReadOnlySpan<byte> annexB)
    {
        byte[] payload = new byte[9 + annexB.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, captureTicks);
        payload[8] = keyframe ? (byte)1 : (byte)0;
        annexB.CopyTo(payload.AsSpan(9));
        WriteMessage(s, MirrorMessageType.Frame, payload);
    }

    public static void WriteDiagnostic(Stream s, string text)
        => WriteMessage(s, MirrorMessageType.Diagnostic, Encoding.UTF8.GetBytes(text));

    public static void WriteError(Stream s, string text)
        => WriteMessage(s, MirrorMessageType.Error, Encoding.UTF8.GetBytes(text));

    public static void WriteStop(Stream s) => WriteMessage(s, MirrorMessageType.Stop, ReadOnlySpan<byte>.Empty);

    private static void WriteMessage(Stream s, MirrorMessageType type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], Magic);
        header[4] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header[5..], payload.Length);
        s.Write(header);
        if (!payload.IsEmpty) s.Write(payload);
        s.Flush();
    }

    /// <summary>
    /// Reads the next message, blocking until it is fully available. Returns <c>null</c> on a
    /// clean end-of-stream (the peer closed the pipe). Throws <see cref="InvalidDataException"/>
    /// on a corrupt frame and <see cref="EndOfStreamException"/> on a truncated one.
    /// </summary>
    public static MirrorMessage? ReadMessage(Stream s)
    {
        Span<byte> header = stackalloc byte[9];
        if (!TryReadExact(s, header)) return null; // clean EOF before any byte of a message

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != Magic)
            throw new InvalidDataException($"mirror pipe: bad frame magic 0x{magic:X8}");

        var type = (MirrorMessageType)header[4];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header[5..]);
        if (length < 0 || length > 64 * 1024 * 1024)
            throw new InvalidDataException($"mirror pipe: implausible payload length {length}");

        byte[] payload = length == 0 ? [] : new byte[length];
        if (length > 0)
            s.ReadExactly(payload);

        return Decode(type, payload);
    }

    private static MirrorMessage Decode(MirrorMessageType type, byte[] payload) => type switch
    {
        MirrorMessageType.Init => new MirrorMessage(type,
            Fps: BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0)),
            BitrateMbps: BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)),
            Width: BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)),
            Height: BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(12))),

        MirrorMessageType.Ready => new MirrorMessage(type,
            Width: BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0)),
            Height: BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4))),

        MirrorMessageType.Frame => new MirrorMessage(type,
            CaptureTicks: BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0)),
            Keyframe: payload[8] != 0,
            Payload: payload[9..]),

        MirrorMessageType.Diagnostic or MirrorMessageType.Error
            => new MirrorMessage(type, Text: Encoding.UTF8.GetString(payload)),

        _ => new MirrorMessage(type),
    };

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes; false only if EOF hits
    /// before the first byte (a clean close between messages).</summary>
    private static bool TryReadExact(Stream s, Span<byte> buffer)
    {
        int read = s.Read(buffer);
        if (read == 0) return false;                 // clean EOF at a message boundary
        if (read == buffer.Length) return true;
        s.ReadExactly(buffer[read..]);               // partial: complete it or throw EndOfStream
        return true;
    }
}
