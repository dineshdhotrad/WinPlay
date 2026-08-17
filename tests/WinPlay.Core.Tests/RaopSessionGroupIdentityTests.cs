// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// The SETUP(session) <c>groupUUID</c> names the group THIS SENDER is forming. It is minted per
/// playback session and sent unchanged to every member; it is never derived from what a receiver
/// advertises about itself.
///
/// <para>An earlier revision echoed the receiver's mDNS <c>gid</c> into this field. That field is
/// a membership advertisement and is legitimately compound — a HomePod in a home group advertises
/// <c>gid=&lt;uuid&gt;+&lt;uuid&gt;</c>, which <c>DevicePicker</c> already splits on <c>'+'</c>.
/// Echoed into SETUP it produced a 73-character <c>groupUUID</c>; a HomePod mini accepted every
/// request, reported no error, and rendered silence. Captured off the wire beside the working
/// build, that single field was the entire difference. owntone-server sizes its own field
/// <c>char group_uuid[37]</c> — one UUID plus terminator — which the compound form cannot fit.</para>
///
/// <para>These tests fix the contract as a validating parse: whatever the source, nothing but a
/// single well-formed UUID can reach the wire.</para>
/// </summary>
public class RaopSessionGroupIdentityTests
{
    /// <summary>
    /// The exact shape that silenced the HomePod mini: two UUIDs joined by '+'. It must be
    /// rejected rather than forwarded, and replaced with something well-formed.
    /// </summary>
    [Fact]
    public void CompoundAdvertisedGroupId_IsNeverSentOnTheWire()
    {
        const string compound = "DF6D51D7-7A76-416A-82FA-C056A0CD6DBE+E320B78B-A3BA-46E7-8BB1-8C2626CB7281";

        string groupUuid = RaopSession.ResolveGroupUuid(compound);

        Assert.NotEqual(compound, groupUuid);
        Assert.DoesNotContain('+', groupUuid);
        Assert.True(Guid.TryParse(groupUuid, out _));
    }

    /// <summary>Anything that is not a single UUID is replaced, not forwarded.</summary>
    [Theory]
    [InlineData("SOME-GROUP-ID")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-uuid-at-all")]
    [InlineData("DF6D51D7-7A76-416A-82FA-C056A0CD6DBE+")]
    public void MalformedGroupIdentity_IsReplacedWithAFreshUuid(string? advertised)
    {
        Assert.True(Guid.TryParse(RaopSession.ResolveGroupUuid(advertised), out _));
    }

    /// <summary>
    /// A caller-supplied session group UUID is honoured verbatim (upper-cased): that is how every
    /// member of one multi-room session is told it belongs to the same group.
    /// </summary>
    [Fact]
    public void WellFormedSessionGroupUuid_IsPreservedForEveryMember()
    {
        string shared = Guid.NewGuid().ToString().ToUpperInvariant();

        Assert.Equal(shared, RaopSession.ResolveGroupUuid(shared));
        Assert.Equal(shared, RaopSession.ResolveGroupUuid(shared.ToLowerInvariant()));
    }

    /// <summary>Absent a supplied identity, each session gets its own group.</summary>
    [Fact]
    public void NoSuppliedIdentity_MintsADistinctGroupPerSession()
    {
        Assert.NotEqual(RaopSession.ResolveGroupUuid(null), RaopSession.ResolveGroupUuid(null));
    }
}
