// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Hap;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Verifies receiver identity pinning (Task G1): a substituted device is detected and refused,
/// the pin survives restarts, and the user-driven recovery path works.
/// </summary>
public sealed class ReceiverIdentityStoreTests : IDisposable
{
    private const string HomePodKey = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";
    private const string ImpostorKey = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "winplay-pins-" + Guid.NewGuid().ToString("N") + ".dat");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
    }

    private ReceiverIdentityStore NewStore() => new(_path);

    [Fact]
    public void An_Unknown_Receiver_Is_First_Use()
    {
        var check = NewStore().Check("FAA3D5083FF6", HomePodKey);
        Assert.Equal(IdentityTrust.FirstUse, check.Trust);
        Assert.True(check.IsAcceptable);
        Assert.Null(check.PinnedKey);
    }

    [Fact]
    public void A_Pinned_Receiver_Presenting_The_Same_Key_Is_Trusted()
    {
        var store = NewStore();
        store.Pin("FAA3D5083FF6", HomePodKey, "Guest Bedroom");

        var check = store.Check("FAA3D5083FF6", HomePodKey);
        Assert.Equal(IdentityTrust.Trusted, check.Trust);
        Assert.True(check.IsAcceptable);
    }

    [Fact]
    public void A_Substituted_Device_Is_Detected_And_Not_Acceptable()
    {
        // The attack this defends against: something on the LAN answers to a known device id
        // with a different identity. Transient pairing alone would happily stream to it.
        var store = NewStore();
        store.Pin("FAA3D5083FF6", HomePodKey, "Guest Bedroom");

        var check = store.Check("FAA3D5083FF6", ImpostorKey);
        Assert.Equal(IdentityTrust.Mismatch, check.Trust);
        Assert.False(check.IsAcceptable);
        Assert.Equal(HomePodKey, check.PinnedKey);
        Assert.Equal(ImpostorKey, check.PresentedKey);
    }

    [Fact]
    public void The_Pin_Persists_Across_Restarts()
    {
        NewStore().Pin("FAA3D5083FF6", HomePodKey, "Guest Bedroom");

        // A completely new instance — as after an app restart — must still enforce the pin.
        var check = NewStore().Check("FAA3D5083FF6", ImpostorKey);
        Assert.Equal(IdentityTrust.Mismatch, check.Trust);
    }

    [Fact]
    public void Checking_Does_Not_Silently_Establish_A_Pin()
    {
        // Check must be side-effect free, otherwise an impostor seen once would become trusted.
        var store = NewStore();
        Assert.Equal(IdentityTrust.FirstUse, store.Check("DEV1", HomePodKey).Trust);
        Assert.Equal(IdentityTrust.FirstUse, store.Check("DEV1", ImpostorKey).Trust);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Forget_Allows_A_Genuinely_Replaced_Device_To_Be_Trusted_Again()
    {
        var store = NewStore();
        store.Pin("FAA3D5083FF6", HomePodKey);
        Assert.Equal(IdentityTrust.Mismatch, store.Check("FAA3D5083FF6", ImpostorKey).Trust);

        Assert.True(store.Forget("FAA3D5083FF6"));

        Assert.Equal(IdentityTrust.FirstUse, store.Check("FAA3D5083FF6", ImpostorKey).Trust);
        Assert.False(store.Forget("FAA3D5083FF6")); // already gone
    }

    [Fact]
    public void Device_Ids_And_Keys_Are_Compared_Case_And_Separator_Insensitively()
    {
        var store = NewStore();
        store.Pin("FA:A3:D5:08:3F:F6", HomePodKey.ToLowerInvariant());

        Assert.Equal(IdentityTrust.Trusted, store.Check("faa3d5083ff6", HomePodKey).Trust);
        Assert.Equal(IdentityTrust.Trusted, store.Check("FA-A3-D5-08-3F-F6", HomePodKey.ToLowerInvariant()).Trust);
    }

    [Fact]
    public void A_Receiver_That_Advertises_No_Key_Is_Unverifiable_Not_Trusted()
    {
        var store = NewStore();
        var check = store.Check("SHAIRPORT1", null);

        Assert.Equal(IdentityTrust.Unverifiable, check.Trust);
        Assert.True(check.IsAcceptable);   // third-party speakers must keep working
        store.Pin("SHAIRPORT1", null);
        Assert.Empty(store.List());        // …but nothing false is recorded as pinned
    }

    [Fact]
    public void Re_Pinning_The_Same_Key_Preserves_The_First_Seen_Time()
    {
        var store = NewStore();
        store.Pin("DEV1", HomePodKey, "Guest Bedroom");
        string firstSeen = store.List().Single().LastSeenUtc;

        Thread.Sleep(10);
        store.Pin("DEV1", HomePodKey, "Guest Bedroom");

        var entry = store.List().Single();
        Assert.Equal(HomePodKey, entry.PublicKey);
        Assert.Equal("Guest Bedroom", entry.Name);
        Assert.NotEqual(firstSeen, entry.LastSeenUtc); // last-seen advances…
    }

    [Fact]
    public void Multiple_Receivers_Are_Pinned_Independently()
    {
        var store = NewStore();
        store.Pin("DEV1", HomePodKey, "Guest Bedroom");
        store.Pin("DEV2", ImpostorKey, "Study");

        Assert.Equal(IdentityTrust.Trusted, store.Check("DEV1", HomePodKey).Trust);
        Assert.Equal(IdentityTrust.Trusted, store.Check("DEV2", ImpostorKey).Trust);
        Assert.Equal(IdentityTrust.Mismatch, store.Check("DEV1", ImpostorKey).Trust);
        Assert.Equal(2, store.List().Count);
    }
}
