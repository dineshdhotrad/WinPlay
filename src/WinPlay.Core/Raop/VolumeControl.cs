// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace WinPlay.Core.Raop;

/// <summary>
/// AirPlay volume is a float dB attenuation applied on the receiver: <c>0 dB</c> = full,
/// <c>−30 dB</c> = minimum, and <c>−144 dB</c> = muted (per the Unofficial AirPlay Specification
/// and owntone's <c>airplay.c</c>). This is the one place the 0–100 UI slider maps onto that
/// scale and the RTSP <c>SET_PARAMETER</c> body is formatted (Task B5) — so a per-destination
/// slider only ever changes that receiver's volume, never the local system volume.
/// </summary>
public static class VolumeControl
{
    public const double MutedDb = -144.0;
    public const double MinDb = -30.0;
    public const double MaxDb = 0.0;

    /// <summary>Maps a 0–100 slider to dB: 0 → muted (−144); 1..100 → linear −30..0.</summary>
    public static double PercentToDb(double percent)
    {
        if (percent <= 0.5) return MutedDb;
        double clamped = Math.Clamp(percent, 0.0, 100.0);
        return MinDb + (clamped / 100.0) * (MaxDb - MinDb);
    }

    /// <summary>
    /// Inverse of <see cref="PercentToDb"/>: maps an AirPlay dB level back to a 0–100 slider
    /// position. Used when a volume change arrives FROM a receiver (its own volume keys, over
    /// DACP) and the picker's slider has to follow.
    /// </summary>
    public static double DbToPercent(double db)
    {
        if (db <= MinDb) return 0; // includes the −144 mute sentinel
        if (db >= MaxDb) return 100;
        return (db - MinDb) / (MaxDb - MinDb) * 100.0;
    }

    /// <summary>
    /// The RTSP <c>SET_PARAMETER</c> request body for a volume in dB. Always formatted with the
    /// invariant culture — a comma decimal separator (e.g. de-DE) would otherwise corrupt the
    /// wire format and the receiver would reject the request.
    /// </summary>
    public static string FormatVolumeBody(double db) =>
        string.Create(CultureInfo.InvariantCulture, $"volume: {db:F6}\r\n");
}
