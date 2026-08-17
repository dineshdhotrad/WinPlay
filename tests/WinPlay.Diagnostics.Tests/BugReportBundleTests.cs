// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using WinPlay.Diagnostics;
using Xunit;

namespace WinPlay.Diagnostics.Tests;

/// <summary>
/// Verifies secret redaction. A bug bundle is written to be shared publicly, so the
/// bar is that no key material survives — by name OR by shape.
/// </summary>
public class SecretRedactorTests
{
    [Theory]
    [InlineData("shk=A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4", "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4")]
    [InlineData("Active-Remote: 1234567890", "1234567890")]
    [InlineData("DACP-ID: B280E61437994301", "B280E61437994301")]
    [InlineData("pin=3939", "3939")]
    [InlineData("password: hunter2", "hunter2")]
    [InlineData("\"privateKey\": \"deadbeefcafe\"", "deadbeefcafe")]
    [InlineData("sessionKey = ZmFrZWtleQ", "ZmFrZWtleQ")]
    [InlineData("Authorization: Bearer abc123xyz", "abc123xyz")]
    public void Named_Secrets_Are_Removed(string line, string secret)
    {
        string redacted = SecretRedactor.Redact(line);
        Assert.DoesNotContain(secret, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SecretRedactor.Placeholder, redacted);
    }

    [Fact]
    public void Long_Hex_Runs_Are_Removed_Even_Without_A_Known_Label()
    {
        // Key material must not survive just because a future log line invents a new name.
        const string key = "4EEA5DD45F97184C0011223344556677889900AABBCCDDEEFF00112233445566";
        string redacted = SecretRedactor.Redact($"some brand new field xyzzy={key} trailing");
        Assert.DoesNotContain(key, redacted);
        Assert.Contains("trailing", redacted); // surrounding context is preserved
    }

    [Fact]
    public void Long_Base64_Runs_Are_Removed()
    {
        string blob = Convert.ToBase64String(Enumerable.Range(0, 48).Select(i => (byte)i).ToArray());
        string redacted = SecretRedactor.Redact($"blob {blob} end");
        Assert.DoesNotContain(blob, redacted);
    }

    [Fact]
    public void Ordinary_Diagnostic_Text_Survives_Intact()
    {
        // Over-redaction is the safe failure mode, but a bundle still has to be useful.
        const string line = "[INF] App: streaming started (buffered) to Den at 10.0.0.3:55384";
        Assert.Equal(line, SecretRedactor.Redact(line));
    }

    [Theory]
    [InlineData("anchored buffered timeline (rtpTime=1594781707)")]
    [InlineData("alive — Den+Den (3): 00:01:15 (9397 pkts)")]
    [InlineData("capture 2560x1440 → encode 1920x1080 @ 60fps, 12 Mbps (AMD VCN)")]
    [InlineData("[capture 0] final: LATE=0, silence-fill=0, gap jumps=0")]
    public void Real_WinPlay_Log_Lines_Are_Not_Mangled(string line)
        => Assert.Equal(line, SecretRedactor.Redact(line));

    [Fact]
    public void Null_And_Empty_Are_Safe()
    {
        Assert.Equal("", SecretRedactor.Redact(""));
        Assert.Equal("plain", SecretRedactor.Redact("plain"));
    }
}

/// <summary>Verifies the bundle contents, and that credentials can never ride along.</summary>
public sealed class BugReportBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "winplay-bundle-" + Guid.NewGuid().ToString("N"));

    public BugReportBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task The_Bundle_Contains_Environment_And_Redacted_Logs()
    {
        string logs = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logs);
        const string secret = "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2";
        await File.WriteAllTextAsync(Path.Combine(logs, "winplay-20260808.log"),
            $"[INF] App: AppStarted WinPlay 0.2.0{Environment.NewLine}[DBG] shk={secret}{Environment.NewLine}");

        string zipPath = Path.Combine(_dir, "bundle.zip");
        string produced = await BugReportBundle.CreateAsync(zipPath, logs);

        Assert.Equal(zipPath, produced);
        using var zip = ZipFile.OpenRead(zipPath);

        string environment = ReadEntry(zip, "environment.txt");
        Assert.Contains("WinPlay diagnostics bundle", environment);
        Assert.Contains("os:", environment);

        string log = ReadEntry(zip, "logs/winplay-20260808.log");
        Assert.Contains("AppStarted WinPlay 0.2.0", log); // useful content kept
        Assert.DoesNotContain(secret, log);               // secret gone
        Assert.Contains(SecretRedactor.Placeholder, log);
    }

    [Fact]
    public async Task Extra_Sections_Are_Included_And_Redacted()
    {
        const string secret = "DEADBEEFDEADBEEFDEADBEEFDEADBEEF";
        string zipPath = Path.Combine(_dir, "bundle.zip");
        await BugReportBundle.CreateAsync(zipPath, Path.Combine(_dir, "no-logs"),
            new Dictionary<string, string>
            {
                ["devices"] = $"Den (HomePod mini) pk={secret}",
            });

        using var zip = ZipFile.OpenRead(zipPath);
        string devices = ReadEntry(zip, "devices.txt");
        Assert.Contains("Den (HomePod mini)", devices);
        Assert.DoesNotContain(secret, devices);
    }

    [Fact]
    public async Task Credential_Stores_Are_Never_Included()
    {
        // The bundle may only contain what it explicitly adds. Even with credential files
        // sitting in the log folder, none may appear — this is the guarantee that matters most.
        string logs = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "credentials.dat"), "TOP SECRET PRIVATE KEY");
        await File.WriteAllTextAsync(Path.Combine(logs, "receivers.dat"), "PINNED KEYS");
        await File.WriteAllTextAsync(Path.Combine(logs, "winplay.log"), "[INF] hello");

        string zipPath = Path.Combine(_dir, "bundle.zip");
        await BugReportBundle.CreateAsync(zipPath, logs);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("credentials", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("receivers", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(zip.Entries, e => e.FullName == "logs/winplay.log");
    }

    [Fact]
    public async Task Only_The_Newest_Logs_Are_Included()
    {
        string logs = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logs);
        for (int i = 0; i < 8; i++)
        {
            string file = Path.Combine(logs, $"winplay-2026080{i}.log");
            await File.WriteAllTextAsync(file, $"day {i}");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-i));
        }

        string zipPath = Path.Combine(_dir, "bundle.zip");
        await BugReportBundle.CreateAsync(zipPath, logs, maxLogFiles: 3);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(3, zip.Entries.Count(e => e.FullName.StartsWith("logs/")));
    }

    [Fact]
    public async Task A_Missing_Log_Directory_Still_Produces_A_Valid_Bundle()
    {
        string zipPath = Path.Combine(_dir, "bundle.zip");
        await BugReportBundle.CreateAsync(zipPath, Path.Combine(_dir, "does-not-exist"));

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Contains(zip.Entries, e => e.FullName == "environment.txt");
    }

    [Fact]
    public async Task A_Section_Name_Cannot_Escape_The_Archive_Layout()
    {
        string zipPath = Path.Combine(_dir, "bundle.zip");
        await BugReportBundle.CreateAsync(zipPath, Path.Combine(_dir, "none"),
            new Dictionary<string, string> { ["../../evil"] = "x" });

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains(".."));
    }
}
