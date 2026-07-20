// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Discovery;

public enum PickerEntryKind
{
    Single,
    StereoPair,
    Group,
}

/// <summary>
/// One row in the iOS Control Center–style device picker: a single receiver, a stereo
/// pair collapsed to one entry, or a multi-device group collapsed to one entry.
/// </summary>
public sealed class PickerEntry
{
    /// <summary>Stable identity for selection persistence: group ID or device ID.</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }
    public required PickerEntryKind Kind { get; init; }

    /// <summary>The member to open the RTSP/streaming session with (advertises igl=1).</summary>
    public required AirPlayDevice Leader { get; init; }

    /// <summary>All physical devices behind this row, leader first.</summary>
    public required IReadOnlyList<AirPlayDevice> Members { get; init; }

    public bool IsAudioCapable => Members.Any(m => m.SupportsAudio);

    /// <summary>
    /// Mirroring is offered only when the leader is an Apple TV / AirPlay 2 TV.
    /// HomePod (AudioAccessory*) rows are audio-only, always.
    /// </summary>
    public bool IsMirroringCapable => Leader.IsMirroringCandidate;

    /// <summary>Contextual subtitle ("Stereo Pair", "3 devices", model name).</summary>
    public required string Subtitle { get; init; }
}

/// <summary>
/// The Control Center collapse algorithm (plan §3.2):
///  1. receivers sharing a `gid` form one group; `pgid` folds nested groups under parents;
///  2. the `igl=1` member is the leader (session endpoint);
///  3. a two-member all-HomePod group sharing a `tsid` is a stereo pair;
///  4. label = `gpn` (group public name), falling back to the leader's name.
///
/// Caveat (documented in the plan): the exact field Apple uses to label "stereo pair" vs
/// "multi-room" is not fully public; gid + tsid + AudioAccessory model is the accepted
/// heuristic (cross-referenced with owntone issue #1413).
/// </summary>
public static class DevicePicker
{
    public static List<PickerEntry> Collapse(IReadOnlyList<AirPlayDevice> devices)
    {
        // Effective group key: parent group wins so nested sub-groups fold upward.
        var grouped = devices
            .GroupBy(d => d.ParentGroupId ?? d.GroupId ?? $"device:{d.DeviceId}")
            .Select(g => g.ToList())
            .ToList();

        // A physical stereo pair is identified by a shared tight-sync id (tsid) — a stable
        // property of how it was set up, independent of playback. While streaming, a pair's
        // members can advertise divergent group ids and would otherwise split into two rows;
        // re-merge any groups that share a tsid so a pair is always one entity.
        MergeByTightSync(grouped);

        List<PickerEntry> entries = [];
        foreach (var members in grouped)
        {
            var leader = members.FirstOrDefault(m => m.IsGroupLeader)
                ?? members.FirstOrDefault(m => m.GroupContainsLeader)
                ?? members[0];

            // Leader first — streaming code targets Members[0].
            members.Remove(leader);
            members.Insert(0, leader);

            var kind = KindOf(members);
            string name = kind == PickerEntryKind.Single
                ? leader.Name
                : members.Select(m => m.GroupPublicName).FirstOrDefault(n => n is not null) ?? leader.Name;

            entries.Add(new PickerEntry
            {
                // Stable identity: the set of physical device IDs. A receiver's group id
                // (gid) changes when it starts playing, so keying on it would spawn a
                // duplicate row mid-stream; the member set does not change on playback.
                Key = string.Join("+", members.Select(m => m.DeviceId).OrderBy(id => id, StringComparer.Ordinal)),
                DisplayName = name,
                Kind = kind,
                Leader = leader,
                Members = members,
                Subtitle = SubtitleFor(kind, members, leader),
            });
        }

        entries.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>
    /// Merges any groups that share a stereo-pair tight-sync id, so a physical pair stays a
    /// single entry even when its members briefly advertise different group ids (as happens
    /// mid-stream). Mutates <paramref name="groups"/> in place.
    /// </summary>
    private static void MergeByTightSync(List<List<AirPlayDevice>> groups)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            var tsids = groups[i].Select(d => d.TightSyncId).Where(t => t is not null).ToHashSet();
            if (tsids.Count == 0) continue;
            for (int j = groups.Count - 1; j > i; j--)
            {
                if (groups[j].Any(d => d.TightSyncId is { } t && tsids.Contains(t)))
                {
                    groups[i].AddRange(groups[j]);
                    groups.RemoveAt(j);
                }
            }
        }
    }

    private static PickerEntryKind KindOf(List<AirPlayDevice> members)
    {
        if (members.Count == 1) return PickerEntryKind.Single;
        if (members.Count == 2
            && members.All(m => m.Subtype == AirPlayDeviceSubtype.HomePod)
            && members[0].TightSyncId is { } tsid
            && members[1].TightSyncId == tsid)
            return PickerEntryKind.StereoPair;
        return PickerEntryKind.Group;
    }

    private static string SubtitleFor(PickerEntryKind kind, List<AirPlayDevice> members, AirPlayDevice leader) => kind switch
    {
        PickerEntryKind.StereoPair => "Stereo Pair",
        PickerEntryKind.Group => $"{members.Count} devices",
        _ => FriendlyModel(leader.Model),
    };

    /// <summary>Marketing names for common models; falls back to the raw model string.</summary>
    public static string FriendlyModel(string? model) => model switch
    {
        null => "AirPlay device",
        "AudioAccessory1,1" or "AudioAccessory1,2" => "HomePod",
        "AudioAccessory5,1" => "HomePod mini",
        "AudioAccessory6,1" or "AudioAccessory6,2" => "HomePod (2nd generation)",
        _ when model.StartsWith("AudioAccessory", StringComparison.Ordinal) => "HomePod",
        _ when model.StartsWith("AppleTV", StringComparison.Ordinal) => "Apple TV",
        _ when model.StartsWith("Macmini", StringComparison.Ordinal)
            || model.StartsWith("MacBook", StringComparison.Ordinal)
            || model.StartsWith("iMac", StringComparison.Ordinal) => "Mac",
        _ => model,
    };
}
