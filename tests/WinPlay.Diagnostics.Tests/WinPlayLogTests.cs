// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using WinPlay.Diagnostics;
using Xunit;

// The logging tests mutate the process-wide Serilog logger, so they must not run
// concurrently with each other. (Assembly attributes must precede the namespace.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WinPlay.Diagnostics.Tests;

public sealed class WinPlayLogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "winplay-diag-tests-" + Guid.NewGuid().ToString("N"));

    public WinPlayLogTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        WinPlayLog.Shutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void LogDirectory_Is_Under_LocalAppData()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, WinPlayLog.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(WinPlayLog.DataDirectory, "logs"), WinPlayLog.LogDirectory);
    }

    [Fact]
    public void Initialize_Writes_Structured_Events_To_A_Rolling_File()
    {
        WinPlayLog.Initialize(LogVerbosity.Normal, directoryOverride: _tempDir);
        WinPlayLog.For("Test").Information("AppStarted marker {N}", 42);
        WinPlayLog.Shutdown(); // flush

        string[] logs = Directory.GetFiles(_tempDir, "winplay-*.log");
        Assert.NotEmpty(logs);
        string text = File.ReadAllText(logs[0]);
        Assert.Contains("AppStarted marker 42", text);
        Assert.Contains("[INF]", text);   // structured level token
        Assert.Contains("Test:", text);   // source context
    }

    [Fact]
    public void Verbose_Level_Includes_Debug_Events()
    {
        WinPlayLog.Initialize(LogVerbosity.Verbose, directoryOverride: _tempDir);
        WinPlayLog.For("Test").Debug("debug-line");
        WinPlayLog.Shutdown();

        string text = File.ReadAllText(Directory.GetFiles(_tempDir, "winplay-*.log")[0]);
        Assert.Contains("debug-line", text);
    }

    [Fact]
    public void Normal_Level_Suppresses_Debug_Events()
    {
        WinPlayLog.Initialize(LogVerbosity.Normal, directoryOverride: _tempDir);
        WinPlayLog.For("Test").Debug("should-not-appear");
        WinPlayLog.For("Test").Information("should-appear");
        WinPlayLog.Shutdown();

        string text = File.ReadAllText(Directory.GetFiles(_tempDir, "winplay-*.log")[0]);
        Assert.DoesNotContain("should-not-appear", text);
        Assert.Contains("should-appear", text);
    }

    [Fact]
    public void CrashMarker_RoundTrips_And_Clears()
    {
        Assert.Null(WinPlayLog.TryConsumeCrashMarker(_tempDir)); // clean start

        WinPlayLog.WriteCrashMarker("UI", new InvalidOperationException("boom"), _tempDir);

        string? marker = WinPlayLog.TryConsumeCrashMarker(_tempDir);
        Assert.NotNull(marker);
        Assert.Contains("CRASH", marker);
        Assert.Contains("boom", marker);

        // Consuming clears it: a second read returns null.
        Assert.Null(WinPlayLog.TryConsumeCrashMarker(_tempDir));
    }

    [Fact]
    public void Session_That_Ends_Cleanly_Leaves_No_Marker()
    {
        WinPlayLog.BeginSession(_tempDir);
        WinPlayLog.EndSessionCleanly(_tempDir);
        Assert.Null(WinPlayLog.TryConsumeCrashMarker(_tempDir));
    }

    [Fact]
    public void Session_Killed_Mid_Run_Leaves_A_Marker_For_Next_Launch()
    {
        // Simulates taskkill /f: BeginSession wrote the marker, but the process died before
        // EndSessionCleanly ran. The next launch must see the leftover marker.
        WinPlayLog.BeginSession(_tempDir);

        string? marker = WinPlayLog.TryConsumeCrashMarker(_tempDir);
        Assert.NotNull(marker);
        Assert.Contains("ACTIVE", marker);
    }

    [Fact]
    public void No_Network_Sink_Is_Referenced_Anywhere()
    {
        // Force the sink assemblies to load, then assert the ONLY Serilog sink present is the
        // File sink only — proving diagnostics cannot leave the machine (no network sink).
        WinPlayLog.Initialize(LogVerbosity.Normal, directoryOverride: _tempDir);
        WinPlayLog.For("Test").Information("load-sinks");
        WinPlayLog.Shutdown();

        string[] sinkAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? "")
            .Where(n => n.StartsWith("Serilog.Sinks.", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        Assert.All(sinkAssemblies, n => Assert.Equal("Serilog.Sinks.File", n));

        // Belt and suspenders: the referenced-assemblies graph names no networking sink.
        string[] referenced = typeof(WinPlayLog).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(referenced, n =>
            n.Contains("Sinks.Network", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Sinks.Http", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Sinks.Seq", StringComparison.OrdinalIgnoreCase));
    }
}
