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

    /// <summary>Raised for each encoded access unit: (Annex-B bytes, isKeyframe).</summary>
    event Action<ReadOnlyMemory<byte>, bool>? FrameEncoded;

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
