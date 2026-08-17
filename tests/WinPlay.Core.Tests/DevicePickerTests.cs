// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Discovery;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Collapse tests over a fixture representing a typical AirPlay LAN topology:
/// a standalone HomePod mini, a HomePod stereo pair ("Office"), an Apple TV–led group
/// containing a nested stereo pair ("Den TV", 3 devices), and a third-party
/// Shairport speaker.
/// </summary>
public class DevicePickerTests
{
    private const string OfficeGid = "22222222-2222-2222-2222-222222222222+1+33333333-3333-3333-3333-333333333333";
    private const string OfficeTsid = "22222222-2222-2222-2222-222222222222";
    private const string DenGid = "44444444-4444-4444-4444-444444444444";
    private const string DenTsid = "55555555-5555-5555-5555-555555555555";

    private static AirPlayDevice HomePod(string id, string name, string? gid = null, string? gpn = null,
        bool igl = false, string? tsid = null, string? pgid = null) => new()
    {
        DeviceId = id, Name = name, Model = "AudioAccessory5,1",
        RawFeatures = (ulong)(AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.SupportsPtp),
        GroupId = gid, GroupPublicName = gpn, IsGroupLeader = igl,
        GroupContainsLeader = gid is not null, TightSyncId = tsid, ParentGroupId = pgid,
        ParentGroupContainsLeader = pgid is not null,
    };

    private static List<AirPlayDevice> SyntheticLanFixture() =>
    [
        HomePod("AABBCC000001", "Bedroom", gid: "11111111-1111-1111-1111-111111111111+AAAA0001", igl: true),
        HomePod("AABBCC000002", "Office HomePod R", gid: OfficeGid, gpn: "Office", igl: true, tsid: OfficeTsid),
        HomePod("AABBCC000003", "Office HomePod L", gid: OfficeGid, gpn: "Office", igl: false, tsid: OfficeTsid, pgid: OfficeGid),
        new AirPlayDevice
        {
            DeviceId = "AABBCC000004", Name = "Den TV", Model = "AppleTV11,1",
            RawFeatures = (ulong)(AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.SupportsAirPlayScreen | AirPlayFeatures.SupportsPtp),
            GroupId = DenGid, GroupPublicName = "Den TV", IsGroupLeader = true, GroupContainsLeader = true,
        },
        HomePod("AABBCC000005", "Den", gid: DenGid, gpn: "Den TV", tsid: DenTsid, pgid: DenGid),
        HomePod("AABBCC000006", "Den (3)", gid: DenGid, gpn: "Den TV", tsid: DenTsid, pgid: DenGid),
        new AirPlayDevice
        {
            DeviceId = "AABBCC000007", Name = "Kitchen Speaker", Model = "Shairport Sync",
            RawFeatures = (ulong)(AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.HasUnifiedAdvertiserInfo),
            GroupId = "66666666-6666-6666-6666-666666666666",
        },
    ];

    [Fact]
    public void Seven_Devices_Collapse_To_Four_Rows()
    {
        var entries = DevicePicker.Collapse(SyntheticLanFixture());
        Assert.Equal(4, entries.Count);
        Assert.Equal(["Bedroom", "Den TV", "Kitchen Speaker", "Office"],
            entries.Select(e => e.DisplayName).ToArray());
    }

    [Fact]
    public void Stereo_Pair_Is_One_Row_With_Leader_First()
    {
        var office = DevicePicker.Collapse(SyntheticLanFixture()).Single(e => e.DisplayName == "Office");

        Assert.Equal(PickerEntryKind.StereoPair, office.Kind);
        Assert.Equal("Stereo Pair", office.Subtitle);
        Assert.Equal(2, office.Members.Count);
        Assert.Equal("AABBCC000002", office.Leader.DeviceId);       // igl=1 → R is leader
        Assert.Same(office.Leader, office.Members[0]);
        Assert.True(office.IsAudioCapable);
        Assert.False(office.IsMirroringCapable);
    }

    [Fact]
    public void AppleTv_Led_Group_Collapses_Nested_Pair_And_Is_Mirroring_Eligible()
    {
        var den = DevicePicker.Collapse(SyntheticLanFixture()).Single(e => e.DisplayName == "Den TV");

        Assert.Equal(PickerEntryKind.Group, den.Kind);
        Assert.Equal(3, den.Members.Count);
        Assert.Equal("3 devices", den.Subtitle);
        Assert.Equal("AABBCC000004", den.Leader.DeviceId);          // the Apple TV leads
        Assert.Equal(AirPlayDeviceSubtype.AppleTv, den.Leader.Subtype);
        Assert.True(den.IsMirroringCapable);
    }

    [Fact]
    public void Standalone_HomePod_With_Own_Gid_Is_Single()
    {
        var bedroom = DevicePicker.Collapse(SyntheticLanFixture()).Single(e => e.DisplayName == "Bedroom");

        Assert.Equal(PickerEntryKind.Single, bedroom.Kind);
        Assert.Equal("HomePod mini", bedroom.Subtitle);
        Assert.False(bedroom.IsMirroringCapable);
    }

    [Fact]
    public void Leaderless_Third_Party_Speaker_Still_Gets_A_Row()
    {
        var kitchen = DevicePicker.Collapse(SyntheticLanFixture()).Single(e => e.DisplayName == "Kitchen Speaker");

        Assert.Equal(PickerEntryKind.Single, kitchen.Kind);
        Assert.Same(kitchen.Leader, kitchen.Members[0]);
        Assert.True(kitchen.IsAudioCapable);
        Assert.False(kitchen.IsMirroringCapable);
    }

    [Fact]
    public void Two_Homepods_Sharing_Gid_Without_Tsid_Are_A_Group_Not_A_Pair()
    {
        List<AirPlayDevice> devices =
        [
            HomePod("A", "Kitchen", gid: "G1", gpn: "Everywhere", igl: true),
            HomePod("B", "Bedroom", gid: "G1", gpn: "Everywhere"),
        ];
        var entry = Assert.Single(DevicePicker.Collapse(devices));
        Assert.Equal(PickerEntryKind.Group, entry.Kind);
        Assert.Equal("Everywhere", entry.DisplayName);
        Assert.Equal("2 devices", entry.Subtitle);
    }

    [Fact]
    public void Devices_Without_Any_Group_Info_Each_Get_A_Row()
    {
        List<AirPlayDevice> devices =
        [
            new() { DeviceId = "A", Name = "Speaker A" },
            new() { DeviceId = "B", Name = "Speaker B" },
        ];
        Assert.Equal(2, DevicePicker.Collapse(devices).Count);
    }

    [Fact]
    public void Compound_Gid_Fuses_A_Genuinely_Present_Sibling()
    {
        // Reproduces a real-world compound-gid TXT record: a HomePod mini advertises a
        // COMPOUND gid ("<self>+<partner>") because its firmware still believes it belongs to
        // a live group. Before this fix DevicePicker only matched whole gid strings, so a
        // partner that is genuinely present but advertises just its own (component) id fell
        // through to a separate row instead of fusing with its leader. When that partner IS
        // visible on the LAN, the two must collapse into one multi-member entry.
        const string leaderId = "AA11BB22CC33-DD44-EE55-FF66-001122334455";
        const string partnerId = "99887766-5544-3322-1100-AABBCCDDEEFF";
        List<AirPlayDevice> devices =
        [
            HomePod("AA11BB22CC33", "Bedroom", gid: $"{leaderId}+{partnerId}", igl: true),
            HomePod("998877665544", "Bedroom Partner", gid: partnerId),
        ];

        var entry = Assert.Single(DevicePicker.Collapse(devices));
        Assert.Equal(PickerEntryKind.Group, entry.Kind);
        Assert.Equal(2, entry.Members.Count);
        Assert.Equal("AA11BB22CC33", entry.Leader.DeviceId);
    }

    [Fact]
    public void Pair_Members_With_Divergent_Gids_Still_Collapse_To_One_Row()
    {
        // Reproduces the streaming bug: while a pair is receiving audio its members can
        // advertise different group ids. They share a tsid, so must remain a single row
        // (not split into two) — and keep the same stable Key so the UI updates in place.
        List<AirPlayDevice> idle =
        [
            HomePod("AABBCC000002", "Office HomePod R", gid: OfficeGid, gpn: "Office", igl: true, tsid: OfficeTsid),
            HomePod("AABBCC000003", "Office HomePod L", gid: OfficeGid, gpn: "Office", tsid: OfficeTsid, pgid: OfficeGid),
        ];
        List<AirPlayDevice> streaming =
        [
            HomePod("AABBCC000002", "Office HomePod R", gid: "NOW-PLAYING-R", igl: true, tsid: OfficeTsid),
            HomePod("AABBCC000003", "Office HomePod L", gid: "NOW-PLAYING-L", tsid: OfficeTsid),
        ];

        var idleEntry = Assert.Single(DevicePicker.Collapse(idle));
        var streamingEntry = Assert.Single(DevicePicker.Collapse(streaming));

        Assert.Equal(PickerEntryKind.StereoPair, streamingEntry.Kind);
        Assert.Equal(2, streamingEntry.Members.Count);
        Assert.Equal(idleEntry.Key, streamingEntry.Key); // same row identity → no duplicate
    }
}
