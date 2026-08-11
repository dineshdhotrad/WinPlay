// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Discovery;
using WinPlay.Core.Dns;
using WinPlay.Core.Mdns;
using Xunit;
using Xunit.Abstractions;

namespace WinPlay.Core.Tests;

/// <summary>
/// Measures the property the browse cache expiry in <see cref="AirPlayBrowser"/> rests on: that a
/// reachable AirPlay responder answers EVERY PTR query, so silence across several rounds is
/// evidence of absence rather than of suppression.
///
/// <para>This talks to real hardware on the real network, so it is not part of the normal suite —
/// it is a measurement, not an assertion about the code. Run it deliberately:
/// <c>dotnet test --filter LiveResponderLivenessProbe</c></para>
/// </summary>
public class LiveResponderLivenessProbe(ITestOutputHelper output)
{
    private const int Rounds = 12;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(16);

    /// <remarks>
    /// Result on a domestic Wi-Fi network, 2026-08-11 — 14 instances (HomePods, a stereo pair, an
    /// Apple TV, a Mac) over 12 rounds: every reachable instance answered, and the longest run of
    /// consecutive missed answers by any of them was 2 rounds. <c>AirPlayBrowser.MaxMissedRounds</c>
    /// is set to 8 on that basis. Re-run this if that constant is ever changed.
    /// </remarks>
    [Fact(Skip = "Live-LAN measurement; run explicitly with --filter LiveResponderLivenessProbe")]
    public async Task Reachable_Responders_Answer_Every_Round()
    {
        using var mdns = new MdnsClient();
        var seen = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        int round = 0;
        var gate = new object();

        mdns.MessageReceived += (msg, _) =>
        {
            if (!msg.IsResponse) return;
            lock (gate)
            {
                foreach (var rr in msg.AllRecords)
                {
                    // Count an instance as "heard" when any record naming it arrives, which is
                    // exactly what AirPlayBrowser.Refresh keys off.
                    string? name = rr.Type switch
                    {
                        DnsType.Ptr when rr.Data is PtrData p => p.Target,
                        DnsType.Srv or DnsType.Txt => rr.Name,
                        _ => null,
                    };
                    if (name is null) continue;
                    if (!name.EndsWith(AirPlayBrowser.AirPlayService, StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(AirPlayBrowser.RaopService, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!seen.TryGetValue(name, out var rounds)) seen[name] = rounds = [];
                    rounds.Add(round);
                }
            }
        };
        mdns.Start();

        for (round = 0; round < Rounds; round++)
        {
            mdns.Query([
                (AirPlayBrowser.AirPlayService, DnsType.Ptr, false),
                (AirPlayBrowser.RaopService, DnsType.Ptr, false),
            ]);
            await Task.Delay(Interval);
        }

        lock (gate)
        {
            output.WriteLine($"{seen.Count} instances over {Rounds} rounds at {Interval.TotalSeconds:F0}s:");
            output.WriteLine("");
            foreach (var (name, rounds) in seen.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                // The number that matters: the longest run of consecutive rounds with no answer.
                // AirPlayBrowser tolerates MaxMissedRounds (5) before evicting.
                int firstSeen = rounds.Min(), worstGap = 0, gap = 0;
                for (int r = firstSeen; r < Rounds; r++)
                {
                    if (rounds.Contains(r)) gap = 0;
                    else worstGap = Math.Max(worstGap, ++gap);
                }
                output.WriteLine($"  answered {rounds.Count,2}/{Rounds - firstSeen,2} rounds "
                    + $"(from round {firstSeen}), longest silence {worstGap} rounds  —  {name}");
            }
        }
    }
}
