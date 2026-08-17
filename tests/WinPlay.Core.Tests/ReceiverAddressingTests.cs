// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using WinPlay.Core.Net;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Pins WinPlay's address-family policy as an explicit, testable contract rather than an
/// assumption repeated across the transport layer.
///
/// <para>The behaviour that matters to a user is the distinction these tests draw: a receiver
/// whose address has not arrived yet is a timing problem and will resolve on its own, while a
/// receiver that published only an IPv6 address is permanently undialable and must be told so.
/// Reporting the second as the first invites a retry that can never succeed.</para>
/// </summary>
public class ReceiverAddressingTests
{
    private static readonly IPAddress V4 = IPAddress.Parse("192.168.1.50");
    private static readonly IPAddress V6 = IPAddress.Parse("2603:8080:fa00:d45::1e59");

    [Fact]
    public void An_IPv4_Address_Is_Chosen_Even_When_IPv6_Comes_First()
    {
        // Real devices publish both, and AAAA often arrives first. The choice must not depend on
        // announcement order.
        Assert.Equal(V4, ReceiverAddressing.Select([V6, V4]));
        Assert.Equal(V4, ReceiverAddressing.Select([V4, V6]));
    }

    [Fact]
    public void An_IPv6_Only_Receiver_Has_No_Dialable_Address()
    {
        Assert.Null(ReceiverAddressing.Select([V6]));
        Assert.False(ReceiverAddressing.IsDialable(V6));
    }

    [Fact]
    public void Unreachable_Family_Is_Distinguished_From_Not_Resolved_Yet()
    {
        // The whole point of the distinction: one is permanent, the other is a moment away.
        Assert.True(ReceiverAddressing.IsUnreachableFamily([V6]));
        Assert.False(ReceiverAddressing.IsUnreachableFamily([]));
        Assert.False(ReceiverAddressing.IsUnreachableFamily([V6, V4]));
    }

    [Fact]
    public void The_Policy_Is_Stated_Once()
    {
        // A guard on the thing the doc comment promises: making WinPlay dual-stack is a change to
        // this constant and its consumers, not an archaeology exercise across the transport layer.
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ReceiverAddressing.PreferredFamily);
        Assert.True(ReceiverAddressing.IsDialable(V4));
    }
}
