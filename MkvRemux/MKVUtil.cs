using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MkvRemux;

/// <summary>
/// Utility class for running external processes (like ffprobe) and capturing their output. 
/// This is used to run ffprobe to get stream information from the input MKV file, and to run 
/// ffmpeg for the actual remuxing process. The RunProcess method handles starting the process, 
/// capturing its standard output and error streams, and returning the results in a structured way. 
/// It also includes error handling to log any issues encountered while trying to run the process or 
/// if the process itself reports errors.
/// </summary>
public partial class MKVUtil
{
    // Path to mkvpropedit for setting MKV tags (used for marking processed files to avoid re-encoding)
    // TODO: Search path or allow user configuration instead of hardcoding
    protected const string mkvpropeditPath = @"C:\Program Files\MKVToolNix\mkvpropedit.exe";

    /// <summary>
    /// Gets a set of video file extensions that are commonly used for MKV files. This set is used to identify
    /// supported video files when scanning directories or processing individual files.
    /// </summary>
    public static HashSet<string> VideoExtensions => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { ".mkv", ".mp4", ".m4v", ".avi", ".mpeg", ".mpg" };

    /// <summary>
    /// Gets the processed tag name associated with this instance.
    /// </summary>
    public static string ProcessedTagName => "COPYRIGHT";

    /// <summary>
    /// Gets the processed tag value.
    /// </summary>
    public static string ProcessedTagValue => "processed";

    /// <summary>
    /// Gets the AUTHOR tag name used to mark files with the author information.
    /// </summary>
    public static string ProcessedAuthorTagName => "AUTHOR";

    /// <summary>
    /// Gets the AUTHOR tag value used to mark files with the author information.
    /// </summary>
    public static string ProcessedAuthorTagValue => "HANF";

    /// <summary>
    /// Runs a process with the specified executable and arguments, and captures its standard output and 
    /// error streams. The method returns a tuple containing the captured output (or null if the process failed) 
    /// and the exit code of the process. It also logs any errors encountered 
    /// while trying to run the process, as well as any error output produced by the process itself.
    /// </summary>
    /// <param name="exe">The path to the executable to run.</param>
    /// <param name="arguments">The arguments to pass to the executable.</param>
    /// <param name="isffMpeg">Indicates whether the process being run is ffmpeg, which affects how output is handled.</param>
    /// <param name="totalDuration">The total duration of the media being processed, used for progress reporting.</param>
    /// <param name="ct">A cancellation token to cancel the process.</param>
    /// <returns>A tuple containing the captured output (or null if the process failed) and the exit code of the process.</returns>
    public static (string?, int) RunProcess(
        string exe,
        string arguments,
        bool isffMpeg = false,
        TimeSpan totalDuration = default,
        CancellationToken ct = default)
    {
        try
        {
            // Set up the process start info with the specified executable and arguments, and configure it to redirect standard output and error
            var psi = new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Use StringBuilder to capture the output and error streams asynchronously
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            // Start the process and begin reading the output and error streams
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            const int winWidth = 100;

            // Set up event handlers to capture the output
            proc.OutputDataReceived += (_, e) =>
            {
                // Make sure that we got something
                if (e.Data is not null)
                {
                    outputBuilder.AppendLine(e.Data);
                    Console.WriteLine($"\r {e.Data/*,winWidth*/}");
                }
            };

            // Capture standard error output in case ffprobe writes warnings or errors there.
            // We will check the exit code later to determine if it was successful.
            proc.ErrorDataReceived += (_, e) =>
            {
                // Make sure that we got something
                if (e.Data is not null)
                {
                    // If this is ffmpeg output, it may contain progress information that we can parse and display in a
                    // user-friendly way. If it's not ffmpeg, we'll just print the error output as-is.
                    if (isffMpeg)
                    {
                        var match = FfmpegProgressRegex().Match(e.Data);
                        if (match.Success)
                        {
                            // Parse ffmpeg progress information from the error output.
                            int frame = int.Parse(match.Groups["frame"].Value);
                            double fps = double.Parse(match.Groups["fps"].Value);
                            double q = double.Parse(match.Groups["q"].Value);
                            string size = match.Groups["size"].Value;            // e.g. "1234KiB"
                            string tmpStr = match.Groups["time"].Value;
                            TimeSpan time = TimeSpan.TryParse(tmpStr, out var tmpTime) ? tmpTime : TimeSpan.Zero;
                            string bitrate = match.Groups["bitrate"].Value;         // e.g. "4567.8kbits/s"
                            tmpStr = match.Groups["speed"].Value;
                            double speed = double.TryParse(tmpStr, out var tmpSpeed) ? tmpSpeed : 0;

                            // Build output string with the parsed progress information.
                            Console.Write($"\r {BuildFfmpegProgressLine(frame, fps, q, size, time, bitrate, speed, totalDuration),winWidth}");
                        }
                        else
                        {
                            // Not formatted, suppress?
                            Debug.WriteLine($"{e.Data}");
                            errorBuilder.AppendLine(e.Data);
                        }
                    }
                    else
                    {
                        // Not formatted
                        Console.WriteLine($"\r {e.Data,winWidth}");
                        errorBuilder.AppendLine(e.Data);
                    }
                }
            };

            // Register kill on cancellation — fires when Ctrl+C is pressed
            using var ctReg = ct.Register(() =>
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true); // .NET 5+ — kills any child processes too
                    return;
                }
                catch { /* process may have already exited */ }
            });

            // Start the process and check if it started successfully. If not, return null to indicate failure.
            if (!proc.Start()) return (null, -1);

            // Begin asynchronous reading of the output and error streams
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            //  Wait for the process to exit and then check the exit code. If it is 0, return the captured output; otherwise, return null to indicate failure.
            proc.WaitForExit();

            // Print a final newline so the next console output starts on a clean line
            if (isffMpeg) Console.WriteLine(Environment.NewLine);

            // If there was any error output, we can log it for debugging purposes, even if the process exited with code 0 (ffprobe may write warnings to stderr)
            if (errorBuilder.Length > 0)
            {
                Console.Error.WriteLine($"  [WARN] {exe} reported errors: {errorBuilder}");
            }

            // Distinguish a clean exit from a cancellation-kill
            if (ct.IsCancellationRequested)
                return (null, -2);   // Our own sentinel for "cancelled"

            // Return the captured output if the process exited successfully, or null if it failed
            return proc.ExitCode == 0 ? (outputBuilder.ToString(), proc.ExitCode) : (null, proc.ExitCode);
        }
        catch (Exception ex)
        {
            // If there was an exception (e.g., ffprobe not found), log the error and return null to indicate failure
            Console.Error.WriteLine($"  [ERROR] Could not run {exe}: {ex.Message}");
            return (null, -1);
        }
    }

    /// <summary>
    /// Runs ffprobe with the specified arguments and captures its output. This is a convenience 
    /// method that calls RunProcess with "ffprobe" as the executable. It returns a tuple 
    /// containing the captured output (or null if ffprobe failed) and the exit code of the ffprobe 
    /// process.
    /// </summary>
    /// <param name="arguments">The arguments to pass to ffprobe.</param>
    /// <param name="ct">A cancellation token to cancel the process.</param>
    /// <returns>A tuple containing the captured output (or null if ffprobe failed) and the exit 
    /// code of the ffprobe process.</returns>
    public static (string?, int) RunffProbe(string arguments, CancellationToken ct = default)
        => RunProcess("ffprobe", arguments, ct: ct);

    /// <summary>
    /// Runs ffmpeg with the specified arguments and captures its output. This is a convenience
    /// method that calls RunProcess with "ffmpeg" as the executable. It returns a tuple 
    /// containing the captured output (or null if ffmpeg failed) and the exit code of the ffmpeg 
    /// process.
    /// </summary>
    /// <param name="arguments">The arguments to pass to ffmpeg.</param>
    /// <param name="totalDuration">The total duration of the media being processed, used for progress reporting.</param>
    /// <param name="ct">A cancellation token to cancel the process.</param>
    /// <returns>A tuple containing the captured output (or null if ffmpeg failed) and the exit 
    /// code of the ffmpeg process.</returns>
    public static (string?, int) RunffMpeg(string arguments, TimeSpan totalDuration = default, CancellationToken ct = default)
        => RunProcess("ffmpeg", arguments, true, totalDuration, ct);

    /// <summary>
    /// Checks if an output file with the same title and episode already exists in the output directory. 
    /// This is done by normalizing the title and episode information from the provided filename and 
    /// comparing it against existing MKV files in the same directory. If a matching title and episode 
    /// are found, it returns true to indicate that an output file already exists, which can be used to 
    /// skip remuxing if desired.
    /// </summary>
    /// <param name="filename">The filename to check for existence.</param>
    /// <returns>True if an output file with the same title and episode exists; otherwise, false.</returns>
    public static bool OutputExists(string? filename, out string? matchedPath)
    {
        matchedPath = null;

        // If we don't have an output path, we can't check for existence
        if (string.IsNullOrEmpty(filename))
            return false;

        // Initialize existence flag
        bool alreadyExists = false;

        // Normalize the title and episode information from the provided filename
        var fTitle = MKVUtil.NormalizeTitle(filename);

        // Check if any existing MKV in the output directory matches our title and episode.
        // Titles must match (case-insensitive). If both descriptors have an episode tag, those
        // must also match — a missing episode tag on either side is treated as a wildcard so
        // that a bare title-only query still hits a titled episode file and vice-versa.
        string outputDir = Path.GetDirectoryName(filename) ?? string.Empty;
        matchedPath = Directory
                .EnumerateFiles(outputDir, "*.mkv")
                // Normalize existing files in the output directory and compare against our target title/episode
                .Select(f => (Path: f, Norm: MKVUtil.NormalizeTitle(f)))
                .FirstOrDefault(t =>
                {
                    if (!string.Equals(t.Norm.Title, fTitle.Title, StringComparison.OrdinalIgnoreCase))
                        return false;

                    // Only compare episodes when both sides have one — avoids false negatives
                    // when the output filename has no episode tag yet.
                    if (!string.IsNullOrEmpty(t.Norm.Episode) && !string.IsNullOrEmpty(fTitle.Episode))
                        return string.Equals(t.Norm.Episode, fTitle.Episode, StringComparison.OrdinalIgnoreCase);

                    return true;
                }).Path;
        alreadyExists = matchedPath is not null;

        // Return the result of the existence check
        return alreadyExists;
    }

    /// <summary>
    /// Builds a formatted progress line for ffmpeg output based on the provided parameters. This method takes
    /// various metrics from ffmpeg's progress output and formats them into a human-readable string, including
    /// a progress bar, elapsed time, total duration, frame count, FPS, quality, size, bitrate, and speed.
    /// </summary>
    /// <param name="frame">The current frame number.</param>
    /// <param name="fps">The current frames per second.</param>
    /// <param name="q">The current quality metric.</param>
    /// <param name="size">The current size of the output file.</param>
    /// <param name="time">The elapsed time.</param>
    /// <param name="bitrate">The current bitrate.</param>
    /// <param name="speed">The current processing speed.</param>
    /// <param name="totalDuration">The total duration of the video.</param>
    /// <returns>A formatted progress line for ffmpeg output.</returns>
    protected static string BuildFfmpegProgressLine(
            int frame, double fps, double q, string size,
            TimeSpan time, string bitrate, double speed,
            TimeSpan totalDuration)
    {
        // Define the width of the progress bar
        const int barWidth = 20;

        // ── Progress bar / percentage (only when duration is known) ──────
        string progressSection;

        // Make sure we have a duration
        if (totalDuration > TimeSpan.Zero)
        {
            // Calculate the percentage of completion based on elapsed time and total duration, and clamp it between 0 and 1 to avoid overflow.
            // Then, determine how many characters in the progress bar should be filled based on this percentage.
            // Finally, format the progress bar using filled and unfilled characters, and include the elapsed time and total duration in a human-readable format.
            double pct = Math.Clamp(time.TotalSeconds / totalDuration.TotalSeconds, 0, 1);
            int filled = (int)Math.Round(pct * barWidth);
            string bar = new string('█', filled) + new string('░', barWidth - filled);
            string elapsed = time.ToString(@"hh\:mm\:ss");
            string total = totalDuration.ToString(@"hh\:mm\:ss");

            // Build the progress section of the output line, which includes the progress bar, percentage, elapsed time, and total duration.
            progressSection = $"[{bar}] {pct:P1} | {elapsed} / {total}";
        }
        else
        {
            // No duration available — fall back to raw elapsed time
            progressSection = $"time={time:hh\\:mm\\:ss\\.ff}";
        }

        // Build the final output line by combining the progress section with other ffmpeg metrics such as frame count, FPS, quality, size, bitrate, and speed.
        return $"{progressSection} | frame={frame} fps={fps:F1} size={size} speed={speed:F2}x";
        //return $"{progressSection} | frame={frame} fps={fps:F1} q={q:F2} size={size} bitrate={bitrate} speed={speed:F2}x";
    }

    /// <summary>
    /// Parses and normalizes the file path to extract title and episode information, removing metadata markers and
    /// standardizing the format.
    /// </summary>
    /// <remarks>Recognizes episode patterns (S01E01, S01E01E02) and title-year formats. Removes
    /// content within curly braces and square brackets before processing.</remarks>
    public static TitleDescriptor NormalizeTitle(string videoFile)
    {
        // Initialize
        string name = videoFile;

        // Remove extension
        name = Path.GetFileNameWithoutExtension(name);

        // Remove {metadata} and [tags]
        name = MetadataRegex().Replace(name, "");
        name = SquareBracketRegex().Replace(name, "");

        // Normalize whitespace early
        name = WhitespaceRegex().Replace(name, " ").Trim();

        // Extract episode pattern (S01E01, S01E01E02, etc.)
        var epMatch = SeasonEpisodeRegex().Match(name);
        string episode = epMatch?.Success == true ? epMatch.Value.ToUpper() : null ?? string.Empty;

        // Extract "Title (Year)"
        var titleMatch = TitleYearRegex().Match(name);
        string title;

        // If we found a title in the "Title (Year)" format, use it. Otherwise, fallback to a
        // more aggressive cleanup that removes episode patterns and trailing metadata.
        if (titleMatch.Success)
        {
            title = titleMatch.Groups[1].Value.Trim();
        }
        else
        {
            // Fallback cleanup
            title = TrailingEpisodeRegex().Replace(name, "");
            title = TrailingMetadataRegex().Replace(title, "").Trim();
        }


        // Save normalized values
        TitleDescriptor tmpDesc = new(title, episode);
        return tmpDesc;
    }



    /// <summary>
    /// Utility method that creates the "processed" tag flag in the output 
    /// MKV file so that us and other apps know that the video has been encoded
    /// </summary>
    /// <remarks>
    /// Tag name = "COPYRIGHT", value = "processed" means that the file 
    /// has already been handled
    /// </remarks>
    /// <returns>Path to the XML for the tag</returns>
    protected static string CreateTagXml(string? tagName = null, string? tagValue = null)
    {
        // File stuff
        string tempName = Path.GetRandomFileName();
        string path = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(tempName, "xml"));
        // Tag XML
        string xml = @"<?xml version=""1.0""?>
                        <Tags>
                          <Tag>
                            <Targets />
                            <Simple>
                              <Name>%ProcessedTagName%</Name>
                              <String>%ProcessedTagValue%</String>
                            </Simple>
                          </Tag>
                        </Tags>";

        // Replace placeholders with actual tag name and value
        xml = xml.Replace("%ProcessedTagName%", tagName ?? MKVUtil.ProcessedTagName);
        xml = xml.Replace("%ProcessedTagValue%", tagValue ?? MKVUtil.ProcessedTagValue);

        // Save
        File.WriteAllText(path, xml);
        return path;
    }

    /// <summary>
    /// Sets the copyright tag in the specified MKV video file using an external tool.
    /// </summary>
    /// <remarks>This method uses the MKVPropEdit tool to apply copyright metadata to the MKV file.
    /// The method creates a temporary tag XML file, applies it to the video, and then deletes the temporary file.
    /// If a debugger is attached, process output and errors are written to the debug output window.</remarks>
    /// <param name="outputFilePath">The video file for which to set the copyright tag. Must not be null and should have a valid output file
    /// path.</param>
    //public static void SetMkvCopyrightTag(string? outputFilePath)
    //{
    //    SetMkvTag(outputFilePath);
    //}

    /// <summary>
    /// Sets the processing tags in the specified MKV video file using an external tool.
    /// </summary>
    /// <remarks>This method uses the MKVPropEdit tool to apply processing metadata to the MKV file.
    /// The method creates a temporary tag XML file, applies it to the video, and then deletes the temporary file.
    /// If a debugger is attached, process output and errors are written to the debug output window.</remarks>
    /// <param name="outputFilePath">The video file for which to set the processing tags. Must not be null and should have a valid output file
    /// path.</param>
    public static void SetMkvProcessingTags(string? outputFilePath)
    {
        // Set up a temp XML file for the tags — mkvpropedit requires an XML file for batch
        string tempName = Path.GetRandomFileName();
        string path = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(tempName, "xml"));

        // Create the tag XML with both the processed tag and the author tag
        string xml = $@"<?xml version=""1.0""?>
<Tags>
  <Tag>
    <Targets />
    <Simple>
      <Name>{ProcessedTagName}</Name>
      <String>{ProcessedTagValue}</String>
    </Simple>
    <Simple>
      <Name>{ProcessedAuthorTagName}</Name>
      <String>{ProcessedAuthorTagValue}</String>
    </Simple>
  </Tag>
</Tags>";

        // Save the XML to the temp file
        File.WriteAllText(path, xml);

        // Set up args to apply the tags to the MKV file using mkvpropedit
        string args = $"\"{outputFilePath}\" --tags global:\"{path}\"";
        var result = RunProcess(mkvpropeditPath, args);
        // Debug output for the process result
        if (Debugger.IsAttached) Debug.WriteLine(result.Item1);
        // Delete the temporary tag XML file after applying the tags
        File.Delete(path);
    }

    //public static void SetMkvTag(string? outputFilePath, string? tagName = null, string? tagValue = null)
    //{
    //    string tagFile = CreateTagXml(tagName, tagValue);
    //    // Set up args
    //    string args = $"\"{outputFilePath}\" --tags global:\"{tagFile}\"";
    //    // Run the tool
    //    var result = MKVUtil.RunProcess(mkvpropeditPath, args);
    //    // Debug
    //    if (Debugger.IsAttached) Debug.WriteLine(result.Item1);
    //    // Delete the old tag file
    //    File.Delete(tagFile);
    //}
    /// <summary>
    /// Sets the AUTHOR tag in the specified MKV video file to "HANF" using an external tool. 
    /// This can be used to mark files as processed by this tool or for other identification 
    /// purposes. The method creates a temporary tag XML file with the AUTHOR tag, applies it 
    /// to the video using MKVPropEdit, and then deletes the temporary file. If a debugger is 
    /// attached, process output and errors are written to the debug output window.
    /// </summary>
    /// <param name="outputFilePath">The video file for which to set the AUTHOR tag. Must not 
    /// be null and should have a valid output file path.</param>
    //public static void SetMkvAuthorTag(string? outputFilePath)
    //{
    //    SetMkvTag(outputFilePath, ProcessedAuthorTagName, ProcessedAuthorTagValue);
        
    //}

    /// <summary>
    /// Checks whether the media file has an AUTHOR tag with the value "HANF", 
    /// which can be used to identify files that were processed by this tool or marked 
    /// in a specific way. This method calls the more general HasProcessedTag method with 
    /// the appropriate tag name and value to check for the presence of this specific metadata 
    /// tag in the media file.
    /// </summary>
    /// <param name="filePath">The path to the media file to check. If null, the current directory 
    /// is used.</param>
    /// <returns><see langword="true"/> if the media file has the AUTHOR tag with the 
    /// value "HANF"; otherwise, <see langword="false"/>.</returns>
    public static bool HasAuthorTag(string? filePath = null)
    {
        // Call the other method
        return HasMkvTag(filePath, ProcessedAuthorTagName, ProcessedAuthorTagValue);
    }

    /// <summary>
    /// Checks whether the media file has been processed by verifying the presence and value of a metadata tag.
    /// </summary>
    /// <returns><see langword="true"/> if the processed tag exists with the expected value; otherwise, <see
    /// langword="false"/>.</returns>
    public static bool HasMkvTag(string? filePath = null, string? tagName = null, string? tagValue = null)
    {
        // Guard: if the file doesn't exist there's nothing to probe — not yet processed
        if (filePath is null || !File.Exists(filePath))
            return false;

        // Use ffprobe to get the value of the processed tag in JSON format
        string args = $"-v quiet -print_format json -show_format \"{filePath}\"";
        // Run ffprobe and capture the output
        var (json, _) = MKVUtil.RunffProbe(args);

        // If ffprobe failed or is not installed, throw an exception
        if (json is null)
            throw new InvalidOperationException(
                "ffprobe failed or is not installed. Make sure ffprobe is in your PATH.");

        // Parse the JSON output
        var doc = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("ffprobe returned invalid JSON.");

        // Tags live under format.tags, not at the root. The node is a JSON object (key/value
        // pairs), not an array — AsObject() is correct here, not AsArray().
        var tags = doc["format"]?["tags"]?.AsObject();
        if (tags == null) return false;

        // Get the value of the specified tag name (or the default processed tag name if not provided)
        var tagsValue = tags[tagName ?? MKVUtil.ProcessedTagName]?.GetValue<string>() ?? null;
        if (tagsValue == null) return false;

        // We made it here, so the tag exists — check if the value matches the expected processed
        // tag value (the parameter tagValue, falling back to the default). The original code
        // compared tagsValue to itself, which always returned true regardless of content.
        return tagsValue.Equals(tagValue ?? MKVUtil.ProcessedTagValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a compiled regular expression that matches text enclosed in curly braces.
    /// </summary>
    /// <returns>A compiled regular expression instance.</returns>
    [GeneratedRegex(@"\{.*?\}")]
    private static partial Regex MetadataRegex();

    /// <summary>
    /// Gets a regular expression that matches text enclosed in square brackets.
    /// </summary>
    /// <returns>A compiled regular expression instance.</returns>
    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex SquareBracketRegex();

    /// <summary>
    /// Gets a regular expression that matches whitespace
    /// </summary>
    /// <returns>A compiled regular expression instance.</returns>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Gets a compiled regular expression that matches season and episode patterns in the format S##E## or
    /// S##E##E## (for double episodes).
    /// </summary>
    /// <returns>A compiled <see cref="Regex"/> instance that matches season/episode identifiers with case-insensitive
    /// matching.</returns>
    [GeneratedRegex(@"S\d{2}E\d{2}(E\d{2})?", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex SeasonEpisodeRegex();

    /// <summary>
    /// Gets a regex that matches a title followed by a four-digit year in parentheses from the beginning of a
    /// string.
    /// </summary>
    /// <returns>A compiled <see cref="Regex"/> instance.</returns>
    [GeneratedRegex(@"^(.*?\(\d{4}\))")]
    private static partial Regex TitleYearRegex();

    /// <summary>
    /// Matches trailing episode information in the format ' - S##E##' at the end of a string.
    /// </summary>
    /// <returns>A <see cref="Regex"/> instance for matching the pattern.</returns>
    [GeneratedRegex(@"\s-\sS\d+E\d+.*$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex TrailingEpisodeRegex();

    /// <summary>
    /// Gets a compiled regular expression that matches trailing metadata in the format " - text" at the end of a
    /// line.
    /// </summary>
    /// <returns>A compiled regular expression instance.</returns>
    [GeneratedRegex(@"\s-\s.*$")]
    private static partial Regex TrailingMetadataRegex();

    /// <summary>
    /// Gets a compiled regular expression that matches ffmpeg progress output lines, capturing frame, 
    /// fps, quality, size, time, bitrate, and speed information.
    /// </summary>
    /// <returns>A compiled <see cref="Regex"/> instance.</returns>
    [GeneratedRegex(
        @"frame=\s*(?<frame>\d+)\s+" +
        @"fps=\s*(?<fps>[\d.]+)\s+" +
        @"q=\s*(?<q>-?[\d.]+)\s+" +          // capture first q
        @"(?:q=\s*-?[\d.]+\s+)*" +            // skip any extra q= fields ← FIX
        @"size=\s*(?<size>[\d.]+\s*(?:[KMGT]i?B|B))\s+" +
        @"time=(?<time>\d{2}:\d{2}:\d{2}\.\d+|N/A)\s+" +
        @"bitrate=\s*(?<bitrate>[\d.]+\s*kbits/s|N/A)\s+" +
        @"speed=\s*(?:(?<speed>[\d.]+)x|N/A)\s+"
    )]
    private static partial Regex FfmpegProgressRegex();
}
