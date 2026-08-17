// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using WinPlay.App.Services;
using WinPlay.Diagnostics;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;
using WinPlay.Core.Raop;

namespace WinPlay.App.ViewModels;

/// <summary>
/// Owns discovery + streaming. Projects browser snapshots into picker rows on the UI
/// thread (updated in place, keyed by group/device ID) and turns row actions into
/// <see cref="StreamController"/> sessions.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    // Rebuilt on resume / network change (see RestartDiscovery), so not readonly — and NULLABLE,
    // because there are ordinary moments when this machine has no usable network and therefore no
    // discovery stack at all. The app has to exist during those moments.
    private AirPlayBrowser? _browser;
    private WinPlay.Core.Mdns.MdnsClient? _mdns;
    private readonly StreamController _streams = new();
    private readonly NowPlayingService _nowPlaying;
    private readonly RemoteControlService _remote;
    private readonly DispatcherQueue _dispatcher;
    private string _status = "Looking for AirPlay devices…";
    private int _deviceCount;

    public ObservableCollection<PickerRowViewModel> Rows { get; } = [];

    /// <summary>
    /// Set by the view: shows a PIN-entry dialog for the named receiver and returns
    /// the PIN, or null on cancel. Enables first-time Apple TV pairing from the flyout.
    /// </summary>
    public Func<string, Task<string?>>? RequestPin
    {
        get => _streams.PinPrompt;
        set => _streams.PinPrompt = value;
    }

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _streams.SessionStage += (key, stage) => _dispatcher.TryEnqueue(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Key == key);
            row?.OnStage(stage);
        });
        _streams.SessionFailed += (key, channel, ex) => _dispatcher.TryEnqueue(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Key == key);
            row?.OnFailed(channel, FriendlyError(ex));
            RefreshStatus();
        });

        _nowPlaying = new NowPlayingService(_streams);
        _nowPlaying.TrackChanged += OnTrackChanged;
        _nowPlaying.Start();

        // Started before discovery, and independent of it: answering remote-control commands
        // needs only a TCP listener.
        _remote = new RemoteControlService(_streams);
        _ = _remote.StartAsync();

        // Discovery is ATTEMPTED, not required. Building the mDNS transport throws when no
        // interface will accept a multicast join, and that is an ordinary state, not an error:
        // WinPlay starts at logon, and Windows routinely launches startup apps before Wi-Fi has
        // finished associating. Constructing it unguarded here meant the exception escaped
        // OnLaunched before the tray icon was ever created — so on those boots the app simply did
        // not appear, with nothing to indicate it had tried. It now comes up regardless and picks
        // discovery up when the network arrives.
        TryStartDiscovery();

        // A receiver's own volume keys move the matching row's slider.
        _streams.VolumeChangedExternally += (key, db) => _dispatcher.TryEnqueue(() =>
            Rows.FirstOrDefault(r => r.Key == key)?.SetVolumeFromRemote(VolumeControl.DbToPercent(db)));
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public bool HasNoDevices => _deviceCount == 0;

    // ------------------------------------------------------------ row actions

    private async Task OnAudioToggleAsync(PickerRowViewModel row, bool on)
    {
        // Every write below is gated on this attempt still being the current one. A connect that
        // the user has since abandoned must not resurrect its switch or overwrite the status of
        // whatever replaced it — see PickerRowViewModel's attempt-identity notes.
        using var attempt = row.BeginAudioAttempt();
        try
        {
            if (on)
            {
                row.SetStatus("Connecting…");
                await _streams.StartAudioAsync(row.Entry, PercentToDb(row.VolumePercent), attempt.Token);
                _dispatcher.TryEnqueue(() =>
                {
                    if (!row.IsCurrent(attempt)) return;
                    row.SetStreamingStatus();
                    RefreshStatus();
                });
            }
            else
            {
                await _streams.StopAudioAsync(row.Key);
                _dispatcher.TryEnqueue(() =>
                {
                    if (!row.IsCurrent(attempt)) return;
                    row.SetStatus(null);
                    RefreshStatus();
                });
            }
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested)
        {
            // Superseded by a newer toggle. The newer attempt owns the row's state now; the only
            // thing this one still owes is releasing the half-built session it left behind.
            await SafeStopAudioAsync(row.Key);
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!row.IsCurrent(attempt)) return;
                row.SetAudioCheckedSilently(false);
                row.SetStatus(FriendlyError(ex));
            });
        }
        finally
        {
            _dispatcher.TryEnqueue(() => row.EndAudioAttempt(attempt));
        }
    }

    /// <summary>
    /// Tears down a session abandoned mid-connect. Failure here is genuinely nothing to report —
    /// the connect it belonged to was already cancelled — but it must never escape and replace the
    /// real outcome the user is waiting on.
    /// </summary>
    private async Task SafeStopAudioAsync(string key)
    {
        try { await _streams.StopAudioAsync(key); }
        catch (Exception ex) { WinPlayLog.For("Picker").Warning(ex, "could not release cancelled audio session"); }
    }

    private async Task SafeStopMirrorAsync(string key)
    {
        try { await _streams.StopMirrorAsync(key); }
        catch (Exception ex) { WinPlayLog.For("Picker").Warning(ex, "could not release cancelled mirror session"); }
    }

    /// <summary>
    /// Moves an Apple TV row to one mode, stopping whatever the previous mode was running.
    ///
    /// <para>Every transition goes through here, which is what makes the two-session state
    /// unreachable rather than merely discouraged: there is one active channel per TV at a time,
    /// and Screen+Audio is a single mirror session carrying both — the only arrangement where
    /// picture and sound share a clock and therefore stay in sync.</para>
    ///
    /// <para>Switching between Screen and Screen+Audio reconnects rather than toggling in place.
    /// The receiver negotiates whether the session carries audio at SETUP time, so it is fixed for
    /// that session's lifetime — an honest protocol constraint, shown as a normal busy transition.</para>
    /// </summary>
    private async Task OnModeChangeAsync(PickerRowViewModel row, PickerRowViewModel.TvStreamMode mode)
    {
        WinPlayLog.For("Session").Information("{Key}: mode change requested → {Mode}", row.Key, mode);

        // STOP FIRST, and only then supersede the channel attempts. A running session's
        // cancellation source is linked to the attempt token it was STARTED under, so beginning a
        // new attempt cancels that token — and doing so before the stop turned every mode change
        // into an ungraceful kill: the running session died by cancellation racing the orderly
        // stop, its TEARDOWN was swallowed by the very token that killed it, and the receiver was
        // left holding a session nobody would ever close. Stopping is authoritative on its own —
        // it cancels the destination directly — so an in-flight connect is aborted here too.
        await SafeStopMirrorAsync(row.Key);
        await SafeStopAudioAsync(row.Key);

        // Each channel's start runs under ITS OWN attempt: audio under the audio attempt, the
        // mirror session under the mirror attempt. Both are begun — a mode change owns the whole
        // row — so stale completions from either channel are superseded and cannot write.
        using var audioAttempt = row.BeginAudioAttempt();
        using var mirrorAttempt = row.BeginMirrorAttempt();
        try
        {
            switch (mode)
            {
                case PickerRowViewModel.TvStreamMode.AudioOnly:
                    row.SetStatus("Connecting…");
                    await _streams.StartAudioAsync(row.Entry, PercentToDb(row.VolumePercent), audioAttempt.Token);
                    break;

                case PickerRowViewModel.TvStreamMode.ScreenOnly:
                    row.SetStatus("Starting mirroring…");
                    await _streams.StartMirrorAsync(row.Entry, includeAudio: false, mirrorAttempt.Token);
                    break;

                case PickerRowViewModel.TvStreamMode.Both:
                    row.SetStatus("Starting mirroring…");
                    await _streams.StartMirrorAsync(row.Entry, includeAudio: true, mirrorAttempt.Token);
                    break;
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (!row.IsCurrent(audioAttempt) || !row.IsCurrent(mirrorAttempt)) return;
                row.SetModeSilently(mode);
                row.SetStatus(mode switch
                {
                    PickerRowViewModel.TvStreamMode.AudioOnly => "Streaming system audio",
                    PickerRowViewModel.TvStreamMode.ScreenOnly => "Mirroring your screen",
                    PickerRowViewModel.TvStreamMode.Both => "Mirroring your screen, with sound",
                    _ => null,
                });
                RefreshStatus();
            });
        }
        catch (OperationCanceledException) when (
            audioAttempt.Token.IsCancellationRequested || mirrorAttempt.Token.IsCancellationRequested)
        {
            await SafeStopMirrorAsync(row.Key);
            await SafeStopAudioAsync(row.Key);
        }
        catch (Exception ex)
        {
            // The requested mode did not happen, so the row must not claim it did.
            await SafeStopMirrorAsync(row.Key);
            await SafeStopAudioAsync(row.Key);
            _dispatcher.TryEnqueue(() =>
            {
                if (!row.IsCurrent(audioAttempt) || !row.IsCurrent(mirrorAttempt)) return;
                row.SetModeSilently(PickerRowViewModel.TvStreamMode.Off);
                row.SetStatus(FriendlyError(ex));
            });
        }
        finally
        {
            _dispatcher.TryEnqueue(() =>
            {
                row.EndAudioAttempt(audioAttempt);
                row.EndMirrorAttempt(mirrorAttempt);
            });
        }
    }

    private async Task OnMirrorToggleAsync(PickerRowViewModel row, bool on)
    {
        using var attempt = row.BeginMirrorAttempt();
        try
        {
            if (on)
            {
                row.SetStatus("Starting mirroring…");
                await _streams.StartMirrorAsync(row.Entry, includeAudio: true, attempt.Token);
                _dispatcher.TryEnqueue(() =>
                {
                    if (!row.IsCurrent(attempt)) return;
                    row.SetMirroringStatus();
                    RefreshStatus();
                });
            }
            else
            {
                await _streams.StopMirrorAsync(row.Key);
                _dispatcher.TryEnqueue(() =>
                {
                    if (!row.IsCurrent(attempt)) return;
                    row.SetStatus(null);
                    RefreshStatus();
                });
            }
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested)
        {
            await SafeStopMirrorAsync(row.Key);
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!row.IsCurrent(attempt)) return;
                row.SetMirrorCheckedSilently(false);
                row.SetStatus(FriendlyError(ex));
            });
        }
        finally
        {
            _dispatcher.TryEnqueue(() => row.EndMirrorAttempt(attempt));
        }
    }

    /// <summary>True while any destination is receiving audio or a mirrored screen.</summary>
    public bool IsStreaming => _streams.ActiveCount > 0;

    /// <summary>Stops every destination and restores local audio (sleep / logoff / user switch).</summary>
    public async Task StopAllStreamsAsync()
    {
        await _streams.StopAllAsync().ConfigureAwait(false);

        // The rows have to be told. Nothing else does it: the only code that clears a row's
        // toggles lives in its own toggle handler, so a stop the USER did not initiate — sleep,
        // logoff, a fast user switch handing the audio endpoint to someone else — tore the
        // sessions down and left every row still switched on, still saying "Streaming system
        // audio". Switching one back off then did nothing, because there was nothing left to
        // stop. A control that does not describe what is running is worse than no control.
        _dispatcher.TryEnqueue(() =>
        {
            foreach (var row in Rows) row.ResetToIdle();
            RefreshStatus();
        });
    }

    /// <summary>
    /// Restarts discovery after the machine wakes or the network changes. The mDNS socket's
    /// multicast group memberships are bound to interface indices that do not survive a
    /// suspend or a Wi-Fi/VPN switch, so the transport is rebuilt rather than reused — a
    /// browser left running on stale joins looks alive but never sees another device.
    /// </summary>
    private readonly object _discoveryLock = new();
    private bool _disposed;

    /// <summary>
    /// Builds a discovery stack — transport, browser, and the diagnostics wiring they need — as
    /// one unit.
    ///
    /// <para>Startup and every rebuild go through here deliberately. Constructing it in two places
    /// meant the two could drift, and the way that drift shows up is the worst kind: discovery
    /// works when the app starts and quietly loses a behaviour after the first sleep, which looks
    /// like a hardware problem rather than a code one.</para>
    ///
    /// <para>Both failure events are logged. They were previously raised into nothing at all, so
    /// the two ways discovery can degrade — a socket that stops receiving, a browse round that
    /// throws — left no trace anywhere for a user to report or for a bundle to capture.</para>
    /// </summary>
    private (WinPlay.Core.Mdns.MdnsClient Mdns, AirPlayBrowser Browser) CreateDiscovery()
    {
        var mdns = new WinPlay.Core.Mdns.MdnsClient();
        mdns.ReceiveError += ex => WinPlayLog.For("Discovery").Warning(ex, "mDNS receive failed");

        var browser = new AirPlayBrowser(mdns);
        browser.Diagnostic += message => WinPlayLog.For("Discovery").Warning("{Detail}", message);
        browser.DevicesChanged += OnDevicesChanged;
        return (mdns, browser);
    }

    /// <summary>
    /// Builds and starts a discovery stack, replacing any existing one. Returns false when the
    /// machine currently has no network that will carry mDNS, having scheduled a retry.
    ///
    /// <para>Cold start and every rebuild share this one path deliberately. The retry machinery
    /// existed only on the rebuild path, so the identical failure — no interface accepts the
    /// multicast join yet — was survivable on resume and fatal at launch, which is the single
    /// most likely moment for it to happen.</para>
    /// </summary>
    private bool TryStartDiscovery()
    {
        try
        {
            // Unsubscribe before disposing: the old browser and transport are about to go, and a
            // handler still attached to a dying instance can deliver one last snapshot built from
            // state that is already being torn down.
            if (_browser is { } old)
            {
                old.DevicesChanged -= OnDevicesChanged;
                old.Dispose();
            }
            _mdns?.Dispose();
            _browser = null;
            _mdns = null;

            // One mDNS transport serves both directions: browsing for receivers, and advertising
            // WinPlay's own DACP control endpoint so receivers can send play/pause back.
            (_mdns, _browser) = CreateDiscovery();
            _browser.Start();

            // Advertising is a separate concern from browsing, and is handled separately.
            // Sharing one catch meant an advertiser problem tore down a perfectly healthy browser
            // and scheduled a full discovery retry — losing the device list over something that
            // only affects whether receivers can send commands BACK to this PC.
            try
            {
                _remote.Readvertise(_mdns);
            }
            catch (Exception ex)
            {
                WinPlayLog.For("Discovery").Warning(ex, "could not advertise the remote-control endpoint");
            }
            return true;
        }
        catch (Exception ex)
        {
            // Legitimately common: no interface accepts the multicast join yet, mid-transition or
            // pre-association. Leave discovery absent and try again shortly rather than dying.
            _browser = null;
            _mdns = null;
            WinPlayLog.For("Discovery").Warning(ex, "discovery unavailable; retrying shortly");
            ScheduleDiscoveryRetry();
            return false;
        }
    }

    public void RestartDiscovery()
    {
        // Called from BACKGROUND threads — the SystemEvents thread on resume, and a thread-pool
        // thread on network change. Waking a laptop routinely fires both within moments of each
        // other, so two concurrent restarts are the normal case, not an edge case. Unsynchronised,
        // each read _browser/_mdns separately and could dispose a different instance than it
        // unsubscribed from, leaking a browser and its bound multicast socket on every sleep
        // cycle — or race Quit's teardown.
        lock (_discoveryLock)
        {
            if (_disposed) return;

            // Drop what discovery can no longer vouch for — but KEEP rows that are still
            // streaming.
            //
            // Most rows describe the world before this event: devices seen over a network that has
            // changed, and toggles for sessions the suspend/logoff handler already stopped. Those
            // must go, and unconditionally: clearing only after a successful rebuild meant a
            // failed rebuild — exactly what happens right after a wake, while Wi-Fi is still
            // reassociating — preserved the stale rows it should have dropped.
            //
            // An actively streaming row is the exception, because a network address change fires
            // for ordinary things (a Wi-Fi roam, a DHCP renewal, a VPN toggle) that do NOT stop
            // playback. Removing those rows made the picker claim a device was idle while it was
            // still playing, and left the header ("Streaming to 1 destination") contradicting the
            // list underneath it. Discovery not currently seeing a device is not evidence that it
            // stopped — the session itself reports that, and it has not.
            _dispatcher.TryEnqueue(() =>
            {
                foreach (var row in Rows.Where(r => !_streams.IsAudioActive(r.Key) && !_streams.IsMirroring(r.Key)).ToList())
                    Rows.Remove(row);
                _deviceCount = Rows.Count;
                RefreshStatus();
            });

            TryStartDiscovery();
        }
    }

    private int _discoveryRetryPending;

    /// <summary>
    /// Retries a failed discovery rebuild with a short backoff. Only one retry is ever in
    /// flight, so repeated failures cannot stack up timers.
    /// </summary>
    private void ScheduleDiscoveryRetry()
    {
        if (Interlocked.Exchange(ref _discoveryRetryPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                lock (_discoveryLock) { if (_disposed) return; }
                RestartDiscovery();
            }
            finally { Interlocked.Exchange(ref _discoveryRetryPending, 0); }
        });
    }

    /// <summary>
    /// A human-readable snapshot of what discovery currently sees, for the diagnostics bundle
    ///. Names/models/addresses make a bug actionable; the bundle redacts key material.
    /// </summary>
    public string DescribeDiscoveredDevices()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"picker rows: {Rows.Count}");
        foreach (var row in Rows)
            sb.AppendLine($"  ● {row.DisplayName} — {row.Subtitle} [{row.Entry.Kind}] key={row.Key}");
        sb.AppendLine();
        foreach (var row in Rows)
        {
            foreach (var member in row.Entry.Members)
            {
                sb.AppendLine($"  {member.Name} ({member.Model}) id={member.DeviceId} "
                    + $"addr={string.Join(",", member.Addresses)} features=0x{member.RawFeatures:X16}");
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ Now Playing surface

    private string _nowPlayingTitle = "";
    private string _nowPlayingArtist = "";
    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _nowPlayingArt;

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set
        {
            _nowPlayingTitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNowPlaying));
            OnPropertyChanged(nameof(HasNoNowPlaying));
        }
    }

    public string NowPlayingArtist
    {
        get => _nowPlayingArtist;
        private set { _nowPlayingArtist = value; OnPropertyChanged(); }
    }

    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? NowPlayingArt
    {
        get => _nowPlayingArt;
        private set { _nowPlayingArt = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNoArt)); }
    }

    public bool HasNowPlaying => _nowPlayingTitle.Length > 0;

    /// <summary>The card stays put and shows a quiet placeholder instead of collapsing.</summary>
    public bool HasNoNowPlaying => !HasNowPlaying;
    public bool HasNoArt => _nowPlayingArt is null;

    private void OnTrackChanged(string title, string artist, string album, byte[]? art) =>
        _dispatcher.TryEnqueue(async () =>
        {
            NowPlayingTitle = title;
            NowPlayingArtist = artist;
            if (art is { Length: > 0 })
            {
                try
                {
                    // Decode on the UI thread — BitmapImage is a UI-affine WinUI type.
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await stream.WriteAsync(art.AsBuffer());
                    stream.Seek(0);
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    NowPlayingArt = bitmap;
                }
                catch (Exception) { NowPlayingArt = null; }
            }
            else
            {
                NowPlayingArt = null;
            }
        });

    private Task OnVolumeAsync(PickerRowViewModel row, double percent) =>
        _streams.SetVolumeAsync(row.Key, PercentToDb(percent));

    /// <summary>0 % = AirPlay mute sentinel −144; otherwise linear −30…0 dB (see VolumeControl).</summary>
    private static double PercentToDb(double percent) => VolumeControl.PercentToDb(percent);

    /// <summary>
    /// Turns a failure into something the user can act on. Two reported bugs came down to every
    /// error collapsing to "Couldn't connect — try again": a HomePod refusing on access control
    /// looked identical to a network problem, so nobody could tell what to change. Each case
    /// that a user can actually fix now names the setting or the next step.
    /// </summary>
    /// <summary>
    /// Turns a failure into something the user can act on.
    ///
    /// <para>Multi-room connects run their members concurrently, so a real cause routinely arrives
    /// wrapped in an AggregateException — sometimes nested, when a retry wraps a retry. Unwrapping
    /// once here means every case below is written against the actual failure, instead of each one
    /// having to remember to also look inside the wrapper. Missing that check was how a HomePod
    /// refusing the connection came out as raw exception text.</para>
    /// </summary>
    private static string FriendlyError(Exception ex) => DescribeError(Unwrap(ex));

    /// <summary>
    /// Digs out the failure worth reporting. A flattened AggregateException from a group connect
    /// holds one entry per member, and they are usually the same failure repeated — the first
    /// meaningful one is what the user needs to read.
    /// </summary>
    private static Exception Unwrap(Exception ex)
    {
        if (ex is not AggregateException agg) return ex;
        var inners = agg.Flatten().InnerExceptions;
        if (inners.Count == 0) return ex;
        // Prefer a cause that says something specific over a bare cancellation from the siblings
        // that were abandoned once the first member failed.
        return inners.FirstOrDefault(i => i is not OperationCanceledException) ?? inners[0];
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        OperationCanceledException => "Cancelled",
        TimeoutException => "Couldn't reach the device — check it's on the same Wi-Fi",

        // The receiver refused us. Almost always the Home app's speaker-access setting.
        ReceiverAccessDeniedException => "This speaker isn't accepting connections. In the Home app, "
            + "hold the speaker → Settings → Speaker Access → allow \"Anyone on the Same Network\".",

        PairingRequiredException => "Needs pairing — a code will appear on the TV",

        ReceiverIdentityChangedException => "This device's identity changed since you last used it. "
            + "If you reset or replaced it, forget it in WinPlay to trust it again.",

        // Discovered but not yet dialable — the address record has not arrived, or only an IPv6
        // one has. Its own message already says what to do.
        ReceiverUnreachableException => ex.Message,

        _ when ex.Message.Contains("no member", StringComparison.OrdinalIgnoreCase)
            => "Device not ready yet — try again in a moment",
        _ when ex.Message.Contains("mirroring", StringComparison.OrdinalIgnoreCase) => ex.Message,

        // Anything genuinely unexpected keeps its real message rather than hiding behind a
        // generic string — an unhelpful error is what made these bugs impossible to report.
        _ => $"Couldn't connect — {ex.Message}",
    };

    // ------------------------------------------------------------ discovery projection

    private void OnDevicesChanged(IReadOnlyList<AirPlayDevice> devices)
    {
        var entries = DevicePicker.Collapse(devices);
        _dispatcher.TryEnqueue(() => Apply(entries, devices.Count));
    }

    private void Apply(List<PickerEntry> entries, int deviceCount)
    {
        var byKey = Rows.ToDictionary(r => r.Key);
        var seen = new HashSet<string>();

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            seen.Add(entry.Key);
            if (byKey.TryGetValue(entry.Key, out var row))
            {
                row.Update(entry);
                int currentIndex = Rows.IndexOf(row);
                if (currentIndex != i) Rows.Move(currentIndex, i);
            }
            else
            {
                Rows.Insert(Math.Min(i, Rows.Count), new PickerRowViewModel(entry)
                {
                    AudioToggleRequested = OnAudioToggleAsync,
                    MirrorToggleRequested = OnMirrorToggleAsync,
                    ModeChangeRequested = OnModeChangeAsync,
                    VolumeChanged = OnVolumeAsync,
                });
            }
        }
        // Only drop rows that are gone AND not actively streaming (don't yank a live session
        // because discovery briefly missed a device).
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Rows[i].Key)
                && !_streams.IsAudioActive(Rows[i].Key) && !_streams.IsMirroring(Rows[i].Key))
            {
                Rows.RemoveAt(i);
            }
        }

        _deviceCount = deviceCount;
        RefreshStatus();
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private void RefreshStatus()
    {
        int active = _streams.ActiveCount;
        Status = active > 0
            ? $"Streaming to {active} destination{(active == 1 ? "" : "s")}"
            : $"{Rows.Count} destination{(Rows.Count == 1 ? "" : "s")} available";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async ValueTask DisposeAsync()
    {
        // Take the same lock RestartDiscovery uses: a resume or network-change event can fire
        // while the user is quitting, and the two must not tear down / rebuild the transport
        // concurrently. Setting _disposed inside the lock makes any later restart a no-op.
        lock (_discoveryLock)
        {
            _disposed = true;
            // Both may legitimately be absent: the machine can have started, or last tried to
            // rebuild, with no network that would carry mDNS.
            if (_browser is { } browser)
            {
                browser.DevicesChanged -= OnDevicesChanged;
                browser.Dispose();
            }
        }
        await _remote.DisposeAsync();   // withdraws the DACP advertisement before the transport closes
        _mdns?.Dispose();               // owned here, since the browser shares it
        await _nowPlaying.DisposeAsync();
        await _streams.DisposeAsync();
    }
}
