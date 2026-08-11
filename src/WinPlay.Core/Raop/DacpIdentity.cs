// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace WinPlay.Core.Raop;

/// <summary>
/// The process-wide DACP identity (Task D3). Every RTSP session advertises these values in its
/// <c>DACP-ID</c> and <c>Active-Remote</c> headers, and <see cref="DacpServer"/> publishes the
/// matching <c>iTunes_Ctrl_&lt;DacpId&gt;</c> DNS-SD instance.
///
/// <para>One identity per process is the correct model, not a convenience: DACP controls "what
/// this PC is playing", which is a single Windows media session shared by every receiver. If
/// each RTSP connection minted its own id — as WinPlay did before — a receiver would resolve a
/// control endpoint that does not exist, and pressing pause on the HomePod would do nothing.
/// (Same process-wide-singleton reasoning as <see cref="Ptp.PtpMaster.Shared"/>.)</para>
/// </summary>
public static class DacpIdentity
{
    /// <summary>16 hex digits, the conventional DACP-ID form.</summary>
    public static string DacpId { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    /// <summary>Shared secret a receiver must echo on every DACP command.</summary>
    public static string ActiveRemote { get; } = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString();
}
