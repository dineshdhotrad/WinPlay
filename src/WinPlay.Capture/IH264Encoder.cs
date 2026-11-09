// SPDX-License-Identifier: GPL-3.0-or-later
using Vortice.Direct3D11;

namespace WinPlay.Capture;

/// <summary>
/// An H.264 encoder: GPU NV12 surfaces (preferred zero-copy path) or NV12 system-memory
/// frames (software fallback) in, Annex-B access units out via <see cref="Encoded"/>.
/// </summary>
internal interface IH264Encoder : IDisposable
{
    /// <summary>Human-readable encoder identity for diagnostics.</summary>
    string Name { get; }

    /// <summary>True when the encoder accepts GPU <see cref="ID3D11Texture2D"/> surfaces directly.</summary>
    bool IsZeroCopy { get; }

    /// <summary>Raised synchronously on the encoder thread for each encoded Annex-B access unit.</summary>
    event Action<ReadOnlyMemory<byte>>? Encoded;

    /// <summary>Submits one GPU-resident NV12 texture for zero-copy hardware encoding.</summary>
    void EncodeTexture(ID3D11Texture2D nv12Texture);

    /// <summary>Submits one CPU system-memory NV12 frame (software encoder fallback).</summary>
    void EncodeCpu(byte[] nv12);

    /// <summary>Requests the encoder emit an IDR keyframe on the next encoded frame.</summary>
    void ForceKeyframe();

    /// <summary>Dynamically updates the encoder's target and max CBR bitrate in bits/second.</summary>
    void UpdateBitrate(int bitrateBps);
}
