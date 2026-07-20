// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using WinPlay.App.Services;
using WinPlay.Core.Discovery;

namespace WinPlay.App.ViewModels;

/// <summary>
/// Owns discovery + streaming. Projects browser snapshots into picker rows on the UI
/// thread (updated in place, keyed by group/device ID) and turns row actions into
/// <see cref="StreamController"/> sessions.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly AirPlayBrowser _browser;
    private readonly StreamController _streams = new();
    private readonly NowPlayingService _nowPlaying;
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
        _streams.SessionFailed += (key, ex) => _dispatcher.TryEnqueue(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Key == key);
            row?.OnFailed(FriendlyError(ex));
        });

        _browser = new AirPlayBrowser();
        _browser.DevicesChanged += OnDevicesChanged;
        _browser.Start();

        _nowPlaying = new NowPlayingService(_streams);
        _nowPlaying.Start();
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
        row.IsBusy = true;
        try
        {
            if (on)
            {
                row.SetStatus("Connecting…");
                await _streams.StartAudioAsync(row.Entry, PercentToDb(row.VolumePercent), CancellationToken.None);
                _dispatcher.TryEnqueue(() => { row.SetStreamingStatus(); RefreshStatus(); });
            }
            else
            {
                await _streams.StopAudioAsync(row.Key);
                _dispatcher.TryEnqueue(() => { row.SetStatus(null); RefreshStatus(); });
            }
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
            {
                row.SetAudioCheckedSilently(false);
                row.SetStatus(FriendlyError(ex));
            });
        }
        finally
        {
            _dispatcher.TryEnqueue(() => row.IsBusy = false);
        }
    }

    private async Task OnMirrorToggleAsync(PickerRowViewModel row, bool on)
    {
        row.IsBusy = true;
        try
        {
            if (on)
            {
                row.SetStatus("Starting mirroring…");
                await _streams.StartMirrorAsync(row.Entry, CancellationToken.None);
                _dispatcher.TryEnqueue(() => { row.SetStatus("Mirroring your screen"); RefreshStatus(); });
            }
            else
            {
                await _streams.StopMirrorAsync(row.Key);
                _dispatcher.TryEnqueue(() => { row.SetStatus(null); RefreshStatus(); });
            }
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
            {
                row.SetMirrorCheckedSilently(false);
                row.SetStatus(FriendlyError(ex));
            });
        }
        finally
        {
            _dispatcher.TryEnqueue(() => row.IsBusy = false);
        }
    }

    private Task OnVolumeAsync(PickerRowViewModel row, double percent) =>
        _streams.SetVolumeAsync(row.Key, PercentToDb(percent));

    /// <summary>0 % = AirPlay mute sentinel −144; otherwise linear −30…0 dBFS.</summary>
    private static double PercentToDb(double percent) =>
        percent <= 0.5 ? -144.0 : -30.0 + (percent / 100.0) * 30.0;

    private static string FriendlyError(Exception ex) => ex switch
    {
        OperationCanceledException => "Cancelled",
        TimeoutException => "Couldn't reach the device",
        _ when ex.Message.Contains("no member", StringComparison.OrdinalIgnoreCase) => "Device not ready yet — try again",
        _ when ex.Message.Contains("mirroring", StringComparison.OrdinalIgnoreCase) => ex.Message,
        _ => "Couldn't connect — try again",
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
        _browser.DevicesChanged -= OnDevicesChanged;
        _browser.Dispose();
        await _nowPlaying.DisposeAsync();
        await _streams.DisposeAsync();
    }
}
