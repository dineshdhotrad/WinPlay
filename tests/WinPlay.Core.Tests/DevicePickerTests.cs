// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Discovery;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Collapse tests over a fixture mirroring the real LAN this project is verified on:
/// a standalone HomePod mini, a HomePod stereo pair ("Study"), an Apple TV–led group
/// containing a nested stereo pair ("Living Room TV", 3 devices), and a Shairport
/// third-party speaker.
/// </summary>
public class DevicePickerTests
{
    private const string StudyGid = "A9C37DDC-F1B3-5FE1-96FC-321C7FCF3E4E+1+3728F550-2993-4FF9-AC16-DC4E8D34F5E4";
    private const string StudyTsid = "A9C37DDC-F1B3-5FE1-96FC-321C7FCF3E4E";
    private const string LrGid = "06A7D64E-AB1E-53D4-AA95-ABF92A31496D";
    private const string LrTsid = "56D59835-A691-5CE9-9FC4-81956C6EEC92";

    private static AirPlayDevice HomePod(string id, string name, string? gid = null, string? gpn = null,
        bool igl = false, string? tsid = null, string? pgid = null) => new()
    {
        DeviceId = id, Name = name, Model = "AudioAccessory5,1",
        RawFeatures = (ulong)(AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.SupportsPtp),
        GroupId = gid, GroupPublicName = gpn, IsGroupLeader = igl,
        GroupContainsLeader = gid is not null, TightSyncId = tsid, ParentGroupId = pgid,
        ParentGroupContainsLeader = pgid is not null,
    };

    private static List<AirPlayDevice> RealLanFixture() =>
    [
        HomePod("FAA3D5083FF6", "Guest Bedroom", gid: "C548CE8E-1620-4DA2-B9C6-3717631D367C+D420E3B0", igl: true),
        HomePod("C60A074F5F7F", "SR HomePod R", gid: StudyGid, gpn: "Study", igl: true, tsid: StudyTsid),
        HomePod("C2D9A60D57E8", "SR HomePod L", gid: StudyGid, gpn: "Study", igl: false, tsid: StudyTsid, pgid: StudyGid),
        new AirPlayDevice
        {
            DeviceId = "869EB3F567EC", Name = "Living Room TV", Model = "AppleTV11,1",
            RawFeatures = (ulong)(AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.SupportsAirPlayScreen | AirPlayFeatures.SupportsPtp),
            GroupId = LrGid, GroupPublicName = "Living Room TV", IsGroupLeader = true, GroupContainsLeader = true,
        },
        HomePod("8E1A6B7EA5C1", "Living Room", gid: LrGid, gpn: "Living Room TV", tsid: LrTsid, pgid: LrGid),
        HomePod("92348FA7104F", "Living Room (3)", gid: LrGid, gpn: "Living Room TV", tsid: LrTsid, pgid: LrGid),
        new AirPlayDevice
        {
            DeviceId = "2CCF6735D31E", Name = "Korus Living Room", Model = "Shairport Sync",
            RawFeatures = (ulong)(AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.HasUnifiedAdvertiserInfo),
            GroupId = "6393961f-b83c-43d9-9929-88a880cea36c",
        },
    ];

    [Fact]
    public void Seven_Devices_Collapse_To_Four_Rows()
    {
        var entries = DevicePicker.Collapse(RealLanFixture());
        Assert.Equal(4, entries.Count);
        Assert.Equal(["Guest Bedroom", "Korus Living Room", "Living Room TV", "Study"],
            entries.Select(e => e.DisplayName).ToArray());
    }

    [Fact]
    public void Stereo_Pair_Is_One_Row_With_Leader_First()
    {
        var study = DevicePicker.Collapse(RealLanFixture()).Single(e => e.DisplayName == "Study");

        Assert.Equal(PickerEntryKind.StereoPair, study.Kind);
        Assert.Equal("Stereo Pair", study.Subtitle);
        Assert.Equal(2, study.Members.Count);
        Assert.Equal("C60A074F5F7F", study.Leader.DeviceId);       // igl=1 → R is leader
        Assert.Same(study.Leader, study.Members[0]);
        Assert.True(study.IsAudioCapable);
        Assert.False(study.IsMirroringCapable);
    }

    [Fact]
    public void AppleTv_Led_Group_Collapses_Nested_Pair_And_Is_Mirroring_Eligible()
    {
        var lr = DevicePicker.Collapse(RealLanFixture()).Single(e => e.DisplayName == "Living Room TV");

        Assert.Equal(PickerEntryKind.Group, lr.Kind);
        Assert.Equal(3, lr.Members.Count);
        Assert.Equal("3 devices", lr.Subtitle);
        Assert.Equal("869EB3F567EC", lr.Leader.DeviceId);          // the Apple TV leads
        Assert.Equal(AirPlayDeviceSubtype.AppleTv, lr.Leader.Subtype);
        Assert.True(lr.IsMirroringCapable);
    }

    [Fact]
    public void Standalone_HomePod_With_Own_Gid_Is_Single()
    {
        var guest = DevicePicker.Collapse(RealLanFixture()).Single(e => e.DisplayName == "Guest Bedroom");

        Assert.Equal(PickerEntryKind.Single, guest.Kind);
        Assert.Equal("HomePod mini", guest.Subtitle);
        Assert.False(guest.IsMirroringCapable);
    }

    [Fact]
    public void Leaderless_Third_Party_Speaker_Still_Gets_A_Row()
    {
        var korus = DevicePicker.Collapse(RealLanFixture()).Single(e => e.DisplayName == "Korus Living Room");

        Assert.Equal(PickerEntryKind.Single, korus.Kind);
        Assert.Same(korus.Leader, korus.Members[0]);
        Assert.True(korus.IsAudioCapable);
        Assert.False(korus.IsMirroringCapable);
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
    public void Pair_Members_With_Divergent_Gids_Still_Collapse_To_One_Row()
    {
        // Reproduces the streaming bug: while a pair is receiving audio its members can
        // advertise different group ids. They share a tsid, so must remain a single row
        // (not split into two) — and keep the same stable Key so the UI updates in place.
        List<AirPlayDevice> idle =
        [
            HomePod("C60A074F5F7F", "SR HomePod R", gid: StudyGid, gpn: "Study", igl: true, tsid: StudyTsid),
            HomePod("C2D9A60D57E8", "SR HomePod L", gid: StudyGid, gpn: "Study", tsid: StudyTsid, pgid: StudyGid),
        ];
        List<AirPlayDevice> streaming =
        [
            HomePod("C60A074F5F7F", "SR HomePod R", gid: "NOW-PLAYING-R", igl: true, tsid: StudyTsid),
            HomePod("C2D9A60D57E8", "SR HomePod L", gid: "NOW-PLAYING-L", tsid: StudyTsid),
        ];

        var idleEntry = Assert.Single(DevicePicker.Collapse(idle));
        var streamingEntry = Assert.Single(DevicePicker.Collapse(streaming));

        Assert.Equal(PickerEntryKind.StereoPair, streamingEntry.Kind);
        Assert.Equal(2, streamingEntry.Members.Count);
        Assert.Equal(idleEntry.Key, streamingEntry.Key); // same row identity → no duplicate
    }
}
