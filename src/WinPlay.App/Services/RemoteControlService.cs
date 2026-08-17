// SPDX-License-Identifier: GPL-3.0-or-later
using Windows.Media.Control;
using WinPlay.Core.Mdns;
using WinPlay.Core.Raop;

using WinPlay.Diagnostics;

namespace WinPlay.App.Services;

/// <summary>
/// Closes the AirPlay control loop: commands issued on the receiver — pause on a
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

        // Hand the streaming layer a way to mint and retire per-destination tokens. Callbacks
        // rather than a reference: this service already depends on StreamController, and a cycle
        // between the two would be a worse answer than two delegates.
        _streams.IssueRemoteToken = _dacp.IssueToken;
        _streams.RevokeRemoteToken = _dacp.RevokeToken;
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

    private void OnCommand(string origin, DacpCommand command)
    {
        // Volume keys address the AirPlay destination that pressed them, not the source app and
        // not every destination.
        if (command is DacpCommand.VolumeUp or DacpCommand.VolumeDown)
        {
            OnVolumeStep(origin, command == DacpCommand.VolumeUp);
            return;
        }
        if (command == DacpCommand.MuteToggle)
        {
            double current = _streams.IsAudioActive(origin) ? _streams.VolumeOf(origin) : _streams.CurrentVolumeDb;
            bool muted = current <= VolumeControl.MutedDb;
            _ = ApplyVolumeAsync(origin, muted ? VolumeControl.MaxDb : VolumeControl.MutedDb);
            return;
        }
        // Everything below is transport — play, pause, next — which addresses the one Windows
        // media session this PC has. The origin is deliberately ignored for those.
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

    /// <summary>
    /// Volume from a receiver applies to the AirPlay destination that ASKED, not the PC's mixer
    /// and not every destination.
    ///
    /// <para>Turning the dial on a HomePod means "make this speaker quieter". Applied to every
    /// active destination, it also silenced an unrelated room the user happened to be streaming
    /// to at the same time — a control doing something the person touching it did not ask for.
    /// The origin comes from the per-destination Active-Remote token the receiver echoes back
    /// (see DacpServer.IssueToken). "this PC" is the shared identity, used by receivers paired
    /// before a per-destination token existed; with nothing to attribute it to, applying it to
    /// everything is the only sensible reading.</para>
    /// </summary>
    /// <summary>
    /// A receiver reporting its ABSOLUTE volume. This is state, not intent — devices announce
    /// their current level shortly after a session starts — and the receiver already has the
    /// volume it is describing, so there is nothing to write anywhere. Writing it back treated
    /// every announcement as a command: a device announcing its idle muted level had that mute
    /// pushed back at it seconds into a healthy stream, and — catastrophically — an announcement
    /// whose origin did not resolve to an active session was applied to EVERY destination, so one
    /// speaker's muted state silenced the whole house. Announcements are recorded for the UI and
    /// go no further.
    /// </summary>
    private void OnVolumeRequested(string origin, double db)
    {
        WinPlayLog.For("Dacp").Information("{Origin}: announced volume {Db:F1} dB (recorded, not echoed)", origin, db);
        _streams.RecordExternalVolume(origin, db);
    }

    /// <summary>
    /// Applies a USER ACTION taken on a receiver (volume step, mute toggle) — the cases where
    /// intent is unambiguous. Strictly to its origin: a command whose origin cannot be resolved is
    /// dropped and logged, never broadcast — "apply it to everything" turned one unresolvable
    /// origin into a house-wide volume write.
    /// </summary>
    private Task ApplyVolumeAsync(string origin, double db)
    {
        if (_streams.IsAudioActive(origin))
        {
            WinPlayLog.For("Dacp").Information("{Origin}: user volume action → {Db:F1} dB", origin, db);
            return _streams.SetVolumeAsync(origin, db);
        }
        WinPlayLog.For("Dacp").Warning("{Origin}: volume action dropped — origin is not an active destination", origin);
        return Task.CompletedTask;
    }

    /// <summary>Handles a receiver's relative volume keys against that destination's own volume.</summary>
    private void OnVolumeStep(string origin, bool up)
    {
        double current = _streams.IsAudioActive(origin) ? _streams.VolumeOf(origin) : _streams.CurrentVolumeDb;
        double target = Math.Clamp(current + (up ? VolumeStepDb : -VolumeStepDb),
            VolumeControl.MinDb, VolumeControl.MaxDb);
        _ = ApplyVolumeAsync(origin, target);
    }

    public ValueTask DisposeAsync()
    {
        _advertiser?.Dispose();
        _dacp.Dispose();
        return ValueTask.CompletedTask;
    }
}
