// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Rtsp;

namespace WinPlay.Core.Hap;

/// <summary>
/// Thrown when a receiver asks for on-screen PIN pairing before it accepts a session — RTSP
/// <c>470 Connection Authorization Required</c>. This is the Apple TV "Require Device
/// Verification" flow: the receiver displays a code the user types once.
/// </summary>
public sealed class PairingRequiredException(string receiver)
    : Exception($"{receiver} requires PIN pairing (Connection Authorization) — pair once, credentials are then stored");

/// <summary>
/// Thrown when stored pairing credentials are no longer accepted — the receiver was reset,
/// restored from a backup, or re-paired elsewhere, so it has forgotten this controller.
///
/// <para>Distinct from the other pairing failures because the correct response is different:
/// the saved credentials must be DISCARDED and pairing repeated. Without that, pair-verify
/// fails identically on every future attempt and the device is effectively bricked in WinPlay
/// until the user deletes credentials.dat by hand.</para>
/// </summary>
public sealed class StalePairingException(string receiver, Exception inner)
    : Exception($"{receiver} no longer recognises this PC — it was reset or re-paired elsewhere. "
                + "WinPlay will forget the old pairing so you can pair again.", inner)
{
    public string Receiver { get; } = receiver;
}

/// <summary>
/// Thrown when a receiver REFUSES the session outright — RTSP <c>401 Unauthorized</c>.
///
/// <para>This is a different condition from <see cref="PairingRequiredException"/> and must not
/// be treated as one: 401 means the receiver's access control rejected this sender, not that it
/// is waiting for a PIN. On a HomePod it is almost always the Home app's speaker-access setting
/// ("Allow Speaker Access" / "Require Password"), which by default can be limited to people who
/// share the home; a HomePod has no screen and never displays a pairing PIN, so answering a 401
/// by starting the on-screen-PIN flow can only fail — instantly, and with a message that tells
/// the user nothing. Reported separately so the app can say exactly which setting to change.</para>
/// </summary>
public sealed class ReceiverAccessDeniedException(string receiver)
    : Exception($"{receiver} refused the connection (access control). On a HomePod, open the Home app "
                + "→ hold the speaker → Settings → Speaker Access, and allow \"Anyone on the Same Network\" "
                + "(or add a password and enter it). On an Apple TV, check Settings → AirPlay & HomeKit → "
                + "Allow Access.")
{
    public string Receiver { get; } = receiver;
}

/// <summary>
/// Thrown when a receiver has been discovered but WinPlay has no address it can dial.
///
/// <para>Almost always transient: mDNS delivers a device's SRV record before its address record,
/// so for a moment the device is known by name and not yet by address. It can also mean the
/// receiver published only an IPv6 address, which WinPlay's streaming stack does not yet dial —
/// every socket in the RTSP, RTP and PTP paths is IPv4, and connecting the control channel over
/// IPv6 while the timing channel stayed on IPv4 would produce a session that appears to connect
/// and then never plays. Refusing clearly beats half-working.</para>
/// </summary>
public sealed class ReceiverUnreachableException(string receiver)
    : Exception($"{receiver} was found but has no reachable address yet. Give it a moment and try "
                + "again — if it keeps happening, check that the speaker and this PC are on the "
                + "same network and that the network is not set to block local device discovery.")
{
    public string Receiver { get; } = receiver;
}

/// <summary>
/// Standalone PIN-pairing flow against a receiver: opens its own RTSP connection,
/// triggers the on-screen PIN, and completes pair-setup once the user has read it.
/// The connection must stay open between Begin and Finish — dispose the handle.
/// </summary>
public static class ReceiverPairing
{
    public static async Task<Handle> BeginAsync(IPAddress address, int port, CancellationToken ct)
    {
        var rtsp = new RtspConnection();
        try
        {
            await rtsp.ConnectAsync(address, port, ct).ConfigureAwait(false);
            var session = await HapVerifiedPairing.StartPairSetupAsync(MakePost(rtsp), ct).ConfigureAwait(false);
            return new Handle(rtsp, session);
        }
        catch
        {
            rtsp.Dispose();
            throw;
        }
    }

    internal static HapVerifiedPairing.PostAsync MakePost(RtspConnection rtsp) =>
        async (endpoint, body, ct) =>
        {
            var resp = await rtsp.RequestAsync(new RtspRequest
            {
                Method = "POST",
                Uri = endpoint,
                Body = body.Length > 0 ? body : null,
                ContentType = body.Length > 0 ? "application/octet-stream" : null,
                Headers = { ["X-Apple-HKP"] = "3" },
            }, ct).ConfigureAwait(false);
            resp.EnsureSuccess($"POST {endpoint}");
            return resp.Body;
        };

    public sealed class Handle(RtspConnection rtsp, HapVerifiedPairing.PinPairingSession session) : IDisposable
    {
        /// <summary>Completes pairing with the PIN currently displayed on the receiver.</summary>
        public Task<HapPairingCredentials> FinishAsync(string pin, CancellationToken ct) =>
            session.FinishAsync(pin, ct);

        public void Dispose() => rtsp.Dispose();
    }
}
