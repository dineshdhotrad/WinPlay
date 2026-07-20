// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Capture;

/// <summary>
/// An H.264 encoder: NV12 system-memory frames in, Annex-B access units out. Implemented
/// by <see cref="HardwareH264Encoder"/> (GPU, preferred) and <see cref="MediaFoundationH264Encoder"/>
/// (software fallback). Output is delivered asynchronously via <see cref="Encoded"/>.
/// </summary>
internal interface IH264Encoder : IDisposable
{
    /// <summary>Human-readable encoder identity for diagnostics.</summary>
    string Name { get; }

    /// <summary>Raised for each encoded Annex-B access unit.</summary>
    event Action<byte[]>? Encoded;

    /// <summary>Submits one NV12 frame for encoding.</summary>
    void Encode(byte[] nv12);
}
