// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;

namespace WinPlay.Capture;

/// <summary>
/// The capture-host child-process entry point (Task A4). Runs the DXGI + Media Foundation
/// capture/encode pipeline in an isolated process and streams encoded H.264 access units to
/// the parent (tray) process over a named pipe. A native GPU/encoder fault therefore takes
/// down only this child; the parent's supervisor restarts it while the UI stays alive.
///
/// <para>The child is the app's own binary launched in a second mode
/// (<c>WinPlay.App.exe --capture-host &lt;pipeName&gt;</c>) — a multi-call binary, so there is
/// no duplicated runtime and no separate installer artifact.</para>
/// </summary>
public static class CaptureHostRunner
{
    /// <summary>Command-line switch that selects capture-host mode.</summary>
    public const string Switch = "--capture-host";

    /// <summary>
    /// Runs the host loop until the parent sends <see cref="MirrorMessageType.Stop"/> or the
    /// pipe breaks. Returns a process exit code (0 = clean shutdown, non-zero = fault).
    /// </summary>
    public static int Run(string pipeName)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);

            // The parent's first message is Init with the negotiated capture parameters.
            if (MirrorPipeProtocol.ReadMessage(pipe) is not { Type: MirrorMessageType.Init } cfg)
                return 2;

            using var cts = new CancellationTokenSource();
            var source = new ScreenMirrorSource(cfg.Fps, cfg.BitrateMbps);
            source.Configure(cfg.Width, cfg.Height);

            var writeLock = new object();
            bool readySent = false;

            source.Diagnostic += text =>
            {
                lock (writeLock) { try { MirrorPipeProtocol.WriteDiagnostic(pipe, text); } catch { /* pipe gone */ } }
            };
            source.FrameEncoded += (au, keyframe) =>
            {
                lock (writeLock)
                {
                    try
                    {
                        // Announce the negotiated encode size once, just before the first frame.
                        if (!readySent) { MirrorPipeProtocol.WriteReady(pipe, source.Width, source.Height); readySent = true; }
                        // Stamp with the shared system QPC timeline so the parent can time from capture.
                        MirrorPipeProtocol.WriteFrame(pipe, Stopwatch.GetTimestamp(), keyframe, au.Span);
                    }
                    catch { cts.Cancel(); } // the parent is gone — stop capturing.
                }
            };

            _ = source.StartAsync(cts.Token);

            // Block on control messages until the parent says Stop, closes the pipe, or dies.
            while (!cts.IsCancellationRequested)
            {
                MirrorMessage? msg;
                try { msg = MirrorPipeProtocol.ReadMessage(pipe); }
                catch { break; }
                if (msg is null || msg.Value.Type == MirrorMessageType.Stop) break;
            }

            cts.Cancel();
            source.DisposeAsync().AsTask().Wait(2000);
            return 0;
        }
        catch (Exception ex)
        {
            if (pipe is { IsConnected: true })
            {
                try { MirrorPipeProtocol.WriteError(pipe, ex.Message); } catch { /* best effort */ }
            }
            return 1;
        }
        finally
        {
            pipe?.Dispose();
        }
    }
}
