// SPDX-License-Identifier: GPL-3.0-or-later
using Windows.Media.Control;

namespace WinPlay.App.Services;

/// <summary>
/// Reads the system's current "now playing" media (whatever app is playing — Spotify,
/// browser, a music player) via the Windows GlobalSystemMediaTransportControls, and
/// pushes title/artist/album (and cover art) to the active AirPlay sessions so the
/// receiver's Now Playing screen matches what's actually playing on the PC.
/// </summary>
public sealed class NowPlayingService : IAsyncDisposable
{
    private readonly StreamController _streams;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private string _lastSignature = "";
    private string _lastPushedSignature = "";

    /// <summary>The PC's current track changed: (title, artist, album, artwork or null). Drives
    /// the picker's Now Playing surface; raised whether or not anything is streaming.</summary>
    public event Action<string, string, string, byte[]?>? TrackChanged;

    public NowPlayingService(StreamController streams) => _streams = streams;

    public void Start() => _loop ??= Task.Run(() => PollLoopAsync(_cts.Token));

    private async Task PollLoopAsync(CancellationToken ct)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        try { manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { return; } // media transport controls unavailable

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var session = manager.GetCurrentSession();
                if (session is not null)
                {
                    var props = await session.TryGetMediaPropertiesAsync();
                    string title = props.Title ?? "";
                    string artist = props.Artist ?? props.AlbumArtist ?? "";
                    string album = props.AlbumTitle ?? "";
                    string signature = $"{title}{artist}{album}";
                    bool hasTrack = title.Length > 0 || artist.Length > 0;
                    byte[]? art = null;

                    // The picker's Now Playing surface follows the PC's media whether or not
                    // anything is streaming.
                    if (signature != _lastSignature && hasTrack)
                    {
                        _lastSignature = signature;
                        art = await TryReadThumbnailAsync(props);
                        TrackChanged?.Invoke(title, artist, album, art);
                    }

                    // Receivers get the track on change AND when a session starts mid-track:
                    // with nothing streaming the delivered-signature resets, so the first tick
                    // after a destination joins re-pushes the current track.
                    if (_streams.ActiveCount == 0)
                    {
                        _lastPushedSignature = "";
                    }
                    else if (signature != _lastPushedSignature && hasTrack)
                    {
                        _lastPushedSignature = signature;
                        art ??= await TryReadThumbnailAsync(props);
                        await _streams.PushNowPlayingAsync(title, artist, album, art).ConfigureAwait(false);
                    }

                    // Position drives the receiver's progress bar; pushed every tick because it
                    // advances continuously even when the track does not change.
                    var timeline = session.GetTimelineProperties();
                    if (_streams.ActiveCount > 0 && timeline is not null && timeline.EndTime > timeline.StartTime)
                    {
                        await _streams.PushProgressAsync(
                            timeline.Position - timeline.StartTime,
                            timeline.EndTime - timeline.StartTime).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception) { /* transient media-session errors are non-fatal */ }

            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task<byte[]?> TryReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail is null) return null;
        try
        {
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (Exception) { /* loop ends on cancellation */ }
        }
        _cts.Dispose();
    }
}
