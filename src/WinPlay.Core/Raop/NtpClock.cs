// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace WinPlay.Core.Raop;

/// <summary>
/// Monotonic wall clock producing 64-bit NTP timestamps (32.32 fixed point, epoch 1900).
/// Anchored to UTC once at construction, advanced by Stopwatch so it never steps.
/// </summary>
public sealed class NtpClock
{
    private const ulong SecondsFrom1900To1970 = 2_208_988_800UL;
    private readonly double _baseUnixSeconds;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public NtpClock()
    {
        _baseUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    /// <summary>Current NTP timestamp: high 32 bits = seconds, low 32 = fraction.</summary>
    public ulong NowNtp
    {
        get
        {
            double seconds = _baseUnixSeconds + _sw.Elapsed.TotalSeconds + SecondsFrom1900To1970;
            ulong whole = (ulong)seconds;
            ulong frac = (ulong)((seconds - whole) * 4294967296.0);
            return (whole << 32) | (frac & 0xFFFFFFFF);
        }
    }
}
