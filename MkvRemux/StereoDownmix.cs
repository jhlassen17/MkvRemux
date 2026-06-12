namespace MkvRemux;

/// <summary>
/// Builds the ffmpeg pan filter string for the stereo downmix track.
///
/// Default mode: ffmpeg's built-in -ac 2 matrix (fast, acceptable for most content)
/// Dolby mode:   explicit pan filter — mixes center dialogue into both ears,
///               blends surround channels at -3 dB, excludes LFE
///
/// Channel layout awareness:
///   5.1(side)  → uses SL/SR channel names
///   5.1(back)  → uses BL/BR channel names
///   7.1        → mixes all four surround channels (SL/SR at -3dB, BL/BR at -6dB)
///   4.0 / quad → blends rear into front without a center
///   ≤ stereo   → no filter applied (passthrough)
/// </summary>
public static class StereoDownmix
{
    /// <summary>
    /// Default mode: ffmpeg's built-in -ac 2 matrix (fast, acceptable for most content)
    /// </summary>
    public enum Mode { Default, Dolby }

    /// <summary>
    /// Builds the ffmpeg pan filter string for the stereo downmix track.
    /// </summary>
    /// <param name="mode">The stereo downmix mode to use.</param>
    /// <param name="source">The source audio stream information.</param>
    /// <returns>The ffmpeg pan filter string, or null if no filter is needed.</returns>
    public static string? GetFilter(Mode mode, AudioStreamInfo source)
    {
        // No filter needed for default mode or if the source is already stereo or mono
        if (mode == Mode.Default || source.Channels <= 2)
            return null;

        // Use the channel layout to determine the appropriate pan filter coefficients
        string layout = source.ChannelLayout.ToLowerInvariant();

        // Build the pan filter string based on the number of channels and layout
        return source.Channels switch
        {
            // ── 7.1 (FL FR FC LFE BL BR SL SR) ────────────────────────────
            8 =>
                "pan=stereo" +
                "|FL=FC*0.707+FL+SL*0.707+BL*0.5" +
                "|FR=FC*0.707+FR+SR*0.707+BR*0.5",

            // ── 6.1 (FL FR FC LFE BC SL SR) ────────────────────────────────
            7 =>
                "pan=stereo" +
                "|FL=FC*0.707+FL+SL*0.707+BC*0.5" +
                "|FR=FC*0.707+FR+SR*0.707+BC*0.5",

            // ── 5.1(side) (FL FR FC LFE SL SR) ─────────────────────────────
            6 when layout.Contains("side") =>
                "pan=stereo" +
                "|FL=FC*0.707+FL+SL*0.707" +
                "|FR=FC*0.707+FR+SR*0.707",

            // ── 5.1 / 5.1(back) (FL FR FC LFE BL BR) ───────────────────────
            6 =>
                "pan=stereo" +
                "|FL=FC*0.707+FL+BL*0.707" +
                "|FR=FC*0.707+FR+BR*0.707",

            // ── 4.0 quad (FL FR BL BR) ──────────────────────────────────────
            4 =>
                "pan=stereo" +
                "|FL=FL+BL*0.707" +
                "|FR=FR+BR*0.707",

            // ── Unknown layout ───────────────────────────────────────────────
            _ => null
        };
    }

    /// <summary>
    /// Parses a string into a Mode enum value. Accepts "default", "dolby", "dpl", or "dplii" (case-insensitive).
    /// </summary>
    /// <param name="value">The string representation of the mode.</param>
    /// <returns>The corresponding Mode enum value.</returns>
    /// <exception cref="ArgumentException">Thrown if the input string does not match any known mode.</exception>
    public static Mode Parse(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "dolby" or "dpl" or "dplii" => Mode.Dolby,
            null or "default"           => Mode.Default,
            _ => throw new ArgumentException(
                $"Unknown stereo filter '{value}'. Use: default, dolby")
        };
}
