// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Capture;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;
using WinPlay.Core.Mirror;
using WinPlay.Core.Plist;
using WinPlay.Core.Raop;
using WinPlay.Core.Rtsp;

// WinPlay protocol test harness.
//   discover [--seconds N] [--verbose]   enumerate receivers + collapsed picker (default)
//   info --to <name>                     GET /info against a receiver, dump the plist
//   play --to <name>[,<name>…] [--minutes M] [--volume dB] [--tone|--lr-test] [--ntp]
//       stream to one or more picker entries; a stereo pair/group automatically gets a
//       coordinated per-member session set (shared PTP grandmaster). Source is system
//       loopback by default, 440 Hz with --tone, L/R channel test with --lr-test.
//   pair --to <name> [--pin NNNN | --pin-file <path>]
//       PIN-pair with a receiver that requires Connection Authorization (Apple TV).
//       Triggers the on-screen PIN, then completes with --pin, or polls --pin-file
//       for up to 60 s so the PIN can be supplied while the session is held open.
//   mirror --to <Apple TV> [--minutes M] [--fps N] [--mbps N]
//       Screen-mirror the desktop to a paired Apple TV (Desktop Duplication + Media
//       Foundation H.264 + FairPlay SAP). Requires a prior `pair`.

string command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "discover";
string? GetOpt(string name) =>
    args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length)
        .Select(x => args[x.i + 1]).FirstOrDefault();
bool HasFlag(string name) => args.Contains(name);

return command switch
{
    "discover" => await DiscoverAsync(),
    "info" => await InfoAsync(),
    "play" => await PlayAsync(),
    "pair" => await PairAsync(),
    "mirror" => await MirrorAsync(),
    _ => Fail($"unknown command '{command}'"),
};

async Task<int> MirrorAsync()
{
    string? name = GetOpt("--to");
    if (name is null) return Fail("mirror requires --to <Apple TV name>");
    double minutes = double.TryParse(GetOpt("--minutes"), out double m) ? m : 5;
    int fps = int.TryParse(GetOpt("--fps"), out int f) ? f : 30;
    int mbps = int.TryParse(GetOpt("--mbps"), out int mb) ? mb : 12;

    var (_, _, snapshot) = await FindTargetsAsync([name]);
    var device = MatchDevice(snapshot, name);
    if (device?.Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        is not { } address)
        return Fail($"device '{name}' not found");
    if (device.Subtype is not AirPlayDeviceSubtype.AppleTv)
        Console.WriteLine($"warning: {device.Name} is {device.Subtype}; screen mirroring only works with Apple TV / AirPlay 2 TVs");

    var credentials = new CredentialStore().Load(device.DeviceId);
    if (credentials is null)
        return Fail($"no stored pairing for {device.Name} — run 'pair --to \"{device.Name}\"' first");

    Console.WriteLine($"mirroring desktop to {device.Name} at {address} for {minutes:F1} min ({fps}fps, {mbps} Mbps)");
    await using var session = await MirrorSession.ConnectAsync(address, device.AirPlayPort ?? 7000, credentials,
        stage => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {stage}"));

    await using var source = new ScreenMirrorSource(fps, mbps);
    source.Diagnostic += d => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] capture: {d}");
    // Audio-in-mirror test: --tone sends a 440 Hz tone, otherwise system audio (loopback).
    using IAudioSource mirrorAudio = HasFlag("--tone") ? new SineAudioSource() : new LoopbackAudioSource();
    _ = session.StartStreamingAsync(source, session.HasAudio ? mirrorAudio : null);
    if (session.HasAudio) Console.WriteLine("  audio-in-mirror active");

    var end = DateTime.UtcNow.AddMinutes(minutes);
    while (DateTime.UtcNow < end)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] alive — {session.FramesSent} frames sent");
    }
    Console.WriteLine("time limit reached — tearing down");
    await session.StopAsync();
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}

static AirPlayDevice? MatchDevice(IReadOnlyList<AirPlayDevice> snapshot, string name) =>
    snapshot.FirstOrDefault(d =>
        string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(d.DeviceId, AirPlayDevice.NormalizeDeviceId(name), StringComparison.OrdinalIgnoreCase));

/// <summary>
/// Resolves device names to collapsed picker entries. Waits until every named device
/// is found, then briefly for its pair/group members — every member must join the
/// coordinated session or a stereo-pair partner stays silent.
/// </summary>
async Task<(List<PickerEntry> Entries, List<string> Missing, IReadOnlyList<AirPlayDevice> Snapshot)>
    FindTargetsAsync(string[] names, int seconds = 8)
{
    Console.WriteLine($"searching for {string.Join(", ", names.Select(n => $"'{n}'"))} ...");
    using var browser = new AirPlayBrowser();
    browser.Start();

    IReadOnlyList<AirPlayDevice> snapshot = [];
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
        snapshot = browser.Snapshot();
        if (names.All(n => MatchDevice(snapshot, n) is { Addresses.Count: > 0 })) break;
        await Task.Delay(400);
    }

    // Grace period for group/pair members of the found targets to appear.
    var memberDeadline = DateTime.UtcNow.AddSeconds(4);
    while (DateTime.UtcNow < memberDeadline)
    {
        snapshot = browser.Snapshot();
        bool complete = names
            .Select(n => MatchDevice(snapshot, n))
            .All(d => d is null
                || (d.TightSyncId is null && d.GroupId is null)
                || snapshot.Any(o => o.DeviceId != d.DeviceId && o.Addresses.Count > 0
                    && ((d.TightSyncId is not null && o.TightSyncId == d.TightSyncId)
                        || (d.GroupId is not null && o.GroupId == d.GroupId))));
        if (complete) break;
        await Task.Delay(400);
    }

    snapshot = browser.Snapshot();
    var picker = DevicePicker.Collapse(snapshot);
    var entries = new List<PickerEntry>();
    var missing = new List<string>();
    foreach (string n in names)
    {
        var device = MatchDevice(snapshot, n);
        var entry = device is null ? null
            : picker.FirstOrDefault(e => e.Members.Any(m => m.DeviceId == device.DeviceId));
        if (entry is null)
        {
            missing.Add(n);
        }
        else if (!entries.Any(e => e.Key == entry.Key))
        {
            entries.Add(entry);
        }
    }
    return (entries, missing, snapshot);
}

async Task<int> InfoAsync()
{
    string? name = GetOpt("--to");
    if (name is null) return Fail("info requires --to <device name>");
    var (_, _, snapshot) = await FindTargetsAsync([name]);
    var device = MatchDevice(snapshot, name);
    if (device?.Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        is not { } address)
        return 1;

    using var rtsp = new RtspConnection();
    await rtsp.ConnectAsync(address, device.AirPlayPort ?? 7000, CancellationToken.None);
    var resp = await rtsp.RequestAsync(new RtspRequest { Method = "GET", Uri = "/info" }, CancellationToken.None);
    resp.EnsureSuccess("GET /info");
    Console.WriteLine($"GET /info → {resp.StatusCode} ({resp.Body.Length} bytes, {resp.Headers.GetValueOrDefault("Content-Type")})");
    PrintPlist(BinaryPlist.Read(resp.Body), indent: 1);
    return 0;
}

async Task<int> PlayAsync()
{
    string? to = GetOpt("--to");
    if (to is null) return Fail("play requires --to <name>[,<name>…]");
    string[] names = to.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    double minutes = double.TryParse(GetOpt("--minutes"), out double m) ? m : 12;
    double volume = double.TryParse(GetOpt("--volume"), out double v) ? v : -18;

    var (entries, missing, _) = await FindTargetsAsync(names);
    foreach (string lost in missing)
        Console.Error.WriteLine($"device '{lost}' not found on the LAN");
    if (entries.Count == 0) return 1;

    IAudioSource MakeSource() => HasFlag("--lr-test")
        ? new ChannelTestAudioSource()
        : HasFlag("--tone")
            ? new SineAudioSource()
            : new LoopbackAudioSource();
    Console.WriteLine(HasFlag("--lr-test")
        ? "source: L/R channel test (4 s left 440 Hz ↔ 4 s right 880 Hz)"
        : HasFlag("--tone") ? "source: 440 Hz test tone" : "source: system audio (WASAPI loopback)");

    var credentialStore = new CredentialStore();
    var groups = new List<GroupSession>();
    try
    {
        foreach (var entry in entries)
        {
            var members = GroupSession.MembersOf(entry, credentialStore);
            if (HasFlag("--ntp"))
                members = [.. members.Select(member => member with { UsePtp = false })];
            if (HasFlag("--solo"))
                members = [.. members.Where(member => names.Any(n =>
                    string.Equals(member.Name, n, StringComparison.OrdinalIgnoreCase)))];
            if (members.Count == 0)
            {
                Console.WriteLine($"  !! {entry.DisplayName}: no member has an IPv4 address yet");
                continue;
            }
            Console.WriteLine($"● {entry.DisplayName} [{entry.Kind}] → "
                + string.Join(" + ", members.Select(member => $"{member.Name}@{member.Address}"))
                + $"  (timing {(members[0].UsePtp ? "PTP, we are grandmaster" : "NTP")})");
            groups.Add(await GroupSession.ConnectAsync(members,
                (memberName, stage) => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] [{memberName}] {stage}")));
        }
        if (groups.Count == 0) return 1;

        foreach (var group in groups)
            await group.StartStreamingAsync(MakeSource(), volume);
        Console.WriteLine($"streaming to {groups.Count} destination(s) for {minutes:F1} min at {volume} dB");

        if (GetOpt("--title") is { } title)
        {
            await Task.Delay(1000);
            foreach (var group in groups)
                await group.SendMetadataAsync(title, GetOpt("--artist"), GetOpt("--album"));
            Console.WriteLine($"sent now-playing metadata: \"{title}\" — {GetOpt("--artist")}");
        }

        var end = DateTime.UtcNow.AddMinutes(minutes);
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] alive — "
                + string.Join(", ", groups.Select(g => $"{string.Join("+", g.MemberNames)}: {g.Elapsed:hh\\:mm\\:ss} ({g.FramesSent} pkts)")));
        }
        Console.WriteLine("time limit reached — tearing down");
        foreach (var group in groups)
            await group.StopAsync();
    }
    finally
    {
        foreach (var group in groups)
            await group.DisposeAsync();
    }
    return 0;
}

async Task<int> PairAsync()
{
    string? name = GetOpt("--to");
    if (name is null) return Fail("pair requires --to <device name>");
    var (_, _, snapshot) = await FindTargetsAsync([name]);
    var device = MatchDevice(snapshot, name);
    if (device?.Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        is not { } address)
        return Fail($"device '{name}' not found");

    Console.WriteLine($"starting PIN pairing with {device.Name} at {address} — a PIN should appear on the receiver now");
    using var handle = await ReceiverPairing.BeginAsync(address, device.AirPlayPort ?? 7000, CancellationToken.None);

    string? pin = GetOpt("--pin");
    if (pin is null && GetOpt("--pin-file") is { } pinFile)
    {
        Console.WriteLine($"waiting up to 60 s for the PIN to be written to {pinFile} ...");
        for (int i = 0; i < 120 && string.IsNullOrWhiteSpace(pin); i++)
        {
            if (File.Exists(pinFile))
            {
                try { pin = File.ReadAllText(pinFile).Trim(); } catch (IOException) { }
            }
            if (string.IsNullOrWhiteSpace(pin)) await Task.Delay(500);
        }
    }
    if (string.IsNullOrWhiteSpace(pin))
        return Fail("no PIN provided — pass --pin NNNN, or --pin-file <path> and write the PIN there");

    var credentials = await handle.FinishAsync(pin, CancellationToken.None);
    new CredentialStore().Save(device.DeviceId, credentials);
    Console.WriteLine($"✔ paired with {device.Name} ({device.DeviceId}) — credentials stored, 'play' will now use pair-verify");
    return 0;
}

async Task<int> DiscoverAsync()
{
    int seconds = int.TryParse(GetOpt("--seconds"), out int s) ? Math.Clamp(s, 1, 300) : 8;
    bool verbose = HasFlag("--verbose") || HasFlag("-v");

    Console.WriteLine($"winplay — browsing _airplay._tcp / _raop._tcp for {seconds}s ...");
    Console.WriteLine();

    using var browser = new AirPlayBrowser();
    var seen = new HashSet<string>();
    browser.DevicesChanged += devices =>
    {
        foreach (var d in devices)
        {
            if (seen.Add(d.DeviceId))
                Console.WriteLine($"  + found {d.Name} [{d.Model ?? "?"}] {d.DeviceId}");
        }
    };
    browser.Start();
    await Task.Delay(TimeSpan.FromSeconds(seconds));

    var snapshot = browser.Snapshot();

    Console.WriteLine();
    var picker = DevicePicker.Collapse(snapshot);
    Console.WriteLine($"=== picker: {snapshot.Count} device(s) → {picker.Count} row(s) ===");
    foreach (var entry in picker)
    {
        string caps = (entry.IsAudioCapable ? "audio" : "") +
                      (entry.IsMirroringCapable ? "+mirroring" : "");
        Console.WriteLine($"  ● {entry.DisplayName,-24} {entry.Subtitle,-22} [{entry.Kind}] {caps}");
        foreach (var member in entry.Members)
            Console.WriteLine($"      {(ReferenceEquals(member, entry.Leader) ? "★ leader" : "  member")}  {member.Name} ({DevicePicker.FriendlyModel(member.Model)})");
    }

    Console.WriteLine();
    Console.WriteLine($"=== {snapshot.Count} AirPlay device(s) ===");
    foreach (var d in snapshot)
    {
        Console.WriteLine();
        Console.WriteLine($"■ {d.Name}");
        Console.WriteLine($"    id        {d.DeviceId}");
        Console.WriteLine($"    model     {d.Model ?? "(none)"}   subtype={d.Subtype}  audio={d.SupportsAudio}  mirroringCandidate={d.IsMirroringCandidate}");
        Console.WriteLine($"    host      {d.Hostname ?? "(unresolved)"}  [{string.Join(", ", d.Addresses)}]");
        Console.WriteLine($"    ports     airplay={(d.AirPlayPort?.ToString() ?? "-")}  raop={(d.RaopPort?.ToString() ?? "-")}");
        Console.WriteLine($"    features  0x{d.RawFeatures:X16}");
        string named = string.Join(", ",
            Enum.GetValues<AirPlayFeatures>().Where(f => f != AirPlayFeatures.None && d.Features.HasFlag(f)));
        Console.WriteLine($"              {(named.Length > 0 ? named : "(none known)")}");
        Console.WriteLine($"    flags     0x{d.StatusFlags:X}");
        if (d.GroupId is not null)
        {
            Console.WriteLine($"    group     gid={d.GroupId}  gpn={d.GroupPublicName ?? "-"}  igl={d.IsGroupLeader}  gcgl={d.GroupContainsLeader}");
            if (d.ParentGroupId is not null)
                Console.WriteLine($"              pgid={d.ParentGroupId}  pgcgl={d.ParentGroupContainsLeader}");
            if (d.TightSyncId is not null)
                Console.WriteLine($"              tsid={d.TightSyncId} (tight sync — stereo-pair signal)");
        }
        if (verbose)
        {
            foreach (var (k, val) in d.AirPlayTxt.OrderBy(p => p.Key))
                Console.WriteLine($"    [airplay] {k}={val}");
            foreach (var (k, val) in d.RaopTxt.OrderBy(p => p.Key))
                Console.WriteLine($"    [raop]    {k}={val}");
        }
    }
    return 0;
}

static void PrintPlist(object? value, int indent)
{
    string pad = new(' ', indent * 2);
    switch (value)
    {
        case Dictionary<string, object?> dict:
            foreach (var (k, v) in dict.OrderBy(p => p.Key))
            {
                if (v is Dictionary<string, object?> or List<object?>)
                {
                    Console.WriteLine($"{pad}{k}:");
                    PrintPlist(v, indent + 1);
                }
                else
                {
                    Console.WriteLine($"{pad}{k} = {FormatScalar(v)}");
                }
            }
            break;
        case List<object?> list:
            for (int i = 0; i < list.Count; i++)
            {
                Console.WriteLine($"{pad}[{i}]:");
                PrintPlist(list[i], indent + 1);
            }
            break;
        default:
            Console.WriteLine($"{pad}{FormatScalar(value)}");
            break;
    }
}

static string FormatScalar(object? v) => v switch
{
    null => "(null)",
    byte[] b when b.Length <= 40 => $"<{Convert.ToHexString(b)}>",
    byte[] b => $"<{b.Length} bytes: {Convert.ToHexString(b.AsSpan(0, 24))}…>",
    long l => $"{l} (0x{l:X})",
    string s => $"\"{s}\"",
    _ => v.ToString() ?? "",
};
