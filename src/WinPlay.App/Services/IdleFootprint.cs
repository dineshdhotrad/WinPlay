// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using WinPlay.Diagnostics;

namespace WinPlay.App.Services;

/// <summary>
/// Keeps WinPlay's resident footprint honest while it sits idle in the tray.
///
/// <para>WinPlay is a background app: it spends almost all of its life with no window shown,
/// waiting for the user to open the picker. Startup and the first flyout render allocate a
/// large, transient working set — XAML parsing, the Acrylic backdrop, JIT — that stays mapped
/// long after it stops being useful. A tray app that holds ~180 MB resident while doing
/// nothing is, correctly, seen as bloated.</para>
///
/// <para>After the flyout closes, this releases the managed heap's unused segments and asks
/// Windows to trim the working set. <c>EmptyWorkingSet</c> does not discard data — pages that
/// are still needed fault straight back in from the standby list or the page file — it simply
/// stops WinPlay from holding physical memory the rest of the system could use. This is the
/// standard behaviour for Windows background apps, and it is why opening the picker again
/// stays fast.</para>
///
/// <para>Deliberately NOT called while streaming: reclaiming pages mid-stream could fault the
/// audio pump at exactly the wrong moment, and the pump has a hard ~8 ms deadline. Footprint
/// is only trimmed when the app is genuinely idle.</para>
/// </summary>
public static class IdleFootprint
{
    /// <summary>Wait after the flyout closes before trimming, so a user who immediately
    /// re-opens the picker never pays for a reclaim.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Shortest gap between two trims. A compacting Gen2 collection stops every thread in the
    /// process for its duration, and <c>EmptyWorkingSet</c> then costs soft faults to page back
    /// whatever is still needed. Paid once when the app settles, that is a good trade; paid every
    /// time the picker closes, it is a tax on the most ordinary interaction there is — glance,
    /// close, glance again — and it makes the app feel slower precisely because it is trying to
    /// be lean.
    /// </summary>
    private static readonly TimeSpan MinTrimInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Resident working set above which a trim is worth its pause.
    ///
    /// <para>Measured against the WORKING SET, not the managed heap. What this reclaims is mostly
    /// not managed objects: it is the pages XAML parsing, the Acrylic backdrop and the JIT leave
    /// mapped, and <c>EmptyWorkingSet</c> is what returns them. Gating on
    /// <c>GC.GetTotalMemory</c> measured the wrong thing entirely — a freshly started WinPlay sits
    /// at ~130 MB resident with only a few MB of managed heap, so the check said "nothing to
    /// reclaim" and skipped the trim that had been reclaiming over 100 MB. Verified by watching
    /// the resident set of a real build, which is the only way this kind of mistake shows up.</para>
    /// </summary>
    private const long WorthTrimmingBytes = 64L * 1024 * 1024;

    private static CancellationTokenSource? _pending;
    private static readonly object Gate = new();
    private static long _lastTrimTicks;   // Environment.TickCount64 of the last trim; 0 = never

    /// <summary>
    /// Schedules a trim once the app has been idle for <see cref="SettleDelay"/>. Repeated
    /// calls coalesce: only the last one runs, so toggling the flyout does not thrash.
    /// </summary>
    /// <param name="isBusy">Returns true while anything is streaming — trimming is skipped then.</param>
    public static void ScheduleTrim(Func<bool> isBusy)
    {
        CancellationTokenSource cts;
        lock (Gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cts = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettleDelay, cts.Token).ConfigureAwait(false);
                if (isBusy())
                {
                    WinPlayLog.For("Footprint").Debug("trim skipped: streaming");
                    return;
                }
                if (!WorthTrimming()) return;
                Trim();
            }
            catch (OperationCanceledException) { /* superseded by a newer request */ }
            catch (Exception ex)
            {
                // Without this the task simply faulted and vanished, taking the reason with it —
                // which is precisely how a trim that silently stopped happening went unnoticed.
                WinPlayLog.For("Footprint").Warning(ex, "idle trim failed");
            }
        });
    }

    /// <summary>
    /// Whether a trim would actually buy anything right now: enough has been allocated to be worth
    /// reclaiming, and the last one was long enough ago. Deciding this here rather than trimming
    /// on every idle tick is the difference between reclaiming memory and just burning CPU to
    /// prove a point.
    /// </summary>
    private static bool WorthTrimming()
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;
            long last = _lastTrimTicks;
            if (last != 0 && now - last < (long)MinTrimInterval.TotalMilliseconds)
            {
                WinPlayLog.For("Footprint").Debug("trim skipped: last was {Ago} s ago", (now - last) / 1000);
                return false;
            }
            long resident = Environment.WorkingSet;
            if (resident < WorthTrimmingBytes)
            {
                WinPlayLog.For("Footprint").Debug("trim skipped: resident {MB} MB is already small", resident >> 20);
                return false;
            }
            _lastTrimTicks = now;
            return true;
        }
    }

    /// <summary>Compacts the managed heap and releases the working set. Safe to call anytime.</summary>
    public static void Trim()
    {
        try
        {
            // Compact the LOH too: cover art bitmaps and capture buffers are large objects, so
            // without compaction the heap keeps holes that hold whole segments mapped.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            // Collect, let finalizers run, then collect again — the second pass is what actually
            // reclaims what the finalizers released. Only the second compacts: compacting twice
            // relocates the whole heap a second time moments after the first, to recover the
            // objects a finalizer freed in between.
            //
            // The first pass is Forced, NOT Aggressive. GCCollectionMode.Aggressive is only legal
            // with blocking AND compacting both true — asking for it without compaction throws
            // ArgumentException, which aborted this method before EmptyWorkingSet and silently
            // stopped the app reclaiming ~110 MB. It was invisible because the catch below logged
            // at Debug, so nothing appeared in a normal run.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            long before = Environment.WorkingSet;
            if (!EmptyWorkingSet(GetCurrentProcess()))
                WinPlayLog.For("Footprint").Debug("EmptyWorkingSet declined; the OS will reclaim on demand.");
            else
                WinPlayLog.For("Footprint").Information(
                    "idle trim: resident {Before} MB -> {After} MB", before >> 20, Environment.WorkingSet >> 20);
        }
        catch (Exception ex)
        {
            // Footprint management must never destabilise the app — but it must not fail quietly
            // either. At Debug this was invisible in a normal run, so a trim that had stopped
            // working entirely looked exactly like a trim that had nothing to do.
            WinPlayLog.For("Footprint").Warning(ex, "working-set trim failed");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
