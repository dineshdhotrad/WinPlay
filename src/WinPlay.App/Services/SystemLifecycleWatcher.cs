// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.NetworkInformation;
using Microsoft.Win32;
using WinPlay.Diagnostics;

namespace WinPlay.App.Services;

/// <summary>
/// Ties WinPlay's streaming state to the machine's own lifecycle — sleep, resume, lock,
/// user switch, logoff and shutdown — so the app behaves correctly from power-on to power-off
/// rather than only while someone is sitting in front of it.
///
/// <para>Why this is not optional. Every one of these transitions breaks a live AirPlay
/// session in a way the session itself cannot detect quickly:</para>
/// <list type="bullet">
/// <item><b>Suspend.</b> The NIC goes down and the PTP grandmaster stops ticking, but the
/// receivers keep their sessions open. Without an orderly stop they play a stalled buffer,
/// then drop out ~30 s later on their own — and, worse, the local endpoint could be left
/// silenced across the sleep. WinPlay stops cleanly BEFORE the machine sleeps, which is the
/// only moment it can still talk to the receivers.</item>
/// <item><b>Resume.</b> Interface indices and addresses can change, so the mDNS multicast
/// joins and any advertisement are stale. Discovery is restarted rather than left blind.</item>
/// <item><b>Logoff / shutdown.</b> The process is about to end without a normal Quit, so the
/// audio endpoint must be restored and the crash marker cleared — otherwise the next launch
/// reports a crash that never happened, and the user may find their speakers muted.</item>
/// <item><b>Network change.</b> Switching Wi-Fi networks or toggling a VPN invalidates the
/// multicast group membership WinPlay depends on for discovery.</item>
/// </list>
///
/// <para>Lock and fast-user-switch deliberately do NOT stop playback: music continuing while
/// the screen is locked is the expected behaviour on both Windows and macOS.</para>
/// </summary>
public sealed class SystemLifecycleWatcher : IDisposable
{
    private readonly Func<Task> _stopAllAsync;
    private readonly Action _onResume;
    private readonly Action _onShuttingDown;
    private bool _disposed;

    /// <param name="stopAllAsync">Stops every active destination and restores local audio.</param>
    /// <param name="onResume">Restart discovery after the machine wakes or the network changes.</param>
    /// <param name="onShuttingDown">Synchronous last-chance cleanup before the session ends.</param>
    public SystemLifecycleWatcher(Func<Task> stopAllAsync, Action onResume, Action onShuttingDown)
    {
        _stopAllAsync = stopAllAsync;
        _onResume = onResume;
        _onShuttingDown = onShuttingDown;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // The last moment the network still works: tear down so receivers are released
                // immediately instead of stalling on a dead sender, and local audio is restored.
                WinPlayLog.For("Lifecycle").Information("System suspending — stopping all streams.");
                RunBounded(_stopAllAsync, "suspend teardown");
                break;

            case PowerModes.Resume:
                WinPlayLog.For("Lifecycle").Information("System resumed — restarting discovery.");
                SafeInvoke(_onResume, "resume");
                break;
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        // Logoff or shutdown: the process ends without a normal Quit. Restore audio and clear
        // the crash marker so the next launch does not report a crash that never happened.
        WinPlayLog.For("Lifecycle").Information("Session ending ({Reason}) — cleaning up.", e.Reason);
        RunBounded(_stopAllAsync, "session-ending teardown");
        SafeInvoke(_onShuttingDown, "session ending");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Playback intentionally continues across lock/unlock — the same as macOS and every
        // other media app. Logged only, so a support bundle still shows what happened.
        WinPlayLog.For("Lifecycle").Debug("Session switch: {Reason}", e.Reason);

        if (e.Reason is SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
        {
            // Fast user switching hands the audio endpoint to another user's session; keeping
            // a capture running there would silence THEIR audio.
            WinPlayLog.For("Lifecycle").Information("Console disconnected — stopping streams.");
            RunBounded(_stopAllAsync, "console-disconnect teardown");
        }
        else if (e.Reason is SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            // Coming back after a fast user switch. Without this there was no path back at all:
            // the streams were stopped on disconnect and discovery was left running against
            // multicast joins made in a session that has since been away, so the picker sat there
            // showing whatever it had last seen and never updated. Rebuilding is the same thing a
            // resume does, and for the same reason.
            WinPlayLog.For("Lifecycle").Information("Console reconnected — restarting discovery.");
            SafeInvoke(_onResume, "console reconnect");
        }
    }

    /// <summary>
    /// How long the network must hold still before discovery is rebuilt.
    ///
    /// <para>One logical change fires this event several times — a DHCP renewal, an adapter metric
    /// update, a roam between mesh access points all produce a burst. Acting on each firing tore
    /// down and rebuilt the mDNS socket repeatedly and blanked the picker every time, so a laptop
    /// roaming between APs watched its device list disappear and slowly repopulate over and over.
    /// Waiting for the burst to end costs a second and rebuilds once, correctly.</para>
    /// </summary>
    private static readonly TimeSpan NetworkSettleDelay = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _networkSettle;
    private readonly object _networkGate = new();

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Interface indices and addresses change with Wi-Fi/VPN transitions, invalidating the
        // multicast joins discovery relies on.
        WinPlayLog.For("Lifecycle").Debug("Network addresses changed.");

        // The token is read HERE, under the lock, not inside the spawned task. Rapid successive
        // network changes (Wi-Fi + VPN transitions fire in bursts) cancel AND dispose the
        // previous source; a task that had been scheduled but not yet started then evaluated
        // `cts.Token` on a disposed source and threw ObjectDisposedException on a thread-pool
        // thread — an unobserved exception surfacing minutes later on the finalizer thread.
        CancellationToken token;
        lock (_networkGate)
        {
            if (_disposed) return;
            _networkSettle?.Cancel();
            _networkSettle?.Dispose();
            _networkSettle = new CancellationTokenSource();
            token = _networkSettle.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NetworkSettleDelay, token).ConfigureAwait(false);
                WinPlayLog.For("Lifecycle").Information("Network settled — restarting discovery.");
                SafeInvoke(_onResume, "network change");
            }
            catch (OperationCanceledException) { /* another change arrived; that one will run */ }
        });
    }

    /// <summary>
    /// Runs async cleanup with a hard deadline. Windows gives an app only a couple of seconds
    /// on these notifications before suspending or killing it, so a teardown that hangs must
    /// not delay the machine — an abandoned wait is better than a blocked shutdown.
    /// </summary>
    private static void RunBounded(Func<Task> work, string what)
    {
        try
        {
            if (!work().Wait(TimeSpan.FromSeconds(2)))
                WinPlayLog.For("Lifecycle").Warning("{What} did not finish within 2 s; continuing.", what);
        }
        catch (Exception ex)
        {
            WinPlayLog.For("Lifecycle").Warning(ex, "{What} failed", what);
        }
    }

    private static void SafeInvoke(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { WinPlayLog.For("Lifecycle").Warning(ex, "{What} handler failed", what); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        // Cancel a debounce still waiting to fire, so quitting during a network transition cannot
        // rebuild discovery on a view model that is already being torn down.
        lock (_networkGate)
        {
            _networkSettle?.Cancel();
            _networkSettle?.Dispose();
            _networkSettle = null;
        }
    }
}
