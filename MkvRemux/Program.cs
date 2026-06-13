using System.Diagnostics;
using MkvRemux;

namespace MkvRemux;

/// <summary>
// ═══════════════════════════════════════════════════════════════════════════
//  MkvRemux v3.7
//
//  Usage:
//    MkvRemux <input.mkv|folder> [output.mkv] [options]
//
//  Options:
//    --encode-video                    Re-encode video to HEVC Main10
//    --encoder <nvenc|qsv|sw>          Force encoder (sw = libx265 CPU fallback)
//    --allow-sw-fallback               Allow libx265 if no GPU encoder found
//    --cq <n>                          Quality: CQ (NVENC), GQ (QSV), CRF (x265). Default 19 (range 0-51, lower is better)
//    --nvenc-preset <p1-p7>            NVENC speed preset (default: p4)
//    --lossless <flac|alac|pcm>        Add lossless track(s); repeatable/comma-separated
//    --stereo-filter <mode>            Stereo downmix: default | dolby  (default: default)
//    --info                            Print stream info and exit (no processing)
//    --skip-existing                   Skip if output file already exists
//    --recursive                       (Batch) scan subfolders too
//    --allow-sw-fallback               (Batch) allow software fallback if no GPU encoder found
//    --allow-dv                        (Batch) allow Dolby Vision if available
//    --yes-i-know-what-im-doing, -y    (Batch) allow potentially dangerous options without confirmation
//    --output-dir <dir>                (Batch) write all outputs to this directory
//    --dry-run                         Print ffmpeg command without running it
//    -h, --help                        Show this help
// ═══════════════════════════════════════════════════════════════════════════
/// </summary>
public class Program
{

    /// <summary>
    /// CancellationTokenSource used to signal cancellation to ffmpeg processes when the user presses Ctrl+C.
    /// </summary>
    protected static CancellationTokenSource cts = new CancellationTokenSource();

    /// <summary>
    /// Main entry point for MkvRemux. Parses command-line arguments, detects encoders, analyzes input 
    /// files, builds ffmpeg commands, and executes them to remux/encode MKV files according to the 
    /// specified options.
    /// </summary>
    /// <param name="args">Command-line arguments. Use --help to see available options.</param>
    /// <returns>Exit code.</returns>
    /// <exception cref="ArgumentException">Thrown when invalid arguments are provided.</exception>
    public static int Main(string[] args)
    {
        // Handle Ctrl+C to allow graceful cancellation of ffmpeg processes.
        // When the user presses Ctrl+C, we set the cancellation token, which ffmpeg will observe and stop processing.
        // This allows us to clean up any partial output files and exit gracefully instead of leaving orphaned processes or corrupted files.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;      // Prevent the OS from immediately killing our process
            Console.WriteLine("\n  [CANCELLED] Stopping running processes...");
            cts.Cancel();
        };

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
        bool allowDolbyVision = false;
        bool alwaysYes = false;

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
                case "--allow-dv": allowDolbyVision = true; break;

                case "--yes-i-know-what-im-doing":
                case "-y":
                    alwaysYes = true;
                    break;

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

        // Allow for preserving Dolby Vision metadata if the user explicitly enables it, but warn them
        if (allowDolbyVision && encodeVideo)
        {
            // Can't have both --allow-dv and --encode-video, because re-encoding will strip the Dolby Vision metadata. We can either disable encoding or exit with an error. 
            Console.Error.WriteLine("[ERROR] --allow-dv cannot be used with --encode-video, since Dolby Vision metadata cannot survive re-encoding. Use stream copy or remove --allow-dv.");
            
            // Check if the user has already acknowledged the risks with the alwaysYes flag. If not, prompt them to choose stream copy instead of encoding.
            if (!alwaysYes)
            {
                // Ask them
                Console.WriteLine("         If you want to preserve Dolby Vision metadata, do not use --encode-video, would you like to enable stream copy instead? (y/n)");
                string response = Console.ReadLine()?.Trim().ToLower() ?? "n";
                // Check response
                if (response == "y")
                {
                    encodeVideo = false;
                }
                else
                {
                    // Fail with an error, since the user doesn't want to enable stream copy and we can't allow them to use --allow-dv with encoding.
                    return 1;
                }
            }
            else
            {
                // Always yes is enabled, so we'll just disable encoding without asking.
                encodeVideo = false;
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
            

            // Exclude files that are likely outputs or deleted items, and only include supported video formats.
            inputFiles = Directory.GetFiles(inputArg, "*.*", searchOption)
                .Where(f => !f.Contains(".deletedByTMM", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains("-trailer.", StringComparison.OrdinalIgnoreCase))
                .Where(f => MKVUtil.VideoExtensions.Contains(Path.GetExtension(f)))
                // Only exclude .remux.mkv files if we're in batch mode and skip-existing is enabled.
                // If outputDir is specified, we won't find any .remux.mkv files in the input directory, so we don't need to exclude them.
                .Where(f => outputDir is not null ||
                            ( ! Path.GetFileName(f).EndsWith(".remux.mkv", StringComparison.OrdinalIgnoreCase)
                             || skipExisting))
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
            // Single file mode, check for valid video extension
            if (MKVUtil.VideoExtensions.Contains(Path.GetExtension(inputArg)))
            {
                // Valid input file
                inputFiles = [Path.GetFullPath(inputArg)];
                isBatch = false;
            }
            else
            {
                // Invalid file type
                Console.Error.WriteLine($"[ERROR] Unsupported file type: {inputArg}");
                return 1;
            }
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
            if (isBatch) ResolveOutputPath(inputArg, outputDir, outputDir, true); // Ensure we can resolve an output path (and create subdir if needed)
            Console.WriteLine($"Out dir: {outputDir}");
        }

        // If second positional argument is provided in single-file mode, treat it as explicit output path
        string? explicitOutput = (!isBatch && positional.Count > 1)
            ? Path.GetFullPath(positional[1])
            : outputDir;

        // I think this works without the previous check, because ResolveOutputPath will handle the case where explicit output
        // is provided in batch mode by creating subdirectories. So we can just set explicitOutput to outputDir if it's provided,
        // and let ResolveOutputPath figure it out.
        // string? explicitOutput = outputDir;

        // Encoder detection (once, before the loop)
        HwEncoder? resolvedEncoder = null;

        // Only detect encoder if we need to encode video and we're not in info mode.
        // If --encoder is used, we'll skip detection and use the forced encoder.
        if (encodeVideo && !infoMode)
        {
            // Check for forced encoder first. This allows the user to bypass detection if they know which encoder they want to use
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
                    // No encoder found, report error and exit.
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
        // Report skip existing flag and check dry run status
        if (skipExisting) Console.WriteLine("Mode   : --skip-existing enabled");
        if (dryRun) Console.WriteLine("Mode   : DRY RUN — ffmpeg will not execute");

        // Clean up
        Console.WriteLine();

        // ── Process files ───────────────────────────────────────────────────────────
        int processed = 0, skipped = 0, failed = 0;

        // Loop through each input file, analyze streams, build ffmpeg command, and execute it.
        for (int fileIdx = 0; fileIdx < inputFiles.Count; fileIdx++)
        {
            // Check for cancellation before processing each file. This allows the user to stop the batch process gracefully.
            if (cts.IsCancellationRequested) 
                return -1;

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

            // Check if output file already exists.
            bool outputExists = MKVUtil.OutputExists(outputPath);
            // If --skip - existing is enabled and the file exists
            if (skipExisting && outputExists)
            {
                // If the output file has the processed tag, we'll skip processing this file.
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
                    Console.WriteLine($"            Deleting existing file to re-process.");
                    Console.ResetColor();
                    Console.WriteLine();
                    // TODO: Maybe we shouldn't delete if we are skipping existing files?
                    File.Delete(outputPath);
                    outputExists = false;
                }
            }
            else if (outputExists)
            {
                // File exists but we're not skipping, so delete it to avoid ffmpeg errors.
                // This can happen in batch mode if a previous run failed after creating the output file.
                File.Delete(outputPath);
                outputExists = false;
            }

            // ── Probe ────────────────────────────────────────────────────────────────
            // Print the input file being processed, and the output path if we're not in info mode
            Console.WriteLine($"  Input  : {inputPath}");
            if (!infoMode) Console.WriteLine($"  Output : {outputPath}");

            // Initialize variables to hold stream information. We'll fill these by analyzing the input file with ffprobe.
            List<VideoStreamInfo> videoStreams;
            List<AudioStreamInfo> audioStreams;
            List<SubtitleStreamInfo> subtitleStreams;

            try
            {
                // Analyze the input file to get stream information.
                (videoStreams, audioStreams, subtitleStreams) = StreamAnalyzer.Analyze(inputPath);
            }
            catch (Exception ex)
            {
                // If ffprobe fails or the output is unexpected, we catch the exception and report an error for this file
                Console.Error.WriteLine($"  [ERROR] {ex.Message}");
                failed++;
                Console.WriteLine();
                continue;
            }

            // Check if we got valid stream information. If not, report an error and skip this file.
            if (infoMode)
            {
                // If we're in info mode, just print the stream information and skip processing.
                StreamReporter.Print(inputPath, videoStreams, audioStreams, subtitleStreams);
                processed++;
                continue;
            }

            // Make sure we have at least one audio stream, since remuxing without audio doesn't make sense in this context.
            // TODO: Does this still make sense if we're only encoding video? Maybe we should allow processing if encodeVideo is true, even if no audio streams are found?
            if (audioStreams.Count == 0)
            {
                Console.Error.WriteLine("  [ERROR] No audio streams found.");
                failed++;
                Console.WriteLine();
                continue;
            }

            // Build video args
            string? videoArgs = null;

            // If encoding is requested and we have a video stream, build the video encoding arguments based on
            // the selected encoder and quality settings.
            if (encodeVideo && resolvedEncoder is not null)
            {
                if (videoStreams.Count == 0)
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
                VideoArgBuilder.PrintSummary(videoStreams, resolvedEncoder.Value, cq, nvencPreset);
                // Build per-stream ffmpeg arguments for all video streams.
                videoArgs = VideoArgBuilder.Build(videoStreams, resolvedEncoder.Value, cq, nvencPreset);
            }

            // Build ffmpeg command
            string ffmpegArgs;
            try
            {
                // Use command builder to construct the full ffmpeg command for this file, based on the input/output paths,
                // stream information, video encoding args, lossless audio options, and stereo downmix mode.
                ffmpegArgs = CommandBuilder.Build(
                    inputPath, outputPath,
                    videoStreams, audioStreams, subtitleStreams,
                    videoArgs, losslessFmts, stereoMode);
            }
            catch (Exception ex)
            {
                // Command builder failed, which likely means there was an issue with the stream information or the
                // options provided. We catch the exception and report an error for this file.
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
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine(Environment.NewLine);

            // If we're in dry-run mode, skip executing ffmpeg and just print the command.
            if (dryRun)
            {
                Console.WriteLine("  [DRY RUN] Skipped.");
                Console.WriteLine();
                processed++;
                continue;
            }

            // Run ffmpeg 
            Console.WriteLine();
            Console.WriteLine();
            var (runOutput, exitCode) = MKVUtil.RunffMpeg(ffmpegArgs, TimeSpan.FromSeconds(videoStreams.Sum(a => a.StreamDuration.TotalSeconds)), cts.Token);
            Console.WriteLine();

            // Check the exit code from ffmpeg to determine if the process succeeded or failed, and report the result.
            if (exitCode == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Done → {outputPath}");
                Console.ResetColor();
                MKVUtil.SetMkvCopyrightTag(outputPath);     // Tag the output file with a copyright notice to identify it as processed by MkvRemux.
                MKVUtil.SetMkvAuthorTag(outputPath);        // Tag the output file with the author information.
                processed++;
            }
            else if (exitCode == -2)
            {
                // If exit code is -2, it means the process was cancelled (e.g. by Ctrl+C).
                // In this case, we should clean up any partial output file that may have been created before exiting.
                if (File.Exists(outputPath))
                {
                    try
                    {
                        // Attempt to delete the partial output file.
                        File.Delete(outputPath);
                        Console.WriteLine($"  [CLEANUP] Deleted partial file: {outputPath}");
                    }
                    catch
                    {
                        // If we fail to delete the partial file, we can log an error but there's not much else we can do at this
                        // point since we're exiting anyway.
                        Console.Error.WriteLine($"  [CLEANUP ERROR] Failed to delete partial file: {outputPath}");
                    }
                }
            }
            else
            {
                // Failure case: ffmpeg exited with a non-zero code, which indicates an error occurred during processing.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  [FAILED] ffmpeg exit code {exitCode}");
                if (!string.IsNullOrEmpty(runOutput))
                {
                    Console.Error.WriteLine($"  [OUTPUT] {runOutput}");
                }
                Console.ResetColor();
                failed++;
            }

            // Add a blank line after each file for readability.
            Console.WriteLine();
            Console.WriteLine();
        }

        // Summary (batch only)
        if (isBatch)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("════════════════════════════════════════════════════════");
            Console.WriteLine($" {processed} processed  {skipped} skipped  {failed} failed");
            Console.WriteLine("════════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine();
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
    static string ResolveOutputPath(string inputPath, string? outputDir, string? explicit_, bool firstRun = false)
    {
        // If an explicit output path is provided (single-file mode), use it.
        if (explicit_ is not null)
        {
            // If the explicit output path is in a different directory than the input file,
            // we need to ensure that the output directory exists.
            if (!(Path.GetDirectoryName(inputPath)?.Equals(Path.GetDirectoryName(explicit_),
                StringComparison.OrdinalIgnoreCase) ?? false) && !firstRun)
            {
                // Create the output directory if it doesn't exist.
                // We create a subdirectory named after the input file to avoid cluttering the source directory with outputs.
                var tmpDir = Directory.CreateDirectory(Path.Combine(explicit_, Path.GetFileNameWithoutExtension(inputPath)));
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
        Console.WriteLine("║          MkvRemux  v3.7              ║");
        Console.WriteLine("║     Smart remux for MKV files        ║");
        Console.WriteLine("║    Add AAC tracks automatically      ║");
        Console.WriteLine("║    Optional HEVC 10-bit encoding     ║");
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
        Console.WriteLine("  MkvRemux <folder>                  [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --encode-video                     Re-encode video to HEVC Main10");
        Console.WriteLine("  --encoder <nvenc|qsv|sw>           Force encoder (sw = libx265 CPU fallback)");
        Console.WriteLine("  --allow-sw-fallback                Fall back to libx265 when no GPU encoder found");
        Console.WriteLine("  --cq <0-51>                        Quality: CQ (NVENC), GQ (QSV), CRF (x265). Default 19");
        Console.WriteLine("  --nvenc-preset <p1-p7>             NVENC preset p1=fastest…p7=best. Default p4");
        Console.WriteLine("  --lossless <flac|alac|pcm>         Add lossless track(s); repeatable or comma-separated");
        Console.WriteLine("  --stereo-filter <mode>             Stereo downmix: default | dolby. Default: default");
        Console.WriteLine("  --info                             Print stream info only, no processing");
        Console.WriteLine("  --skip-existing                    Skip files whose output already exists");
        Console.WriteLine("  --recursive                        (Batch) scan subfolders");
        Console.WriteLine("  --allow-dv                         Allow Dolby Vision metadata (no video encoding)");
        Console.WriteLine("  --yes-i-know-what-im-doing, -y     Bypass warnings about incompatible options");
        Console.WriteLine("  --output-dir <dir>                 (Batch) write all outputs to this directory");
        Console.WriteLine("  --dry-run                          Print ffmpeg commands without running them");
        Console.WriteLine("  -h, --help                         Show this help");
        Console.WriteLine();
        Console.WriteLine("Audio priority: TrueHD Atmos > TrueHD > DTS-HD MA > DTS:X > DTS-HD HRA");
        Console.WriteLine("                > E-AC3 > DTS > AC3 > AAC > FLAC > ALAC > MP3 > PCM");
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
