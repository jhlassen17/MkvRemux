namespace MkvRemux;

/// <summary>
/// Builds ffmpeg codec arguments and display metadata for lossless audio tracks.
///
/// Track mapping is handled by CommandBuilder — this class only produces the
/// per-track codec/option string and the human-readable title.
///
/// Codec flags:
///   FLAC  -c:a:N flac  -compression_level 8
///   ALAC  -c:a:N alac
///   PCM   -c:a:N pcm_s{bits}le   (bit depth derived from source stream)
///
/// PCM codec selection by effective bit depth:
///   16-bit → pcm_s16le
///   24-bit → pcm_s24le   (most common for HD audio; default when unknown)
///   32-bit → pcm_s32le
///   other  → pcm_s24le   (safe fallback)
/// </summary>
static class LosslessArgBuilder
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the ffmpeg codec argument fragment for one lossless track,
    /// e.g. "flac -compression_level 8" (caller prepends "-c:a:N ").
    /// </summary>
    /// <param name="fmt">The lossless format.</param>
    /// <param name="source">The source audio stream information.</param>
    /// <returns>The ffmpeg codec argument fragment for the specified lossless track.</returns>
    public static string CodecArgs(LosslessFormat fmt, AudioStreamInfo source) =>
        // Note: FLAC compression level 8 is the slowest but offers the best compression,
        fmt switch
        {
            LosslessFormat.Flac => "flac -compression_level 8",
            LosslessFormat.Alac => "alac",
            LosslessFormat.Pcm  => $"pcm_{PcmCodecSuffix(source.EffectiveBitDepth)}",
            _                   => throw new ArgumentOutOfRangeException(nameof(fmt))
        };

    /// <summary>
    /// Returns the metadata title for the lossless track,
    /// e.g. "FLAC 5.1", "ALAC 7.1", "PCM 24-bit 5.1".
    /// </summary>
    /// <param name="fmt">The lossless format.</param>
    /// <param name="source">The source audio stream information.</param>
    /// <returns>The metadata title for the specified lossless track.</returns>
    public static string TrackTitle(LosslessFormat fmt, AudioStreamInfo source) =>
        // Note: Include the effective bit depth for PCM since it can vary widely and is relevant to users,
        fmt switch
        {
            LosslessFormat.Flac => $"FLAC {source.ChannelDesc}",
            LosslessFormat.Alac => $"ALAC {source.ChannelDesc}",
            LosslessFormat.Pcm  => $"PCM {source.EffectiveBitDepth}-bit {source.ChannelDesc}",
            _                   => throw new ArgumentOutOfRangeException(nameof(fmt))
        };

    /// <summary>
    /// Parses a comma-separated or repeated --lossless argument value.
    /// Accepts: flac, alac, pcm (case-insensitive).
    /// Returns a de-duplicated list in the order: FLAC → ALAC → PCM.
    /// </summary>
    /// <param name="values">The input values to parse.</param>
    /// <returns>A de-duplicated list of lossless formats in the order: FLAC → ALAC → PCM.</returns>
    public static List<LosslessFormat> Parse(IEnumerable<string> values)
    {
        // Use a HashSet to de-duplicate formats specified multiple times or in combination,
        var result = new HashSet<LosslessFormat>();

        // Parse each value, splitting on commas to allow both repeated and comma-separated formats,
        foreach (string v in values)
        {
            foreach (string token in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                // Try to parse the token as a LosslessFormat enum value, ignoring case,
                if (!Enum.TryParse<LosslessFormat>(token.Trim(), ignoreCase: true, out var fmt))
                    throw new ArgumentException(
                        $"Unknown lossless format '{token}'. Valid options: flac, alac, pcm");
                result.Add(fmt);
            }
        }

        // Stable output order regardless of how the user specified them
        return new[] { LosslessFormat.Flac, LosslessFormat.Alac, LosslessFormat.Pcm }
            .Where(result.Contains)
            .ToList();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a bit depth to the signed little-endian PCM codec name suffix,
    /// e.g. 24 → "s24le" → codec "pcm_s24le".
    /// </summary>
    /// <param name="bits">The bit depth of the PCM audio.</param>
    /// <returns>The PCM codec name suffix for the specified bit depth.</returns>
    private static string PcmCodecSuffix(int bits) => bits switch
    {
        16 => "s16le",
        24 => "s24le",
        32 => "s32le",
        _  => "s24le"   // safe default for compressed sources (DTS-HD, TrueHD decode to 24-bit)
    };
}
