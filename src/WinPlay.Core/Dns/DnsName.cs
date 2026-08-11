// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace WinPlay.Core.Dns;

/// <summary>
/// Conversion between a DNS name's wire form — a sequence of independent labels — and the dotted
/// string the rest of the code passes around, using RFC 1035 §5.1 presentation-format escaping.
///
/// <para>The escaping is not decoration. A DNS name is a LIST of labels, and a label may contain
/// any byte, including <c>.</c>; RFC 6763 §4.3 makes that explicit for DNS-SD, whose service
/// instance names are arbitrary user-chosen UTF-8. Flattening labels into a dotted string with no
/// escaping loses the boundaries, and splitting that string back on <c>.</c> does not recover
/// them. Two concrete failures, both reachable by a user simply naming a speaker in the Home
/// app:</para>
/// <list type="bullet">
/// <item>A receiver named <c>Kitchen.</c> flattens to <c>Kitchen.._airplay._tcp.local</c>, which
/// splits into an EMPTY label — unencodable, so building a follow-up query for it threw and took
/// the browse loop down with it. One badly-named speaker anywhere on the LAN ended discovery for
/// the rest of the session.</item>
/// <item>A receiver named <c>Mr. Roboto</c> splits into two labels where there was one, so every
/// targeted SRV/TXT query for it addresses a name that does not exist. The device is discovered,
/// never resolves, and sits in the picker unusable.</item>
/// </list>
///
/// <para>With escaping the round-trip is lossless, which is the property every <c>Split('.')</c>
/// in this codebase was already assuming. This is the same representation mDNSResponder and
/// Avahi use, so escaped names are directly comparable with theirs.</para>
/// </summary>
public static class DnsName
{
    /// <summary>Maximum wire length of one label (RFC 1035 §2.3.4).</summary>
    public const int MaxLabelBytes = 63;

    /// <summary>Maximum wire length of a whole name, including length octets and the root.</summary>
    public const int MaxNameBytes = 255;

    /// <summary>
    /// Renders one wire label into presentation form. Only the characters that would otherwise be
    /// read back as structure are escaped — <c>.</c> and <c>\</c> — plus control characters, which
    /// have no printable form. UTF-8 above ASCII passes through unchanged: it is unambiguous, and
    /// keeping it readable means a device name survives intact into log files and the UI.
    /// </summary>
    public static string Escape(string label)
    {
        // Overwhelmingly the common case: nothing to escape, so don't allocate.
        bool needs = false;
        foreach (char c in label)
            if (c is '.' or '\\' || c < 0x20 || c == 0x7F) { needs = true; break; }
        if (!needs) return label;

        var sb = new StringBuilder(label.Length + 8);
        foreach (char c in label)
        {
            if (c is '.' or '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c == 0x7F) sb.Append('\\').Append(((int)c).ToString("D3"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Recovers a single label from presentation form — the inverse of <see cref="Escape"/>.
    /// Used for anything shown to a user, so a speaker called <c>Mr. Roboto</c> reads as its own
    /// name rather than as <c>Mr\. Roboto</c>.
    /// </summary>
    public static string Unescape(string label)
    {
        if (!label.Contains('\\')) return label;
        var sb = new StringBuilder(label.Length);
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] != '\\') { sb.Append(label[i]); continue; }
            if (i + 3 < label.Length && IsDigit(label[i + 1]) && IsDigit(label[i + 2]) && IsDigit(label[i + 3]))
            {
                int v = (label[i + 1] - '0') * 100 + (label[i + 2] - '0') * 10 + (label[i + 3] - '0');
                if (v <= 255) { sb.Append((char)v); i += 3; continue; }
            }
            if (i + 1 < label.Length) sb.Append(label[++i]);
            // A trailing lone backslash is malformed; dropping it is the only lossless-ish option
            // and it cannot arise from Escape's output.
        }
        return sb.ToString();

        static bool IsDigit(char c) => c is >= '0' and <= '9';
    }

    /// <summary>
    /// Splits a presentation-format name into its wire labels, honouring escapes. A trailing root
    /// dot is accepted and ignored, so both <c>x.local</c> and <c>x.local.</c> parse the same.
    /// </summary>
    /// <exception cref="FormatException">
    /// The name cannot be encoded on the wire: an empty label, a label over 63 bytes, or a total
    /// over 255 bytes. Callers that take names from the network must expect this.
    /// </exception>
    public static List<string> SplitLabels(string name)
    {
        List<string> labels = [];
        var current = new StringBuilder();
        int total = 1;   // the root label's terminating zero octet

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '\\')
            {
                if (i + 3 < name.Length && IsDigit(name[i + 1]) && IsDigit(name[i + 2]) && IsDigit(name[i + 3]))
                {
                    int v = (name[i + 1] - '0') * 100 + (name[i + 2] - '0') * 10 + (name[i + 3] - '0');
                    if (v > 255) throw new FormatException($"invalid escape '\\{name.Substring(i + 1, 3)}' in '{name}'");
                    current.Append((char)v);
                    i += 3;
                    continue;
                }
                if (i + 1 >= name.Length) throw new FormatException($"name ends with a lone backslash: '{name}'");
                current.Append(name[++i]);
                continue;
            }
            if (c != '.') { current.Append(c); continue; }

            // A dot at the very end is the root and terminates the name rather than opening an
            // empty label; anywhere else, an empty label is unencodable.
            if (i == name.Length - 1 && current.Length > 0) break;
            total += Commit(labels, current, name);
        }
        if (current.Length > 0)
            total += Commit(labels, current, name);

        if (labels.Count == 0) throw new FormatException("empty DNS name");
        if (total > MaxNameBytes) throw new FormatException($"DNS name exceeds {MaxNameBytes} bytes: '{name}'");
        return labels;

        static bool IsDigit(char c) => c is >= '0' and <= '9';

        static int Commit(List<string> labels, StringBuilder current, string name)
        {
            if (current.Length == 0) throw new FormatException($"empty label in DNS name '{name}'");
            string label = current.ToString();
            int bytes = Encoding.UTF8.GetByteCount(label);
            if (bytes > MaxLabelBytes)
                throw new FormatException($"DNS label exceeds {MaxLabelBytes} bytes in '{name}'");
            labels.Add(label);
            current.Clear();
            return bytes + 1;   // the label plus its length octet
        }
    }

    /// <summary>Joins wire labels into a presentation-format name, escaping each.</summary>
    public static string Join(IEnumerable<string> labels) => string.Join('.', labels.Select(Escape));

    /// <summary>
    /// Whether <paramref name="name"/> can be encoded on the wire. Lets callers reject a hostile
    /// or badly-formed name at the point it arrives instead of discovering it much later, at the
    /// point they try to send it.
    /// </summary>
    public static bool IsEncodable(string name)
    {
        try { SplitLabels(name); return true; }
        catch (FormatException) { return false; }
    }
}
