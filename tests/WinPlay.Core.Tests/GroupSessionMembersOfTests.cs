// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Discovery;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// GroupSession.MembersOf tests for two pieces of the "Bedroom" HomePod mini fix:
/// 1. a device's own mDNS group id (gid) must ride along onto its Member record so
/// RaopSession.ConnectAsync can echo it into the SETUP(session) `groupUUID` (see
/// RaopSessionGroupIdentityTests for the payload logic itself). There is deliberately no
/// equivalent for gcgl: `groupContainsGroupLeader` is a hardcoded `false` at the SETUP(session)
/// call site for every session WinPlay ever opens, so Member carries no gcgl field at all —
/// see the comment at that payload's construction site in RaopSession for why;
/// 2. Member.ExtraPeers — documented but previously dead (nothing ever assigned it) — must be
/// populated for the one place MembersOf resolves FEWER connectable session members than a
/// leader's own group membership implies: an Apple-TV-led group whose paired HomePod(s)
/// don't qualify as standalone-audio speakers, so MembersOf falls back to the ATV alone.
/// </summary>
public class GroupSessionMembersOfTests
{
    private static AirPlayDevice HomePod(string id, string name, IPAddress address,
        string? gid = null, bool igl = false, bool gcgl = false, bool supportsAudio = true) => new()
    {
        DeviceId = id, Name = name, Model = "AudioAccessory5,1",
        Addresses = [address],
        AirPlayPort = 7000,
        RawFeatures = (ulong)((supportsAudio ? AirPlayFeatures.SupportsAirPlayAudio : 0)
            | AirPlayFeatures.SupportsPtp),
        GroupId = gid, IsGroupLeader = igl, GroupContainsLeader = gcgl,
    };

    private static AirPlayDevice AppleTv(string id, string name, IPAddress address,
        string? gid = null, bool igl = false, bool gcgl = false) => new()
    {
        DeviceId = id, Name = name, Model = "AppleTV11,1",
        Addresses = [address],
        AirPlayPort = 7000,
        RawFeatures = (ulong)(AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.SupportsAirPlayScreen
            | AirPlayFeatures.SupportsPtp),
        GroupId = gid, IsGroupLeader = igl, GroupContainsLeader = gcgl,
    };

    private static PickerEntry SingleEntry(AirPlayDevice device) => new()
    {
        Key = device.DeviceId, DisplayName = device.Name, Kind = PickerEntryKind.Single,
        Leader = device, Members = [device], Subtitle = "HomePod mini",
    };

    [Fact]
    public void Threads_The_Devices_Own_GroupId_Onto_The_Member()
    {
        // A HomePod mini advertising igl=1/gcgl=1 and a compound gid. MembersOf must carry the
        // gid through unchanged so RaopSession can echo it into groupUUID. Its gcgl self-report
        // is deliberately NOT threaded onto Member at all — see the class doc comment.
        var device = HomePod("AABBCC000001", "Bedroom", IPAddress.Parse("10.0.0.251"),
            gid: "77777777-7777-7777-7777-777777777777+88888888-8888-8888-8888-888888888888",
            igl: true, gcgl: true);

        var member = Assert.Single(GroupSession.MembersOf(SingleEntry(device)));

        Assert.Equal("77777777-7777-7777-7777-777777777777+88888888-8888-8888-8888-888888888888", member.GroupId);
    }

    [Fact]
    public void Device_With_No_Advertised_Group_Gets_A_Null_GroupId_On_Its_Member()
    {
        // Other HomePods and the Apple TV never advertise a group id while idle — MembersOf
        // must not invent one.
        var device = HomePod("112233445566", "Kitchen", IPAddress.Parse("10.0.0.54"));

        var member = Assert.Single(GroupSession.MembersOf(SingleEntry(device)));

        Assert.Null(member.GroupId);
    }

    [Fact]
    public void AppleTv_Fallback_With_No_Speakers_Declares_Its_Paired_HomePod_As_An_ExtraPeer()
    {
        // The one place in MembersOf today where the connectable session-member set is a
        // strict subset of entry.Members: an Apple-TV-led group where the OTHER member does not
        // qualify as a standalone-audio speaker (here: SupportsAudio is false), so MembersOf
        // falls back to streaming to the ATV alone. The paired HomePod is a real, addressable
        // member of the SAME group — just not one we open a session to — so its address must
        // still ride along as a PTP peer, or it never gets clocked and stays silent (exactly
        // the failure mode the ExtraPeers plumbing exists to prevent).
        var atv = AppleTv("AABBCC000004", "Den TV", IPAddress.Parse("10.0.0.3"),
            gid: "LR-GROUP", igl: true, gcgl: true);
        var pairedHomePod = HomePod("AABBCC000005", "Den", IPAddress.Parse("10.0.0.231"),
            gid: "LR-GROUP", supportsAudio: false);
        var entry = new PickerEntry
        {
            Key = "combined", DisplayName = "Den TV", Kind = PickerEntryKind.Group,
            Leader = atv, Members = [atv, pairedHomePod], Subtitle = "2 devices",
        };

        var member = Assert.Single(GroupSession.MembersOf(entry));

        Assert.Equal(atv.DeviceId, member.DeviceId);
        Assert.NotNull(member.ExtraPeers);
        Assert.Equal([IPAddress.Parse("10.0.0.231")], member.ExtraPeers);
    }

    [Fact]
    public void AppleTv_Group_With_A_Real_Speaker_Leaves_ExtraPeers_Unset()
    {
        // The already-working case must be untouched: when the paired HomePod DOES qualify as
        // a standalone-audio speaker, MembersOf streams to it directly as a full session
        // member — it is not a bolt-on PTP peer — and ExtraPeers stays unset.
        var atv = AppleTv("AABBCC000004", "Den TV", IPAddress.Parse("10.0.0.3"),
            gid: "LR-GROUP", igl: true, gcgl: true);
        var speaker = HomePod("AABBCC000005", "Den", IPAddress.Parse("10.0.0.231"),
            gid: "LR-GROUP");
        var entry = new PickerEntry
        {
            Key = "combined", DisplayName = "Den TV", Kind = PickerEntryKind.Group,
            Leader = atv, Members = [atv, speaker], Subtitle = "2 devices",
        };

        var member = Assert.Single(GroupSession.MembersOf(entry));

        Assert.Equal(speaker.DeviceId, member.DeviceId);
        Assert.Null(member.ExtraPeers);
    }
}
