// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinPlay.Core.Hap;

/// <summary>Outcome of checking a receiver's advertised identity against what WinPlay pinned.</summary>
public enum IdentityTrust
{
    /// <summary>No pin exists yet — this connection establishes one (trust on first use).</summary>
    FirstUse,

    /// <summary>The advertised identity matches the pinned one.</summary>
    Trusted,

    /// <summary>The advertised identity differs from the pin — a different device (or a
    /// spoof) is answering to this device id. Streaming must not proceed silently.</summary>
    Mismatch,

    /// <summary>The receiver advertises no public key, so there is nothing to pin.</summary>
    Unverifiable,
}

/// <summary>The result of a trust check, including the keys involved so callers can explain it.</summary>
public sealed record IdentityCheck(IdentityTrust Trust, string? PinnedKey, string? PresentedKey)
{
    /// <summary>True when it is safe to proceed without asking the user.</summary>
    public bool IsAcceptable => Trust is IdentityTrust.Trusted or IdentityTrust.FirstUse or IdentityTrust.Unverifiable;
}

/// <summary>
/// Pins each receiver's long-term Ed25519 identity across sessions, closing the gap
/// SECURITY.md documented.
///
/// <para><b>Two tiers of assurance, deliberately not conflated.</b>
/// <list type="number">
/// <item><b>PIN-paired receivers (Apple TV).</b> Already cryptographically strong without this
/// store: <see cref="HapVerifiedPairing.PairVerifyAsync"/> requires the receiver to sign a
/// challenge with the private key established at pair-setup, so an impostor cannot complete the
/// handshake even if it clones every advertised field.</item>
/// <item><b>Transient-paired receivers (HomePod, PIN 3939).</b> Transient pairing establishes no
/// long-term identity, so there is nothing to sign against — historically WinPlay would talk to
/// anything claiming to be your HomePod. This store pins the Ed25519 public key the receiver
/// advertises (the <c>pk</c> TXT record) on first use and refuses to stream when it later
/// changes. That reliably detects the realistic LAN attack — a substituted or spoofed device
/// standing up under a known name/id — and it is honestly weaker than tier 1: it proves the
/// identity is *unchanged*, not that the peer *holds* the private key. Receivers that support
/// PIN pairing should be paired for full proof.</item>
/// </list></para>
///
/// <para>The file is DPAPI-protected for the current user. The pins are public keys, not
/// secrets, but an attacker who could silently rewrite them would defeat pinning entirely, so
/// integrity is what the protection buys.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReceiverIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public ReceiverIdentityStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinPlay", "receivers.dat");
    }

    /// <param name="PublicKey">Advertised Ed25519 identity (hex, normalised uppercase).</param>
    /// <param name="Name">Last known display name — for a human-readable warning.</param>
    private sealed record PinnedIdentity(string PublicKey, string Name, string FirstSeenUtc, string LastSeenUtc);

    /// <summary>
    /// Checks an advertised identity against the pin WITHOUT modifying the store, so a caller can
    /// decide before committing. <paramref name="publicKey"/> is the receiver's <c>pk</c>.
    /// </summary>
    public IdentityCheck Check(string deviceId, string? publicKey)
    {
        string normalized = NormalizeKey(publicKey);
        if (normalized.Length == 0) return new IdentityCheck(IdentityTrust.Unverifiable, null, null);

        lock (_lock)
        {
            var pins = ReadAll();
            if (!pins.TryGetValue(NormalizeId(deviceId), out var pin))
                return new IdentityCheck(IdentityTrust.FirstUse, null, normalized);

            // Fixed-time comparison: identity checks should not leak the pinned key by timing.
            bool same = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(pin.PublicKey),
                System.Text.Encoding.ASCII.GetBytes(normalized));
            return new IdentityCheck(same ? IdentityTrust.Trusted : IdentityTrust.Mismatch,
                pin.PublicKey, normalized);
        }
    }

    /// <summary>
    /// Records (or refreshes) the pin for a receiver. Call only after a connection has been
    /// accepted, so a rejected impostor never overwrites a good pin.
    /// </summary>
    public void Pin(string deviceId, string? publicKey, string? name = null)
    {
        string normalized = NormalizeKey(publicKey);
        if (normalized.Length == 0) return; // nothing to pin

        lock (_lock)
        {
            var pins = ReadAll();
            string id = NormalizeId(deviceId);
            string nowUtc = DateTime.UtcNow.ToString("O");
            string firstSeen = pins.TryGetValue(id, out var existing) && existing.PublicKey == normalized
                ? existing.FirstSeenUtc
                : nowUtc;
            pins[id] = new PinnedIdentity(normalized, name ?? existing?.Name ?? "", firstSeen, nowUtc);
            WriteAll(pins);
        }
    }

    /// <summary>Drops a pin so the next connection re-establishes trust — the explicit,
    /// user-driven recovery path after a receiver is genuinely reset or replaced.</summary>
    public bool Forget(string deviceId)
    {
        lock (_lock)
        {
            var pins = ReadAll();
            if (!pins.Remove(NormalizeId(deviceId))) return false;
            WriteAll(pins);
            return true;
        }
    }

    /// <summary>Pinned device ids with their key and last-seen time — for diagnostics.</summary>
    public IReadOnlyList<(string DeviceId, string PublicKey, string Name, string LastSeenUtc)> List()
    {
        lock (_lock)
        {
            return [.. ReadAll().Select(kv => (kv.Key, kv.Value.PublicKey, kv.Value.Name, kv.Value.LastSeenUtc))];
        }
    }

    private static string NormalizeId(string deviceId) =>
        deviceId.Replace(":", "").Replace("-", "").ToUpperInvariant();

    private static string NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "" : key.Trim().Replace(":", "").Replace("-", "").ToUpperInvariant();

    private Dictionary<string, PinnedIdentity> ReadAll()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, PinnedIdentity>>(plain, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Unreadable store (different profile, corruption): treat as no pins. This fails
            // OPEN by design — the alternative is bricking playback on a corrupt file — and the
            // next successful connection re-pins.
            return [];
        }
    }

    private void WriteAll(Dictionary<string, PinnedIdentity> pins)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(pins, JsonOptions);
        File.WriteAllBytes(_path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }
}

/// <summary>
/// Thrown when a receiver presents a different long-term identity than the one WinPlay pinned —
/// the device was reset/replaced, or something is impersonating it. Streaming is refused; the
/// user resolves it explicitly via <see cref="ReceiverIdentityStore.Forget"/>.
/// </summary>
public sealed class ReceiverIdentityChangedException(string deviceName, string? pinned, string? presented)
    : Exception($"{deviceName} presented a different identity than the one WinPlay trusted. "
                + "The device may have been reset or replaced — or something on the network is "
                + "impersonating it. Forget the device in WinPlay to trust the new identity.")
{
    public string DeviceName { get; } = deviceName;
    public string? PinnedKey { get; } = pinned;
    public string? PresentedKey { get; } = presented;
}
