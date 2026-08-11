// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WinPlay.Core.Raop;

/// <summary>Transport commands a receiver can send back to the sender over DACP.</summary>
public enum DacpCommand
{
    Unknown,
    PlayPause,
    Play,
    Pause,
    Stop,
    Next,
    Previous,
    ShuffleToggle,
    RepeatToggle,
    VolumeUp,
    VolumeDown,
    MuteToggle,
}

/// <summary>
/// The sender-side DACP (Digital Audio Control Protocol) endpoint — the channel that lets a
/// HomePod or Apple TV control playback on the PC (Task D3).
///
/// <para><b>How the loop closes.</b> Every RTSP request WinPlay sends carries
/// <c>DACP-ID: &lt;id&gt;</c> and <c>Active-Remote: &lt;token&gt;</c> headers. When the user
/// taps pause on the receiver, it resolves the DNS-SD instance
/// <c>iTunes_Ctrl_&lt;id&gt;._dacp._tcp.local</c> (advertised by
/// <see cref="Mdns.MdnsServiceAdvertiser"/>), connects to the address/port in that SRV record,
/// and issues <c>GET /ctrl-int/1/playpause</c> with the matching <c>Active-Remote</c> token.
/// This server answers that request and raises <see cref="CommandReceived"/>, which the app
/// maps onto the Windows media session.</para>
///
/// <para>Implemented over a raw <see cref="TcpListener"/> rather than
/// <see cref="System.Net.HttpListener"/> deliberately: HttpListener needs a <c>netsh
/// http add urlacl</c> registration (or elevation) to accept non-loopback connections on
/// Windows, which would break WinPlay's per-user, no-admin install. The DACP request surface
/// is a single unauthenticated GET line, so a minimal, strictly-bounded HTTP reader is both
/// sufficient and safer than a general-purpose server.</para>
///
/// <para>Requests whose <c>Active-Remote</c> does not match are rejected with 403 — the token
/// is the shared secret that stops other hosts on the LAN from driving playback.</para>
/// </summary>
public sealed class DacpServer : IDisposable
{
    private const int MaxRequestBytes = 8 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>Stable per-app DACP identity, sent as the <c>DACP-ID</c> RTSP header (hex).</summary>
    public string DacpId { get; }

    /// <summary>Shared secret sent as the <c>Active-Remote</c> RTSP header; required on every command.</summary>
    public string ActiveRemote { get; }

    /// <summary>The TCP port this endpoint listens on (advertised in the DNS-SD SRV record).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The DNS-SD instance name receivers resolve: <c>iTunes_Ctrl_&lt;DACP-ID&gt;</c>.</summary>
    public string ServiceInstanceName => $"iTunes_Ctrl_{DacpId}";

    /// <summary>Raised on a validated command from a receiver.</summary>
    public event Action<DacpCommand>? CommandReceived;

    /// <summary>Raised with the requested absolute volume in AirPlay dB when the receiver sets one.</summary>
    public event Action<double>? VolumeRequested;

    public event Action<string>? Diagnostic;

    /// <param name="dacpId">Identity to advertise; a random 16-hex-digit id when omitted.</param>
    /// <param name="activeRemote">Shared secret; a random 31-bit token when omitted.</param>
    public DacpServer(string? dacpId = null, string? activeRemote = null)
    {
        DacpId = dacpId ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        ActiveRemote = activeRemote ?? RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString();
        _listener = new TcpListener(IPAddress.Any, 0); // ephemeral port
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
        Diagnostic?.Invoke($"DACP endpoint listening on port {Port} as {ServiceInstanceName}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            // Each command is a short, independent request; serve it without blocking accepts.
            _ = Task.Run(() => ServeAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                string? head = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
                if (head is null) return;

                var request = DacpRequest.Parse(head);
                if (request is null)
                {
                    await WriteStatusAsync(stream, "400 Bad Request", ct).ConfigureAwait(false);
                    return;
                }

                // Constant-time comparison: the token is a shared secret.
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(request.ActiveRemote ?? ""),
                        Encoding.UTF8.GetBytes(ActiveRemote)))
                {
                    Diagnostic?.Invoke("DACP request rejected: Active-Remote mismatch");
                    await WriteStatusAsync(stream, "403 Forbidden", ct).ConfigureAwait(false);
                    return;
                }

                await WriteStatusAsync(stream, "204 No Content", ct).ConfigureAwait(false);

                if (request.Volume is { } db)
                {
                    Diagnostic?.Invoke($"DACP volume request: {db:F1} dB");
                    VolumeRequested?.Invoke(db);
                }
                else if (request.Command != DacpCommand.Unknown)
                {
                    Diagnostic?.Invoke($"DACP command: {request.Command}");
                    CommandReceived?.Invoke(request.Command);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Receiver hung up mid-request — nothing to recover.
            }
        }
    }

    /// <summary>Reads until the end of the HTTP headers, bounded to <see cref="MaxRequestBytes"/>.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            var span = buffer.AsSpan(0, total);
            if (span.IndexOf("\r\n\r\n"u8) >= 0 || span.IndexOf("\n\n"u8) >= 0)
                return Encoding.UTF8.GetString(span);
        }
        return total > 0 ? Encoding.UTF8.GetString(buffer.AsSpan(0, total)) : null;
    }

    private static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken ct)
    {
        byte[] response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nServer: WinPlay\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        _cts.Dispose();
    }
}

/// <summary>A parsed DACP control request. Parsing is pure so it can be unit-tested exhaustively.</summary>
public sealed record DacpRequest(DacpCommand Command, string? ActiveRemote, double? Volume)
{
    /// <summary>
    /// Parses the head of a DACP HTTP request. Returns <c>null</c> when the request line is not a
    /// well-formed <c>GET /ctrl-int/&lt;n&gt;/&lt;command&gt;</c>.
    /// </summary>
    public static DacpRequest? Parse(string head)
    {
        string[] lines = head.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0) return null;

        string[] requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2 || !requestLine[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
            return null;

        string target = requestLine[1];
        if (!target.StartsWith("/ctrl-int/", StringComparison.OrdinalIgnoreCase))
            return null;

        string? activeRemote = null;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals("Active-Remote", StringComparison.OrdinalIgnoreCase))
            {
                activeRemote = line[(colon + 1)..].Trim();
                break;
            }
        }

        // Split the path from any query string: ".../setproperty?dmcp.device-volume=-14.5".
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        string query = q < 0 ? "" : target[(q + 1)..];

        string verb = path.TrimEnd('/');
        int lastSlash = verb.LastIndexOf('/');
        verb = lastSlash >= 0 ? verb[(lastSlash + 1)..] : verb;

        double? volume = null;
        if (verb.Equals("setproperty", StringComparison.OrdinalIgnoreCase))
            volume = ParseVolume(query);

        return new DacpRequest(MapCommand(verb), activeRemote, volume);
    }

    /// <summary>Extracts <c>dmcp.device-volume</c> (AirPlay dB: 0 = full, −30 = min, −144 = mute).</summary>
    private static double? ParseVolume(string query)
    {
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals("dmcp.device-volume", StringComparison.OrdinalIgnoreCase)) continue;
            if (double.TryParse(pair[(eq + 1)..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double db))
                return db;
        }
        return null;
    }

    private static DacpCommand MapCommand(string verb) => verb.ToLowerInvariant() switch
    {
        "playpause" => DacpCommand.PlayPause,
        "play" or "playresume" => DacpCommand.Play,
        "pause" => DacpCommand.Pause,
        "stop" => DacpCommand.Stop,
        "nextitem" => DacpCommand.Next,
        "previtem" => DacpCommand.Previous,
        "shufflesongs" => DacpCommand.ShuffleToggle,
        "repeatadvance" => DacpCommand.RepeatToggle,
        "volumeup" => DacpCommand.VolumeUp,
        "volumedown" => DacpCommand.VolumeDown,
        "mutetoggle" => DacpCommand.MuteToggle,
        _ => DacpCommand.Unknown,
    };
}
