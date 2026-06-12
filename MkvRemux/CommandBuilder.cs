using System.Text;

namespace MkvRemux;

/// <summary>
/// Selects streams and assembles the complete ffmpeg argument string.
///
/// Output audio track layout (conditional on source):
///
///   Stereo source (≤ 2ch):
///     [0]        Main audio          — bitstream copy
///
///   5.1+ AAC source:
///     [0]        Main audio          — bitstream copy
///     [1]        AAC Stereo          — downmix (default or Dolby pan filter)
///
///   5.1+ non-AAC source:
///     [0]        Main audio          — bitstream copy
///     [1]        AAC Surround        — same channels as main
///     [2]        AAC Stereo          — downmix (default or Dolby pan filter)
///
///   [N..N+L-1]  Lossless tracks     — FLAC / ALAC / PCM (optional)
///   [N+L..]     Secondary ENG/UND   — bitstream copy
///
/// Disposition flags (default track markers):
///   Video    0 → default, all others cleared
///   Audio    0 → default, all others cleared
///   Subtitle — SDH track → +default+hearing_impaired; non-SDH → no auto-default (opt-in behavior)
/// </summary>
public static class CommandBuilder
{
    /// <summary>
    /// Preferred languages for main audio and subtitle selection. Case-insensitive.
    /// </summary>
    public static readonly HashSet<string> PreferredLangs =
        new(StringComparer.OrdinalIgnoreCase) { "eng", "und" };

    /// <summary>
    /// Builds the ffmpeg argument string based on the input file's streams and the specified options.
    /// </summary>
    /// <param name="inputPath">The path to the input MKV file.</param>
    /// <param name="outputPath">The path to the output MKV file.</param>
    /// <param name="video">Information about the video stream.</param>
    /// <param name="allAudio">A list of all audio streams in the input file.</param>
    /// <param name="allSubs">A list of all subtitle streams in the input file.</param>
    /// <param name="videoArgs">Additional arguments for video encoding.</param>
    /// <param name="losslessFmts">A list of lossless audio formats to include.</param>
    /// <param name="stereoMode">The stereo downmix mode to use.</param>
    /// <returns>The constructed ffmpeg argument string.</returns>
    public static string Build(
        string inputPath,
        string outputPath,
        VideoStreamInfo? video,
        List<AudioStreamInfo> allAudio,
        List<SubtitleStreamInfo> allSubs,
        string? videoArgs = null,
        List<LosslessFormat>? losslessFmts = null,
        StereoDownmix.Mode stereoMode = StereoDownmix.Mode.Default)
    {
        // Ensure losslessFmts is not null to simplify later logic
        losslessFmts ??= [];

        // ── 1. Select audio ──────────────────────────────────────────────────
        var engUnd = allAudio
            .Where(a => PreferredLangs.Contains(a.Language))
            .OrderBy(a => a.CodecPriority)
            .ThenBy(a => a.GlobalIndex)
            .ToList();

        AudioStreamInfo mainAudio;
        List<AudioStreamInfo> secondary;

        // If multiple ENG/UND tracks are found, the one with the highest codec priority (and lowest index as tiebreaker) is chosen as main.
        if (engUnd.Count > 0)
        {
            mainAudio = engUnd[0];
            secondary = engUnd.Skip(1).ToList();
        }
        else
        {
            // No ENG/UND audio found — include all tracks as secondary and pick the best one as main.
            Console.WriteLine("  [WARN] No ENG/UND audio found — including all audio tracks.");
            var all = allAudio.OrderBy(a => a.CodecPriority).ThenBy(a => a.GlobalIndex).ToList();
            mainAudio = all[0];
            secondary = all.Skip(1).ToList();
        }

        // ── 2. Select subtitles ──────────────────────────────────────────────
        var engUndSubs = allSubs.Where(s => PreferredLangs.Contains(s.Language)).ToList();
        var chosenSubs = engUndSubs.Count > 0 ? engUndSubs : allSubs;
        var selectedSub = SelectSubtitleStream(chosenSubs);

        // ── 3. Audio track layout ─────────────────────────────────────────────
        // Stereo source (≤ 2ch)  → 1 main track:  copy only
        // 5.1+ AAC source        → 2 main tracks: copy + AAC stereo downmix
        // 5.1+ non-AAC source    → 3 main tracks: copy + AAC surround + AAC stereo downmix
        bool isStereoOnly  = mainAudio.Channels <= 2;
        bool isAacSurround = !isStereoOnly && mainAudio.Codec.Equals("aac", StringComparison.OrdinalIgnoreCase);
        bool needsSurround = !isStereoOnly && !isAacSurround;   // true only for 5.1+ non-AAC

        // The stereo downmix track sits at index 1 when there's no separate surround track, 2 otherwise.
        int stereoOutIdx   = needsSurround ? 2 : 1;
        int mainTrackCount = isStereoOnly ? 1 : (needsSurround ? 3 : 2);

        // Lossless and secondary tracks follow the main group.
        int losslessStart  = mainTrackCount;
        int secondaryStart = losslessStart + losslessFmts.Count;
        int totalAudioOut  = mainTrackCount + losslessFmts.Count + secondary.Count;

        // ── 4. Stereo filter ─────────────────────────────────────────────────
        string? panFilter = StereoDownmix.GetFilter(stereoMode, mainAudio);

        // ── 5. Log selections ────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  Stream selection:");

        // Video handling: if videoArgs is provided, we assume re-encoding; otherwise, we copy.
        if (video is not null)
        {
            string videoMode = videoArgs is not null ? "encode → HEVC Main10" : "copy";
            Console.WriteLine($"    Video       [{video.GlobalIndex}]: {video.Codec.ToUpper()} {video.Resolution} — {videoMode}");
        }

        // Audio: log only the tracks that will actually be written.
        string stereoDesc = panFilter is not null ? "AAC Stereo (Dolby pan)" : "AAC Stereo (default)";
        Console.WriteLine($"    Main audio  [{mainAudio.GlobalIndex}]: {mainAudio.DisplayName} ({mainAudio.Language})");
        Console.WriteLine($"      → Track 0: copy  [DEFAULT]");
        if (needsSurround)
            Console.WriteLine($"      → Track 1: AAC {mainAudio.ChannelDesc} @ {mainAudio.AacSurroundBitrate}k");
        if (!isStereoOnly)
            Console.WriteLine($"      → Track {stereoOutIdx}: {stereoDesc} @ 256k");

        // Lossless tracks
        for (int i = 0; i < losslessFmts.Count; i++)
            Console.WriteLine($"      → Track {losslessStart + i}: {LosslessArgBuilder.TrackTitle(losslessFmts[i], mainAudio)}");

        // Secondary audio
        foreach (var a in secondary)
            Console.WriteLine($"    Extra audio [{a.GlobalIndex}]: {a.DisplayName} ({a.Language})");

        // Subtitles: show which track will be flagged as default, matching SelectSubtitleStream's logic.
        if (chosenSubs.Count == 0)
        {
            Console.WriteLine("    Subtitles  : none found");
        }
        else
        {
            for (int i = 0; i < chosenSubs.Count; i++)
            {
                // Only SDH tracks are marked as default; non-SDH selected tracks are opt-in (no auto-default).
                bool isDefault = selectedSub is not null && chosenSubs[i] == selectedSub && selectedSub.IsSDH;
                string defLabel = isDefault ? "  [DEFAULT]" : "";
                Console.WriteLine($"    Subtitle    [{chosenSubs[i].GlobalIndex}]: {chosenSubs[i].DisplayName}{defLabel}");
            }
        }

        // ── 6. Assemble arguments ────────────────────────────────────────────
        var sb = new StringBuilder();

        // Input file
        sb.Append($"-i \"{inputPath}\"");

       // Video map: use the explicit global index rather than "-map 0:v", which would pick up
        // all video streams including embedded cover art / thumbnail streams.
        if (video is not null)
            sb.Append($" -map 0:{video.GlobalIndex}");

        // Audio maps: track 0 (copy) is always present; surround and stereo are conditional.
        sb.Append($" -map 0:{mainAudio.GlobalIndex}");   // track 0: copy (always)
        if (needsSurround)
            sb.Append($" -map 0:{mainAudio.GlobalIndex}");   // track 1: AAC surround
        if (!isStereoOnly)
            sb.Append($" -map 0:{mainAudio.GlobalIndex}");   // track stereoOutIdx: AAC stereo

        // Lossless tracks
        foreach (var _ in losslessFmts)
            sb.Append($" -map 0:{mainAudio.GlobalIndex}");

        // Secondary audio
        foreach (var a in secondary)
            sb.Append($" -map 0:{a.GlobalIndex}");

        // Subtitles — mapped in chosenSubs order; metadata indices must match this order exactly.
        foreach (var sub in chosenSubs)
            sb.Append($" -map 0:{sub.GlobalIndex}");

        // Chapters
        sb.Append(" -map_chapters 0");

        // Video codec
        sb.Append(videoArgs is not null ? $" {videoArgs}" : " -c:v copy");

        // Audio codecs
        sb.Append(" -c:a:0 copy");

        // Surround track for non-AAC sources, or stereo track for AAC sources — both conditional on source properties.
        if (needsSurround)
            sb.Append($" -c:a:1 aac -b:a:1 {mainAudio.AacSurroundBitrate}k");

        // Stereo downmix track for 5.1+ sources — conditional on source properties.
        // If a Dolby pan filter is available for the source, it is used here; otherwise,
        // a default downmix is applied.
        if (!isStereoOnly)
        {
            if (panFilter is not null)
                sb.Append($" -c:a:{stereoOutIdx} aac -b:a:{stereoOutIdx} 256k -filter:a:{stereoOutIdx} \"{panFilter}\"");
            else
                sb.Append($" -c:a:{stereoOutIdx} aac -ac:a:{stereoOutIdx} 2 -b:a:{stereoOutIdx} 256k");
        }

        // Lossless tracks
        for (int i = 0; i < losslessFmts.Count; i++)
            sb.Append($" -c:a:{losslessStart + i} {LosslessArgBuilder.CodecArgs(losslessFmts[i], mainAudio)}");

        // Secondary audio (bitstream copy)
        for (int i = 0; i < secondary.Count; i++)
            sb.Append($" -c:a:{secondaryStart + i} copy");

        // Subtitles
        sb.Append(" -c:s copy");

        // ── 7. Disposition flags ─────────────────────────────────────────────
        // Video: track 0 default, clear any others ffmpeg may have inherited.
        sb.Append(" -disposition:v:0 default");

        // Audio: track 0 (bitstream copy) default, all others explicitly cleared.
        sb.Append(" -disposition:a:0 default");
        for (int i = 1; i < totalAudioOut; i++)
            sb.Append($" -disposition:a:{i} 0");

        // Subtitles: SDH tracks get +default+hearing_impaired; non-SDH selected tracks get "0"
        // (no auto-default — the viewer opts in). All unselected tracks are explicitly cleared.
        if (chosenSubs.Count > 0)
        {
            if (selectedSub is not null)
            {
                int selectedOutIdx = chosenSubs.IndexOf(selectedSub);
                Console.WriteLine($"    → Default subtitle track: {selectedSub.DisplayName} ({selectedSub.Language})");

                string subDisposition = selectedSub.IsSDH
                    ? "+default+hearing_impaired"    // SDH: set default + hearing_impaired flag
                    : "0";                           // Non-SDH: no auto-default (opt-in behavior)

                sb.Append($" -disposition:s:{selectedOutIdx} {subDisposition}");
            }
            else
            {
                sb.Append(" -disposition:s:0 default");
            }

            // Clear dispositions for all non-selected subtitle tracks.
            // Use i (not i+1) — i is already the correct output-relative index since the
            // streams are mapped in chosenSubs order and the selected track is skipped via continue.
            for (int i = 0; i < chosenSubs.Count; i++)
            {
                if (selectedSub is not null && i == chosenSubs.IndexOf(selectedSub))
                    continue;   // already handled above
                sb.Append($" -disposition:s:{i} 0");
            }
        }

        // ── 8. Metadata ──────────────────────────────────────────────────────
        // Audio metadata — indices must match the map order established in step 6.
        Metadata(sb, "a", 0, mainAudio.DisplayName, mainAudio.Language);
        // For the surround track, we preserve the channel description from the source (e.g. "5.1") since
        // it's more informative than just "AAC Surround". The stereo track is labelled "AAC Stereo"
        // since the channel count is always 2, but we don't want to include the source channel
        // description here since it can be misleading (e.g. "5.1" source downmixed to stereo).
        if (needsSurround)
            Metadata(sb, "a", 1, $"AAC {mainAudio.ChannelDesc}", mainAudio.Language);
        if (!isStereoOnly)
            Metadata(sb, "a", stereoOutIdx, "AAC Stereo", mainAudio.Language);

        // Lossless tracks
        for (int i = 0; i < losslessFmts.Count; i++)
            Metadata(sb, "a", losslessStart + i,
                     LosslessArgBuilder.TrackTitle(losslessFmts[i], mainAudio),
                     mainAudio.Language);

        // Secondary audio
        for (int i = 0; i < secondary.Count; i++)
            Metadata(sb, "a", secondaryStart + i, secondary[i].DisplayName, secondary[i].Language);

        // Subtitle metadata — must be written in the same order as the subtitle maps (chosenSubs order).
        // No reordering is applied here; the disposition flag is what media players use to find the
        // preferred track, not stream position.
        for (int i = 0; i < chosenSubs.Count; i++)
            Metadata(sb, "s", i, chosenSubs[i].DisplayName, chosenSubs[i].Language);

        // Output file
        sb.Append($" \"{outputPath}\"");

        // Return the complete argument string.
        return sb.ToString();
    }

    /// <summary>
    /// Appends metadata arguments for a specific stream to the StringBuilder.
    /// This includes the title and language tags.
    /// </summary>
    /// <param name="sb">The StringBuilder to append the metadata arguments to.</param>
    /// <param name="type">The type of the stream (e.g., "a" for audio, "s" for subtitles).</param>
    /// <param name="idx">The index of the stream.</param>
    /// <param name="title">The title of the stream.</param>
    /// <param name="lang">The language of the stream.</param>
    private static void Metadata(StringBuilder sb, string type, int idx, string title, string lang)
    {
        title = title.Replace("\"", "\\\"");
        sb.Append($" -metadata:s:{type}:{idx} title=\"{title}\"");
        sb.Append($" -metadata:s:{type}:{idx} language={lang}");
    }

    /// <summary>
    /// Selects the preferred subtitle stream, favouring SDH tracks for the
    /// target language, then falling back gracefully.
    /// </summary>
    public static SubtitleStreamInfo? SelectSubtitleStream(
        IReadOnlyList<SubtitleStreamInfo> streams,
        /* string preferredLanguage = "eng",*/
        bool preferSdh = true)
    {
        // If no subtitle streams are available, return null.
        if (streams.Count == 0) return null;

        // First filter to preferred languages, then apply the selection logic
        var langMatches = streams
            .Where(s => CommandBuilder.PreferredLangs.Contains(s.Language))
            .ToList();

        // Prefer SDH tracks in the preferred language(s)
        if (preferSdh)
        {
            // 1. Preferred language + SDH
            var sdh = langMatches.FirstOrDefault(s => s.IsSDH);
            if (sdh is not null) return sdh;
        }

        // 2. Preferred language (any)
        var langAny = langMatches.FirstOrDefault();
        if (langAny is not null) return langAny;

        // If no preferred language tracks are found, fall back to any SDH track if preferSdh is true.
        if (preferSdh)
        {
            // 3. Any SDH track regardless of language
            var anySdh = streams.FirstOrDefault(s => s.IsSDH);
            if (anySdh is not null) return anySdh;
        }

        // 4. First available
        return streams.First();
    }
}
