// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using WinPlay.Core.Mirror;

namespace WinPlay.Capture;

/// <summary>
/// A crash-isolated <see cref="IH264VideoSource"/> (Task A4). Instead of capturing and encoding
/// in the tray process, it launches the capture pipeline in a supervised child process
/// (<c>WinPlay.App.exe --capture-host …</c>) and relays the child's encoded H.264 frames as its
/// own. A native GPU/encoder fault kills only the child; this supervisor restarts it (with
/// backoff) while the tray UI stays alive — transparently to <see cref="Core.Mirror.MirrorSession"/>,
/// which sees an ordinary video source.
///
/// <para>If the child cannot be kept running (e.g. it is blocked from launching), the source
/// falls back to capturing <em>in-process</em> so mirroring still works — degraded isolation,
/// never a dead feature.</para>
/// </summary>
public sealed class SupervisedMirrorSource : IH264VideoSource
{
    private const int MaxConsecutiveFailuresBeforeFallback = 3;

    private readonly int _fps;
    private readonly int _bitrateMbps;
    private readonly string _hostExePath;
    private int _receiverW;
    private int _receiverH;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile Process? _child;
    private ScreenMirrorSource? _inProcess;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public event Action<ReadOnlyMemory<byte>, bool>? FrameEncoded;
    public event Action<string>? Diagnostic;

    /// <summary>
    /// Capture is over — the in-process fallback could not start either, so there is no path left
    /// to a picture. Raised once; the session tears down and reports it.
    /// </summary>
    public event Action<Exception>? Failed;

    private int _failedRaised;

    private void RaiseFailed(Exception ex)
    {
        if (Interlocked.Exchange(ref _failedRaised, 1) == 0) Failed?.Invoke(ex);
    }

    /// <param name="hostExePath">The child host binary; defaults to the current executable.</param>
    public SupervisedMirrorSource(int fps = 60, int bitrateMbps = 0, string? hostExePath = null)
    {
        _fps = fps;
        _bitrateMbps = bitrateMbps;
        _hostExePath = hostExePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve the capture-host executable path");
    }

    public void Configure(int receiverDisplayWidth, int receiverDisplayHeight)
    {
        _receiverW = receiverDisplayWidth;
        _receiverH = receiverDisplayHeight;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _thread = new Thread(() => Supervise(_cts.Token))
        {
            IsBackground = true,
            Name = "WinPlay-MirrorSupervisor",
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public void RequestKeyframe() { /* the encoder emits an IDR + SPS/PPS first; a restart re-emits */ }

    private void Supervise(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();
            try { RunChildSession(ct); }
            catch (Exception ex) { Diagnostic?.Invoke($"mirror host session error: {ex.Message}"); }

            if (ct.IsCancellationRequested) break; // normal stop

            // The child exited unexpectedly. A session that ran a good while is a fresh start,
            // not part of a crash loop.
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 30) failures = 0;
            failures++;

            if (failures >= MaxConsecutiveFailuresBeforeFallback)
            {
                Diagnostic?.Invoke("mirror host failed repeatedly; falling back to in-process capture.");
                RunInProcess(ct);
                break;
            }

            int backoff = RestartBackoffMs(failures);
            Diagnostic?.Invoke($"mirror host exited; restarting in {backoff}ms (attempt {failures}).");
            if (ct.WaitHandle.WaitOne(backoff)) break;
        }
    }

    /// <summary>Restart backoff: 250 ms per attempt, capped at 1.5 s (so a restart lands well
    /// within the 2 s recovery budget).</summary>
    internal static int RestartBackoffMs(int attempt) => Math.Min(250 * Math.Max(1, attempt), 1500);

    /// <summary>
    /// The live control pipe, while a child session is running. Held so <see cref="DisposeAsync"/>
    /// can send the protocol's Stop message: the message existed and was tested, but nothing ever
    /// sent it, so every teardown was a hard kill. Killing works — the OS reclaims everything —
    /// but it denies the encoder a chance to release its hardware session, and on rapid
    /// stop/start cycles some vendors are slow to hand that slot back.
    /// </summary>
    private volatile NamedPipeServerStream? _pipe;

    private void RunChildSession(CancellationToken ct)
    {
        string pipeName = "WinPlay.Mirror." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        // Disposing the pipe on cancellation unblocks the synchronous read below immediately.
        using var reg = ct.Register(() => { try { server.Dispose(); } catch { /* racing dispose */ } });

        var psi = new ProcessStartInfo { FileName = _hostExePath, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(CaptureHostRunner.Switch);
        psi.ArgumentList.Add(pipeName);

        Process child = Process.Start(psi) ?? throw new InvalidOperationException("failed to start capture host");
        _child = child;
        try
        {
            if (!WaitForConnection(server, child, ct)) return;

            MirrorPipeProtocol.WriteInit(server, _fps, _bitrateMbps, _receiverW, _receiverH);
            // Published so shutdown can ask the child to stop before resorting to killing it.
            _pipe = server;

            while (!ct.IsCancellationRequested)
            {
                MirrorMessage? msg;
                try { msg = MirrorPipeProtocol.ReadMessage(server); }
                catch { return; } // pipe disposed (cancel) or broke (child died) → restart decision upstream
                if (msg is null) return;

                switch (msg.Value.Type)
                {
                    case MirrorMessageType.Ready:
                        Width = msg.Value.Width;
                        Height = msg.Value.Height;
                        Diagnostic?.Invoke($"mirror host ready ({Width}x{Height}).");
                        break;
                    case MirrorMessageType.Frame:
                        FrameEncoded?.Invoke(msg.Value.Payload, msg.Value.Keyframe);
                        break;
                    case MirrorMessageType.Diagnostic:
                        if (msg.Value.Text is { } d) Diagnostic?.Invoke(d);
                        break;
                    case MirrorMessageType.Error:
                        Diagnostic?.Invoke($"mirror host error: {msg.Value.Text}");
                        return;
                }
            }
        }
        finally
        {
            _child = null;
            try { if (!child.WaitForExit(1500)) child.Kill(); } catch { /* already gone */ }
            child.Dispose();
        }
    }

    private static bool WaitForConnection(NamedPipeServerStream server, Process child, CancellationToken ct)
    {
        Task task = server.WaitForConnectionAsync(ct);
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (ct.IsCancellationRequested || child.HasExited || sw.Elapsed > TimeSpan.FromSeconds(10))
                return false;
            Thread.Sleep(20);
        }
        return task.Status == TaskStatus.RanToCompletion && server.IsConnected;
    }

    private void RunInProcess(CancellationToken ct)
    {
        var src = new ScreenMirrorSource(_fps, _bitrateMbps);
        _inProcess = src;
        src.Configure(_receiverW, _receiverH);
        src.Diagnostic += t => Diagnostic?.Invoke(t);
        src.FrameEncoded += (au, keyframe) => FrameEncoded?.Invoke(au, keyframe);
        // The last line of defence. The child host already failed repeatedly to get here, so if
        // in-process capture cannot start either, mirroring is genuinely impossible on this
        // machine right now (Remote Desktop is the usual reason) and the user needs to be told.
        src.Failed += RaiseFailed;
        src.StartAsync(ct).GetAwaiter().GetResult(); // returns immediately; the source runs on its own thread

        while (!ct.IsCancellationRequested)
        {
            if (src.Width > 0) { Width = src.Width; Height = src.Height; }
            if (ct.WaitHandle.WaitOne(200)) break;
        }
    }

    /// <summary>How long the child gets to exit on its own after being asked to stop.</summary>
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromMilliseconds(600);

    public async ValueTask DisposeAsync()
    {
        Process? child = _child;

        // Ask first. The child closes its encoder and duplication cleanly, which returns the
        // hardware encode session promptly rather than leaving it to process teardown.
        if (child is { HasExited: false } && _pipe is { } pipe)
        {
            try
            {
                MirrorPipeProtocol.WriteStop(pipe);
                child.WaitForExit((int)GracefulStopTimeout.TotalMilliseconds);
            }
            catch (Exception)
            {
                // Pipe already broken, or the child is not listening — the kill below covers it.
            }
        }

        _cts?.Cancel();

        // Then enforce. Killing the child breaks the pipe, unblocking the supervisor's read
        // immediately; it is the backstop, no longer the only mechanism.
        if (child is not null)
        {
            try { if (!child.HasExited) child.Kill(); } catch { /* already gone */ }
        }

        if (_thread is { IsAlive: true }) _thread.Join(3000);
        if (_inProcess is not null) await _inProcess.DisposeAsync();
        _pipe = null;
        _cts?.Dispose();
    }
}
