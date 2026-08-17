// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Mirror;

/// <summary>
/// A source of H.264 access units for a mirroring session. Implementations capture the
/// screen and encode it (Desktop Duplication + Media Foundation on Windows); the session
/// consumes the Annex-B frames, extracts SPS/PPS for the codec packet, and encrypts the
/// VCL data.
/// </summary>
public interface IH264VideoSource : IAsyncDisposable
{
    /// <summary>Encoded frame width (valid after <see cref="StartAsync"/> has begun).</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// Raised for each encoded access unit: (Annex-B bytes, isKeyframe, captureTicks). captureTicks
    /// is a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value taken at the moment the
    /// frame was captured (not encoded-and-delivered) — QueryPerformanceCounter is machine-wide on
    /// Windows, so this is directly comparable to the consuming session's own clock even when the
    /// source runs in a different (capture-host) process. Consumers use it to timestamp the frame
    /// from its true capture instant rather than from whenever this event happens to be observed,
    /// so pipe/queueing delay between capture and here cannot silently skew A/V sync.
    /// </summary>
    event Action<ReadOnlyMemory<byte>, bool, long>? FrameEncoded;

    /// <summary>
    /// Capture has stopped for good and no further frames will arrive. The session must surface
    /// this rather than wait: a source that silently produces nothing is indistinguishable, from
    /// the outside, from a mirror that is simply still starting — which is how "click Mirror, and
    /// nothing whatsoever happens" became a supported outcome.
    /// </summary>
    event Action<Exception>? Failed;

    /// <summary>
    /// Negotiated receiver constraints, applied before <see cref="StartAsync"/>: the
    /// receiver's display size in pixels (0 = not advertised → use the desktop size).
    /// The source fits the desktop into this to pick the encode resolution.
    /// </summary>
    void Configure(int receiverDisplayWidth, int receiverDisplayHeight);

    /// <summary>Begins capture + encode; runs until cancelled.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Requests the encoder emit an IDR/keyframe with fresh SPS/PPS on the next frame.</summary>
    void RequestKeyframe();
}
