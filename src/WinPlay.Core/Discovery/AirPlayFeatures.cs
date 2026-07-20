// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Discovery;

/// <summary>
/// AirPlay `features` TXT bitfield (64-bit). Named bits follow the openairplay
/// airplay-spec / emanuelecozzi.net documentation; the enum is deliberately
/// non-exhaustive — undocumented bits are preserved in <c>AirPlayDevice.RawFeatures</c>.
/// </summary>
[Flags]
public enum AirPlayFeatures : ulong
{
    None = 0,
    SupportsAirPlayVideoV1 = 1UL << 0,
    SupportsAirPlayPhoto = 1UL << 1,
    SupportsAirPlaySlideshow = 1UL << 5,
    SupportsAirPlayScreen = 1UL << 7,
    SupportsAirPlayAudio = 1UL << 9,
    AudioRedundant = 1UL << 11,
    FpSapV2p5AesGcm = 1UL << 14,
    MetadataArtwork = 1UL << 17,
    MetadataProgress = 1UL << 18,
    MetadataText = 1UL << 19,
    AudioFormat1 = 1UL << 20,
    AudioFormat2 = 1UL << 21,
    AudioFormat3 = 1UL << 22,
    AudioFormat4 = 1UL << 23,
    AuthenticationRsa = 1UL << 26,
    AuthenticationMfi = 1UL << 27,
    HasUnifiedAdvertiserInfo = 1UL << 30,
    SupportsCoreUtilsPairingAndEncryption = 1UL << 38,
    SupportsBufferedAudio = 1UL << 40,

    /// <summary>"Bit needed for device to show as supporting multi-room audio."</summary>
    SupportsPtp = 1UL << 41,

    SupportsScreenMultiCodec = 1UL << 42,
    SupportsSystemPairing = 1UL << 43,
    SupportsHkPairingAndAccessControl = 1UL << 46,
    SupportsTransientPairing = 1UL << 48,
    SupportsUnifiedPairSetupAndMfi = 1UL << 51,
    SupportsSetPeersExtendedMessage = 1UL << 52,
}

public static class AirPlayFeaturesExtensions
{
    /// <summary>
    /// Minimal capability set for multi-room audio per the spec: SupportsAirPlayAudio (9),
    /// AudioRedundant (11), HasUnifiedAdvertiserInfo (30), SupportsBufferedAudio (40),
    /// SupportsPTP (41), SupportsUnifiedPairSetupAndMFi (51).
    /// </summary>
    public const AirPlayFeatures MultiRoomCapabilitySet =
        AirPlayFeatures.SupportsAirPlayAudio
        | AirPlayFeatures.AudioRedundant
        | AirPlayFeatures.HasUnifiedAdvertiserInfo
        | AirPlayFeatures.SupportsBufferedAudio
        | AirPlayFeatures.SupportsPtp
        | AirPlayFeatures.SupportsUnifiedPairSetupAndMfi;

    /// <summary>
    /// Parses the TXT `features` value: either a single hex/decimal value or two
    /// comma-separated 32-bit hex values where the FIRST is the least-significant word
    /// (e.g. "0x4A7FCA00,0x3C155FDE" → 0x3C155FDE_4A7FCA00).
    /// </summary>
    public static ulong ParseFeatures(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
            return ParseOne(parts[0]);
        if (parts.Length == 2)
            return ParseOne(parts[0]) | (ParseOne(parts[1]) << 32);
        return 0;

        static ulong ParseOne(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out ulong hex) ? hex : 0;
            return ulong.TryParse(s, out ulong dec) ? dec : 0;
        }
    }
}
