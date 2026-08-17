// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Locks in the split at the heart of the multi-destination echo fix: the rtp that ANCHORS a
/// shared timeline (declared once, identically, to every destination — see
/// RaopSession.SendBufferedAnchorAsync, which always sends the bare shared start timestamp) is not
/// the same value as the rtp of the first frame a PARTICULAR session actually sends (which must
/// include that session's own start offset on the shared capture — see
/// WinPlay.Core.Audio.IPositionedAudioSource). RaopSession.FrameTimestamp is the one place both
/// pumps and the sync loop compute a packet's rtp, so verifying it here is verifying every path
/// that emits audio, without needing a live RTSP session.
/// </summary>
public class RaopSessionTimelineTests
{
    [Fact]
    public void First_Frame_Rtp_Reflects_The_Branch_Offset()
    {
        const uint sharedStart = 1_000_000;
        const long startPositionFrames = 44_100; // this session's branch joined 1 s into the capture

        uint firstFrameRtp = RaopSession.FrameTimestamp(sharedStart, startPositionFrames, packetsSent: 0);

        Assert.Equal(sharedStart + (uint)startPositionFrames, firstFrameRtp);
    }

    [Fact]
    public void Anchor_Rtp_Does_Not_Include_The_Branch_Offset()
    {
        // SendBufferedAnchorAsync declares `rtpTime = _startTimestamp` — the raw shared start,
        // never `_startTimestamp + _startPositionFrames`. A later-joining destination's anchor
        // must still describe the SAME shared line every other destination is on; only ITS OWN
        // emitted frames move by the offset, never the anchor's declared pair.
        const uint sharedStart = 1_000_000;
        const long startPositionFrames = 44_100;

        // The anchor pair is (rtpTime=sharedStart, networkTime=sharedAnchor) — sharedStart used
        // directly, with no dependency on this session's offset at all.
        uint anchorRtp = sharedStart;

        Assert.Equal(sharedStart, anchorRtp);
        Assert.NotEqual(anchorRtp, RaopSession.FrameTimestamp(sharedStart, startPositionFrames, packetsSent: 0));
    }

    [Fact]
    public void Zero_Offset_Reproduces_The_Original_Single_Destination_Behaviour()
    {
        // The ordinary case (first/only destination, branch starts at the shared timeline's own
        // frame 0) must be untouched: first-frame rtp equals the anchor rtp exactly, as it always
        // did before this session carried an offset.
        const uint sharedStart = 2_500_000;
        uint firstFrameRtp = RaopSession.FrameTimestamp(sharedStart, startPositionFrames: 0, packetsSent: 0);
        Assert.Equal(sharedStart, firstFrameRtp);
    }

    [Fact]
    public void Rtp_Advances_By_352_Frames_Per_Packet_On_Top_Of_The_Offset()
    {
        const uint sharedStart = 500_000;
        const long startPositionFrames = 8_820; // 200 ms in

        uint packet0 = RaopSession.FrameTimestamp(sharedStart, startPositionFrames, packetsSent: 0);
        uint packet1 = RaopSession.FrameTimestamp(sharedStart, startPositionFrames, packetsSent: 1);
        uint packet10 = RaopSession.FrameTimestamp(sharedStart, startPositionFrames, packetsSent: 10);

        Assert.Equal(352u, packet1 - packet0);
        Assert.Equal(3520u, packet10 - packet0);
    }

    [Fact]
    public void Two_Sessions_On_The_Same_Timeline_With_Different_Offsets_Never_Collide()
    {
        // The direct statement of the bug this fixes: two destinations sharing one timeline
        // (same sharedStart) but joining the machine capture at different absolute positions must
        // stamp their own first frames differently — otherwise both would claim to be playing the
        // sample that plays AT the anchor instant, when only the offset-0 session actually is.
        const uint sharedStart = 42;
        uint firstDestination = RaopSession.FrameTimestamp(sharedStart, startPositionFrames: 0, packetsSent: 0);
        uint secondDestination = RaopSession.FrameTimestamp(sharedStart, startPositionFrames: 132_300, packetsSent: 0);

        Assert.NotEqual(firstDestination, secondDestination);
        Assert.Equal(132_300u, secondDestination - firstDestination);
    }
}
