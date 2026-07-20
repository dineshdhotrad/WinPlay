// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinPlay.Core.Net;

namespace WinPlay.Core.Rtsp;

public sealed class RtspRequest
{
    public required string Method { get; init; }
    public required string Uri { get; init; }
    public Dictionary<string, string> Headers { get; } = [];
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
}

public sealed class RtspResponse
{
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];

    public void EnsureSuccess(string context)
    {
        if (StatusCode == 200) return;
        string detail = Body.Length > 0
            ? $" body[{Body.Length}]: {Convert.ToHexString(Body.AsSpan(0, Math.Min(Body.Length, 64)))}"
            : "";
        throw new RtspException($"{context}: {StatusCode} {ReasonPhrase}{detail}") { StatusCode = StatusCode };
    }
}

public sealed class RtspException(string message) : Exception(message)
{
    /// <summary>RTSP status code that caused the failure, 0 when not status-related.</summary>
    public int StatusCode { get; init; }
}

/// <summary>
/// RTSP/1.0 client connection to an AirPlay receiver. Plaintext until pair-setup
/// completes, then every byte in both directions runs through <see cref="ChannelCrypto"/>
/// HAP framing. Requests are strictly sequential (protocol is lockstep).
/// </summary>
public sealed class RtspConnection : IDisposable
{
    private readonly TcpClient _tcp = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly MemoryStream _plain = new();   // decrypted-or-raw incoming bytes
    private readonly MemoryStream _rawPending = new(); // undecrypted partial frames
    private NetworkStream? _stream;
    private ChannelCrypto? _crypto;
    private int _cseq;
    private int _plainReadPos;

    // Stable per-connection sender identity headers.
    private readonly string _dacpId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private readonly string _activeRemote = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString();
    private readonly string _clientInstance = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    public string ClientName { get; init; } = "WinPlay";

    // .NET may report IPv4-mapped IPv6 endpoints (::ffff:a.b.c.d) from dual-mode
    // sockets; normalize to IPv4 so UDP sockets and URI strings stay AF_INET.
    public IPAddress LocalAddress => Normalize(((IPEndPoint)(_tcp.Client.LocalEndPoint
        ?? throw new InvalidOperationException("not connected"))).Address);

    public IPAddress RemoteAddress => Normalize(((IPEndPoint)(_tcp.Client.RemoteEndPoint
        ?? throw new InvalidOperationException("not connected"))).Address);

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    public async Task ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        await _tcp.ConnectAsync(address, port, ct).ConfigureAwait(false);
        _tcp.NoDelay = true;
        _stream = _tcp.GetStream();
    }

    /// <summary>Switches the connection to encrypted framing (call right after pair-setup).</summary>
    public void EnableEncryption(ChannelCrypto crypto) => _crypto = crypto;

    public async Task<RtspResponse> RequestAsync(RtspRequest request, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] wire = BuildWire(request);
            byte[] payload = _crypto is null ? wire : _crypto.Encrypt(wire);
            await _stream!.WriteAsync(payload, ct).ConfigureAwait(false);
            return await ReadResponseAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[] BuildWire(RtspRequest request)
    {
        var sb = new StringBuilder();
        sb.Append(request.Method).Append(' ').Append(request.Uri).Append(" RTSP/1.0\r\n");
        sb.Append("CSeq: ").Append(++_cseq).Append("\r\n");
        sb.Append("User-Agent: AirPlay/550.10\r\n");
        sb.Append("DACP-ID: ").Append(_dacpId).Append("\r\n");
        sb.Append("Active-Remote: ").Append(_activeRemote).Append("\r\n");
        sb.Append("Client-Instance: ").Append(_clientInstance).Append("\r\n");
        sb.Append("X-Apple-Client-Name: ").Append(ClientName).Append("\r\n");
        foreach (var (k, v) in request.Headers)
            sb.Append(k).Append(": ").Append(v).Append("\r\n");
        if (request.Body is { Length: > 0 })
        {
            if (request.ContentType is not null)
                sb.Append("Content-Type: ").Append(request.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(request.Body.Length).Append("\r\n");
        }
        sb.Append("\r\n");

        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        return request.Body is { Length: > 0 } ? [.. head, .. request.Body] : head;
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken ct)
    {
        byte[] readBuf = new byte[16 * 1024];
        while (true)
        {
            if (TryParseResponse(out var response))
                return response;

            int n = await _stream!.ReadAsync(readBuf, ct).ConfigureAwait(false);
            if (n == 0) throw new RtspException("connection closed by receiver");

            if (_crypto is null)
            {
                _plain.Write(readBuf, 0, n);
            }
            else
            {
                _rawPending.Write(readBuf, 0, n);
                int consumed = _crypto.DecryptFrames(
                    _rawPending.GetBuffer().AsSpan(0, (int)_rawPending.Length), _plain);
                CompactStream(_rawPending, consumed);
            }
        }
    }

    private bool TryParseResponse(out RtspResponse response)
    {
        response = null!;
        ReadOnlySpan<byte> buf = _plain.GetBuffer().AsSpan(_plainReadPos, (int)_plain.Length - _plainReadPos);
        int headerEnd = buf.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return false;

        string head = Encoding.ASCII.GetString(buf[..headerEnd]);
        string[] lines = head.Split("\r\n");
        string[] statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length < 2 || !(statusParts[0].StartsWith("RTSP/") || statusParts[0].StartsWith("HTTP/")))
            throw new RtspException($"unexpected response line: '{lines[0]}'");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        int contentLength = headers.TryGetValue("Content-Length", out string? cl) && int.TryParse(cl, out int len)
            ? len : 0;
        int total = headerEnd + 4 + contentLength;
        if (buf.Length < total) return false;

        response = new RtspResponse
        {
            StatusCode = int.TryParse(statusParts[1], out int sc) ? sc : 0,
            ReasonPhrase = statusParts.Length > 2 ? statusParts[2] : "",
            Headers = headers,
            Body = buf.Slice(headerEnd + 4, contentLength).ToArray(),
        };

        _plainReadPos += total;
        if (_plainReadPos == _plain.Length)
        {
            _plain.SetLength(0);
            _plainReadPos = 0;
        }
        return true;
    }

    private static void CompactStream(MemoryStream ms, int consumed)
    {
        if (consumed <= 0) return;
        int remaining = (int)ms.Length - consumed;
        if (remaining > 0)
        {
            byte[] tail = ms.GetBuffer().AsSpan(consumed, remaining).ToArray();
            ms.SetLength(0);
            ms.Write(tail);
        }
        else
        {
            ms.SetLength(0);
        }
    }

    public void Dispose()
    {
        _tcp.Dispose();
        _crypto?.Dispose();
        _lock.Dispose();
    }
}
