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

        // Local-only structured logging (A6), registered first so even an early failure is
        // captured. Only the primary instance ever constructs App (single-instancing lives in
        // Program.Main), so session/crash-marker bookkeeping in OnLaunched is unconditional.
        WinPlayLog.Initialize(Program.LaunchVerbosity);
        UnhandledException += (_, e) => HandleFatal("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleFatal("Domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => HandleFatal("Task", e.Exception);
    }

    private string? _previousCrashMarker;

    /// <summary>
    /// Last-chance handler: persist a crash marker so the next launch restores audio state
    /// (B4) and log full context.
    /// </summary>
    private void HandleFatal(string source, Exception? ex)
    {
        WinPlayLog.WriteCrashMarker(source, ex);
        WinPlayLog.For("crash").Fatal(ex, "Unhandled {Source} exception", source);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single-instancing is enforced in Program.Main via Windows App SDK activation
        // redirection, so only the primary instance ever reaches here (a second launch is
        // handed to us and surfaces the picker — see SurfacePickerFromActivation). Take
        // ownership of session/crash-marker state: consume any marker left by a previous
        // abnormal exit before marking this session active, so recovery (audio restore, B4)
        // can act on it.
        _previousCrashMarker = WinPlayLog.TryConsumeCrashMarker();
        WinPlayLog.BeginSession();
        string appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
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

        // Global hotkey (F3): opens the picker from anywhere with the muscle memory of the
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
    /// already owned by another application (F3). Win+Shift+A is common enough to collide —
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
    /// Program.Main (A3) — the native "you're already running, here I am" behaviour.
    /// </summary>
    public void SurfacePickerFromActivation() =>
        _dispatcher?.TryEnqueue(() => _flyout?.ShowNearTray());

    private const string RepositoryUrl = "https://github.com/dineshdhotrad/WinPlay";

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
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
    /// Writes a redacted diagnostics bundle to the desktop and reveals it in Explorer (H2), so
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
        _lifecycle?.Dispose();
        _lifecycle = null;
        _tray?.Dispose();
        _tray = null;
        _flyout?.AllowClose();
        _flyout?.HideFlyout();
        if (_viewModel is not null)
            await _viewModel.DisposeAsync();
        WinPlayLog.For("App").Information("AppExiting cleanly.");
        WinPlayLog.EndSessionCleanly();
        WinPlayLog.Shutdown();
        Exit();
    }
}
