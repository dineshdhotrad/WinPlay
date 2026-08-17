// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;

namespace WinPlay.Core.Net;

/// <summary>
/// The single place that decides which of a receiver's addresses WinPlay dials.
///
/// <para><b>WinPlay speaks IPv4 to receivers.</b> That is a deliberate, whole-stack decision, not
/// an oversight: RTSP, the RTP audio and control sockets, the PTP grandmaster and the mirroring
/// data channel must all reach the same device over the same family, because they are one session
/// on one clock. Connecting the control channel over IPv6 while the timing channel stayed on IPv4
/// would produce a session that appears to connect and then never plays in sync — strictly worse
/// than declining, because the user cannot tell what went wrong.</para>
///
/// <para>It stays IPv4 for now because it cannot be verified otherwise. Every AirPlay receiver
/// tested against publishes both families, so a dual-stack implementation could be written but not
/// proven, and unproven protocol code shipped to real users is the thing this project refuses to
/// do. The limitation is therefore explicit, tested, and centralised: making WinPlay dual-stack
/// means changing <see cref="PreferredFamily"/> and the socket construction that reads it, not
/// hunting assumptions scattered across the transport layer.</para>
///
/// <para>A device that publishes only an IPv6 address is discovered and listed, and refused with
/// an explanation when selected — see <c>ReceiverUnreachableException</c>.</para>
/// </summary>
public static class ReceiverAddressing
{
    /// <summary>The address family every receiver-facing socket uses.</summary>
    public const AddressFamily PreferredFamily = AddressFamily.InterNetwork;

    /// <summary>Whether WinPlay can open a session to this address.</summary>
    public static bool IsDialable(IPAddress address) => address.AddressFamily == PreferredFamily;

    /// <summary>
    /// Picks the address to dial for a receiver, or null when none of its addresses can be used.
    /// Null is a meaningful answer — the caller reports it rather than guessing — and is what
    /// separates "not resolved yet" from "resolved, but not over a family we speak".
    /// </summary>
    public static IPAddress? Select(IEnumerable<IPAddress> addresses) =>
        addresses.FirstOrDefault(IsDialable);

    /// <summary>
    /// True when a receiver was resolved but published no address WinPlay can dial — the
    /// IPv6-only case, which is permanent rather than a timing problem and must not be reported
    /// as "try again in a moment".
    /// </summary>
    public static bool IsUnreachableFamily(IReadOnlyCollection<IPAddress> addresses) =>
        addresses.Count > 0 && !addresses.Any(IsDialable);
}
