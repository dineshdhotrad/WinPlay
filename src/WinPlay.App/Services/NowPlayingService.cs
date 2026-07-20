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
                if (session is not null && _streams.ActiveCount > 0)
                {
                    var props = await session.TryGetMediaPropertiesAsync();
                    string title = props.Title ?? "";
                    string artist = props.Artist ?? props.AlbumArtist ?? "";
                    string album = props.AlbumTitle ?? "";
                    string signature = $"{title}{artist}{album}";
                    if (signature != _lastSignature && (title.Length > 0 || artist.Length > 0))
                    {
                        _lastSignature = signature;
                        byte[]? art = await TryReadThumbnailAsync(props);
                        await _streams.PushNowPlayingAsync(title, artist, album, art).ConfigureAwait(false);
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
