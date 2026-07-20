// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinPlay.Core.Hap;

/// <summary>
/// Persists pairing credentials per receiver device id, DPAPI-protected for the current
/// user (%APPDATA%\WinPlay\credentials.dat). The long-term Ed25519 private key must
/// never touch disk in the clear — it authenticates us to every paired Apple TV.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public CredentialStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinPlay", "credentials.dat");
    }

    private sealed record Entry(string ReceiverPublicKey, string ReceiverId, string OurPrivateKey, string OurId);

    public HapPairingCredentials? Load(string deviceId)
    {
        lock (_lock)
        {
            var entries = ReadAll();
            if (!entries.TryGetValue(Normalize(deviceId), out var e)) return null;
            return new HapPairingCredentials
            {
                ReceiverPublicKey = Convert.FromHexString(e.ReceiverPublicKey),
                ReceiverId = Convert.FromHexString(e.ReceiverId),
                OurPrivateKey = Convert.FromHexString(e.OurPrivateKey),
                OurId = Convert.FromHexString(e.OurId),
            };
        }
    }

    public void Save(string deviceId, HapPairingCredentials credentials)
    {
        lock (_lock)
        {
            var entries = ReadAll();
            entries[Normalize(deviceId)] = new Entry(
                Convert.ToHexString(credentials.ReceiverPublicKey),
                Convert.ToHexString(credentials.ReceiverId),
                Convert.ToHexString(credentials.OurPrivateKey),
                Convert.ToHexString(credentials.OurId));
            WriteAll(entries);
        }
    }

    public void Remove(string deviceId)
    {
        lock (_lock)
        {
            var entries = ReadAll();
            if (entries.Remove(Normalize(deviceId)))
                WriteAll(entries);
        }
    }

    private static string Normalize(string deviceId) =>
        deviceId.Replace(":", "").Replace("-", "").ToUpperInvariant();

    private Dictionary<string, Entry> ReadAll()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(plain, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Unreadable store (different user profile, corruption): treat as empty
            // rather than blocking every connection; a re-pair rewrites it.
            return [];
        }
    }

    private void WriteAll(Dictionary<string, Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        File.WriteAllBytes(_path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }
}
