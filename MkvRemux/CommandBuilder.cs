using System.Text;

namespace MkvRemux;

/// <summary>
/// Selects streams and assembles the complete ffmpeg argument string.
///
/// Output audio track layout:
///   [0]        Main audio          — bitstream copy
///   [1]        AAC Surround        — same channels as main
///   [2]        AAC Stereo          — downmix (default or Dolby pan filter)
///   [3..3+L-1] Lossless tracks     — FLAC / ALAC / PCM (optional)
///   [3+L..]    Secondary ENG/UND   — bitstream copy
///
/// Disposition flags (default track markers):
///   Video   0 → default, all others cleared
///   Audio   0 → default, all others cleared
///   Subtitle 0 → default, all others cleared   
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
            // Main audio is the top-priority ENG/UND track; the rest are secondary.
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

        // ── 3. Stereo filter ─────────────────────────────────────────────────
        string? panFilter = StereoDownmix.GetFilter(stereoMode, mainAudio);

        // ── 4. Log selections ────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  Stream selection:");

        // Video handling: if videoArgs is provided, we assume re-encoding; otherwise, we copy. Log accordingly.
        if (video is not null)
        {
            string videoMode = videoArgs is not null ? "encode → HEVC Main10" : "copy";
            Console.WriteLine($"    Video       [{video.GlobalIndex}]: {video.Codec.ToUpper()} {video.Resolution} — {videoMode}");
        }

        // Audio handling: main audio is always included as track 0 (copy), track 1 (AAC surround),
        // and track 2 (AAC stereo). Log the details of each track, including the downmix method for track 2.
        string stereoDesc = panFilter is not null ? $"AAC Stereo (Dolby pan)" : "AAC Stereo (default)";
        Console.WriteLine($"    Main audio  [{mainAudio.GlobalIndex}]: {mainAudio.DisplayName} ({mainAudio.Language})");
        Console.WriteLine($"      → Track 0: copy  [DEFAULT]");
        Console.WriteLine($"      → Track 1: AAC {mainAudio.ChannelDesc} @ {mainAudio.AacSurroundBitrate}k");
        Console.WriteLine($"      → Track 2: {stereoDesc} @ 256k");

        // Lossless tracks: if any are selected, they are added after the main audio tracks.
        // Log each one with its format and source details.
        int losslessStart = 3;
        for (int i = 0; i < losslessFmts.Count; i++)
            Console.WriteLine($"      → Track {losslessStart + i}: {LosslessArgBuilder.TrackTitle(losslessFmts[i], mainAudio)}");

        // Secondary audio: all selected secondary tracks are copied. Log each one.
        foreach (var a in secondary)
            Console.WriteLine($"    Extra audio [{a.GlobalIndex}]: {a.DisplayName} ({a.Language})");

        // Subtitles: if no ENG/UND subs are found, all subs are included. Log the chosen subs or indicate if none are found.
        if (chosenSubs.Count == 0)
            Console.WriteLine("    Subtitles  : none found");
        else
        {
            bool isHandled = false;
            for (int i = 0; i < chosenSubs.Count; i++)
            {
                if (chosenSubs[i].IsSDH && !isHandled) isHandled = true;
                if (!isHandled && i == chosenSubs.Count - 1) isHandled = true;
                string defLabel = isHandled ? "  [DEFAULT]" : "";
                Console.WriteLine($"    Subtitle    [{chosenSubs[i].GlobalIndex}]: {chosenSubs[i].DisplayName}{defLabel}");
            }
        }

        // ── 5. Assemble arguments ────────────────────────────────────────────
        var sb = new StringBuilder();

        // Input file
        sb.Append($"-i \"{inputPath}\"");

        // Maps video
        sb.Append(" -map 0:v");
        // Maps audio: main audio is mapped to three tracks (copy, AAC surround, AAC stereo),
        // followed by any lossless formats and secondary audio.
        sb.Append($" -map 0:{mainAudio.GlobalIndex}");   // track 0: copy
        sb.Append($" -map 0:{mainAudio.GlobalIndex}");   // track 1: AAC surround
        sb.Append($" -map 0:{mainAudio.GlobalIndex}");   // track 2: AAC stereo

        // Lossless tracks
        foreach (var _ in losslessFmts)
            sb.Append($" -map 0:{mainAudio.GlobalIndex}");

        // Secondary audio
        foreach (var a in secondary)
            sb.Append($" -map 0:{a.GlobalIndex}");

        // Subtitles
        foreach (var sub in chosenSubs)
            sb.Append($" -map 0:{sub.GlobalIndex}");

        // Chapters
        sb.Append(" -map_chapters 0");

        // Video codec
        sb.Append(videoArgs is not null ? $" {videoArgs}" : " -c:v copy");

        // Audio codecs
        sb.Append(" -c:a:0 copy");
        sb.Append($" -c:a:1 aac -b:a:1 {mainAudio.AacSurroundBitrate}k");

        // Track 2: stereo — apply Dolby pan filter or default downmix
        if (panFilter is not null)
            sb.Append($" -c:a:2 aac -b:a:2 256k -filter:a:2 \"{panFilter}\"");
        else
            sb.Append(" -c:a:2 aac -ac:a:2 2 -b:a:2 256k");

        // Lossless tracks
        for (int i = 0; i < losslessFmts.Count; i++)
            sb.Append($" -c:a:{losslessStart + i} {LosslessArgBuilder.CodecArgs(losslessFmts[i], mainAudio)}");

        // Secondary audio
        int secondaryStart = losslessStart + losslessFmts.Count;
        for (int i = 0; i < secondary.Count; i++)
            sb.Append($" -c:a:{secondaryStart + i} copy");

        // Subtitles
        sb.Append(" -c:s copy");

        // ── 6. Disposition flags ─────────────────────────────────────────────
        // Video: track 0 default, clear any others ffmpeg may have inherited
        sb.Append(" -disposition:v:0 default");

        // Audio: track 0 (bitstream copy) default, all others explicitly cleared
        int totalAudioOut = 3 + losslessFmts.Count + secondary.Count;
        sb.Append(" -disposition:a:0 default");
        for (int i = 1; i < totalAudioOut; i++)
            sb.Append($" -disposition:a:{i} 0");

        // Subtitles: prefer SDH track as default if available; otherwise, the first subtitle track. All others are explicitly cleared.
        if (chosenSubs.Count > 0)
        {
            // Determine which subtitle track to mark as default based on the selection logic.
            if (selectedSub is not null)
            {
                // Use the output-relative index (position in chosenSubs), not the source GlobalIndex.
                int selectedOutIdx = chosenSubs.IndexOf(selectedSub);
                Console.WriteLine($"    → Default subtitle track: {selectedSub.DisplayName} ({selectedSub.Language})");
                string subDisposition = selectedSub.IsSDH
                                        ? "+default+hearing_impaired"   // sets hearing_impaired, plus default
                                        : "0";                 // clear all dispositions (non-SDH, non-default)

                sb.Append($" -disposition:s:{selectedOutIdx} {subDisposition}");
            }
            else
            {
                sb.Append(" -disposition:s:0 default");
            }

            // At this point we have set 1 subtitle track as default (either the selected SDH or the first one).

            // Clear disposition for all other subtitle tracks to ensure only the selected one is marked as default.
            if (chosenSubs.Count > 1)
            {
                // Clear disposition for all other subtitle tracks to ensure only the selected one is marked as default.
                for (int i = 0; i < chosenSubs.Count; i++)
                {
                    // Skip the selected subtitle since we've already set its disposition above.
                    if (selectedSub is not null && i == chosenSubs.IndexOf(selectedSub))
                        continue; // already handled the selected subtitle above

                    // Clear disposition for all non-selected subtitle tracks. (i + 1) because subtitle tracks start
                    // at index 0 in the output, but we want to skip the first one if it's selected.
                    sb.Append($" -disposition:s:{i + 1} 0");
                }
            }

            if(selectedSub is not null)
            {
                Metadata(sb, "s", 0, selectedSub?.DisplayName ?? string.Empty, selectedSub?.Language ?? string.Empty);
            }

            var remainingSubs = chosenSubs.Except([selectedSub]).ToList();

            // Metadata for subtitles: set title and language tags for all included subtitle tracks.
            // The selected subtitle gets the same title as the source; others use their own titles.
            int tmpOffset = 0;
            for (int i = 0; i < remainingSubs.Count; i++)
            {
                // Make sure that we have a preferred subtitle selected, and if so, assign the title and language based on whether this is the selected one or not.
                if (selectedSub is not null)
                {
                    // Check the first run
                    if (i == 0)
                    {
                        // Apply preferred subtitle's metadata to the first subtitle track in the output,
                        // regardless of its position in the chosenSubs list. This ensures the default track has the correct metadata.
                        Metadata(sb, "s", 0, selectedSub?.DisplayName ?? chosenSubs[0].DisplayName, selectedSub?.Language 
                            ?? chosenSubs[0].Language);
                        continue;
                    }
                    else if (i == chosenSubs.IndexOf(selectedSub))
                    {
                        // We already set the metadata for the selected subtitle in the first track, so we can skip it here to avoid duplication.
                        tmpOffset--; 
                        continue;
                    }
                    else
                    {
                        Metadata(sb, "s", i + tmpOffset, chosenSubs[i].DisplayName, chosenSubs[i].Language);
                    }
                }
                else
                {
                    Metadata(sb, "s", i, chosenSubs[i].DisplayName, chosenSubs[i].Language);
                }
            }
        }


        // Metadata
        Metadata(sb, "a", 0, mainAudio.DisplayName, mainAudio.Language);
        Metadata(sb, "a", 1, $"AAC {mainAudio.ChannelDesc}", mainAudio.Language);
        Metadata(sb, "a", 2, "AAC Stereo", mainAudio.Language);

        // Lossless tracks
        for (int i = 0; i < losslessFmts.Count; i++)
            Metadata(sb, "a", losslessStart + i,
                     LosslessArgBuilder.TrackTitle(losslessFmts[i], mainAudio),
                     mainAudio.Language);

        // Secondary audio
        for (int i = 0; i < secondary.Count; i++)
            Metadata(sb, "a", secondaryStart + i, secondary[i].DisplayName, secondary[i].Language);

        //  Output file
        sb.Append($" \"{outputPath}\"");

        // Final assembled command string
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
        string preferredLanguage = "eng",
        bool preferSdh = true)
    {
        if (streams.Count == 0) return null;

        var langMatches = streams
            .Where(s => s.Language.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (preferSdh)
        {
            // 1. Preferred language + SDH
            var sdh = langMatches.FirstOrDefault(s => s.IsSDH);
            if (sdh is not null) return sdh;
        }

        // 2. Preferred language (any)
        var langAny = langMatches.FirstOrDefault();
        if (langAny is not null) return langAny;

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
