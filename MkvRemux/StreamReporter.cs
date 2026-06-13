namespace MkvRemux;

/// <summary>
/// Prints a human-readable stream summary for --info mode.
/// No commands are built or executed — purely informational.
/// </summary>
static class StreamReporter
{
    /// <summary>
    /// Prints a human-readable summary of the file and its streams to the console.
    /// </summary>
    /// <param name="filePath">The path to the media file.</param>
    /// <param name="video">The video stream information.</param>
    /// <param name="audio">The list of audio streams.</param>
    /// <param name="subs">The list of subtitle streams.</param>
    public static void Print(
        string filePath,
        List<VideoStreamInfo> video,
        List<AudioStreamInfo> audio,
        List<SubtitleStreamInfo> subs)
    {
        // ── File Info ───────────────────────────────────────────────────────────
        var fi = new FileInfo(filePath);

        // Print file info
        WriteHeader("FILE");
        Console.WriteLine($"  Path : {filePath}");
        Console.WriteLine($"  Name : {fi.Name}");
        Console.WriteLine($"  Title: {MKVUtil.NormalizeTitle(fi.Name).Title}");
        Console.WriteLine($"  Size : {FormatSize(fi.Length)}");
        Console.WriteLine();

        // ── Video ────────────────────────────────────────────────────────────
        WriteHeader("VIDEO");
        if (video is null || video.Count > 0)
        {
            // No video stream found
            Console.WriteLine("  (no video streams)");
        }
        else
        {
            // Print basic video info
            foreach (var v in video)
            {
                Console.WriteLine($"  [{v.GlobalIndex}] {v.Codec.ToUpper()}  {v.Resolution}  {v.PixFmt}");


                // Print HDR info if applicable
                if (v.IsHdr)
                {
                    // Determine HDR type for display
                    string hdrType = v.IsDolbyVision ? "Dolby Vision"
                                   : v.IsHdr10 ? "HDR10"
                                   : v.IsHlg ? "HLG"
                                   : "HDR";

                    // Print detailed color and HDR info
                    Console.WriteLine($"      HDR        : {hdrType}");
                    Console.WriteLine($"      Transfer   : {v.ColorTransfer}");
                    Console.WriteLine($"      Primaries  : {v.ColorPrimaries}");
                    Console.WriteLine($"      ColorSpace : {v.ColorSpace}");

                    // Print mastering display and max CLL if available
                    if (v.MasteringDisplay is not null)
                        Console.WriteLine($"      MasterDisp : {v.MasteringDisplay.ToFfmpegString()}");
                    if (v.MaxCll is not null)
                        Console.WriteLine($"      MaxCLL     : {v.MaxCll.ToFfmpegString()}");
                    if (v.IsDolbyVision)
                        Console.WriteLine("      [NOTE] Dolby Vision metadata cannot survive re-encoding");
                }
                else
                {
                    // For SDR content, color info is often missing or unreliable, but we'll print it if available
                    Console.WriteLine($"      SDR  primaries={v.ColorPrimaries}  trc={v.ColorTransfer}");
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine();

        // ── Audio ─────────────────────────────────────────────────────────────
        WriteHeader("AUDIO");
        if (audio.Count == 0)
        {
            // No audio streams found
            Console.WriteLine("  (no audio streams)");
        }
        else
        {
            // Print each audio stream with preferred language highlighted
            foreach (var a in audio)
            {
                // Format bit depth and title for display
                string depth = a.BitsPerSample > 0 ? $"  {a.BitsPerSample}-bit" : "";
                string title = string.IsNullOrWhiteSpace(a.Title) ? "" : $"  \"{a.Title}\"";

                // Highlight preferred languages in green, others in dark gray
                Console.ForegroundColor = IsPreferredLang(a.Language)
                    ? ConsoleColor.Green
                    : ConsoleColor.DarkGray;

                Console.Write($"  [{a.GlobalIndex}] {a.DisplayName,-28}{depth,-8}  lang={a.Language}{title}");
                Console.ResetColor();

                // Flag the track that would be selected as main
                if (a == audio.Where(x => IsPreferredLang(x.Language))
                               .OrderBy(x => x.CodecPriority)
                               .FirstOrDefault())
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("  ← main");
                    Console.ResetColor();
                }
                Console.WriteLine();
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  Green");
            Console.ResetColor();
            Console.Write(" = ENG/UND preferred  ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("← main");
            Console.ResetColor();
            Console.WriteLine(" = would be selected as primary track");
        }
        Console.WriteLine();

        // ── Subtitles ────────────────────────────────────────────────────────
        WriteHeader("SUBTITLES");
        if (subs.Count == 0)
        {
            // No subtitle streams found
            Console.WriteLine("  (no subtitle streams)");
        }
        else
        {
            // Print each subtitle stream with preferred language highlighted
            foreach (var s in subs)
            {
                // Format title for display
                string title = string.IsNullOrWhiteSpace(s.Title) ? "" : $"  \"{s.Title}\"";

                // Highlight preferred languages in green, others in dark gray
                Console.ForegroundColor = IsPreferredLang(s.Language)
                    ? ConsoleColor.Green
                    : ConsoleColor.DarkGray;
                Console.WriteLine($"  [{s.GlobalIndex}] {s.Codec,-28}  lang={s.Language}{title}");
                Console.ResetColor();
            }
        }
        Console.WriteLine();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a section header to the console with a cyan color and a decorative line.
    /// </summary>
    /// <param name="label">The label for the section header.</param>
    private static void WriteHeader(string label)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"── {label} {'─',0}".PadRight(48, '─'));
        Console.ResetColor();
    }

    /// <summary>
    /// Determines if the given language code is a preferred language (English or undefined).
    /// </summary>
    /// <param name="lang">The language code to check.</param>
    /// <returns>True if the language is preferred, false otherwise.</returns>
    private static bool IsPreferredLang(string lang) =>
        CommandBuilder.PreferredLangs.Contains(lang);

    /// <summary>
    /// Formats a byte size into a human-readable string with appropriate units (KB, MB, GB).
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>A human-readable string representing the size.</returns>
    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}
