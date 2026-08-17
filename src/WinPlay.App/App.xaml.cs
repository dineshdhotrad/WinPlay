// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinPlay.App.Services;
using WinPlay.App.Tray;
using WinPlay.App.ViewModels;
using WinPlay.Core.Input;
using WinPlay.Diagnostics;

namespace WinPlay.App;

/// <summary>
/// Tray-first app: no main window. A notification-area icon toggles the Acrylic flyout;
/// right-click exits. The flyout window is created eagerly (hidden) so discovery is warm
/// by the first click.
/// </summary>
public partial class App : Application
{
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private MainViewModel? _viewModel;

    public App()
    {
        InitializeComponent();

        // Local-only structured logging, registered first so even an early failure is
        // captured. Only the primary instance ever constructs App (single-instancing lives in
        // Program.Main), so session/crash-marker bookkeeping in OnLaunched is unconditional.
        WinPlayLog.Initialize(Program.LaunchVerbosity);
        UnhandledException += (_, e) => HandleFatal("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleFatal("Domain", e.ExceptionObject as Exception);
        // Covers exits that never reach OnExit — the runtime raises this for a normal process end
        // too, so a session closed here is one the receiver does not have to time out on its own.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TearDownReceivers("process exit");
        // Unobserved task exceptions are NOT fatal — since .NET 4.5 the process survives them —
        // so they must not take the crash path: routing them through HandleFatal wrote a crash
        // marker and TORE DOWN every live receiver, turning a harmless background hiccup (e.g. a
        // race in a settle timer) into all audio stopping mid-play. Observe, log, carry on.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            WinPlayLog.For("crash").Warning(e.Exception, "Unobserved task exception (non-fatal)");
        };
    }

    private string? _previousCrashMarker;

    /// <summary>
    /// Last-chance handler: persist a crash marker so the next launch restores audio state
    /// and log full context.
    /// </summary>
    private void HandleFatal(string source, Exception? ex)
    {
        WinPlayLog.WriteCrashMarker(source, ex);
        WinPlayLog.For("crash").Fatal(ex, "Unhandled {Source} exception", source);
        TearDownReceivers("crash");
    }

    /// <summary>
    /// Best-effort TEARDOWN of every live session on an abrupt exit.
    ///
    /// <para>An AirPlay receiver holds session state until the sender closes it. A sender that
    /// vanishes — crash, Task Manager, a killed debug run — leaves the receiver believing a
    /// session is still live, and enough abandoned sessions in a row leave it refusing new ones
    /// until it is power-cycled. Asking a user to unplug a speaker to make an app work is not an
    /// acceptable failure mode, and every abandoned session is one WinPlay itself created.</para>
    ///
    /// <para>Bounded hard, because this runs on paths where the process is already going away and
    /// blocking shutdown is its own bug. It cannot help a <c>TerminateProcess</c> — nothing can —
    /// but it covers the crash and normal-exit paths, which is where WinPlay's own abandoned
    /// sessions actually come from.</para>
    /// </summary>
    private void TearDownReceivers(string reason)
    {
        try
        {
            if (_viewModel?.StopAllStreamsAsync() is { } stopping &&
                !stopping.Wait(TimeSpan.FromSeconds(3)))
                WinPlayLog.For("App").Warning("TEARDOWN on {Reason} timed out; receivers may hold state", reason);
        }
        catch (Exception ex)
        {
            WinPlayLog.For("App").Warning(ex, "TEARDOWN on {Reason} failed", reason);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single-instancing is enforced in Program.Main via Windows App SDK activation
        // redirection, so only the primary instance ever reaches here (a second launch is
        // handed to us and surfaces the picker — see SurfacePickerFromActivation). Take
        // ownership of session/crash-marker state: consume any marker left by a previous
        // abnormal exit before marking this session active, so recovery (audio restore)
        // can act on it.
        _previousCrashMarker = WinPlayLog.TryConsumeCrashMarker();
        WinPlayLog.BeginSession();
        string appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0";
        WinPlayLog.For("App").Information("AppStarted WinPlay {Version}", appVersion);
        if (_previousCrashMarker is not null)
            WinPlayLog.For("App").Warning("Recovering from an abnormal previous exit: {Marker}", _previousCrashMarker);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;
        _viewModel = new MainViewModel(dispatcher);
        _flyout = new FlyoutWindow(_viewModel);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "winplay.ico");
        _tray = new TrayIcon("WinPlay — AirPlay to speakers and TVs", iconPath);
        _tray.LeftClicked += () => dispatcher.TryEnqueue(() => _flyout?.Toggle());
        _tray.MenuBuilder = BuildTrayMenu;

        // Global hotkey: opens the picker from anywhere with the muscle memory of the
        // native flyouts. Remappable via HKCU\Software\WinPlay → "Hotkey" (e.g. "Ctrl+Alt+P");
        // a malformed value falls back to Win+Shift+A rather than misfiring.
        _tray.HotkeyPressed += () => dispatcher.TryEnqueue(() => _flyout?.ShowNearTray());
        RegisterPickerHotkey();

        // Background-app hygiene: startup and the first flyout render leave a large transient
        // working set mapped. Release it once the app settles, and again whenever the picker
        // closes — but never while streaming (see IdleFootprint).
        _flyout.Hidden += () => IdleFootprint.ScheduleTrim(IsStreaming);
        IdleFootprint.ScheduleTrim(IsStreaming);

        // Follow the machine's lifecycle: stop cleanly before sleep/logoff (while the network
        // still works), restart discovery on resume or a network change, and never leave the
        // speakers muted when the session ends. See SystemLifecycleWatcher.
        _lifecycle = new SystemLifecycleWatcher(
            stopAllAsync: () => _viewModel?.StopAllStreamsAsync() ?? Task.CompletedTask,
            onResume: () => _viewModel?.RestartDiscovery(),
            onShuttingDown: () =>
            {
                WinPlayLog.For("App").Information("AppExiting (session ending).");
                WinPlayLog.EndSessionCleanly();
                WinPlayLog.Shutdown();
            });
    }

    private SystemLifecycleWatcher? _lifecycle;

    /// <summary>True while any destination is streaming — footprint trimming stands down then.</summary>
    private bool IsStreaming() => _viewModel?.IsStreaming ?? false;

    /// <summary>
    /// Registers the picker hotkey, falling back through alternates when a combination is
    /// already owned by another application. Win+Shift+A is common enough to collide —
    /// observed in the wild — and a hotkey that silently does nothing is worse than one that
    /// quietly moved, so WinPlay takes the first free combination and logs which it got.
    /// An explicitly configured gesture is never overridden: if the user asked for a specific
    /// key, failing loudly is the honest outcome.
    /// </summary>
    private void RegisterPickerHotkey()
    {
        if (_tray is null) return;

        var configured = ReadConfiguredHotkey(out bool isExplicit);
        IEnumerable<HotkeyGesture> candidates = isExplicit
            ? [configured]
            : [configured, .. HotkeyGesture.DefaultAlternates];

        foreach (var gesture in candidates)
        {
            if (!_tray.TryRegisterHotkey(gesture.Modifiers | HotkeyGesture.ModNoRepeat, gesture.VirtualKey))
                continue;
            _hotkey = gesture;
            WinPlayLog.For("App").Information("Global hotkey registered: {Gesture}", gesture);
            return;
        }

        WinPlayLog.For("App").Warning(
            "No global hotkey could be registered ({Tried} already in use); the tray icon still opens the picker.",
            string.Join(", ", candidates));
    }

    private HotkeyGesture? _hotkey;

    /// <param name="isExplicit">True when the user configured a specific gesture, which must
    /// then be honoured exactly rather than silently replaced by a fallback.</param>
    private static HotkeyGesture ReadConfiguredHotkey(out bool isExplicit)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\WinPlay");
            if (key?.GetValue("Hotkey") is string text && HotkeyGesture.TryParse(text, out var custom))
            {
                isExplicit = true;
                return custom;
            }
        }
        catch (Exception) { /* unreadable registry — use the default */ }
        isExplicit = false;
        return HotkeyGesture.Default;
    }

    /// <summary>
    /// Brings the picker forward in response to a second launch redirected here by
    /// Program.Main — the native "you're already running, here I am" behaviour.
    /// </summary>
    public void SurfacePickerFromActivation() =>
        _dispatcher?.TryEnqueue(() => _flyout?.ShowNearTray());

    private const string RepositoryUrl = "https://github.com/dineshdhotrad/WinPlay";

    /// <summary>Where people can tip the author, if WinPlay is worth something to them.</summary>
    private const string KoFiUrl = "https://ko-fi.com/thedinesh";

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0";
        return
        [
            new TrayMenuItem { Text = "Open WinPlay", IsDefault = true, Clicked = () => _dispatcher?.TryEnqueue(() => _flyout?.ShowNearTray()) },
            TrayMenuItem.Separator,
            new TrayMenuItem
            {
                Text = "Start with Windows",
                IsChecked = StartupManager.IsEnabled(),
                Clicked = () => StartupManager.SetEnabled(!StartupManager.IsEnabled()),
            },
            TrayMenuItem.Separator,
            new TrayMenuItem { Text = "Buy me a coffee ☕", Clicked = () => OpenUrl(KoFiUrl) },
            new TrayMenuItem { Text = "Support on GitHub", Clicked = () => OpenUrl(RepositoryUrl) },
            new TrayMenuItem { Text = "Report an issue", Clicked = () => OpenUrl($"{RepositoryUrl}/issues/new/choose") },
            new TrayMenuItem { Text = "Export diagnostics…", Clicked = () => _ = ExportDiagnosticsAsync() },
            new TrayMenuItem { Text = $"WinPlay {version}", IsEnabled = false },
            TrayMenuItem.Separator,
            new TrayMenuItem { Text = "Quit WinPlay", Clicked = () => _dispatcher?.TryEnqueue(ExitApp) },
        ];
    }

    private DispatcherQueue? _dispatcher;

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>
    /// Writes a redacted diagnostics bundle to the desktop and reveals it in Explorer, so
    /// attaching it to an issue is one click. Pairing credentials are never included, and every
    /// file is scrubbed of key material before it is written.
    /// </summary>
    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            var sections = new Dictionary<string, string>
            {
                ["devices"] = _viewModel?.DescribeDiscoveredDevices() ?? "(no device snapshot)",
            };
            string path = await BugReportBundle.CreateAsync(extraSections: sections).ConfigureAwait(false);
            WinPlayLog.For("App").Information("Diagnostics bundle written to {Path}", path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WinPlayLog.For("App").Error(ex, "Failed to export the diagnostics bundle");
        }
    }

    private async void ExitApp()
    {
        // Every step is guarded and the clean-exit record is written in a finally: this is an
        // async void handler, so an escaping exception re-throws on the dispatcher — a CRASH ON
        // QUIT — and skipping EndSessionCleanly makes the next launch tell the user WinPlay
        // crashed when it did not. Teardown failures are logged, never allowed to change the
        // outcome of quitting.
        try
        {
            _lifecycle?.Dispose();
            _lifecycle = null;
            _tray?.Dispose();
            _tray = null;
            _flyout?.AllowClose();
            _flyout?.HideFlyout();
            if (_viewModel is not null)
                await _viewModel.DisposeAsync();
            WinPlayLog.For("App").Information("AppExiting cleanly.");
        }
        catch (Exception ex)
        {
            WinPlayLog.For("App").Warning(ex, "teardown failed during exit; quitting anyway");
        }
        finally
        {
            WinPlayLog.EndSessionCleanly();
            WinPlayLog.Shutdown();
            Exit();
        }
    }
}
