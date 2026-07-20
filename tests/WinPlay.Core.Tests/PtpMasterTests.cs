// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using WinPlay.Core.Ptp;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Wire-format checks against owntone's libairptp structs (ptp_definitions.h) and the
/// iOS-captured field values in ptp_msg_handle.c.
/// </summary>
public class PtpMasterTests
{
    private const ulong ClockId = 0xFFFF123456789ABCUL;

    [Fact]
    public void Announce_MatchesAppleProfile()
    {
        byte[] m = PtpMaster.BuildAnnounce(ClockId, 0x1234);

        Assert.Equal(76, m.Length); // header 34 + body 30 + PATH_TRACE TLV 12
        Assert.Equal(0x1B, m[0]);   // transportSpecific=1 | Announce (0x0B)
        Assert.Equal(0x02, m[1]);   // PTPv2
        Assert.Equal(76, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(2)));
        Assert.Equal(0, m[4]);      // domain
        Assert.Equal(0x0408, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(6))); // UNICAST|TIMESCALE
        Assert.Equal(ClockId, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(20)));
        Assert.Equal(0x80, m[28]);  // port number 0x8005, same as iOS
        Assert.Equal(0x05, m[29]);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(30)));
        Assert.Equal(0, m[33]);     // logMessageInterval 0 → 1 s announce

        Assert.Equal(128, m[47]);   // priority1
        Assert.Equal(0x0621436AU, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(48))); // class 6/GPS
        Assert.Equal(128, m[52]);   // priority2
        Assert.Equal(ClockId, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(53))); // grandmaster
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(61)));       // stepsRemoved
        Assert.Equal(0x20, m[63]);  // timeSource GPS
        Assert.Equal(0x0008, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(64)));  // PATH_TRACE
        Assert.Equal(8, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(66)));
        Assert.Equal(ClockId, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(68)));
    }

    [Fact]
    public void Sync_IsTwoStepWithZeroTimestamp()
    {
        byte[] m = PtpMaster.BuildSync(ClockId, 7);

        Assert.Equal(44, m.Length);
        Assert.Equal(0x10, m[0]); // transportSpecific=1 | Sync (0x00)
        Assert.Equal(0x0608, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(6))); // +TWO_STEP
        Assert.Equal(unchecked((byte)-3), m[33]); // logInterval -3 → 125 ms
        Assert.All(m[34..44], b => Assert.Equal(0, b)); // originTimestamp zero
    }

    [Fact]
    public void FollowUp_CarriesTimestampAndBothTlvs()
    {
        byte[] m = PtpMaster.BuildFollowUp(ClockId, 7, (0x1_00000002UL, 999_999_999));

        Assert.Equal(96, m.Length); // header 34 + ts 10 + IEEE TLV 32 + Apple TLV 20
        Assert.Equal(0x18, m[0]);   // Follow_Up (0x08)
        Assert.Equal(0x0408, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(6)));
        // preciseOriginTimestamp: 48-bit seconds + 32-bit nanos
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(34)));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(36)));
        Assert.Equal(999_999_999U, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(40)));
        // IEEE 802.1 Follow_Up information TLV
        Assert.Equal(0x0003, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(44)));
        Assert.Equal(28, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(46)));
        Assert.Equal(new byte[] { 0x00, 0x80, 0xC2, 0x00, 0x00, 0x01 }, m[48..54]);
        // Apple clock-ID TLV
        Assert.Equal(0x0003, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(76)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(78)));
        Assert.Equal(new byte[] { 0x00, 0x0D, 0x93, 0x00, 0x00, 0x04 }, m[80..86]);
        Assert.Equal(ClockId, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(86)));
    }

    [Fact]
    public void Signaling_HasControlFieldAndAppleTlvs()
    {
        byte[] m = PtpMaster.BuildSignaling(ClockId, 3);

        Assert.Equal(106, m.Length);
        Assert.Equal(0x1C, m[0]);  // Signaling (0x0C)
        Assert.Equal(0x05, m[32]); // controlField "Other Message"
        Assert.Equal(unchecked((byte)-128), m[33]);
        Assert.All(m[34..44], b => Assert.Equal(0, b)); // targetPortIdentity zero
        Assert.Equal(new byte[] { 0x00, 0x0D, 0x93, 0x00, 0x00, 0x01, 0x00, 0x00, 0x03, 0x01 }, m[48..58]);
        Assert.Equal(new byte[] { 0x00, 0x0D, 0x93, 0x00, 0x00, 0x05, 0x00, 0x00, 0x03, 0x01 }, m[74..84]);
    }

    [Fact]
    public void DelayResp_EchoesSequenceAndRequesterIdentity()
    {
        byte[] req = new byte[44];
        req[0] = 0x11; // Delay_Req
        req[1] = 0x02;
        for (int i = 0; i < 10; i++) req[20 + i] = (byte)(0xA0 + i); // requester identity
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(30), 0xBEEF);

        byte[] m = PtpMaster.BuildDelayResp(ClockId, req, (5, 123));

        Assert.Equal(54, m.Length);
        Assert.Equal(0x19, m[0]); // Delay_Resp (0x09)
        Assert.Equal(0x0608, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(6)));
        Assert.Equal(0xBEEF, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(30))); // echoed seq
        Assert.Equal(ClockId, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(20))); // OUR identity
        Assert.Equal(5U, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(36)));      // receive ts
        Assert.Equal(123U, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(40)));
        Assert.Equal(req[20..30], m[44..54]); // requestingPortIdentity copied verbatim
    }

    [Fact]
    public void MonotonicClock_IsMonotonicAndConsistent()
    {
        ulong a = MonotonicClock.NowNanoseconds;
        ulong b = MonotonicClock.NowNanoseconds;
        Assert.True(b >= a);

        var (sec, nanos) = MonotonicClock.Now;
        Assert.True(nanos < 1_000_000_000U);
        ulong recombined = sec * 1_000_000_000UL + nanos;
        ulong now = MonotonicClock.NowNanoseconds;
        Assert.True(now >= recombined && now - recombined < 1_000_000_000UL);
    }
}
