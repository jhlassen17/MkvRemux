using System.Diagnostics;
using MkvRemux;

namespace MkvRemux;

// ═══════════════════════════════════════════════════════════════════════════
//  MkvRemux v3.3
//
//  Usage:
//    MkvRemux <input.mkv|folder> [output.mkv] [options]
//
//  Options:
//    --encode-video              Re-encode video to HEVC Main10
//    --encoder <nvenc|qsv|sw>    Force encoder (sw = libx265 CPU fallback)
//    --allow-sw-fallback         Allow libx265 if no GPU encoder found
//    --cq <n>                    Quality: CQ (NVENC), GQ (QSV), CRF (x265). Default 19 (range 0-51, lower is better)
//    --nvenc-preset <p1-p7>      NVENC speed preset (default: p4)
//    --lossless <flac|alac|pcm>  Add lossless track(s); repeatable/comma-separated
//    --stereo-filter <mode>      Stereo downmix: default | dolby  (default: default)
//    --info                      Print stream info and exit (no processing)
//    --skip-existing             Skip if output file already exists
//    --recursive                 (Batch) scan subfolders too
//    --output-dir <dir>          (Batch) write all outputs to this directory
//    --dry-run                   Print ffmpeg command without running it
//    -h, --help                  Show this help
// ═══════════════════════════════════════════════════════════════════════════

public class Program
{
    /// <summary>
    /// Main entry point for MkvRemux. Parses command-line arguments, detects encoders, analyzes input 
    /// files, builds ffmpeg commands, and executes them to remux/encode MKV files according to the 
    /// specified options.
    /// </summary>
    /// <param name="args">Command-line arguments. Use --help to see available options.</param>
    /// <returns>Exit code.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static int Main(string[] args)
    {
        // Welcome the user
        PrintBanner();

        // If no arguments or help requested, show usage
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        // ── Parse CLI args ──────────────────────────────────────────────────────────

        // Options with defaults
        bool encodeVideo = false;
        bool dryRun = false;
        bool infoMode = false;
        bool skipExisting = false;
        bool allowSwFb = false;
        bool recursive = false;
        int cq = 19;
        string nvencPreset = "p4";
        HwEncoder? forcedEncoder = null;
        StereoDownmix.Mode stereoMode = StereoDownmix.Mode.Default;
        var losslessRaw = new List<string>();
        string? outputDir = null;
        var positional = new List<string>();

        // Simple manual parsing loop
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--encode-video": encodeVideo = true; break;
                case "--dry-run": dryRun = true; break;
                case "--info": infoMode = true; break;
                case "--skip-existing": skipExisting = true; break;
                case "--allow-sw-fallback": allowSwFb = true; break;
                case "--recursive": recursive = true; break;

                case "--cq":
                    if (++i < args.Length && int.TryParse(args[i], out int cqVal))
                        cq = Math.Clamp(cqVal, 0, 51);
                    else { Console.Error.WriteLine("[ERROR] --cq requires 0-51."); return 1; }
                    break;

                case "--nvenc-preset":
                    if (++i < args.Length) nvencPreset = args[i];
                    else { Console.Error.WriteLine("[ERROR] --nvenc-preset requires a value."); return 1; }
                    break;

                case "--encoder":
                    if (++i >= args.Length) { Console.Error.WriteLine("[ERROR] --encoder requires nvenc|qsv|sw."); return 1; }
                    forcedEncoder = args[i].ToLower() switch
                    {
                        "nvenc" => HwEncoder.NvencHevc,
                        "qsv" => HwEncoder.QsvHevc,
                        "sw" or "x265" => HwEncoder.SoftwareX265,
                        _ => throw new ArgumentException($"Unknown encoder '{args[i]}'. Use nvenc, qsv, or sw.")
                    };
                    break;

                case "--lossless":
                    if (++i < args.Length) losslessRaw.Add(args[i]);
                    else { Console.Error.WriteLine("[ERROR] --lossless requires flac|alac|pcm."); return 1; }
                    break;

                case "--stereo-filter":
                    if (++i < args.Length)
                    {
                        try { stereoMode = StereoDownmix.Parse(args[i]); }
                        catch (ArgumentException ex) { Console.Error.WriteLine($"[ERROR] {ex.Message}"); return 1; }
                    }
                    else { Console.Error.WriteLine("[ERROR] --stereo-filter requires default|dolby."); return 1; }
                    break;

                case "--output-dir":
                    if (++i < args.Length) outputDir = args[i];
                    else { Console.Error.WriteLine("[ERROR] --output-dir requires a path."); return 1; }
                    break;

                default:
                    if (!args[i].StartsWith("--")) positional.Add(args[i]);
                    else { Console.Error.WriteLine($"[ERROR] Unknown option: {args[i]}"); return 1; }
                    break;
            }
        }

        // Validate and parse lossless formats
        List<LosslessFormat> losslessFmts;
        try { losslessFmts = LosslessArgBuilder.Parse(losslessRaw); }
        catch (ArgumentException ex) { Console.Error.WriteLine($"[ERROR] {ex.Message}"); return 1; }

        // Validate positional args
        if (positional.Count < 1)
        {
            Console.Error.WriteLine("[ERROR] No input file or folder specified.");
            PrintHelp();
            return 1;
        }

        // The first positional argument is the input file or folder
        string inputArg = positional[0];

        // ── Resolve input files ─────────────────────────────────────────────────────

        List<string> inputFiles;
        bool isBatch;

        // If it's a directory, scan for video files. Otherwise, treat it as a single file.
        if (Directory.Exists(inputArg))
        {
            // Search for video files in the directory (and subdirectories if --recursive)
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { ".mkv", ".mp4", ".m4v", ".avi", ".mpeg", ".mpg" };

            // Exclude files that are likely outputs or deleted items, and only include supported video formats.
            inputFiles = Directory.GetFiles(inputArg, "*.*", searchOption)
                .Where(f => !f.Contains(".deletedByTMM", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains("-trailer.", StringComparison.OrdinalIgnoreCase))
                .Where(f => videoExtensions.Contains(Path.GetExtension(f)))
                .Where(f => outputDir is not null ||
                            !Path.GetFileName(f).EndsWith(".remux.mkv", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            // Set batch mode if we found any files.
            // If the directory is empty or has no eligible files, we'll report an error.
            isBatch = true;

            // If no files found, report an error and exit.
            if (inputFiles.Count == 0)
            {
                Console.Error.WriteLine($"[ERROR] No eligible .mkv files found in: {inputArg}");
                return 1;
            }

            // Report batch mode and file count
            Console.WriteLine($"Batch  : {inputFiles.Count} file(s) in {Path.GetFullPath(inputArg)}");
            if (recursive) Console.WriteLine("         (recursive)");
        }
        else if (File.Exists(inputArg))
        {
            // Single file mode
            inputFiles = [Path.GetFullPath(inputArg)];
            isBatch = false;
        }
        else
        {
            // Input path does not exist
            Console.Error.WriteLine($"[ERROR] Input not found: {inputArg}");
            return 1;
        }

        // If output directory specified, ensure it exists
        if (outputDir is not null)
        {
            outputDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(outputDir);
            Console.WriteLine($"Out dir: {outputDir}");
        }

        // If second positional argument is provided in single-file mode, treat it as explicit output path
        string? explicitOutput = (!isBatch && positional.Count > 1)
            ? Path.GetFullPath(positional[1])
            : null;

        // ── Encoder detection (once, before the loop) ───────────────────────────────

        HwEncoder? resolvedEncoder = null;

        // Only detect encoder if we need to encode video and we're not in info mode.
        // If --encoder is used, we'll skip detection and use the forced encoder.
        if (encodeVideo && !infoMode)
        {
            if (forcedEncoder is not null)
            {
                // If the user forced an encoder, use it without detection.
                resolvedEncoder = forcedEncoder.Value;
                Console.WriteLine($"Encoder: forced → {EncoderLabel(resolvedEncoder.Value)}");
            }
            else if (dryRun)
            {
                // In dry-run mode, we can't reliably detect encoders (since we won't run ffmpeg),
                // so we'll assume NVENC if encoding is requested
                resolvedEncoder = HwEncoder.NvencHevc;
                Console.WriteLine("Encoder: NVENC assumed for dry-run (use --encoder to override)");
            }
            else
            {
                // If encoding is requested and no encoder is forced, detect available encoders.
                Console.WriteLine();
                Console.WriteLine("==> Detecting encoders");
                resolvedEncoder = EncoderDetector.Detect(allowSwFb);

                // If no encoder found, report error and exit. The user can use --allow-sw-fallback
                // to permit libx265 as a fallback.
                if (resolvedEncoder == HwEncoder.None)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("[ERROR] No HEVC encoder found.");
                    Console.Error.WriteLine("        Add --allow-sw-fallback to permit libx265, or --encoder sw.");
                    return 1;
                }
            }
        }

        // Check stereo downmix mode and lossless formats, and report configuration summary
        if (stereoMode == StereoDownmix.Mode.Dolby)
            Console.WriteLine("Stereo : Dolby pan filter");
        if (losslessFmts.Count > 0)
            Console.WriteLine($"Lossless: {string.Join(", ", losslessFmts).ToUpper()}");
        if (skipExisting) Console.WriteLine("Mode   : --skip-existing enabled");
        if (dryRun) Console.WriteLine("Mode   : DRY RUN — ffmpeg will not execute");

        Console.WriteLine();

        // ── Process files ───────────────────────────────────────────────────────────

        int processed = 0, skipped = 0, failed = 0;

        // Loop through each input file, analyze streams, build ffmpeg command, and execute it.
        for (int fileIdx = 0; fileIdx < inputFiles.Count; fileIdx++)
        {
            // Resolve output path for this input file, based on batch mode and options.
            string inputPath = inputFiles[fileIdx];
            string outputPath = ResolveOutputPath(inputPath, outputDir, explicitOutput);

            // In batch mode, print a header for each file. In single-file mode, we'll just print details without a header.
            if (isBatch)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[{fileIdx + 1}/{inputFiles.Count}] {Path.GetFileName(inputPath)}");
                Console.ResetColor();
            }

            // ── --skip-existing ──────────────────────────────────────────────────────
            bool outputExists = MKVUtil.OutputExists(outputPath);
            if (skipExisting && outputExists)
            {
                if (MKVUtil.HasProcessedTag(outputPath))
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  SKIP — output exists: {MKVUtil.NormalizeTitle(outputPath)}");
                    Console.ResetColor();
                    Console.WriteLine();
                    skipped++;
                    continue;
                }
                else
                {
                    // Output file exists but doesn't have the processed tag, which means it may be a
                    // leftover from a failed run or an unprocessed file.
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  PROCESS — output exists but not processed: {MKVUtil.NormalizeTitle(outputPath)}");
                    Console.ResetColor();
                    Console.WriteLine();
                }
            }
            else if (outputExists)
            {
                // File exists but we're not skipping, so delete it to avoid ffmpeg errors.
                // This can happen in batch mode if a previous run failed after creating the output file.
                File.Delete(outputPath);
                outputExists= false;
            }

            // ── Probe ────────────────────────────────────────────────────────────────
            Console.WriteLine($"  Input  : {inputPath}");
            if (!infoMode) Console.WriteLine($"  Output : {outputPath}");

            VideoStreamInfo? videoInfo;
            List<AudioStreamInfo> audioStreams;
            List<SubtitleStreamInfo> subtitleStreams;

            try 
            {
                // Analyze the input file to get stream information.
                (videoInfo, audioStreams, subtitleStreams) = StreamAnalyzer.Analyze(inputPath); 
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [ERROR] {ex.Message}");
                failed++;
                Console.WriteLine();
                continue;
            }

            // ── --info mode ──────────────────────────────────────────────────────────
            if (infoMode)
            {
                // If we're in info mode, just print the stream information and skip processing.
                StreamReporter.Print(inputPath, videoInfo, audioStreams, subtitleStreams);
                processed++;
                continue;
            }

            // Make sure we have at least one audio stream, since remuxing without audio doesn't make sense in this context.
            if (audioStreams.Count == 0)
            {
                Console.Error.WriteLine("  [ERROR] No audio streams found.");
                failed++;
                Console.WriteLine();
                continue;
            }

            // ── Build video args ─────────────────────────────────────────────────────
            string? videoArgs = null;

            // If encoding is requested and we have a video stream, build the video encoding arguments based on
            // the selected encoder and quality settings.
            if (encodeVideo && resolvedEncoder is not null)
            {
                if (videoInfo is null)
                {
                    Console.Error.WriteLine("  [ERROR] No video stream — cannot encode.");
                    failed++;
                    Console.WriteLine();
                    continue;
                }

                // Print the selected encoder and quality settings for this file.
                Console.WriteLine();
                Console.WriteLine("  Encoding configuration:");

                // Print a summary of the video encoding configuration, including the selected encoder and quality settings.
                VideoArgBuilder.PrintSummary(videoInfo, resolvedEncoder.Value, cq, nvencPreset);
                // Build the ffmpeg arguments for video encoding based on the selected encoder and quality settings.
                videoArgs = VideoArgBuilder.Build(videoInfo, resolvedEncoder.Value, cq, nvencPreset);
            }

            // ── Build ffmpeg command ─────────────────────────────────────────────────
            string ffmpegArgs;
            try
            {
                ffmpegArgs = CommandBuilder.Build(
                    inputPath, outputPath,
                    videoInfo, audioStreams, subtitleStreams,
                    videoArgs, losslessFmts, stereoMode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [ERROR] {ex.Message}");
                failed++;
                Console.WriteLine();
                continue;
            }

            // Print the ffmpeg command that will be executed.
            Console.WriteLine();
            Console.WriteLine("  ─── ffmpeg command ───────────────────────────────────────────────────");
            Console.WriteLine($"  ffmpeg {ffmpegArgs}");
            Console.WriteLine("  ──────────────────────────────────────────────────────────────────────");

            // If we're in dry-run mode, skip executing ffmpeg and just print the command.
            if (dryRun)
            {
                Console.WriteLine("  [DRY RUN] Skipped.");
                Console.WriteLine();
                processed++;
                continue;
            }

            // ── Run ffmpeg ───────────────────────────────────────────────────────────
            Console.WriteLine();
            int exitCode = RunFfmpeg(ffmpegArgs);

            // Check the exit code from ffmpeg to determine if the process succeeded or failed, and report the result.
            if (exitCode == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Done → {outputPath}");
                Console.ResetColor();
                MKVUtil.SetMkvCopyrightTag(outputPath);     // Tag the output file with a copyright notice to identify it as processed by MkvRemux.
                processed++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  [FAILED] ffmpeg exit code {exitCode}");
                Console.ResetColor();
                failed++;
            }
            Console.WriteLine();
        }

        // ── Summary (batch only) ────────────────────────────────────────────────────

        if (isBatch)
        {
            Console.WriteLine("════════════════════════════════════════");
            Console.WriteLine($" {processed} processed  {skipped} skipped  {failed} failed");
            Console.WriteLine("════════════════════════════════════════");
        }

        // Return 0 if all files processed successfully (or skipped), or 1 if any failures occurred.
        return failed > 0 ? 1 : 0;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the output path for a given input file based on the specified options. 
    /// In single-file mode, if an explicit output path is provided, it will be used. 
    /// Otherwise, the output file will be named by appending ".remux.mkv" to the input filename 
    /// (without extension) and placed in the same directory as the input file. In batch mode, the 
    /// output file will be named similarly but placed in the specified output directory if provided, 
    /// or alongside the input file if no output directory is specified.
    /// </summary>
    /// <param name="inputPath">The path to the input file.</param>
    /// <param name="outputDir">The directory where the output file should be placed, if specified.</param>
    /// <param name="explicit_">An explicit output path, if provided.</param>
    /// <returns>The resolved output path.</returns>
    static string ResolveOutputPath(string inputPath, string? outputDir, string? explicit_)
    {
        // If an explicit output path is provided (single-file mode), use it.
        if (explicit_ is not null)
        {
            // If the explicit output path is in a different directory than the input file,
            // we need to ensure that the output directory exists.
            if (!Path.GetDirectoryName(inputPath)?.Equals(Path.GetDirectoryName(explicit_), 
                StringComparison.OrdinalIgnoreCase) ?? false)
            {
                // Create the output directory if it doesn't exist.
                // We create a subdirectory named after the input file to avoid cluttering the source directory with outputs.
                var tmpDir = Directory.CreateDirectory(Path.Combine(explicit_, Path.GetFileName(inputPath)));
                return Path.Combine(tmpDir.FullName, Path.GetFileName(inputPath));
            }
            else
            {
                // No sub-directory needed, just return the explicit path.
                return explicit_;
            }
        }

        // Otherwise, construct the output filename by appending ".remux.mkv" to the input filename (without extension).
        string fileName = Path.GetFileNameWithoutExtension(inputPath) + ".remux.mkv";
        string dir = outputDir ?? Path.GetDirectoryName(inputPath) ?? ".";
        return Path.Combine(dir, fileName);
    }

    /// <summary>
    /// Returns a user-friendly label for a given hardware encoder enum value, which can be used in 
    /// console output to indicate which encoder is being used. This helps make the output more 
    /// readable and informative for users who may not be familiar with the internal enum names.
    /// </summary>
    /// <param name="enc">The hardware encoder enum value.</param>
    /// <returns>A user-friendly label for the encoder.</returns>
    static string EncoderLabel(HwEncoder enc) => enc switch
    {
        HwEncoder.NvencHevc => "NVIDIA NVENC (hevc_nvenc)",
        HwEncoder.QsvHevc => "Intel QuickSync (hevc_qsv)",
        HwEncoder.SoftwareX265 => "libx265 (CPU)",
        _ => enc.ToString()
    };

    /// <summary>
    /// Runs ffmpeg with the given arguments, logging output in real time and returning the exit code.
    /// </summary>
    /// <param name="arguments">The arguments to pass to ffmpeg.</param>
    /// <returns>The exit code of the ffmpeg process.</returns>
    static int RunFfmpeg(string arguments)
    {
        var (output, exitCode) = MKVUtil.RunffMpeg(arguments); // Log ffmpeg output in real time, and capture exit code
        Debug.WriteLine(output); // Write ffmpeg output to debug log (can be viewed in Visual Studio's Output window)
        return exitCode;
    }

    /// <summary>
    /// Prints a banner with the application name and description to the console. This is called at the 
    /// start of the program to welcome the user and provide a brief introduction to the tool. The banner 
    /// is styled with ASCII art and colored text for visual appeal. It also sets the console output 
    /// encoding to UTF-8 to ensure proper display of special characters in the banner.
    /// </summary>
    static void PrintBanner()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║          MkvRemux  v3.3              ║");
        Console.WriteLine("║  Smart audio remux for MKV files     ║");
        Console.WriteLine("╚══════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints usage information and available command-line options to the console. 
    /// This is called when the user requests help (using --help or -h) or when no arguments are provided. 
    /// The help text includes
    /// </summary>
    static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  MkvRemux <input.mkv>  [output.mkv] [options]");
        Console.WriteLine("  MkvRemux <folder>               [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --encode-video              Re-encode video to HEVC Main10");
        Console.WriteLine("  --encoder <nvenc|qsv|sw>    Force encoder (sw = libx265 CPU fallback)");
        Console.WriteLine("  --allow-sw-fallback         Fall back to libx265 when no GPU encoder found");
        Console.WriteLine("  --cq <0-51>                 Quality: CQ (NVENC), GQ (QSV), CRF (x265). Default 20");
        Console.WriteLine("  --nvenc-preset <p1-p7>      NVENC preset p1=fastest…p7=best. Default p4");
        Console.WriteLine("  --lossless <flac|alac|pcm>  Add lossless track(s); repeatable or comma-separated");
        Console.WriteLine("  --stereo-filter <mode>      Stereo downmix: default | dolby. Default: default");
        Console.WriteLine("  --info                      Print stream info only, no processing");
        Console.WriteLine("  --skip-existing             Skip files whose output already exists");
        Console.WriteLine("  --recursive                 (Batch) scan subfolders");
        Console.WriteLine("  --output-dir <dir>          (Batch) write all outputs to this directory");
        Console.WriteLine("  --dry-run                   Print ffmpeg commands without running them");
        Console.WriteLine("  -h, --help                  Show this help");
        Console.WriteLine();
        Console.WriteLine("Audio priority: TrueHD Atmos > TrueHD > DTS-HD MA > DTS:X > DTS-HD HRA");
        Console.WriteLine("                > E-AC3 > DTS > AC3 > AAC > MP3");
        Console.WriteLine("Output tracks:  0=Original copy  1=AAC Surround  2=AAC Stereo");
        Console.WriteLine("                3+=Lossless  N+=Other ENG/UND audio");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  • Batch mode excludes *.remux.mkv from the scan (previous outputs) if skip-existing is enabled.");
        Console.WriteLine("  • Use --output-dir to keep outputs separate from sources.");
        Console.WriteLine("  • --stereo-filter dolby has no effect on mono or stereo sources.");
        Console.WriteLine("  • Dolby Vision metadata cannot survive re-encoding; use stream copy.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  MkvRemux movie.mkv --info");
        Console.WriteLine("  MkvRemux movie.mkv out.mkv --stereo-filter dolby --lossless flac");
        Console.WriteLine("  MkvRemux movie.mkv out.mkv --encode-video --allow-sw-fallback --cq 18");
        Console.WriteLine("  MkvRemux /movies/ --encode-video --skip-existing --output-dir /encoded/");
        Console.WriteLine("  MkvRemux /movies/ --recursive --info");
        Console.WriteLine("  MkvRemux /movies/ --lossless flac --skip-existing --dry-run");
    }
}
