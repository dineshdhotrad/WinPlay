// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinPlay.Core.Discovery;

namespace WinPlay.App.ViewModels;

/// <summary>
/// One picker row (a collapsed <see cref="PickerEntry"/>) with its audio + mirroring state.
/// Exposes friendly, user-facing status — raw protocol stage messages are filtered out so
/// the flyout never shows log noise.
/// </summary>
public sealed class PickerRowViewModel : INotifyPropertyChanged
{
    private bool _isAudioChecked;
    private bool _isMirrorChecked;
    private bool _isBusy;
    private string? _statusOverride;
    private double _volumePercent = 60;

    public PickerRowViewModel(PickerEntry entry) => Entry = entry;

    /// <summary>Invoked when the user turns audio streaming on/off for this row.</summary>
    public Func<PickerRowViewModel, bool, Task>? AudioToggleRequested { get; set; }

    /// <summary>Invoked when the user turns screen mirroring on/off for this row.</summary>
    public Func<PickerRowViewModel, bool, Task>? MirrorToggleRequested { get; set; }

    public Func<PickerRowViewModel, double, Task>? VolumeChanged { get; set; }

    public PickerEntry Entry { get; private set; }

    public string Key => Entry.Key;
    public string DisplayName => Entry.DisplayName;
    public bool IsAudioCapable => Entry.IsAudioCapable;

    /// <summary>Mirroring is offered only for Apple TV / AirPlay 2 TV rows.</summary>
    public bool CanMirror => Entry.IsMirroringCapable
        && Entry.Leader.Subtype == AirPlayDeviceSubtype.AppleTv;

    /// <summary>Audio-only device (speaker / HomePod / stereo pair) — the whole row toggles audio.</summary>
    public bool IsAudioOnly => !CanMirror;

    /// <summary>Friendly device-kind label shown under the name (e.g. "HomePod mini", "Stereo Pair", "Apple TV").</summary>
    public string TypeLabel => Entry.Subtitle;

    public string Subtitle => _statusOverride ?? Entry.Subtitle;

    /// <summary>Segoe Fluent Icons glyph, chosen by device kind.</summary>
    public string Glyph => CanMirror
        ? "\uE7F4"                                 // Tv (Apple TV)
        : Entry.Kind == PickerEntryKind.Group ? "\uE902" : "\uE7F5"; // group / speakers

    /// <summary>Mirror-button glyph.</summary>
    public string MirrorGlyph => "\uEBC6";

    public string MembersTooltip => string.Join("\n",
        Entry.Members.Select(m => $"{m.Name} — {DevicePicker.FriendlyModel(m.Model)}"));

    // ---- audio toggle ----

    public bool IsAudioChecked
    {
        get => _isAudioChecked;
        set
        {
            if (_isAudioChecked == value) return;
            _isAudioChecked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowVolume));
            OnPropertyChanged(nameof(ShowAudioCheck));
            _ = AudioToggleRequested?.Invoke(this, value);
        }
    }

    public void SetAudioCheckedSilently(bool value)
    {
        if (_isAudioChecked == value) return;
        _isAudioChecked = value;
        OnPropertyChanged(nameof(IsAudioChecked));
        OnPropertyChanged(nameof(ShowVolume));
        OnPropertyChanged(nameof(ShowAudioCheck));
    }

    // ---- mirror toggle ----

    public bool IsMirrorChecked
    {
        get => _isMirrorChecked;
        set
        {
            if (_isMirrorChecked == value) return;
            _isMirrorChecked = value;
            OnPropertyChanged();
            _ = MirrorToggleRequested?.Invoke(this, value);
        }
    }

    public void SetMirrorCheckedSilently(bool value)
    {
        if (_isMirrorChecked == value) return;
        _isMirrorChecked = value;
        OnPropertyChanged(nameof(IsMirrorChecked));
    }

    // ---- shared state ----

    public bool IsBusy
    {
        get => _isBusy;
        set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowVolume)); OnPropertyChanged(nameof(ShowAudioCheck)); } }
    }

    public bool ShowVolume => _isAudioChecked && !_isBusy;

    /// <summary>Trailing checkmark: audio is on and not mid-connect.</summary>
    public bool ShowAudioCheck => _isAudioChecked && !_isBusy;

    /// <summary>0–100 UI volume; mapped to AirPlay dBFS by the owner.</summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (Math.Abs(_volumePercent - value) < 0.5) return;
            _volumePercent = value;
            OnPropertyChanged();
            _ = VolumeChanged?.Invoke(this, value);
        }
    }

    public void SetStatus(string? status)
    {
        _statusOverride = status;
        OnPropertyChanged(nameof(Subtitle));
    }

    public void SetStreamingStatus() => SetStatus(_isMirrorChecked ? "Mirroring your screen" : "Streaming system audio");

    /// <summary>Translates a raw protocol stage into a friendly status while connecting.</summary>
    public void OnStage(string stage)
    {
        if (!_isBusy) return; // only surface progress while connecting
        string friendly = stage switch
        {
            _ when stage.Contains("PIN", StringComparison.OrdinalIgnoreCase) => "Enter the PIN shown on your device",
            _ when stage.Contains("pair", StringComparison.OrdinalIgnoreCase) => "Pairing…",
            _ when stage.Contains("session live", StringComparison.Ordinal) => "Almost ready…",
            _ when stage.Contains("reconnect", StringComparison.OrdinalIgnoreCase) => "Reconnecting…",
            _ => "Connecting…",
        };
        SetStatus(friendly);
    }

    public void OnFailed(string friendlyMessage)
    {
        SetAudioCheckedSilently(false);
        SetMirrorCheckedSilently(false);
        IsBusy = false;
        SetStatus(friendlyMessage);
    }

    /// <summary>Refreshes from a newer collapse result, preserving interaction state.</summary>
    public void Update(PickerEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(MembersTooltip));
        OnPropertyChanged(nameof(IsAudioCapable));
        OnPropertyChanged(nameof(CanMirror));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}