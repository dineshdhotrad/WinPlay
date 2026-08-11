// SPDX-License-Identifier: GPL-3.0-or-later
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace WinPlay.Diagnostics;

/// <summary>Log detail level, selectable at launch via <c>--verbose</c> / <c>--trace</c>.</summary>
public enum LogVerbosity
{
    /// <summary>Information and above (default).</summary>
    Normal,
    /// <summary>Debug and above (<c>--verbose</c>).</summary>
    Verbose,
    /// <summary>Everything, including per-packet traces (<c>--trace</c>).</summary>
    Trace,
}

/// <summary>
/// WinPlay's local-only structured logging and crash-detection facility (Task A6 / H1).
///
/// <para>Logs roll daily into <c>%LOCALAPPDATA%\WinPlay\logs</c>. There is deliberately NO
/// network sink of any kind — WinPlay never transmits diagnostics off the machine.</para>
///
/// <para>A single <c>crash.marker</c> file records that a session is in progress. It is
/// written at launch (<see cref="BeginSession"/>), overwritten with detail when an unhandled
/// exception fires (<see cref="WriteCrashMarker"/>), and deleted on clean exit
/// (<see cref="EndSessionCleanly"/>). If it survives to the next launch, the previous session
/// died abnormally (crash, <c>taskkill /f</c>, power loss) — the launcher detects this via
/// <see cref="TryConsumeCrashMarker"/> and runs recovery (restoring audio state, per B4).</para>
///
/// <para>Every method is exception-safe: diagnostics must never throw into a failing path.</para>
/// </summary>
public static class WinPlayLog
{
    private const string MarkerFileName = "crash.marker";
    private static readonly object Gate = new();

    /// <summary><c>%LOCALAPPDATA%\WinPlay</c> — root for logs and recovery state.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlay");

    /// <summary><c>%LOCALAPPDATA%\WinPlay\logs</c>.</summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>The active logger. A no-op sink until <see cref="Initialize"/> is called.</summary>
    public static ILogger Logger { get; private set; } = Serilog.Core.Logger.None;

    /// <summary>Returns a logger tagged with a source context for readable, filterable logs.</summary>
    public static ILogger For(string sourceContext) => Logger.ForContext("SourceContext", sourceContext);

    /// <summary>
    /// Configures the rolling-file logger. Idempotent by rebuild: calling again disposes the
    /// previous logger and constructs a fresh one (used by tests with a directory override).
    /// </summary>
    public static void Initialize(LogVerbosity verbosity = LogVerbosity.Normal, string? directoryOverride = null)
    {
        lock (Gate)
        {
            string dir = directoryOverride ?? LogDirectory;
            TryCreateDirectory(dir);

            LogEventLevel level = verbosity switch
            {
                LogVerbosity.Trace => LogEventLevel.Verbose,
                LogVerbosity.Verbose => LogEventLevel.Debug,
                _ => LogEventLevel.Information,
            };

            var logger = new LoggerConfiguration()
                .MinimumLevel.Is(level)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    path: Path.Combine(dir, "winplay-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            (Logger as IDisposable)?.Dispose();
            Logger = logger;
            Log.Logger = logger; // also back the static Serilog.Log facade
        }
    }

    /// <summary>Flushes and disposes the logger. Safe to call more than once.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            (Logger as IDisposable)?.Dispose();
            Logger = Serilog.Core.Logger.None;
            Log.CloseAndFlush();
        }
    }

    /// <summary>Marks a session as active. Presence at next launch ⇒ previous abnormal exit.</summary>
    public static void BeginSession(string? dataDir = null)
    {
        string dir = dataDir ?? DataDirectory;
        WriteMarker(dir, $"ACTIVE\t{DateTimeOffset.Now:O}\tpid={Environment.ProcessId}");
    }

    /// <summary>Overwrites the marker with unhandled-exception detail (called from crash handlers).</summary>
    public static void WriteCrashMarker(string source, Exception? ex, string? dataDir = null)
    {
        string dir = dataDir ?? DataDirectory;
        WriteMarker(dir, $"CRASH\t{DateTimeOffset.Now:O}\t{source}\t{ex?.GetType().FullName}\t{ex?.Message}");
    }

    /// <summary>Deletes the marker to record a clean shutdown.</summary>
    public static void EndSessionCleanly(string? dataDir = null)
    {
        try
        {
            string path = Path.Combine(dataDir ?? DataDirectory, MarkerFileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception) { /* never throw from the shutdown path */ }
    }

    /// <summary>
    /// Returns and clears any marker left by a previous abnormal exit, or <c>null</c> if the
    /// previous session ended cleanly. Call once at launch, before <see cref="BeginSession"/>.
    /// </summary>
    public static string? TryConsumeCrashMarker(string? dataDir = null)
    {
        try
        {
            string path = Path.Combine(dataDir ?? DataDirectory, MarkerFileName);
            if (!File.Exists(path)) return null;
            string content = File.ReadAllText(path);
            File.Delete(path);
            return content;
        }
        catch (Exception) { return null; }
    }

    private static void WriteMarker(string dir, string content)
    {
        try
        {
            TryCreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), content);
        }
        catch (Exception) { /* diagnostics must never throw into the caller's path */ }
    }

    private static void TryCreateDirectory(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch (Exception) { }
    }
}
