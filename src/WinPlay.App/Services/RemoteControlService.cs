// SPDX-License-Identifier: GPL-3.0-or-later
using Windows.Media.Control;
using WinPlay.Core.Mdns;
using WinPlay.Core.Raop;

namespace WinPlay.App.Services;

/// <summary>
/// Closes the AirPlay control loop (Task D3): commands issued on the receiver — pause on a
/// HomePod, the Apple TV remote, or Control Center on an iPhone — are applied to whatever app is
/// playing on the PC.
///
/// <para>Three pieces cooperate. <see cref="DacpServer"/> listens for the receiver's HTTP control
/// requests; <see cref="MdnsServiceAdvertiser"/> publishes that endpoint as
/// <c>iTunes_Ctrl_&lt;DACP-ID&gt;._dacp._tcp.local</c> so the receiver can find it (the id matches
/// the <c>DACP-ID</c> header WinPlay sends on every RTSP request); and this service translates the
/// decoded commands into <see cref="GlobalSystemMediaTransportControlsSession"/> calls, which
/// Windows routes to the app that owns playback — Spotify, a browser, anything.</para>
///
/// <para>Volume commands are applied to the AirPlay destinations rather than the source app, so
/// changing volume on the HomePod changes that receiver's volume, exactly as it does from an
/// Apple device.</para>
/// </summary>
public sealed class RemoteControlService : IAsyncDisposable
{
    /// <summary>Step used for the receiver's volume-up/down keys, in AirPlay dB (0 = full, −30 = min).</summary>
    private const double VolumeStepDb = 3.0;

    private readonly StreamController _streams;
    private readonly DacpServer _dacp;
    private MdnsServiceAdvertiser? _advertiser;
    private GlobalSystemMediaTransportControlsSessionManager? _sessions;
    private bool _started;

    public event Action<string>? Diagnostic;

    /// <summary>
    /// </summary>
    /// <remarks>
    /// Deliberately takes no mDNS transport. Answering remote-control commands needs a TCP
    /// listener and nothing else; only ADVERTISING where that listener lives needs multicast.
    /// Requiring a transport up front conflated the two, which meant this service could not
    /// exist at all until the network did — and on a machine that launches WinPlay at logon,
    /// the network frequently is not up yet. Call <see cref="Readvertise"/> once a transport
    /// becomes available, and again whenever it is replaced.
    /// </remarks>
    public RemoteControlService(StreamController streams)
    {
        _streams = streams;
        _dacp = new DacpServer(DacpIdentity.DacpId, DacpIdentity.ActiveRemote);
        _dacp.Diagnostic += d => Diagnostic?.Invoke(d);
        _dacp.CommandReceived += OnCommand;
        _dacp.VolumeRequested += OnVolumeRequested;
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        try { _sessions = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception ex)
        {
            // Without media-transport access we can still accept commands; they simply have no
            // source app to drive. Volume control keeps working.
            Diagnostic?.Invoke($"media transport controls unavailable: {ex.Message}");
        }

        // The listener comes up regardless of the network. Advertising follows separately,
        // whenever a transport exists — see Readvertise.
        _dacp.Start();
    }

    /// <summary>
    /// Publishes the DACP endpoint on an mDNS transport — the first time one becomes available,
    /// and again each time it is replaced after a network change or a resume. The DACP server
    /// itself keeps running throughout; its TCP port and identity are unaffected. What goes stale
    /// is the advertisement, which lived on a transport whose multicast joins no longer hold, so
    /// receivers could no longer resolve where to send commands.
    /// </summary>
    public void Readvertise(IMdnsTransport transport)
    {
        if (!_started) return;
        _advertiser?.Dispose();
        _advertiser = new MdnsServiceAdvertiser(transport, "_dacp._tcp.local",
            _dacp.ServiceInstanceName, _dacp.Port,
            new Dictionary<string, string> { ["txtvers"] = "1", ["Ver"] = "131077", ["DbId"] = DacpIdentity.DacpId });
        _advertiser.Diagnostic += d => Diagnostic?.Invoke(d);
        _advertiser.Start();
    }

    private void OnCommand(DacpCommand command)
    {
        // Volume keys address the AirPlay destinations, not the source app.
        if (command is DacpCommand.VolumeUp or DacpCommand.VolumeDown)
        {
            OnVolumeStep(command == DacpCommand.VolumeUp);
            return;
        }
        if (command == DacpCommand.MuteToggle)
        {
            bool muted = _streams.CurrentVolumeDb <= VolumeControl.MutedDb;
            _ = _streams.SetAllVolumesAsync(muted ? VolumeControl.MaxDb : VolumeControl.MutedDb);
            return;
        }
        _ = ApplyAsync(command);
    }

    private async Task ApplyAsync(DacpCommand command)
    {
        var session = _sessions?.GetCurrentSession();
        if (session is null)
        {
            Diagnostic?.Invoke($"remote command {command} ignored: nothing is playing on this PC");
            return;
        }

        try
        {
            bool handled = command switch
            {
                DacpCommand.PlayPause => await session.TryTogglePlayPauseAsync(),
                DacpCommand.Play => await session.TryPlayAsync(),
                DacpCommand.Pause => await session.TryPauseAsync(),
                DacpCommand.Stop => await session.TryStopAsync(),
                DacpCommand.Next => await session.TrySkipNextAsync(),
                DacpCommand.Previous => await session.TrySkipPreviousAsync(),
                DacpCommand.ShuffleToggle => await ToggleShuffleAsync(session),
                DacpCommand.RepeatToggle => await AdvanceRepeatAsync(session),
                _ => false,
            };
            Diagnostic?.Invoke($"remote command {command} → {(handled ? "applied" : "not supported by the source app")}");
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"remote command {command} failed: {ex.Message}");
        }
    }

    private static async Task<bool> ToggleShuffleAsync(GlobalSystemMediaTransportControlsSession session)
    {
        bool current = session.GetPlaybackInfo()?.IsShuffleActive ?? false;
        return await session.TryChangeShuffleActiveAsync(!current);
    }

    private static async Task<bool> AdvanceRepeatAsync(GlobalSystemMediaTransportControlsSession session)
    {
        // Cycle none → track → list, matching the receiver's repeat button.
        var next = session.GetPlaybackInfo()?.AutoRepeatMode switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            Windows.Media.MediaPlaybackAutoRepeatMode.Track => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
        };
        return await session.TryChangeAutoRepeatModeAsync(next);
    }

    /// <summary>Volume from the receiver applies to the AirPlay destinations, not the PC's mixer.</summary>
    private void OnVolumeRequested(double db) => _ = _streams.SetAllVolumesAsync(db);

    /// <summary>Handles the receiver's relative volume keys against the current destination volume.</summary>
    private void OnVolumeStep(bool up) =>
        _ = _streams.SetAllVolumesAsync(Math.Clamp(_streams.CurrentVolumeDb + (up ? VolumeStepDb : -VolumeStepDb),
            VolumeControl.MinDb, VolumeControl.MaxDb));

    public ValueTask DisposeAsync()
    {
        _advertiser?.Dispose();
        _dacp.Dispose();
        return ValueTask.CompletedTask;
    }
}
