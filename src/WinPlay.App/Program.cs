// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinPlay.Capture;

namespace WinPlay.App;

/// <summary>
/// Custom application entry point (replaces the XAML-generated <c>Main</c>; see the
/// <c>DISABLE_XAML_GENERATED_MAIN</c> constant in the project file).
///
/// <para>Single-instancing is done the fundamentally correct way — Windows App SDK
/// activation redirection. A second launch registers against the same key, discovers
/// it is not the owner, hands its activation to the already-running WinPlay (which surfaces
/// its picker), and exits. Only the primary instance constructs <see cref="App"/>, so it
/// alone binds the PTP grandmaster ports and owns the crash/session markers. This replaces
/// the old mutex + <c>Process.Kill()</c> path, which silently killed the second launch.</para>
/// </summary>
public static class Program
{
    private const string SingleInstanceKey = "WinPlay.SingleInstance";

    /// <summary>Log verbosity selected on the command line (<c>--verbose</c> / <c>--trace</c>).</summary>
    internal static WinPlay.Diagnostics.LogVerbosity LaunchVerbosity { get; private set; } =
        WinPlay.Diagnostics.LogVerbosity.Normal;

    [STAThread]
    private static int Main(string[] args)
    {
        // Multi-call binary: the supervised capture pipeline runs as this same executable in
        // host mode (WinPlay.App.exe --capture-host <pipe>), so a GPU/encoder crash is isolated
        // to that child process. Handle it before any WinUI / single-instance init.
        if (args.Length >= 2 && args[0] == CaptureHostRunner.Switch)
            return CaptureHostRunner.Run(args[1]);

        // Log verbosity from the command line. Read before App is constructed so the very
        // first log line already honours it.
        LaunchVerbosity = args.Any(a => a.Equals("--trace", StringComparison.OrdinalIgnoreCase))
            ? WinPlay.Diagnostics.LogVerbosity.Trace
            : args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase))
                ? WinPlay.Diagnostics.LogVerbosity.Verbose
                : WinPlay.Diagnostics.LogVerbosity.Normal;

        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return 0; // another instance owns the key; we redirected our activation and exit.

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// Registers this process under the single-instance key. Returns true when another
    /// instance already owns it — this launch redirected its activation there and must exit.
    /// </summary>
    private static bool DecideRedirection()
    {
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance primary = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        if (primary.IsCurrent)
        {
            // We are the primary: react to activations redirected from later launches.
            primary.Activated += OnActivatedFromOtherInstance;
            return false;
        }

        RedirectActivationTo(activation, primary);
        return true;
    }

    private static void OnActivatedFromOtherInstance(object? sender, AppActivationArguments args)
        => (Current as App)?.SurfacePickerFromActivation();

    private static Application? Current => Application.Current;

    // Microsoft's documented redirect pattern for unpackaged apps: run the async redirect on
    // a worker thread and pump COM on this STA thread until it signals, so the hand-off is
    // reliable regardless of the launching process's apartment state.
    private static IntPtr _redirectEvent;

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance primary)
    {
        _redirectEvent = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            // The event is signalled in a finally: if the redirect throws — the primary quit
            // between discovery and hand-off, or COM activation failed — the old code skipped
            // SetEvent and this process waited on INFINITE forever, an invisible zombie the user
            // could only find in Task Manager. A failed redirect just means "nothing to hand
            // off"; exiting is the correct outcome either way.
            try { primary.RedirectActivationToAsync(args).AsTask().Wait(); }
            catch (Exception) { /* primary gone mid-hand-off — exit regardless */ }
            finally { SetEvent(_redirectEvent); }
        });
        // Bounded for the same reason: a wedged primary must not strand this launcher.
        const uint timeoutMs = 30_000;
        _ = CoWaitForMultipleObjects(0, timeoutMs, 1, [_redirectEvent], out _);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);
}
