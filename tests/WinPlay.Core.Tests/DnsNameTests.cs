// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using WinPlay.Core.Dns;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// A DNS name is a list of labels, and the code passes it around as a dotted string. These tests
/// pin the property that makes that safe: labels → string → labels must be the identity, for every
/// name a device can legally advertise. DNS-SD instance names are arbitrary user-chosen UTF-8
/// (RFC 6763 §4.3), so "legally" includes dots, backslashes, and emoji.
/// </summary>
public class DnsNameTests
{
    /// <summary>
    /// Encodes labels exactly as a responder would put them on the wire, then reads them back
    /// through the real parser — a one-question message is the smallest packet that carries a
    /// bare name.
    /// </summary>
    private static string WireRoundTrip(params string[] labels)
    {
        List<byte> buf = [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0];  // header, QDCOUNT=1
        foreach (string label in labels)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.Add(0);
        buf.AddRange([0, (byte)DnsType.Srv, 0, 1]);             // QTYPE, QCLASS
        return DnsMessage.Parse([.. buf]).Questions[0].Name;
    }

    public static TheoryData<string[]> Names =>
    [
        // Ordinary names — escaping must not touch them.
        ["Living Room", "_airplay", "_tcp", "local"],
        // The name that used to kill discovery outright: a trailing dot in the instance label made
        // an empty label on the way back out, and encoding that threw straight out of the browse
        // loop, ending discovery for the rest of the session.
        ["Kitchen.", "_airplay", "_tcp", "local"],
        // A dot mid-label silently became two labels, so every follow-up query missed.
        ["Mr. Roboto", "_raop", "_tcp", "local"],
        [".", "_airplay", "_tcp", "local"],
        ["a.b.c.d", "_airplay", "_tcp", "local"],
        ["back\\slash", "_airplay", "_tcp", "local"],
        ["Dinesh’s MacBook Pro", "_airplay", "_tcp", "local"],   // real device, curly apostrophe
        ["Küche 🔊", "_raop", "_tcp", "local"],
        ["AABBCCDDEEFF@Living Room", "_raop", "_tcp", "local"],
    ];

    [Theory]
    [MemberData(nameof(Names))]
    public void Labels_Survive_The_Round_Trip_Exactly(string[] labels)
    {
        string presentation = WireRoundTrip(labels);
        Assert.Equal(labels, DnsName.SplitLabels(presentation));
    }

    [Fact]
    public void The_Instance_Label_Is_Recoverable_For_Display()
    {
        string presentation = WireRoundTrip("Mr. Roboto", "_raop", "_tcp", "local");
        Assert.Equal("Mr\\. Roboto._raop._tcp.local", presentation);
        Assert.Equal("Mr. Roboto", DnsName.Unescape(DnsName.SplitLabels(presentation)[0]));
    }

    [Fact]
    public void A_Name_With_A_Dotted_Label_Encodes_Back_To_The_Same_Wire_Bytes()
    {
        // The end-to-end property the browser depends on: a targeted query for a discovered
        // instance has to address the instance that was actually discovered.
        string presentation = WireRoundTrip("Kitchen.", "_airplay", "_tcp", "local");
        byte[] query = DnsQueryWriter.BuildQuery([(presentation, DnsType.Srv, false)]);
        Assert.Equal(presentation, DnsMessage.Parse(query).Questions[0].Name);
    }

    [Fact]
    public void A_Trailing_Root_Dot_Is_Accepted_And_Ignored()
    {
        Assert.Equal(DnsName.SplitLabels("x.local"), DnsName.SplitLabels("x.local."));
    }

    [Theory]
    [InlineData("")]                    // no labels at all
    [InlineData(".")]
    [InlineData("a..b")]                // a genuinely empty label
    [InlineData("trailing\\")]          // lone backslash
    public void Unencodable_Names_Are_Rejected_As_FormatException(string name)
    {
        Assert.IsType<FormatException>(Record.Exception(() => DnsName.SplitLabels(name)));
        Assert.False(DnsName.IsEncodable(name));
    }

    [Fact]
    public void Oversized_Labels_And_Names_Are_Rejected()
    {
        Assert.False(DnsName.IsEncodable(new string('a', 64) + ".local"));
        Assert.True(DnsName.IsEncodable(new string('a', 63) + ".local"));

        string long255 = string.Join('.', Enumerable.Repeat(new string('a', 63), 5));
        Assert.False(DnsName.IsEncodable(long255));
    }

    [Fact]
    public void Control_Characters_Round_Trip_Through_Decimal_Escapes()
    {
        const string raw = "belltab	";
        string escaped = DnsName.Escape(raw);
        Assert.Equal(@"bell\007tab\009", escaped);
        Assert.Equal(raw, DnsName.Unescape(escaped));
        Assert.Equal([raw], DnsName.SplitLabels(escaped));
    }
}
