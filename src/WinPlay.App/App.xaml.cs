// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinPlay.App.Services;
using WinPlay.App.Tray;
using WinPlay.App.ViewModels;

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
    private static Mutex? _instanceMutex;

    public App()
    {
        InitializeComponent();

        // Crash diagnostics: log unhandled exceptions to %TEMP%\winplay-crash.log so a
        // failure that would otherwise vanish (WinUI swallows some) is recoverable.
        UnhandledException += (_, e) => LogCrash("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("Domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash("Task", e.Exception);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "winplay-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch (Exception) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: a second WinPlay would fail to bind the PTP grandmaster ports
        // (319/320) and break streaming. If one is already running, exit quietly.
        _instanceMutex = new Mutex(true, @"Local\WinPlay.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Process.GetCurrentProcess().Kill();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;
        _viewModel = new MainViewModel(dispatcher);
        _flyout = new FlyoutWindow(_viewModel);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "winplay.ico");
        _tray = new TrayIcon("WinPlay — AirPlay to speakers and TVs", iconPath);
        _tray.LeftClicked += () => dispatcher.TryEnqueue(() => _flyout?.Toggle());
        _tray.MenuBuilder = BuildTrayMenu;
    }

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

    private async void ExitApp()
    {
        _tray?.Dispose();
        _tray = null;
        _flyout?.HideFlyout();
        if (_viewModel is not null)
            await _viewModel.DisposeAsync();
        Exit();
    }
}
