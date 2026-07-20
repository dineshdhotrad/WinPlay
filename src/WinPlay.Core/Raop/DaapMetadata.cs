// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace WinPlay.Core.Raop;

/// <summary>
/// DMAP/DAAP "now playing" metadata encoding for AirPlay (RTSP SET_PARAMETER,
/// Content-Type <c>application/x-dmap-tagged</c>). Each tag is a 4-byte ASCII code, a
/// 4-byte big-endian length, then the payload; the item is wrapped in an <c>mlit</c>
/// listing-item container. Receivers show this on their Now Playing UI.
/// </summary>
public static class DaapMetadata
{
    /// <summary>Builds the DMAP body for the given track fields (empty fields omitted).</summary>
    public static byte[] Encode(string? title, string? artist, string? album)
    {
        using var inner = new MemoryStream();
        WriteString(inner, "minm", title);   // dmap.itemname
        WriteString(inner, "asar", artist);  // daap.songartist
        WriteString(inner, "asal", album);   // daap.songalbum

        using var outer = new MemoryStream();
        WriteTag(outer, "mlit", inner.ToArray()); // dmap.listingitem
        return outer.ToArray();
    }

    private static void WriteString(Stream s, string code, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        WriteTag(s, code, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteTag(Stream s, string code, byte[] payload)
    {
        Span<byte> header = stackalloc byte[8];
        Encoding.ASCII.GetBytes(code).CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], payload.Length);
        s.Write(header);
        s.Write(payload);
    }
}
