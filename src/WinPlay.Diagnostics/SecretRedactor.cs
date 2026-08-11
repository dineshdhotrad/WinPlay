// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace WinPlay.Diagnostics;

/// <summary>
/// Strips secrets from text before it can leave the machine in a bug report (Task H2).
///
/// <para>WinPlay's logs can legitimately contain sensitive material — a trace-level plist dump
/// carries the <c>shk</c> audio session key, RTSP headers carry the <c>Active-Remote</c> token
/// that authorises playback control, and pairing flows carry PINs and Ed25519 key material. A
/// bug report is written to be shared, so this runs over every line of every file the bundle
/// includes.</para>
///
/// <para>The design is deliberately <b>deny-by-default on shape, not just on name</b>: as well
/// as redacting known key names, any long hex or base64 run is replaced, because that is what
/// key material looks like regardless of the label a future log line gives it. Over-redaction
/// is the correct failure mode for a file the user is about to post publicly.</para>
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "«redacted»";

    /// <summary>Applies every rule to one string.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string result = NamedSecret().Replace(text, m => m.Groups["k"].Value + m.Groups["sep"].Value + Placeholder);
        result = LongHex().Replace(result, Placeholder);
        result = LongBase64().Replace(result, Placeholder);
        return result;
    }

    /// <summary>Redacts a whole file line by line (streaming, so a large log never loads at once).</summary>
    public static async Task RedactFileAsync(string sourcePath, TextWriter destination, CancellationToken ct = default)
    {
        using var reader = new StreamReader(sourcePath);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            await destination.WriteLineAsync(Redact(line)).ConfigureAwait(false);
    }

    /// <summary>
    /// Secrets identified by name: <c>key=value</c>, <c>key: value</c>, or <c>"key": "value"</c>.
    /// Covers the session/audio keys, pairing material, control tokens and PINs WinPlay handles.
    /// The value may carry an auth scheme prefix (<c>Authorization: Bearer &lt;token&gt;</c>),
    /// which must be consumed too — otherwise the scheme word absorbs the match and the actual
    /// token survives.
    /// </summary>
    [GeneratedRegex(
        """(?<k>\b(?:shk|shiv|aeskey|aesiv|sessionkey|session_key|sharedsecret|shared_secret|privatekey|private_key|secret|password|passphrase|pin|token|active-remote|activeremote|dacp-id|dacpid|ourprivatekey|receiverpublickey|publickey|pk|apikey|api_key|authorization|auth)\b)(?<sep>"?\s*[:=]\s*"?)(?<v>(?:Bearer|Basic|Digest|Token)\s+)?[^\s,;}"]+""",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecret();

    /// <summary>Any hex run of 32+ characters — the shape of a key, hash, or nonce.</summary>
    [GeneratedRegex(@"\b[0-9a-fA-F]{32,}\b")]
    private static partial Regex LongHex();

    /// <summary>Any base64-looking run of 40+ characters — the shape of an encoded key blob.</summary>
    [GeneratedRegex(@"\b[A-Za-z0-9+/]{40,}={0,2}\b")]
    private static partial Regex LongBase64();
}
