// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace WinPlay.Diagnostics;

/// <summary>
/// Builds the one-click diagnostics bundle a user attaches to a bug report.
///
/// <para>The bundle is a zip containing an environment summary, the recent rolling logs, and an
/// optional caller-supplied section (typically the discovered-device list). Two rules make it
/// safe to share:</para>
/// <list type="number">
/// <item><b>Credential stores are never read.</b> <c>credentials.dat</c> holds the Ed25519
/// private key that authenticates this PC to every paired Apple TV; it is excluded by
/// construction — only files this class explicitly adds can appear, so there is no path by
/// which a future change silently sweeps it in.</item>
/// <item><b>Everything included is redacted</b> through <see cref="SecretRedactor"/>, which
/// strips key material by shape as well as by name.</item>
/// </list>
/// </summary>
public static class BugReportBundle
{
    /// <summary>Default location for a generated bundle: the desktop, where the user can find it.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        $"winplay-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

    /// <summary>
    /// Writes the bundle and returns its path.
    /// </summary>
    /// <param name="destinationPath">Target zip; <see cref="DefaultPath"/> when null.</param>
    /// <param name="logDirectory">Log folder; WinPlay's own when null.</param>
    /// <param name="extraSections">Extra "name → content" sections (e.g. the device list),
    /// redacted like everything else.</param>
    /// <param name="maxLogFiles">Newest N log files to include (default 5) — enough for context
    /// without attaching weeks of history.</param>
    public static async Task<string> CreateAsync(
        string? destinationPath = null,
        string? logDirectory = null,
        IReadOnlyDictionary<string, string>? extraSections = null,
        int maxLogFiles = 5,
        CancellationToken ct = default)
    {
        string path = destinationPath ?? DefaultPath;
        string logs = logDirectory ?? WinPlayLog.LogDirectory;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        await WriteEntryAsync(zip, "environment.txt", DescribeEnvironment(), ct).ConfigureAwait(false);

        foreach (var (name, content) in extraSections ?? new Dictionary<string, string>())
            await WriteEntryAsync(zip, SafeEntryName(name), content, ct).ConfigureAwait(false);

        if (Directory.Exists(logs))
        {
            var files = new DirectoryInfo(logs).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(maxLogFiles);
            foreach (var file in files)
            {
                var entry = zip.CreateEntry($"logs/{file.Name}", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                try
                {
                    await SecretRedactor.RedactFileAsync(file.FullName, writer, ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // A log being actively written can be locked; note it rather than failing
                    // the whole bundle.
                    await writer.WriteLineAsync($"[bundle] could not read this log: {ex.Message}").ConfigureAwait(false);
                }
            }
        }

        return path;
    }

    /// <summary>Environment facts that make a bug actionable — versions, OS, GPU-relevant bits.</summary>
    private static string DescribeEnvironment()
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinPlay diagnostics bundle");
        sb.AppendLine($"generated:        {DateTimeOffset.Now:O}");
        sb.AppendLine($"winplay version:  {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown"}");
        sb.AppendLine($"os:               {Environment.OSVersion.VersionString}");
        sb.AppendLine($"os 64-bit:        {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"process 64-bit:   {Environment.Is64BitProcess}");
        sb.AppendLine($"architecture:     {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"runtime:          {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"processors:       {Environment.ProcessorCount}");
        sb.AppendLine($"culture:          {System.Globalization.CultureInfo.CurrentCulture.Name}");
        sb.AppendLine();
        sb.AppendLine("Note: pairing credentials are never included in this bundle, and every");
        sb.AppendLine("included file is redacted for key material before being written.");
        return sb.ToString();
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(SecretRedactor.Redact(content).AsMemory(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps caller-supplied section names inside the archive layout: separators and invalid
    /// characters are replaced, and any leading dots are dropped so no name can express a
    /// relative path a naive extractor might follow outside the destination.
    /// </summary>
    private static string SafeEntryName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = string.Concat(name.Select(c =>
            c is '/' or '\\' || invalid.Contains(c) ? '_' : c)).Trim('.', ' ', '_');
        if (cleaned.Length == 0) cleaned = "section";
        return cleaned.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? cleaned : cleaned + ".txt";
    }
}
