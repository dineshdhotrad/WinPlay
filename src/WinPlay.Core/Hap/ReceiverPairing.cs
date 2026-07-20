// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Rtsp;

namespace WinPlay.Core.Hap;

/// <summary>Thrown when a receiver requires PIN pairing before it accepts a session.</summary>
public sealed class PairingRequiredException(string receiver)
    : Exception($"{receiver} requires PIN pairing (Connection Authorization) — pair once, credentials are then stored");

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
