// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Dns;

namespace WinPlay.Core.Mdns;

/// <summary>
/// Publishes one DNS-SD service from this host (RFC 6763) over the shared mDNS transport — the
/// responder half of WinPlay's bundled mDNS stack, next to <see cref="MdnsClient"/>'s browser.
/// WinPlay uses it to advertise its DACP control endpoint so receivers can send transport
/// commands back; nothing here is DACP-specific.
///
/// <para>Advertising a service means publishing four records:
/// <list type="bullet">
/// <item><b>PTR</b> <c>_dacp._tcp.local</c> → <c>instance._dacp._tcp.local</c> (shared, so no
/// cache-flush bit — other hosts advertise the same service type).</item>
/// <item><b>SRV</b> <c>instance…</c> → port + host, <b>TXT</b> <c>instance…</c>, and
/// <b>A</b> <c>host.local</c> → our IPv4 — all unique to this host, so all carry the
/// cache-flush bit.</item>
/// </list></para>
///
/// <para>On <see cref="Start"/> the records are announced unsolicited (RFC 6762 §8.3), then
/// re-announced on demand whenever a query matches. Queries requesting a unicast reply (the QU
/// bit) are answered directly to the asker; everything else is multicast. On dispose the
/// records are withdrawn with a TTL of 0 (§10.1) so peers evict them immediately rather than
/// caching a dead endpoint for 75 minutes.</para>
/// </summary>
public sealed class MdnsServiceAdvertiser : IDisposable
{
    private readonly IMdnsTransport _transport;
    private readonly string _serviceType;      // e.g. "_dacp._tcp.local"
    private readonly string _instanceName;     // e.g. "iTunes_Ctrl_ABCD1234"
    private readonly string _hostName;         // e.g. "winplay-abcd1234.local"
    private readonly int _port;
    private readonly IReadOnlyDictionary<string, string> _txt;
    private readonly string _fullInstance;     // "instance._dacp._tcp.local"
    // Safely in the past (TickCount64 is ms since boot, ≥ 0); long.MinValue would wrap on subtraction.
    private long _lastMulticastAnswerMs = -10_000;
    private bool _started;
    private bool _disposed;

    public event Action<string>? Diagnostic;

    public MdnsServiceAdvertiser(IMdnsTransport transport, string serviceType, string instanceName,
        int port, IReadOnlyDictionary<string, string>? txt = null, string? hostName = null)
    {
        _transport = transport;
        _serviceType = serviceType.TrimEnd('.');
        _instanceName = instanceName;
        _port = port;
        _txt = txt ?? new Dictionary<string, string> { ["txtvers"] = "1" };
        // The instance name is one label and arrives raw, so it is escaped before being joined
        // into a dotted name — otherwise a dot in it would be encoded as a label boundary and the
        // service would be published under a name that is not the one it answers to.
        _fullInstance = $"{DnsName.Escape(_instanceName)}.{_serviceType}";
        _hostName = hostName ?? $"winplay-{Environment.ProcessId:x}.local";
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _transport.MessageReceived += OnMessage;
        Announce();
        Diagnostic?.Invoke($"advertising {_fullInstance} → {_hostName}:{_port}");
    }

    /// <summary>Unsolicited announcement of the full record set (RFC 6762 §8.3).</summary>
    public void Announce() => _transport.Send(DnsRecordWriter.BuildResponse(BuildRecords(DnsRecordWriter.SharedTtlSeconds,
        DnsRecordWriter.UniqueTtlSeconds)));

    private void OnMessage(DnsMessage message, IPEndPoint from)
    {
        if (message.IsResponse || message.Questions.Count == 0) return;

        bool matched = false;
        bool unicast = false;
        foreach (var question in message.Questions)
        {
            if (!Matches(question)) continue;
            matched = true;
            unicast |= question.UnicastResponse;
        }
        if (!matched) return;

        // Known-answer suppression (RFC 6762 §7.1): if the asker's query already carries our
        // PTR with at least half its TTL remaining, it knows about us — stay silent. This
        // matters on Wi-Fi, where every multicast burns airtime the audio streams need.
        if (HasFreshKnownAnswer(message)) return;

        // Multicast rate limiting (§6): the same record set at most once per second. Unicast
        // answers are direct to one host and exempt.
        if (!unicast)
        {
            long now = Environment.TickCount64;
            if (now - _lastMulticastAnswerMs < 1000) return;
            _lastMulticastAnswerMs = now;
        }

        byte[] response = DnsRecordWriter.BuildResponse(
            BuildRecords(DnsRecordWriter.SharedTtlSeconds, DnsRecordWriter.UniqueTtlSeconds));
        _transport.Send(response, unicast ? from : null);
    }

    /// <summary>True when the query's known-answer section already names our instance freshly.</summary>
    internal bool HasFreshKnownAnswer(DnsMessage message) =>
        message.Answers.Any(a => a.Type == DnsType.Ptr
            && Equal(a.Name, _serviceType)
            && a.Data is PtrData ptr && Equal(ptr.Target, _fullInstance)
            && a.Ttl >= DnsRecordWriter.SharedTtlSeconds / 2);

    /// <summary>True when a question asks for any record this advertiser owns.</summary>
    internal bool Matches(DnsQuestion question)
    {
        bool isServiceType = Equal(question.Name, _serviceType);
        bool isInstance = Equal(question.Name, _fullInstance);
        bool isHost = Equal(question.Name, _hostName);

        return question.Type switch
        {
            DnsType.Ptr => isServiceType || isInstance,
            DnsType.Srv or DnsType.Txt => isInstance,
            DnsType.A => isHost,
            DnsType.Any => isServiceType || isInstance || isHost,
            _ => false,
        };
    }

    private static bool Equal(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The complete record set. TTL 0 withdraws the service (RFC 6762 §10.1).</summary>
    internal List<DnsResourceRecord> BuildRecords(uint sharedTtl, uint uniqueTtl)
    {
        var records = new List<DnsResourceRecord>
        {
            new()
            {
                Name = _serviceType,
                Type = DnsType.Ptr,
                Class = DnsRecordWriter.ClassIn, // shared record: never cache-flush
                Ttl = sharedTtl,
                Data = new PtrData(_fullInstance),
            },
            new()
            {
                Name = _fullInstance,
                Type = DnsType.Srv,
                Class = DnsRecordWriter.ClassInFlush,
                Ttl = uniqueTtl,
                Data = new SrvData(0, 0, (ushort)_port, _hostName),
            },
            new()
            {
                Name = _fullInstance,
                Type = DnsType.Txt,
                Class = DnsRecordWriter.ClassInFlush,
                Ttl = uniqueTtl,
                Data = new TxtData(_txt),
            },
        };

        foreach (var address in _transport.LocalAddresses)
        {
            records.Add(new DnsResourceRecord
            {
                Name = _hostName,
                Type = DnsType.A,
                Class = DnsRecordWriter.ClassInFlush,
                Ttl = uniqueTtl,
                Data = address,
            });
        }
        return records;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_started) return;
        _transport.MessageReceived -= OnMessage;
        try
        {
            // Goodbye packet: TTL 0 evicts our records from every peer's cache immediately.
            _transport.Send(DnsRecordWriter.BuildResponse(BuildRecords(0, 0)));
        }
        catch (Exception) { /* transport already gone */ }
    }
}
