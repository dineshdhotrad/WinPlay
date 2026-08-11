// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace WinPlay.Core.Plist;

/// <summary>
/// Minimal Apple binary property list ("bplist00") reader/writer covering the types
/// AirPlay 2 uses: dict, array, ASCII/UTF-16 string, data, int, real, bool, date, uid.
/// Values map to: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, string, byte[],
/// long, double, bool, DateTime.
/// </summary>
public static class BinaryPlist
{
    private static readonly byte[] Magic = "bplist00"u8.ToArray();
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- writing

    public static byte[] Write(object root)
    {
        var objects = new List<object?>();
        Flatten(root, objects);

        int refSize = objects.Count <= 0xFF ? 1 : 2;
        var body = new MemoryStream();
        body.Write(Magic);

        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = body.Position;
            WriteObject(body, objects[i], objects, refSize);
        }

        long offsetTableOffset = body.Position;
        int offsetIntSize = offsetTableOffset <= 0xFF ? 1 : offsetTableOffset <= 0xFFFF ? 2 : 4;
        foreach (long off in offsets)
            WriteBe(body, (ulong)off, offsetIntSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetIntSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)offsetTableOffset);
        body.Write(trailer);
        return body.ToArray();
    }

    /// <summary>Pre-order flatten; children of dict/array get their own object slots.</summary>
    private static int Flatten(object? value, List<object?> objects)
    {
        int index = objects.Count;
        objects.Add(value);

        switch (value)
        {
            case IReadOnlyDictionary<string, object?> dict:
            {
                var keyRefs = new int[dict.Count];
                var valRefs = new int[dict.Count];
                int i = 0;
                foreach (var (k, v) in dict)
                {
                    keyRefs[i] = Flatten(k, objects);
                    valRefs[i] = Flatten(v, objects);
                    i++;
                }
                objects[index] = new FlatDict(dict.Count, keyRefs, valRefs);
                break;
            }
            case IReadOnlyList<object?> list when value is not string and not byte[]:
            {
                var refs = new int[list.Count];
                for (int i = 0; i < list.Count; i++)
                    refs[i] = Flatten(list[i], objects);
                objects[index] = new FlatArray(refs);
                break;
            }
        }
        return index;
    }

    private sealed record FlatDict(int Count, int[] KeyRefs, int[] ValRefs);
    private sealed record FlatArray(int[] Refs);

    private static void WriteObject(MemoryStream s, object? value, List<object?> objects, int refSize)
    {
        switch (value)
        {
            case null:
                s.WriteByte(0x00);
                break;
            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
            {
                long v = Convert.ToInt64(value);
                WriteInt(s, v);
                break;
            }
            case ulong u:
                WriteInt(s, unchecked((long)u));
                break;
            case float f:
                s.WriteByte(0x23);
                WriteBe(s, BitConverter.DoubleToUInt64Bits(f), 8);
                break;
            case double d:
                s.WriteByte(0x23);
                WriteBe(s, BitConverter.DoubleToUInt64Bits(d), 8);
                break;
            case DateTime dt:
                s.WriteByte(0x33);
                WriteBe(s, BitConverter.DoubleToUInt64Bits((dt.ToUniversalTime() - AppleEpoch).TotalSeconds), 8);
                break;
            case string str:
                WriteString(s, str);
                break;
            case byte[] data:
                WriteMarker(s, 0x40, data.Length);
                s.Write(data);
                break;
            case FlatArray arr:
                WriteMarker(s, 0xA0, arr.Refs.Length);
                foreach (int r in arr.Refs) WriteBe(s, (ulong)r, refSize);
                break;
            case FlatDict dict:
                WriteMarker(s, 0xD0, dict.Count);
                foreach (int r in dict.KeyRefs) WriteBe(s, (ulong)r, refSize);
                foreach (int r in dict.ValRefs) WriteBe(s, (ulong)r, refSize);
                break;
            default:
                throw new NotSupportedException($"binary plist cannot encode {value.GetType()}");
        }
    }

    private static void WriteInt(MemoryStream s, long v)
    {
        if (v < 0) { s.WriteByte(0x13); WriteBe(s, unchecked((ulong)v), 8); }
        else if (v <= 0xFF) { s.WriteByte(0x10); s.WriteByte((byte)v); }
        else if (v <= 0xFFFF) { s.WriteByte(0x11); WriteBe(s, (ulong)v, 2); }
        else if (v <= 0xFFFFFFFF) { s.WriteByte(0x12); WriteBe(s, (ulong)v, 4); }
        else { s.WriteByte(0x13); WriteBe(s, (ulong)v, 8); }
    }

    private static void WriteString(MemoryStream s, string str)
    {
        bool ascii = str.All(c => c < 0x80);
        if (ascii)
        {
            WriteMarker(s, 0x50, str.Length);
            s.Write(Encoding.ASCII.GetBytes(str));
        }
        else
        {
            WriteMarker(s, 0x60, str.Length); // count = UTF-16 code units
            s.Write(Encoding.BigEndianUnicode.GetBytes(str));
        }
    }

    private static void WriteMarker(MemoryStream s, byte marker, int count)
    {
        if (count < 15)
        {
            s.WriteByte((byte)(marker | count));
        }
        else
        {
            s.WriteByte((byte)(marker | 0x0F));
            WriteInt(s, count);
        }
    }

    private static void WriteBe(MemoryStream s, ulong v, int size)
    {
        for (int i = size - 1; i >= 0; i--)
            s.WriteByte((byte)(v >> (8 * i)));
    }

    // ---------------------------------------------------------------- reading

    public static object? Read(ReadOnlySpan<byte> plist)
    {
        if (plist.Length < Magic.Length + 32 || !plist[..8].SequenceEqual(Magic))
            throw new FormatException("not a bplist00");

        ReadOnlySpan<byte> trailer = plist[^32..];
        int offsetIntSize = trailer[6];
        int refSize = trailer[7];
        long numObjects = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        long topObject = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        long offsetTableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);

        if (offsetIntSize is < 1 or > 8 || refSize is < 1 or > 8)
            throw new FormatException("bad trailer sizes");
        if (numObjects <= 0 || numObjects > 1_000_000 || topObject >= numObjects)
            throw new FormatException("bad object counts");
        if (offsetTableOffset < 8 || offsetTableOffset + numObjects * offsetIntSize > plist.Length - 32)
            throw new FormatException("offset table out of range");

        var offsets = new long[numObjects];
        for (long i = 0; i < numObjects; i++)
            offsets[i] = (long)ReadBe(plist.Slice((int)(offsetTableOffset + i * offsetIntSize), offsetIntSize));

        try
        {
            return ReadObject(plist, offsets, refSize, (int)topObject, depth: 0);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException
                                      or ArgumentException or OverflowException)
        {
            // Every parse failure must present as FormatException. Callers guard against
            // malformed plists with `catch (FormatException)`; a stray range exception escaping
            // instead killed the background loop that was parsing — including the 2 s /feedback
            // keep-alive, which silently ended playback ~30 s later with nothing surfaced.
            throw new FormatException($"malformed plist: {ex.Message}", ex);
        }
    }

    /// <summary>Convenience: read and require a top-level dictionary.</summary>
    public static Dictionary<string, object?> ReadDictionary(ReadOnlySpan<byte> plist) =>
        Read(plist) as Dictionary<string, object?>
        ?? throw new FormatException("plist root is not a dictionary");

    private static object? ReadObject(ReadOnlySpan<byte> buf, long[] offsets, int refSize, int index, int depth)
    {
        if (depth > 64) throw new FormatException("plist nesting too deep");
        if (index < 0 || index >= offsets.Length) throw new FormatException("object ref out of range");

        int p = (int)offsets[index];
        if (p < 8 || p >= buf.Length - 32) throw new FormatException("object offset out of range");
        byte marker = buf[p++];
        int type = marker >> 4;
        int info = marker & 0x0F;

        switch (type)
        {
            case 0x0:
                return marker switch
                {
                    0x00 => null,
                    0x08 => false,
                    0x09 => true,
                    _ => throw new FormatException($"unknown singleton marker 0x{marker:X2}"),
                };
            case 0x1: // int, 2^info bytes
            {
                int size = 1 << info;
                if (size > 16) throw new FormatException("int too wide");
                ulong raw = ReadBe(buf.Slice(p, Math.Min(size, 8)));
                return size == 8 ? unchecked((long)raw) : (long)raw;
            }
            case 0x2: // real
                return info switch
                {
                    2 => (double)BinaryPrimitives.ReadSingleBigEndian(buf.Slice(p, 4)),
                    3 => BinaryPrimitives.ReadDoubleBigEndian(buf.Slice(p, 8)),
                    _ => throw new FormatException("unsupported real size"),
                };
            case 0x3: // date
                return AppleEpoch.AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(buf.Slice(p, 8)));
            case 0x4: // data
            {
                int count = ReadCount(buf, ref p, info);
                return buf.Slice(p, count).ToArray();
            }
            case 0x5: // ASCII string
            {
                int count = ReadCount(buf, ref p, info);
                return Encoding.ASCII.GetString(buf.Slice(p, count));
            }
            case 0x6: // UTF-16BE string
            {
                int count = ReadCount(buf, ref p, info, bytesPerElement: 2);
                return Encoding.BigEndianUnicode.GetString(buf.Slice(p, count * 2));
            }
            case 0x8: // uid
                return (long)ReadBe(buf.Slice(p, info + 1));
            case 0xA: // array
            case 0xC: // set — treat as array
            {
                int count = ReadCount(buf, ref p, info, bytesPerElement: refSize);
                var list = new List<object?>(count);
                for (int i = 0; i < count; i++)
                {
                    int r = (int)ReadBe(buf.Slice(p + i * refSize, refSize));
                    list.Add(ReadObject(buf, offsets, refSize, r, depth + 1));
                }
                return list;
            }
            case 0xD: // dict
            {
                // Each entry costs a key ref AND a value ref.
                int count = ReadCount(buf, ref p, info, bytesPerElement: refSize * 2);
                var dict = new Dictionary<string, object?>(count);
                for (int i = 0; i < count; i++)
                {
                    int kr = (int)ReadBe(buf.Slice(p + i * refSize, refSize));
                    int vr = (int)ReadBe(buf.Slice(p + (count + i) * refSize, refSize));
                    string key = ReadObject(buf, offsets, refSize, kr, depth + 1) as string
                        ?? throw new FormatException("non-string dict key");
                    dict[key] = ReadObject(buf, offsets, refSize, vr, depth + 1);
                }
                return dict;
            }
            default:
                throw new FormatException($"unsupported plist marker 0x{marker:X2}");
        }
    }

    /// <summary>
    /// Reads an element count and validates it against what the buffer can actually hold.
    ///
    /// <para>The count comes straight off the wire, so it must never be trusted for sizing.
    /// Unvalidated it allowed two attacks from any device on the LAN — a few hundred bytes
    /// declaring a count near <see cref="int.MaxValue"/> caused a multi-gigabyte
    /// <c>new List(count)</c>/<c>new Dictionary(count)</c> allocation, and oversized counts made
    /// the following <c>Slice</c> throw <see cref="ArgumentOutOfRangeException"/> rather than
    /// <see cref="FormatException"/>, escaping call sites that only guard against malformed
    /// plists. Since every element consumes at least <paramref name="bytesPerElement"/> bytes, a
    /// count that cannot fit in the remaining buffer is definitionally invalid.</para>
    /// </summary>
    private static int ReadCount(ReadOnlySpan<byte> buf, ref int p, int info, int bytesPerElement = 1)
    {
        int count;
        if (info != 0x0F)
        {
            count = info;
        }
        else
        {
            if (p >= buf.Length) throw new FormatException("length marker overruns buffer");
            byte marker = buf[p++];
            if ((marker >> 4) != 0x1) throw new FormatException("length marker is not an int");
            int size = 1 << (marker & 0x0F);
            if (size > 8 || p + size > buf.Length) throw new FormatException("length field overruns buffer");
            ulong raw = ReadBe(buf.Slice(p, size));
            if (raw > int.MaxValue) throw new FormatException($"implausible element count {raw}");
            count = (int)raw;
            p += size;
        }

        if (count < 0) throw new FormatException("negative element count");
        long required = (long)count * Math.Max(1, bytesPerElement);
        if (required > buf.Length - p)
            throw new FormatException($"element count {count} exceeds the remaining {buf.Length - p} bytes");
        return count;
    }

    private static ulong ReadBe(ReadOnlySpan<byte> bytes)
    {
        ulong v = 0;
        foreach (byte b in bytes) v = (v << 8) | b;
        return v;
    }
}
