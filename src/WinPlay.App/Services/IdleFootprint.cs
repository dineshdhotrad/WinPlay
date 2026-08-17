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
/// <para>After the flyout closes, this collects and compacts the managed heap — including the
/// LOH, where cover art and capture buffers land. That reclaims real memory: the segments go
/// back to the allocator and the process stops holding what it no longer uses.</para>
///
/// <para><b>What this deliberately does NOT do, and why.</b> It does not call
/// <c>EmptyWorkingSet</c>, and it does not use <c>GCCollectionMode.Aggressive</c>. Both evict
/// pages this process is about to need again: <c>EmptyWorkingSet</c> pushes the entire working
/// set onto the standby list, and Aggressive decommits segments back to the OS. Neither frees
/// memory in any sense the rest of the system benefits from — Windows already reclaims genuinely
/// idle pages when something else needs them — but both guarantee a hard-fault storm the next
/// time WinPlay streams.</para>
///
/// <para>Measured on real hardware: a trim logged <c>resident 126 MB -&gt; 3 MB</c>, and the next
/// session 44 s later connected cleanly through every RTSP stage, sent keep-alives, and produced
/// silence and audible clicking on the receivers. Skipping the trim WHILE streaming (which this
/// class already did) does not help — the cost is paid by the session that comes after. An audio
/// pump on an ~8 ms deadline cannot take page faults, and a cosmetically smaller number in Task
/// Manager is worth nothing next to audio that does not break up.</para>
/// </summary>
public static class IdleFootprint
{
    /// <summary>Wait after the flyout closes before trimming, so a user who immediately
    /// re-opens the picker never pays for a reclaim.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Shortest gap between two trims. A compacting Gen2 collection stops every thread in the
    /// process for its duration. Paid once when the app settles, that is a good trade; paid every
    /// time the picker closes, it is a tax on the most ordinary interaction there is — glance,
    /// close, glance again — and it makes the app feel slower precisely because it is trying to
    /// be lean.
    /// </summary>
    private static readonly TimeSpan MinTrimInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Managed heap above which a compaction is worth its stop-the-world pause.
    ///
    /// <para>Gated on the MANAGED HEAP, because that is now the only thing a trim reclaims. This
    /// gate previously measured the working set, which was correct when the trim ended in
    /// <c>EmptyWorkingSet</c> — the working set was what it acted on. Now that eviction is gone
    /// (see the type comment), the working set is no longer a measure of anything this method can
    /// change, and gating on it would fire full compactions that reclaim nothing.</para>
    /// </summary>
    private const long WorthTrimmingBytes = 32L * 1024 * 1024;

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
            long heap = GC.GetTotalMemory(forceFullCollection: false);
            if (heap < WorthTrimmingBytes)
            {
                WinPlayLog.For("Footprint").Debug("trim skipped: managed heap {MB} MB is already small", heap >> 20);
                return false;
            }
            _lastTrimTicks = now;
            return true;
        }
    }

    /// <summary>
    /// Collects and compacts the managed heap. Safe to call anytime, but see the type comment for
    /// why it never evicts the working set.
    /// </summary>
    public static void Trim()
    {
        try
        {
            long heapBefore = GC.GetTotalMemory(forceFullCollection: false);

            // Compact the LOH too: cover art bitmaps and capture buffers are large objects, so
            // without compaction the heap keeps holes that hold whole segments mapped.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            // Collect, let finalizers run, then collect again — the second pass is what actually
            // reclaims what the finalizers released. Only the second compacts: compacting twice
            // relocates the whole heap a second time moments after the first, to recover the
            // objects a finalizer freed in between.
            //
            // Both passes are Forced. Aggressive additionally decommits segments to the OS, which
            // this must not do — see the type comment: the pages come straight back as hard faults
            // under the next stream's audio pump.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            WinPlayLog.For("Footprint").Information(
                "idle trim: managed heap {Before} MB -> {After} MB (working set left resident)",
                heapBefore >> 20, GC.GetTotalMemory(forceFullCollection: false) >> 20);
        }
        catch (Exception ex)
        {
            // Footprint management must never destabilise the app — but it must not fail quietly
            // either. At Debug this was invisible in a normal run, so a trim that had stopped
            // working entirely looked exactly like a trim that had nothing to do.
            WinPlayLog.For("Footprint").Warning(ex, "idle heap trim failed");
        }
    }

}
