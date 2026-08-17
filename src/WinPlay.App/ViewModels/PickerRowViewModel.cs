// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using WinPlay.App.Services;
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
    private bool _isAudioBusy;
    private bool _isMirrorBusy;
    private string? _statusOverride;
    private string? _deferredAudioFault;
    private string? _deferredMirrorFault;
    private double _volumePercent = 60;
    private int _audioEpoch;
    private int _mirrorEpoch;
    private CancellationTokenSource? _audioAttempt;
    private CancellationTokenSource? _mirrorAttempt;

    public PickerRowViewModel(PickerEntry entry) => Entry = entry;

    /// <summary>Invoked when the user turns audio streaming on/off for this row.</summary>
    public Func<PickerRowViewModel, bool, Task>? AudioToggleRequested { get; set; }

    /// <summary>Invoked when the user turns screen mirroring on/off for this row.</summary>
    public Func<PickerRowViewModel, bool, Task>? MirrorToggleRequested { get; set; }

    /// <summary>
    /// Invoked when the user picks a mode on an Apple TV row. The owner performs the coordinated
    /// stop/start and then commits the outcome with <see cref="SetModeSilently"/>.
    /// </summary>
    public Func<PickerRowViewModel, TvStreamMode, Task>? ModeChangeRequested { get; set; }

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

    /// <summary>
    /// Segoe Fluent Icons glyph for this row's device kind.
    ///
    /// <para>Every audio row draws an actual speaker. Two earlier attempts borrowed an unrelated
    /// icon for a multi-room group and both read as wrong at a glance: E902 ("Group") is a
    /// three-person silhouette, so a set of speakers was drawn as a picture of people, and E772
    /// ("Devices") renders as a rack panel of boxes and dials, so it was drawn as server
    /// hardware. Neither depicts a speaker, and no amount of styling fixes an icon that shows the
    /// wrong object.</para>
    ///
    /// <para>A group is MORE SPEAKERS, not a different kind of thing — so it gets a speaker, with
    /// a second layered behind it (see <see cref="PairGlyph"/>) to say "more than one". What
    /// separates a pair from a three-room group is the subtitle, which says it in words instead
    /// of asking the user to tell two similar pictograms apart at 18 pixels.</para>
    /// </summary>
    public string Glyph => CanMirror
        ? "\uE7F4"                                    // TVMonitor: Apple TV / AirPlay 2 TV
        : "\uE7F5";                                   // Speaker: every audio destination

    /// <summary>
    /// A second speaker layered behind <see cref="Glyph"/> whenever the row is more than one
    /// device, so "several speakers" is legible at a glance and at icon size.
    /// </summary>
    public string PairGlyph => HasPairGlyph ? "\uE7F5" : "";

    /// <summary>True when this row is a stereo pair or a multi-room group of speakers.</summary>
    public bool HasPairGlyph => !CanMirror
        && Entry.Kind is PickerEntryKind.StereoPair or PickerEntryKind.Group;

    /// <summary>Mirror-button glyph (Project: the icon Windows itself uses for screen projection).</summary>
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

    // ---- Apple TV mode ----
    //
    // A TV row used to carry two independent switches, Audio and Mirror. Both could be on, and
    // that started TWO sessions to the same device on TWO clocks — picture and sound drifting
    // apart by construction. Worse, the mirror session dropped its own audio when an audio-only
    // session already existed, so which stream you actually got depended on the order you pressed
    // the buttons.
    //
    // There is only ever one mode. Screen+Audio is ONE mirror session carrying both on one clock,
    // which is the only arrangement that stays in sync. The illegal combination is not warned
    // about or corrected after the fact — it is unreachable, because nothing can select two modes.

    /// <summary>What an Apple TV row is currently doing.</summary>
    public enum TvStreamMode
    {
        Off,
        AudioOnly,
        ScreenOnly,
        /// <summary>Screen and sound together, in one session on one clock.</summary>
        Both,
    }

    private TvStreamMode _mode;

    public TvStreamMode Mode => _mode;
    public bool IsAudioOnlySelected => _mode == TvStreamMode.AudioOnly;
    public bool IsScreenOnlySelected => _mode == TvStreamMode.ScreenOnly;
    public bool IsBothSelected => _mode == TvStreamMode.Both;

    /// <summary>The pills are disabled mid-transition, so a second press cannot race the first.</summary>
    public bool IsModeSelectorEnabled => !IsBusy;

    /// <summary>Records the mode actually reached, without asking for another change.</summary>
    public void SetModeSilently(TvStreamMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsAudioOnlySelected));
        OnPropertyChanged(nameof(IsScreenOnlySelected));
        OnPropertyChanged(nameof(IsBothSelected));
        OnPropertyChanged(nameof(ShowVolume));
    }

    /// <summary>Pressing the mode already selected turns it off, matching the speaker rows.</summary>
    private void RequestMode(TvStreamMode requested) =>
        _ = ModeChangeRequested?.Invoke(this, _mode == requested ? TvStreamMode.Off : requested);

    public void OnAudioOnlyClicked(object sender, RoutedEventArgs e) => RequestMode(TvStreamMode.AudioOnly);
    public void OnScreenOnlyClicked(object sender, RoutedEventArgs e) => RequestMode(TvStreamMode.ScreenOnly);
    public void OnBothClicked(object sender, RoutedEventArgs e) => RequestMode(TvStreamMode.Both);

    // ---- attempt identity ----
    //
    // Connecting to a receiver takes seconds, and its result is written back into this row long
    // after the user moved on. Without an identity for the attempt, the LAST write wins instead
    // of the LATEST INTENT: switch on, change your mind, switch off — and the abandoned attempt's
    // failure then flips the switch back and paints an error over a row the user already settled.
    // A fault from a session that has since been replaced does the same thing.
    //
    // So each channel carries a monotonic epoch. Any new user action bumps it and cancels the
    // attempt it supersedes, and every deferred write is dropped unless its epoch is still
    // current. Every mutation here happens on the UI thread — toggles arrive from bindings,
    // completions come back through the DispatcherQueue — so a plain int is the whole mechanism;
    // no interlocked, no lock.

    /// <summary>
    /// Identifies one connect/disconnect attempt and carries the token that cancels it. The epoch
    /// is only meaningful within its own channel, so the channel is part of the identity — audio
    /// attempt 3 and mirror attempt 3 are different attempts.
    /// The attempt owns the source and disposes it via <see cref="Dispose"/> when it unwinds.
    /// </summary>
    public sealed class Attempt(StreamChannel channel, int epoch, CancellationTokenSource cts) : IDisposable
    {
        public StreamChannel Channel { get; } = channel;
        public int Epoch { get; } = epoch;
        public CancellationToken Token { get; } = cts.Token;
        public void Dispose() => cts.Dispose();
    }

    /// <summary>
    /// Opens a new audio attempt, cancelling any attempt still in flight. Cancelling is what makes
    /// switching off mid-connect actually stop the connect instead of waiting out its full timeout
    /// and then reporting a failure nobody is waiting for.
    /// </summary>
    public Attempt BeginAudioAttempt()
    {
        // The previous attempt's source may already be disposed: End* is dispatched to the UI
        // queue while the owning `using` scope disposes immediately, and a fast next click can
        // land between the two. Cancelling a disposed source throws; a disposed source needs no
        // cancelling.
        try { _audioAttempt?.Cancel(); } catch (ObjectDisposedException) { }          // the superseded attempt disposes its own source
        _deferredAudioFault = null;       // a previous session's fault must not leak into this one
        var cts = new CancellationTokenSource();
        _audioAttempt = cts;
        IsAudioBusy = true;
        return new Attempt(StreamChannel.Audio, ++_audioEpoch, cts);
    }

    public Attempt BeginMirrorAttempt()
    {
        // The previous attempt's source may already be disposed: End* is dispatched to the UI
        // queue while the owning `using` scope disposes immediately, and a fast next click can
        // land between the two. Cancelling a disposed source throws; a disposed source needs no
        // cancelling.
        try { _mirrorAttempt?.Cancel(); } catch (ObjectDisposedException) { }
        _deferredMirrorFault = null;
        var cts = new CancellationTokenSource();
        _mirrorAttempt = cts;
        IsMirrorBusy = true;
        return new Attempt(StreamChannel.Mirror, ++_mirrorEpoch, cts);
    }

    /// <summary>True while <paramref name="attempt"/> is still the one the user is waiting on.</summary>
    public bool IsCurrent(Attempt attempt) => attempt.Epoch ==
        (attempt.Channel == StreamChannel.Audio ? _audioEpoch : _mirrorEpoch);

    /// <summary>
    /// Closes out an audio attempt. Clears the busy flag only if no newer attempt has taken over —
    /// otherwise the superseded attempt would report "idle" over a connect that is still running.
    /// </summary>
    public void EndAudioAttempt(Attempt attempt)
    {
        if (!IsCurrent(attempt)) return;
        _audioAttempt = null;
        IsAudioBusy = false;
    }

    public void EndMirrorAttempt(Attempt attempt)
    {
        if (!IsCurrent(attempt)) return;
        _mirrorAttempt = null;
        IsMirrorBusy = false;
    }

    // ---- shared state ----

    /// <summary>
    /// Audio and mirroring are independent channels and are tracked separately. Sharing one flag
    /// meant starting mirroring hid the audio volume slider and its checkmark — state belonging to
    /// a stream that was playing fine and had nothing to do with the mirror connect.
    /// </summary>
    public bool IsAudioBusy
    {
        get => _isAudioBusy;
        private set
        {
            if (_isAudioBusy == value) return;
            _isAudioBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsModeSelectorEnabled));
            OnPropertyChanged(nameof(ShowVolume));
            OnPropertyChanged(nameof(ShowAudioCheck));
        }
    }

    public bool IsMirrorBusy
    {
        get => _isMirrorBusy;
        private set
        {
            if (_isMirrorBusy == value) return;
            _isMirrorBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsModeSelectorEnabled));
            OnPropertyChanged(nameof(ShowVolume));
        }
    }

    /// <summary>True while either channel is mid-transition; drives the row's progress affordance.</summary>
    public bool IsBusy => _isAudioBusy || _isMirrorBusy;

    /// <summary>
    /// The volume slider belongs to whatever is actually carrying audio: a speaker row's audio
    /// toggle, or a TV row in a mode that includes sound.
    /// </summary>
    public bool ShowVolume => IsAudioOnly
        ? _isAudioChecked && !_isAudioBusy
        : _mode is TvStreamMode.AudioOnly or TvStreamMode.Both && !IsBusy;

    /// <summary>Trailing checkmark: audio is on and not mid-connect.</summary>
    public bool ShowAudioCheck => _isAudioChecked && !_isAudioBusy;

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

    /// <summary>
    /// Updates the slider to a volume that originated ON the receiver (its own volume keys,
    /// arriving over DACP). Deliberately does NOT raise <see cref="VolumeChanged"/>: echoing the
    /// value back would fight the receiver for control of its own volume.
    /// </summary>
    public void SetVolumeFromRemote(double percent)
    {
        if (Math.Abs(_volumePercent - percent) < 0.5) return;
        _volumePercent = percent;
        OnPropertyChanged(nameof(VolumePercent));
    }

    public void SetStatus(string? status)
    {
        _statusOverride = status;
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>
    /// Settles the row after a successful audio connect. If the audio session faulted during the
    /// sliver between the connect returning and the attempt closing, that fault was parked rather
    /// than dropped — apply it now, so the row never claims to be streaming to a receiver that is
    /// already gone.
    /// </summary>
    public void SetStreamingStatus()
    {
        if (Drain(StreamChannel.Audio)) return;
        SetStatus(_isMirrorChecked ? "Mirroring your screen" : "Streaming system audio");
    }

    /// <summary>The mirror equivalent of <see cref="SetStreamingStatus"/>, including the drain.</summary>
    public void SetMirroringStatus()
    {
        if (Drain(StreamChannel.Mirror)) return;
        SetStatus("Mirroring your screen");
    }

    /// <summary>
    /// Applies a fault parked while this channel was mid-attempt, if there is one. Each channel
    /// keeps its own: a mirror connect finishing successfully says nothing about whether an audio
    /// fault that arrived meanwhile is still true, and draining one through the other's success
    /// path would either discard it or report it against the wrong stream.
    /// </summary>
    private bool Drain(StreamChannel channel)
    {
        ref string? parked = ref (channel == StreamChannel.Audio ? ref _deferredAudioFault : ref _deferredMirrorFault);
        if (parked is not { } fault) return false;
        parked = null;
        ApplyFault(channel, fault);
        return true;
    }

    /// <summary>Translates a raw protocol stage into a friendly status while connecting.</summary>
    public void OnStage(string stage)
    {
        // A reconnect is progress the user needs to see even though no toggle is in flight: the
        // receiver dropped on its own, so IsBusy is false, and gating on it meant every
        // "connection lost — reconnecting…" message was thrown away. The row sat there claiming
        // to be streaming while nothing played.
        bool isRecovery = stage.Contains("reconnect", StringComparison.OrdinalIgnoreCase)
                          || stage.Contains("connection lost", StringComparison.OrdinalIgnoreCase);
        if (!IsBusy && !isRecovery) return; // otherwise only surface progress while connecting
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

    /// <summary>
    /// A LIVE session for this row faulted — the receiver dropped off mid-stream.
    ///
    /// <para>Scoped to the channel that actually failed. Audio and mirroring to one device are
    /// separate sessions, and clearing both on either one's failure meant an audio dropout
    /// switched the mirroring toggle off while the TV was still receiving the screen — the toggle
    /// stopped describing anything real, and switching it back on did nothing because as far as
    /// the app was concerned mirroring was already running.</para>
    ///
    /// <para>While that channel has an attempt in flight the fault belongs to the session the
    /// attempt is replacing, so it is parked instead of applied: applying it would switch off a
    /// row the user is actively switching on. The matching success path drains it, and beginning
    /// another attempt on that channel discards it.</para>
    /// </summary>
    public void OnFailed(StreamChannel channel, string friendlyMessage)
    {
        bool busy = channel == StreamChannel.Audio ? _isAudioBusy : _isMirrorBusy;
        if (busy)
        {
            if (channel == StreamChannel.Audio) _deferredAudioFault = friendlyMessage;
            else _deferredMirrorFault = friendlyMessage;
            return;
        }
        ApplyFault(channel, friendlyMessage);
    }

    private void ApplyFault(StreamChannel channel, string friendlyMessage)
    {
        if (channel == StreamChannel.Audio)
        {
            SetAudioCheckedSilently(false);
            IsAudioBusy = false;
        }
        else
        {
            SetMirrorCheckedSilently(false);
            IsMirrorBusy = false;
        }
        SetStatus(friendlyMessage);
    }

    /// <summary>
    /// Returns the row to "nothing is running", without asking the controller to stop anything.
    ///
    /// <para>For stops that happened outside the picker — sleep, logoff, a fast user switch — where
    /// the sessions are already gone and the row is the only thing still claiming otherwise. Both
    /// channels are advanced to a fresh epoch so that if a connect was in flight when the machine
    /// suspended, its result cannot land afterwards and switch the row back on.</para>
    /// </summary>
    public void ResetToIdle()
    {
        _audioAttempt?.Cancel();
        _mirrorAttempt?.Cancel();
        _audioAttempt = null;
        _mirrorAttempt = null;
        _audioEpoch++;
        _mirrorEpoch++;
        _deferredAudioFault = null;
        _deferredMirrorFault = null;
        SetAudioCheckedSilently(false);
        SetMirrorCheckedSilently(false);
        IsAudioBusy = false;
        IsMirrorBusy = false;
        SetStatus(null);
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