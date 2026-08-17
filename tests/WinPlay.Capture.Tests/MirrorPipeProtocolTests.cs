// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Capture;
using Xunit;

namespace WinPlay.Capture.Tests;

/// <summary>
/// Round-trip coverage for the parent↔child capture-host IPC protocol. Framing and every
/// message type must survive a write→read cycle byte-for-byte, several messages must stream
/// back-to-back, and a clean end-of-stream must be distinguishable from a corrupt frame.
/// </summary>
public class MirrorPipeProtocolTests
{
    [Fact]
    public void Init_RoundTrips()
    {
        var ms = new MemoryStream();
        MirrorPipeProtocol.WriteInit(ms, fps: 60, bitrateMbps: 25, receiverWidth: 2560, receiverHeight: 1440);
        ms.Position = 0;

        MirrorMessage m = MirrorPipeProtocol.ReadMessage(ms)!.Value;
        Assert.Equal(MirrorMessageType.Init, m.Type);
        Assert.Equal(60, m.Fps);
        Assert.Equal(25, m.BitrateMbps);
        Assert.Equal(2560, m.Width);
        Assert.Equal(1440, m.Height);
    }

    [Fact]
    public void Ready_RoundTrips()
    {
        var ms = new MemoryStream();
        MirrorPipeProtocol.WriteReady(ms, 1920, 1080);
        ms.Position = 0;

        MirrorMessage m = MirrorPipeProtocol.ReadMessage(ms)!.Value;
        Assert.Equal(MirrorMessageType.Ready, m.Type);
        Assert.Equal(1920, m.Width);
        Assert.Equal(1080, m.Height);
    }

    [Fact]
    public void Frame_RoundTrips_Preserving_Timestamp_Keyframe_And_Payload()
    {
        byte[] annexB = [0x00, 0x00, 0x00, 0x01, 0x65, 0xDE, 0xAD, 0xBE, 0xEF];
        var ms = new MemoryStream();
        MirrorPipeProtocol.WriteFrame(ms, captureTicks: 123_456_789_012, keyframe: true, annexB);
        ms.Position = 0;

        MirrorMessage m = MirrorPipeProtocol.ReadMessage(ms)!.Value;
        Assert.Equal(MirrorMessageType.Frame, m.Type);
        Assert.Equal(123_456_789_012, m.CaptureTicks);
        Assert.True(m.Keyframe);
        Assert.Equal(annexB, m.Payload);
    }

    [Fact]
    public void Empty_Frame_Payload_Is_Handled()
    {
        var ms = new MemoryStream();
        MirrorPipeProtocol.WriteFrame(ms, 1, keyframe: false, ReadOnlySpan<byte>.Empty);
        ms.Position = 0;

        MirrorMessage m = MirrorPipeProtocol.ReadMessage(ms)!.Value;
        Assert.Equal(MirrorMessageType.Frame, m.Type);
        Assert.False(m.Keyframe);
        Assert.Empty(m.Payload!);
    }

    [Fact]
    public void Diagnostic_And_Error_Text_RoundTrip()
    {
        var ms = new MemoryStream();
        MirrorPipeProtocol.WriteDiagnostic(ms, "capture 2560x1440 → 1920x1080 @ 60fps (AMD VCN)");
        MirrorPipeProtocol.WriteError(ms, "encoder MFT crashed: 0xC0000409");
        ms.Position = 0;

        MirrorMessage d = MirrorPipeProtocol.ReadMessage(ms)!.Value;
        Assert.Equal(MirrorMessageType.Diagnostic, d.Type);
        Assert.Equal("capture 2560x1440 → 1920x1080 @ 60fps (AMD VCN)", d.Text);

        MirrorMessage e = MirrorPipeProtocol.ReadMessage(ms)!.Value;
        Assert.Equal(MirrorMessageType.Error, e.Type);
        Assert.Contains("0xC0000409", e.Text);
    }

    [Fact]
    public void Multiple_Messages_Stream_Back_To_Back()
    {
        var ms = new MemoryStream();
        MirrorPipeProtocol.WriteInit(ms, 60, 20, 3840, 2160);
        MirrorPipeProtocol.WriteReady(ms, 3840, 2160);
        MirrorPipeProtocol.WriteFrame(ms, 10, true, [1, 2, 3]);
        MirrorPipeProtocol.WriteFrame(ms, 20, false, [4, 5]);
        MirrorPipeProtocol.WriteStop(ms);
        ms.Position = 0;

        Assert.Equal(MirrorMessageType.Init, MirrorPipeProtocol.ReadMessage(ms)!.Value.Type);
        Assert.Equal(MirrorMessageType.Ready, MirrorPipeProtocol.ReadMessage(ms)!.Value.Type);
        Assert.Equal(10, MirrorPipeProtocol.ReadMessage(ms)!.Value.CaptureTicks);
        MirrorMessage f2 = MirrorPipeProtocol.ReadMessage(ms)!.Value;
        Assert.Equal(20, f2.CaptureTicks);
        Assert.Equal([4, 5], f2.Payload);
        Assert.Equal(MirrorMessageType.Stop, MirrorPipeProtocol.ReadMessage(ms)!.Value.Type);
        Assert.Null(MirrorPipeProtocol.ReadMessage(ms)); // clean EOF
    }

    [Fact]
    public void Clean_Eof_Returns_Null_Not_An_Exception()
        => Assert.Null(MirrorPipeProtocol.ReadMessage(new MemoryStream()));

    [Fact]
    public void Corrupt_Magic_Throws()
    {
        var ms = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x00, 0x00, 0x00, 0x00]);
        Assert.Throws<InvalidDataException>(() => MirrorPipeProtocol.ReadMessage(ms));
    }

    [Fact]
    public void Truncated_Frame_Throws_EndOfStream()
    {
        // A well-formed header claiming 100 payload bytes, but only 3 provided.
        var full = new MemoryStream();
        MirrorPipeProtocol.WriteFrame(full, 1, true, new byte[100]);
        byte[] bytes = full.ToArray();
        var truncated = new MemoryStream(bytes[..12]); // header + a few payload bytes only
        Assert.Throws<EndOfStreamException>(() => MirrorPipeProtocol.ReadMessage(truncated));
    }
}
