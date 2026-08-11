// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using WinPlay.Core.Dns;
using WinPlay.Core.Mdns;

namespace WinPlay.Core.Discovery;

/// <summary>
/// Browses `_airplay._tcp.local` and `_raop._tcp.local`, correlates PTR/SRV/TXT/A/AAAA
/// records into <see cref="AirPlayDevice"/>s, and raises <see cref="DevicesChanged"/>
/// snapshots. RAOP instances ("MAC@Name") and AirPlay instances are merged by device ID.
/// </summary>
public sealed class AirPlayBrowser : IDisposable
{
    public const string AirPlayService = "_airplay._tcp.local";
    public const string RaopService = "_raop._tcp.local";

    private sealed class ServiceEntry
    {
        public required string InstanceName;   // full, e.g. "Living Room._airplay._tcp.local"
        public required string ServiceType;
        public string? Host;
        public int Port;
        public IReadOnlyDictionary<string, string>? Txt;
        public DateTime LastProbeUtc;
        public int ProbeCount;

        /// <summary>Query round in which a record for this instance last arrived.</summary>
        public long LastSeenRound = -1;

        /// <summary>
        /// Rounds of silence tolerated before this instance is dropped, derived from the shortest
        /// TTL its records advertised. Seeded at the maximum so the first record can only lower it.
        /// </summary>
        public int RoundBudget = MaxMissedRounds;

        public bool IsExpired(long round) => round - LastSeenRound > RoundBudget;
    }

    // ---------------------------------------------------------------- cache expiry
    //
    // A receiver only sends a TTL-0 goodbye when it shuts down in an orderly way. Pull its power,
    // drop it off Wi-Fi, or carry it out of range and it simply stops answering. Without an expiry
    // sweep those entries live forever: the picker keeps offering a HomePod that was unplugged
    // hours ago, and the only way the user finds out is a connection that fails.
    //
    // Liveness is counted in QUERY ROUNDS, not elapsed time. Time is the wrong clock for this:
    // wall-clock jumps when the machine suspends or an NTP step lands, and a sweep run against it
    // would declare every device gone the instant the lid opens — emptying the picker for the
    // second or two before answers come back. Rounds advance only when a query is actually sent,
    // so a suspended process ages nothing, a stalled query loop expires nothing, and the counter is
    // monotonic by construction. There is no clock to get wrong.
    //
    // Silence is only evidence of absence because WinPlay deliberately sends no known-answer
    // records: RFC 6762 §7.1 suppression never applies, so every reachable responder answers every
    // round. That property is what this sweep rests on — see QueryLoopAsync.

    /// <summary>
    /// Rounds of silence that mean "gone" — about 2.4 minutes at the steady-state cadence.
    ///
    /// <para>Measured, not guessed. <c>LiveResponderLivenessProbe</c> watched 14 real instances
    /// (HomePods, a stereo pair, an Apple TV, a Mac) over 12 rounds on a domestic Wi-Fi network:
    /// every reachable one answered, and the longest run of consecutive missed answers by any of
    /// them was 2 rounds. This budget is 4× that worst case.</para>
    ///
    /// <para>The margin is deliberately generous because the two failure modes are not equally
    /// bad. Too small and a speaker that is sitting right there vanishes from the picker mid-use,
    /// which is a bug the user watches happen. Too large and an unplugged speaker lingers a while
    /// longer before disappearing on its own, which almost nobody notices. When in doubt, wait.</para>
    /// </summary>
    internal const int MaxMissedRounds = 8;

    /// <summary>
    /// Floor on the tolerance. Below two rounds a single dropped multicast packet would evict a
    /// device that is sitting right there, and its row would flicker out and back on every sweep.
    /// </summary>
    private const int MinMissedRounds = 2;

    /// <summary>Steady-state gap between browse queries; the cap in <see cref="QueryLoopAsync"/>.</summary>
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(16);

    /// <summary>
    /// Converts an advertised record TTL into a round budget, which is how RFC 6762 §10 is honoured
    /// here without introducing a second, weaker expiry mechanism. AirPlay responders advertise
    /// 4500 s on PTR and 120 s on SRV/TXT — both far beyond the round budget, so in practice this
    /// always clamps to <see cref="MaxMissedRounds"/>. A responder that asked for a genuinely short
    /// lifetime gets a proportionally shorter budget instead of being over-cached.
    /// </summary>
    private static int RoundBudgetFor(uint ttlSeconds)
    {
        double rounds = Math.Ceiling(Math.Min(ttlSeconds, 86400) / QueryInterval.TotalSeconds);
        return (int)Math.Clamp(rounds, MinMissedRounds, MaxMissedRounds);
    }

    private readonly bool _ownsMdns;
    private readonly object _lock = new();
    private readonly Dictionary<string, ServiceEntry> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IPAddress>> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Serialises "build a snapshot" with "hand it to subscribers".
    ///
    /// <para>Two threads publish: the mDNS receive thread on every incoming record, and the
    /// query-loop thread after each expiry sweep. Without this they could each build a snapshot
    /// and then race to deliver it, so subscribers could receive an OLDER snapshot last — a device
    /// that was just expired reappears, or one that just arrived vanishes, until some later update
    /// happens to correct it. Held only across compute-and-deliver, and never taken while holding
    /// <c>_lock</c>, so the two cannot deadlock against each other.</para>
    /// </summary>
    private readonly object _publishLock = new();
    private readonly TimeProvider _time;
    private IMdnsTransport? _mdns;
    private Task? _queryLoop;

    /// <summary>
    /// Browse rounds sent so far — the clock the expiry sweep runs on. Read from the receive
    /// thread and advanced by the query loop, so it is touched through <see cref="Interlocked"/>
    /// rather than under <c>_lock</c>: taking the entry lock to stamp a round would put the
    /// query loop behind every inbound packet.
    /// </summary>
    private long _round;

    /// <summary>Fired with a full snapshot whenever the merged device list changes.</summary>
    public event Action<IReadOnlyList<AirPlayDevice>>? DevicesChanged;

    /// <summary>
    /// Non-fatal trouble during browsing — a round that failed and was skipped. Discovery
    /// degrading quietly is the worst outcome here, so it is reported rather than swallowed.
    /// </summary>
    public event Action<string>? Diagnostic;

    /// <summary>
    /// </summary>
    /// <param name="transport">mDNS transport to browse on; one is created and owned when null.</param>
    /// <param name="time">
    /// Clock driving the browse cadence. Defaults to <see cref="TimeProvider.System"/>. Injectable
    /// so the expiry sweep — whose failure mode is evicting a receiver that is sitting right there
    /// — can be tested at exact round boundaries instead of by waiting out real minutes and hoping
    /// the timing lands the same way twice.
    /// </param>
    public AirPlayBrowser(IMdnsTransport? transport = null, TimeProvider? time = null)
    {
        _ownsMdns = transport is null;
        _mdns = transport;
        _time = time ?? TimeProvider.System;
        if (_mdns is not null)
            _mdns.MessageReceived += OnMessage;
    }

    public void Start()
    {
        if (_mdns is null)
        {
            _mdns = new MdnsClient();
            _mdns.MessageReceived += OnMessage;
        }
        _mdns.Start();
        _queryLoop ??= Task.Run(QueryLoopAsync);
    }

    public IReadOnlyList<AirPlayDevice> Snapshot()
    {
        lock (_lock)
            return BuildDevicesLocked();
    }

    private async Task QueryLoopAsync()
    {
        // Captured once, deliberately. Dispose() cancels and then DISPOSES the source, and the
        // Token property throws ObjectDisposedException once it has — so reading _cts.Token on
        // each pass meant a Dispose landing mid-round faulted this task with an exception nobody
        // awaited, and the runtime discarded it. RestartDiscovery disposes the old browser on
        // every wake and every network change, so that was routine, not a shutdown corner case.
        // A token captured while the source was alive stays usable: it is already cancelled by
        // the time the source goes, so Task.Delay completes without touching it.
        CancellationToken ct = _cts.Token;

        // 0s, 1s, 2s, 4s, ... capped at QueryInterval — standard mDNS continuous-browse backoff.
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // No known-answer records are attached. RFC 6762 §7.1 lets a querier suppress
                // answers it already holds, and a browse that did so would go quiet — at which
                // point silence would mean nothing and the expiry sweep below would have no
                // ground to stand on. The cost is one small multicast packet per receiver per
                // round; what it buys is a picker that can tell present from absent.
                _mdns!.Query(
                [
                    (AirPlayService, DnsType.Ptr, false),
                    (RaopService, DnsType.Ptr, false),
                ]);
                ProbeIncompleteEntries();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // This loop IS discovery. If it ever unwinds, the device list silently freezes for
                // the rest of the session and every symptom the user sees — an empty picker, a
                // receiver that never appears — points somewhere else entirely. Its inputs come
                // off the network from devices we do not control, so the only safe contract is
                // that no single round can end it. Report and take the next round.
                Diagnostic?.Invoke($"browse round failed: {ex.Message}");
            }

            try { await Task.Delay(delay, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (delay < QueryInterval) delay += delay;

            // Sweep AFTER the wait, so answers to the round just sent have had the full interval
            // to arrive. Advancing the round here — never inside the send — means a round only
            // counts once a receiver has actually had its chance to reply to it.
            Interlocked.Increment(ref _round);
            ExpireStaleEntries();
        }
    }

    /// <summary>
    /// Drops instances that have missed their round budget, along with any host addresses left
    /// with nothing pointing at them. Raises <see cref="DevicesChanged"/> only when something
    /// actually went, so a steady LAN produces no snapshot churn at all.
    /// </summary>
    private void ExpireStaleEntries()
    {
        long round = Interlocked.Read(ref _round);
        lock (_lock)
        {
            List<string>? dead = null;
            foreach (var e in _services.Values)
                if (e.IsExpired(round))
                    (dead ??= []).Add(e.InstanceName);

            if (dead is null) return;
            foreach (string name in dead) _services.Remove(name);
            PruneOrphanedHostsLocked();
        }

        PublishSnapshot();
    }

    /// <summary>
    /// Some responders answer a PTR query without SRV/TXT/A additionals; chase the
    /// missing records with direct instance queries (bounded per instance).
    /// </summary>
    private void ProbeIncompleteEntries()
    {
        List<(string, DnsType, bool)> questions = [];
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var e in _services.Values)
            {
                // "Complete" has to mean CONNECTABLE, not merely "some address arrived". Every
                // connect path in WinPlay dials IPv4, so an entry holding only an AAAA is just as
                // unusable as one holding no address at all — and treating it as finished meant
                // the A record was never chased. The device then sat in the picker looking normal
                // and failed every attempt with "has no IPv4 address", permanently, because
                // nothing was ever going to ask for the record that would have fixed it.
                bool hasUsableAddress = e.Host is not null
                    && _hostAddresses.TryGetValue(e.Host, out var known)
                    && known.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
                bool incomplete = e.Host is null || e.Txt is null || !hasUsableAddress;
                if (!incomplete || e.ProbeCount >= 5 || (now - e.LastProbeUtc) < TimeSpan.FromSeconds(1))
                    continue;
                e.LastProbeUtc = now;
                e.ProbeCount++;
                questions.Add((e.InstanceName, DnsType.Srv, false));
                questions.Add((e.InstanceName, DnsType.Txt, false));
                if (e.Host is not null)
                    questions.Add((e.Host, DnsType.A, false));
            }
        }
        if (questions.Count > 0)
            _mdns!.Query(questions);
    }

    private void OnMessage(DnsMessage msg, IPEndPoint from)
    {
        if (!msg.IsResponse) return;

        bool changed = false;
        lock (_lock)
        {
            foreach (var rr in msg.AllRecords)
            {
                switch (rr.Type)
                {
                    case DnsType.Ptr when rr.Data is PtrData ptr
                            && ServiceTypeOf(rr.Name) is { } svcFromPtr:
                    {
                        bool removed = UpsertInstanceLocked(ptr.Target, svcFromPtr, rr.Ttl);
                        if (removed && rr.Ttl == 0) PruneOrphanedHostsLocked();
                        changed |= removed;
                        break;
                    }

                    case DnsType.Srv when rr.Data is SrvData srv
                            && ServiceTypeOf(rr.Name) is { } svcOfSrv:
                    {
                        var e = GetOrAddLocked(rr.Name, svcOfSrv);
                        if (rr.Ttl == 0)
                        {
                            if (_services.Remove(rr.Name)) { PruneOrphanedHostsLocked(); changed = true; }
                            break;
                        }
                        if (e.Host != srv.Target || e.Port != srv.Port) changed = true;
                        e.Host = srv.Target;
                        e.Port = srv.Port;
                        Refresh(e, rr.Ttl);
                        break;
                    }

                    case DnsType.Txt when rr.Data is TxtData txt
                            && ServiceTypeOf(rr.Name) is { } svcOfTxt:
                    {
                        var e = GetOrAddLocked(rr.Name, svcOfTxt);
                        if (rr.Ttl == 0)
                        {
                            if (_services.Remove(rr.Name)) { PruneOrphanedHostsLocked(); changed = true; }
                            break;
                        }
                        if (e.Txt is null || !TxtEquals(e.Txt, txt.Pairs)) changed = true;
                        e.Txt = txt.Pairs;
                        Refresh(e, rr.Ttl);
                        break;
                    }

                    case DnsType.A or DnsType.Aaaa when rr.Data is IPAddress ip:
                    {
                        if (!_hostAddresses.TryGetValue(rr.Name, out var list))
                            _hostAddresses[rr.Name] = list = [];
                        if (rr.CacheFlush)
                        {
                            // Cache-flush: replace addresses of the same family.
                            int removed = list.RemoveAll(a => a.AddressFamily == ip.AddressFamily && !a.Equals(ip));
                            changed |= removed > 0;
                        }
                        if (!list.Contains(ip)) { list.Add(ip); changed = true; }
                        break;
                    }
                }
            }
        }

        if (changed)
            PublishSnapshot();
    }

    /// <summary>
    /// Releases address records no surviving instance points at. Addresses are keyed by hostname
    /// and shared between an instance's _airplay and _raop entries, so a host can only go once
    /// BOTH are gone — which is why this is a sweep over what remains rather than a delete
    /// alongside each removal.
    ///
    /// <para>Called from every path that removes an instance, including the TTL-0 goodbyes handled
    /// in <see cref="OnMessage"/>. Hanging it off the expiry sweep alone meant a receiver that
    /// always shut down cleanly — announcing its goodbye exactly as it should — leaked its
    /// addresses for the life of the process, because it never went silent long enough to be
    /// swept. The tidiest devices on the network were the ones that leaked.</para>
    /// </summary>
    private void PruneOrphanedHostsLocked()
    {
        if (_hostAddresses.Count == 0) return;
        var live = new HashSet<string>(
            _services.Values.Where(s => s.Host is not null).Select(s => s.Host!),
            StringComparer.OrdinalIgnoreCase);
        foreach (string host in _hostAddresses.Keys.Where(h => !live.Contains(h)).ToList())
            _hostAddresses.Remove(host);
    }

    /// <summary>Builds a device snapshot and delivers it, one publisher at a time.</summary>
    private void PublishSnapshot()
    {
        lock (_publishLock)
            DevicesChanged?.Invoke(Snapshot());
    }

    private static string? ServiceTypeOf(string name)
    {
        if (name.EndsWith(AirPlayService, StringComparison.OrdinalIgnoreCase)) return AirPlayService;
        if (name.EndsWith(RaopService, StringComparison.OrdinalIgnoreCase)) return RaopService;
        return null;
    }

    /// <summary>
    /// Marks an instance as answered right now and takes the shortest lifetime its records have
    /// asked for. The shortest wins because an instance is only usable while ALL of its records
    /// hold: a PTR good for 4500 s says nothing about an SRV whose host and port expire in 120 s.
    /// </summary>
    /// <summary>
    /// Marks an instance as answered in the current round and takes the strictest budget its
    /// records have asked for. Strictest wins because an instance is only usable while ALL of its
    /// records hold: a PTR good for 4500 s says nothing about an SRV whose host and port are the
    /// part that expires.
    /// </summary>
    private void Refresh(ServiceEntry e, uint ttl)
    {
        int budget = RoundBudgetFor(ttl);
        if (budget < e.RoundBudget) e.RoundBudget = budget;  // seeded at the max, so this also takes the first
        e.LastSeenRound = Interlocked.Read(ref _round);
    }

    private bool UpsertInstanceLocked(string instanceName, string serviceType, uint ttl)
    {
        if (ttl == 0)
            return _services.Remove(instanceName);
        if (_services.TryGetValue(instanceName, out var existing))
        {
            Refresh(existing, ttl);
            return false;
        }
        var entry = new ServiceEntry { InstanceName = instanceName, ServiceType = serviceType };
        Refresh(entry, ttl);
        _services[instanceName] = entry;
        return true;
    }

    /// <summary>
    /// Finds or creates an instance entry. Deliberately does NOT stamp <c>LastSeenUtc</c>: only
    /// <see cref="Refresh"/> does that, so "when did we last hear from this instance" always means
    /// a record actually arrived for it. Every caller refreshes immediately afterwards.
    /// </summary>
    private ServiceEntry GetOrAddLocked(string instanceName, string serviceType)
    {
        if (!_services.TryGetValue(instanceName, out var e))
            _services[instanceName] = e = new ServiceEntry
            {
                InstanceName = instanceName,
                ServiceType = serviceType,
            };
        return e;
    }

    private static bool TxtEquals(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out string? v) && v == kv.Value);

    private List<AirPlayDevice> BuildDevicesLocked()
    {
        // Group service entries by normalized device ID.
        var byId = new Dictionary<string, (ServiceEntry? airplay, ServiceEntry? raop)>();

        foreach (var e in _services.Values)
        {
            string? id = DeviceIdOf(e);
            if (id is null) continue;
            byId.TryGetValue(id, out var pair);
            if (e.ServiceType == AirPlayService) pair.airplay = e;
            else pair.raop = e;
            byId[id] = pair;
        }

        List<AirPlayDevice> devices = [];
        foreach (var (id, (ap, raop)) in byId)
        {
            var apTxt = ap?.Txt ?? new Dictionary<string, string>();
            var raopTxt = raop?.Txt ?? new Dictionary<string, string>();
            // Unescape for display: the cached instance name is in DNS presentation form, so a
            // speaker the user called "Mr. Roboto" is held as "Mr\. Roboto" and must not be shown
            // that way.
            string name = DnsName.Unescape(ap is not null
                ? StripSuffix(ap.InstanceName, AirPlayService)
                : StripRaopPrefix(StripSuffix(raop!.InstanceName, RaopService)));

            string? host = ap?.Host ?? raop?.Host;
            List<IPAddress> addresses = host is not null && _hostAddresses.TryGetValue(host, out var addrs)
                ? [.. addrs] : [];

            devices.Add(new AirPlayDevice
            {
                DeviceId = id,
                Name = name,
                Model = Get(apTxt, "model") ?? Get(raopTxt, "am"),
                RawFeatures = AirPlayFeaturesExtensions.ParseFeatures(Get(apTxt, "features") ?? Get(raopTxt, "ft")),
                StatusFlags = AirPlayFeaturesExtensions.ParseFeatures(Get(apTxt, "flags") ?? Get(raopTxt, "sf")),
                Hostname = host,
                Addresses = addresses,
                AirPlayPort = ap is { Host: not null } ? ap.Port : null,
                RaopPort = raop is { Host: not null } ? raop.Port : null,
                GroupId = Get(apTxt, "gid"),
                GroupPublicName = Get(apTxt, "gpn"),
                IsGroupLeader = IsTruthy(Get(apTxt, "igl")),
                GroupContainsLeader = IsTruthy(Get(apTxt, "gcgl")),
                ParentGroupId = Get(apTxt, "pgid"),
                ParentGroupContainsLeader = IsTruthy(Get(apTxt, "pgcgl")),
                TightSyncId = Get(apTxt, "tsid"),
                PublicKey = Get(apTxt, "pk"),
                PairingIdentity = Get(apTxt, "pi"),
                SourceVersion = Get(apTxt, "srcvers") ?? Get(raopTxt, "vs"),
                AirPlayTxt = apTxt,
                RaopTxt = raopTxt,
            });
        }

        devices.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return devices;
    }

    private static string? DeviceIdOf(ServiceEntry e)
    {
        if (e.ServiceType == AirPlayService)
        {
            string? raw = e.Txt is not null && e.Txt.TryGetValue("deviceid", out string? v) ? v : null;
            return raw is null ? null : AirPlayDevice.NormalizeDeviceId(raw);
        }
        // RAOP instance name: "AABBCCDDEEFF@Living Room._raop._tcp.local"
        string instance = StripSuffix(e.InstanceName, RaopService);
        int at = instance.IndexOf('@');
        return at > 0 ? AirPlayDevice.NormalizeDeviceId(instance[..at]) : null;
    }

    private static string StripSuffix(string instanceName, string serviceType)
    {
        string s = instanceName;
        if (s.EndsWith(serviceType, StringComparison.OrdinalIgnoreCase))
            s = s[..^serviceType.Length].TrimEnd('.');
        return s;
    }

    private static string StripRaopPrefix(string instance)
    {
        int at = instance.IndexOf('@');
        return at >= 0 ? instance[(at + 1)..] : instance;
    }

    private static string? Get(IReadOnlyDictionary<string, string> txt, string key) =>
        txt.TryGetValue(key, out string? v) && v.Length > 0 ? v : null;

    private static bool IsTruthy(string? v) => v is "1" or "true" or "yes";

    public void Dispose()
    {
        _cts.Cancel();
        if (_mdns is not null)
        {
            _mdns.MessageReceived -= OnMessage;
            if (_ownsMdns) _mdns.Dispose();
        }
        _cts.Dispose();
    }
}
