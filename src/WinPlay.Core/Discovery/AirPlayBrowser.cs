// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
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
        public DateTime LastSeenUtc;
        public DateTime LastProbeUtc;
        public int ProbeCount;
    }

    private readonly bool _ownsMdns;
    private readonly object _lock = new();
    private readonly Dictionary<string, ServiceEntry> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IPAddress>> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private IMdnsTransport? _mdns;
    private Task? _queryLoop;

    /// <summary>Fired with a full snapshot whenever the merged device list changes.</summary>
    public event Action<IReadOnlyList<AirPlayDevice>>? DevicesChanged;

    public AirPlayBrowser(IMdnsTransport? transport = null)
    {
        _ownsMdns = transport is null;
        _mdns = transport;
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
        // 0s, 1s, 2s, 4s, ... capped at 16s — standard mDNS continuous-browse backoff.
        var delay = TimeSpan.FromSeconds(1);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
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

            try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (delay < TimeSpan.FromSeconds(16)) delay += delay;
        }
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
                bool incomplete = e.Host is null || e.Txt is null
                    || (e.Host is not null && !_hostAddresses.ContainsKey(e.Host));
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
                        changed |= UpsertInstanceLocked(ptr.Target, svcFromPtr, rr.Ttl);
                        break;

                    case DnsType.Srv when rr.Data is SrvData srv
                            && ServiceTypeOf(rr.Name) is { } svcOfSrv:
                    {
                        var e = GetOrAddLocked(rr.Name, svcOfSrv);
                        if (rr.Ttl == 0) { changed |= _services.Remove(rr.Name); break; }
                        if (e.Host != srv.Target || e.Port != srv.Port) changed = true;
                        e.Host = srv.Target;
                        e.Port = srv.Port;
                        e.LastSeenUtc = DateTime.UtcNow;
                        break;
                    }

                    case DnsType.Txt when rr.Data is TxtData txt
                            && ServiceTypeOf(rr.Name) is { } svcOfTxt:
                    {
                        var e = GetOrAddLocked(rr.Name, svcOfTxt);
                        if (rr.Ttl == 0) { changed |= _services.Remove(rr.Name); break; }
                        if (e.Txt is null || !TxtEquals(e.Txt, txt.Pairs)) changed = true;
                        e.Txt = txt.Pairs;
                        e.LastSeenUtc = DateTime.UtcNow;
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
            DevicesChanged?.Invoke(Snapshot());
    }

    private static string? ServiceTypeOf(string name)
    {
        if (name.EndsWith(AirPlayService, StringComparison.OrdinalIgnoreCase)) return AirPlayService;
        if (name.EndsWith(RaopService, StringComparison.OrdinalIgnoreCase)) return RaopService;
        return null;
    }

    private bool UpsertInstanceLocked(string instanceName, string serviceType, uint ttl)
    {
        if (ttl == 0)
            return _services.Remove(instanceName);
        if (_services.ContainsKey(instanceName))
        {
            _services[instanceName].LastSeenUtc = DateTime.UtcNow;
            return false;
        }
        _services[instanceName] = new ServiceEntry
        {
            InstanceName = instanceName,
            ServiceType = serviceType,
            LastSeenUtc = DateTime.UtcNow,
        };
        return true;
    }

    private ServiceEntry GetOrAddLocked(string instanceName, string serviceType)
    {
        if (!_services.TryGetValue(instanceName, out var e))
            _services[instanceName] = e = new ServiceEntry
            {
                InstanceName = instanceName,
                ServiceType = serviceType,
                LastSeenUtc = DateTime.UtcNow,
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
            string name = ap is not null
                ? StripSuffix(ap.InstanceName, AirPlayService)
                : StripRaopPrefix(StripSuffix(raop!.InstanceName, RaopService));

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
