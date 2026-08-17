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
// discover [--seconds N] [--verbose] enumerate receivers + collapsed picker (default)
// info --to <name> GET /info against a receiver, dump the plist
// play --to <name>[,<name>…] [--minutes M] [--volume dB] [--tone|--lr-test] [--ntp] [--buffered]
// stream to one or more picker entries; a stereo pair/group automatically gets a
// coordinated per-member session set (shared PTP grandmaster). Source is system
// loopback by default, 440 Hz with --tone, L/R channel test with --lr-test.
// pair --to <name> [--pin NNNN | --pin-file <path>]
// PIN-pair with a receiver that requires Connection Authorization (Apple TV).
// Triggers the on-screen PIN, then completes with --pin, or polls --pin-file
// for up to 60 s so the PIN can be supplied while the session is held open.
// mirror --to <Apple TV> [--minutes M] [--fps N] [--mbps N]
// Screen-mirror the desktop to a paired Apple TV (Desktop Duplication + Media
// Foundation H.264 + FairPlay SAP). Requires a prior `pair`.

// AAC-LC encoder for Apple-TV-targeted buffered audio (Media Foundation, same as the app).
WinPlay.Core.Raop.GroupSession.AacEncoderFactory = () => new WinPlay.Capture.MediaFoundationAacEncoder();

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
    "audio" => Audio(),
    "trust" => Trust(),
    "diagnostics" => await DiagnosticsAsync(),
    _ => Fail($"unknown command '{command}'"),
};

// Writes the redacted diagnostics bundle: logs + environment, with pairing credentials
// excluded by construction and all key material scrubbed.
async Task<int> DiagnosticsAsync()
{
    var identities = new ReceiverIdentityStore().List();
    var sections = new Dictionary<string, string>
    {
        ["pinned-receivers"] = identities.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, identities.Select(p =>
                $"{p.Name} id={p.DeviceId} pk={p.PublicKey} lastSeen={p.LastSeenUtc}")),
    };
    string path = await WinPlay.Diagnostics.BugReportBundle.CreateAsync(
        GetOpt("--out"), extraSections: sections);
    Console.WriteLine($"diagnostics bundle written to {path}");
    Console.WriteLine("pairing credentials are never included; all key material is redacted");
    return 0;
}

// Inspects and manages pinned receiver identities. `trust` lists them;
// `trust --forget <deviceId>` drops one so a genuinely reset device can be trusted again.
int Trust()
{
    var store = new ReceiverIdentityStore();
    if (GetOpt("--forget") is { } forgetId)
    {
        Console.WriteLine(store.Forget(forgetId)
            ? $"forgot pinned identity for {forgetId} — the next connection will trust it afresh"
            : $"no pinned identity for {forgetId}");
        return 0;
    }

    var pins = store.List();
    if (pins.Count == 0)
    {
        Console.WriteLine("no receiver identities pinned yet");
        return 0;
    }
    Console.WriteLine($"=== {pins.Count} pinned receiver identit{(pins.Count == 1 ? "y" : "ies")} ===");
    foreach (var (deviceId, publicKey, name, lastSeen) in pins)
        Console.WriteLine($"  ● {(name.Length > 0 ? name : "(unnamed)"),-24} {deviceId}  pk={publicKey[..16]}…  last seen {lastSeen}");
    return 0;
}

// Prints the default render endpoint's mute/volume; `--unmute` clears a stuck mute. Endpoint
// loopback captures AFTER the mute, so a muted endpoint records (and streams) silence.
int Audio()
{
    var endpoint = new WinPlay.Core.Audio.WasapiEndpointController();
    // null = whatever is currently the default; the app pins a device id instead, so that a
    // restore always targets the endpoint it actually muted.
    string? deviceId = endpoint.DefaultRenderDeviceId;
    if (!endpoint.TryGetState(deviceId, out bool mute, out float vol))
        return Fail("no default render endpoint");
    Console.WriteLine($"default render endpoint: id={deviceId ?? "(none)"}  mute={mute}  volume={vol:P0}");

    // Report the capture period the engine actually grants (target: <= 10 ms, i.e. the WASAPI
    // shared-mode default engine period; see IAudioClient3 on low-latency shared streams).
    try
    {
        using var probe = new WinPlay.Core.Audio.ProcessLoopbackAudioSource((uint)Environment.ProcessId);
        double ms = probe.CapturePeriodMs;
        Console.WriteLine($"requested buffer: {ms:F2} ms");
        Console.WriteLine($"  low-latency: {probe.CaptureLatencyStatus}");

        // The requested buffer is not the delivery cadence: with event-driven capture the engine
        // signals at ITS period. Measure the real inter-callback interval — that is the actual
        // capture-side latency, whatever the requested buffer claims.
        var gaps = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double last = -1;
        probe.SampleBatchObserved += () =>
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (last >= 0) lock (gaps) gaps.Add(now - last);
            last = now;
        };
        System.Threading.Thread.Sleep(3000);
        lock (gaps)
        {
            if (gaps.Count > 2)
            {
                gaps.Sort();
                Console.WriteLine($"  measured callback cadence: median {gaps[gaps.Count / 2]:F2} ms, "
                    + $"p95 {gaps[(int)(gaps.Count * 0.95)]:F2} ms over {gaps.Count} callbacks");
            }
            else
            {
                Console.WriteLine("  measured callback cadence: no audio rendering, nothing to measure");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"capture period: probe failed ({ex.Message})");
    }
    if (HasFlag("--unmute"))
    {
        endpoint.TrySetMute(deviceId, false);
        endpoint.TryGetState(deviceId, out bool m2, out float v2);
        Console.WriteLine($"→ after unmute: mute={m2}  volume={v2:P0}");
    }
    return 0;
}

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

    // A target name may be a collapsed picker row ("Study" — the pair's group public name,
    // which only exists once every member's TXT record arrives) OR a raw device name.
    // Resolution therefore goes picker-entry first, device second, and the wait loop keeps
    // browsing until every name resolves to an entry whose members are all connectable.
    PickerEntry? Resolve(IReadOnlyList<AirPlayDevice> snap, List<PickerEntry> picker, string n) =>
        picker.FirstOrDefault(e => string.Equals(e.DisplayName, n, StringComparison.OrdinalIgnoreCase))
        ?? picker.FirstOrDefault(e => e.DisplayName.Contains(n, StringComparison.OrdinalIgnoreCase))
        ?? (MatchDevice(snap, n) is { } d
            ? picker.FirstOrDefault(e => e.Members.Any(m => m.DeviceId == d.DeviceId))
            : null);

    IReadOnlyList<AirPlayDevice> snapshot = [];
    List<PickerEntry> collapsed = [];
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
        snapshot = browser.Snapshot();
        collapsed = DevicePicker.Collapse(snapshot);
        bool ready = names.All(n => Resolve(snapshot, collapsed, n) is { } e
            && e.Members.All(m => m.Addresses.Count > 0));
        if (ready) break;
        await Task.Delay(400);
    }

    snapshot = browser.Snapshot();
    collapsed = DevicePicker.Collapse(snapshot);
    var entries = new List<PickerEntry>();
    var missing = new List<string>();
    foreach (string n in names)
    {
        var entry = Resolve(snapshot, collapsed, n);
        if (entry is null)
            missing.Add(n);
        else if (!entries.Any(e => e.Key == entry.Key))
            entries.Add(entry);
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

    var captureSources = new List<ICaptureDiagnostics>();
    IAudioSource MakeSource()
    {
        if (HasFlag("--lr-test")) return new ChannelTestAudioSource();
        if (HasFlag("--tone")) return new SineAudioSource();
        // Same capture path as the app: process-loopback (event-driven, ~10 ms cadence),
        // excluding our own output. NAudio endpoint loopback (~50 ms polled) is the fallback.
        IAudioSource capture;
        try { capture = new ProcessLoopbackAudioSource((uint)Environment.ProcessId); }
        catch (Exception) { capture = new LoopbackAudioSource(); }
        if (capture is ICaptureDiagnostics diag) captureSources.Add(diag);
        return capture;
    }
    Console.WriteLine(HasFlag("--lr-test")
        ? "source: L/R channel test (4 s left 440 Hz ↔ 4 s right 880 Hz)"
        : HasFlag("--tone") ? "source: 440 Hz test tone" : "source: system audio (process loopback)");

    // DACP control endpoint: advertised as iTunes_Ctrl_<DACP-ID> so the receiver can send
    // play/pause/next back. Kept in the CLI for live protocol verification against real devices;
    // the app additionally routes these onto the Windows media session.
    using var dacp = new WinPlay.Core.Raop.DacpServer(
        WinPlay.Core.Raop.DacpIdentity.DacpId, WinPlay.Core.Raop.DacpIdentity.ActiveRemote);
    using var dacpMdns = new WinPlay.Core.Mdns.MdnsClient();
    dacpMdns.Start();
    dacp.Diagnostic += d => Console.WriteLine($"  [dacp] {d}");
    dacp.CommandReceived += (origin, c) => Console.WriteLine($"  [dacp] ◀ REMOTE COMMAND FROM {origin}: {c}");
    dacp.VolumeRequested += (origin, db) => Console.WriteLine($"  [dacp] ◀ REMOTE VOLUME FROM {origin}: {db:F1} dB");
    dacp.Start();
    using var dacpAd = new WinPlay.Core.Mdns.MdnsServiceAdvertiser(dacpMdns, "_dacp._tcp.local",
        dacp.ServiceInstanceName, dacp.Port,
        new Dictionary<string, string> { ["txtvers"] = "1", ["Ver"] = "131077" });
    dacpAd.Diagnostic += d => Console.WriteLine($"  [dacp] {d}");
    dacpAd.Start();

    var credentialStore = new CredentialStore();
    var identityStore = new ReceiverIdentityStore();
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
            bool buffered = HasFlag("--buffered");
            Console.WriteLine($"● {entry.DisplayName} [{entry.Kind}] → "
                + string.Join(" + ", members.Select(member => $"{member.Name}@{member.Address}"))
                + $"  (timing {(members[0].UsePtp ? "PTP, we are grandmaster" : "NTP")}"
                + $"{(buffered ? ", buffered" : ", realtime")})");
            groups.Add(await GroupSession.ConnectAsync(members,
                (memberName, stage) => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] [{memberName}] {stage}"),
                buffered: buffered, identities: identityStore));
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
            Console.WriteLine($"  [debug] ptp delayReqsAnswered={WinPlay.Core.Ptp.PtpMaster.Shared.DelayRequestsAnswered}");
            // Capture health. LATE frames are the real damage (music that arrived after its
            // moment passed → audible cuts); underruns alone can be benign silence (nothing
            // rendering). Attribution decides where to fix, so both are reported.
            for (int i = 0; i < captureSources.Count; i++)
            {
                var (underruns, late, gaps) = captureSources[i].CaptureStats;
                if (underruns > 0 || late > 0 || gaps > 0)
                    Console.WriteLine($"  [capture {i}] LATE(dropped music)={late} (~{late / 44100.0:F2}s), silence-fill={underruns} (~{underruns / 44100.0:F2}s), gap jumps={gaps}");
            }
        }
        Console.WriteLine("time limit reached — tearing down");
        for (int i = 0; i < captureSources.Count; i++)
        {
            var (underruns, late, gaps) = captureSources[i].CaptureStats;
            Console.WriteLine($"  [capture {i}] final: LATE(dropped music)={late} (~{late / 44100.0:F2}s), silence-fill={underruns} (~{underruns / 44100.0:F2}s), gap jumps={gaps}");
        }
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
