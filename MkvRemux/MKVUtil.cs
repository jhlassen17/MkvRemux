using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
    protected const string mkvpropeditPath = @"C:\Program Files\MKVToolNix\mkvpropedit.exe";

    /// <summary>
    /// Gets the processed tag name associated with this instance.
    /// </summary>
    public static string ProcessedTagName => "COPYRIGHT";

    /// <summary>
    /// Gets the processed tag value.
    /// </summary>
    public static string ProcessedTagValue => "processed";

    /// <summary>
    /// Runs a process with the specified executable and arguments, and captures its standard output and 
    /// error streams. The method returns a tuple containing the captured output (or null if the process failed) 
    /// and the exit code of the process. It also logs any errors encountered 
    /// while trying to run the process, as well as any error output produced by the process itself.
    /// </summary>
    /// <param name="exe">The path to the executable to run.</param>
    /// <param name="arguments">The arguments to pass to the executable.</param>
    /// <returns>A tuple containing the captured output (or null if the process failed) and the exit code of the process.</returns>
    public static (string?, int) RunProcess(string exe, string arguments)
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

            // Set up event handlers to capture the output and error data as it is received
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    outputBuilder.AppendLine(e.Data);
            };

            // Capture standard error output in case ffprobe writes warnings or errors there. We will check the exit code later to determine if it was successful.
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    errorBuilder.AppendLine(e.Data);
            };

            // Start the process and check if it started successfully. If not, return null to indicate failure.
            if (!proc.Start()) return (null, -1);

            // Begin asynchronous reading of the output and error streams
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            //  Wait for the process to exit and then check the exit code. If it is 0, return the captured output; otherwise, return null to indicate failure.
            proc.WaitForExit();

            // If there was any error output, we can log it for debugging purposes, even if the process exited with code 0 (ffprobe may write warnings to stderr)
            if (errorBuilder.Length > 0)
            {
                Console.Error.WriteLine($"  [WARN] {exe} reported errors: {errorBuilder}");
            }

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
    /// <returns>A tuple containing the captured output (or null if ffprobe failed) and the exit 
    /// code of the ffprobe process.</returns>
    public static (string?, int) RunffProbe(string arguments)
        => RunProcess("ffprobe", arguments);

    /// <summary>
    /// Runs ffmpeg with the specified arguments and captures its output. This is a convenience
    /// method that calls RunProcess with "ffmpeg" as the executable. It returns a tuple 
    /// containing the captured output (or null if ffmpeg failed) and the exit code of the ffmpeg 
    /// process.
    /// </summary>
    /// <param name="arguments">The arguments to pass to ffmpeg.</param>
    /// <returns>A tuple containing the captured output (or null if ffmpeg failed) and the exit 
    /// code of the ffmpeg process.</returns>
    public static (string?, int) RunffMpeg(string arguments)
        => RunProcess("ffmpeg", arguments);

    /// <summary>
    /// Checks if an output file with the same title and episode already exists in the output directory. 
    /// This is done by normalizing the title and episode information from the provided filename and 
    /// comparing it against existing MKV files in the same directory. If a matching title and episode 
    /// are found, it returns true to indicate that an output file already exists, which can be used to 
    /// skip remuxing if desired.
    /// </summary>
    /// <param name="filename">The filename to check for existence.</param>
    /// <returns>True if an output file with the same title and episode exists; otherwise, false.</returns>
    public static bool OutputExists(string? filename = null)
    {

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
        alreadyExists = Directory
                .EnumerateFiles(outputDir, "*.mkv")
                .Select(f => MKVUtil.NormalizeTitle(f))
                .Any(existing =>
                {
                    if (!string.Equals(existing.Title, fTitle.Title, StringComparison.OrdinalIgnoreCase))
                        return false;

                    // Only compare episodes when both sides have one — avoids false negatives
                    // when the output filename has no episode tag yet.
                    if (!string.IsNullOrEmpty(existing.Episode) && !string.IsNullOrEmpty(fTitle.Episode))
                        return string.Equals(existing.Episode, fTitle.Episode, StringComparison.OrdinalIgnoreCase);

                    return true;
                });

        // Return the result of the existence check
        return alreadyExists;
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

        // ✅ Extract episode pattern (S01E01, S01E01E02, etc.)
        var epMatch = SeasonEpisodeRegex().Match(name);
        string episode = epMatch?.Success == true ? epMatch.Value.ToUpper() : null ?? string.Empty;

        // ✅ Extract "Title (Year)"
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
    protected static string CreateTagXml()
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
                              <Name>COPYRIGHT</Name>
                              <String>processed</String>
                            </Simple>
                          </Tag>
                        </Tags>";
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
    public static void SetMkvCopyrightTag(string? outputFilePath)
    {
        try
        {
            // Create the lag flag file
            string tagFile = CreateTagXml();
            // Set up args
            string args = $"\"{outputFilePath}\" --tags global:\"{tagFile}\"";
            // Run the tool
            var result = MKVUtil.RunProcess(mkvpropeditPath, args);
            // Debug
            if (Debugger.IsAttached) Debug.WriteLine(result.Item1);
            // Should we also apply the tag to the source file?
            //args = $"\"{video.FilePath}\" --tags global:\"{tagFile}\"";
            //result = MKVUtil.RunProcess(HBEState.MKVPropEditPath, args, Debugger.IsAttached);
            //if (Debugger.IsAttached) Debug.WriteLine(result);
            // Delete the old tag file
            File.Delete(tagFile);
        }
        catch (Exception ex)
        {
            // It gave me error
            Debug.WriteLine($"\n⚠️  {ex.Data}");
            //throw;
        }
    }

    /// <summary>
    /// Checks whether the media file has been processed by verifying the presence and value of a metadata tag.
    /// </summary>
    /// <returns><see langword="true"/> if the processed tag exists with the expected value; otherwise, <see
    /// langword="false"/>.</returns>
    public static bool HasProcessedTag(string? filePath = null)
    {
        // Use ffprobe to get the value of the processed tag in JSON format
        string args = $"-v quiet -print_format json -show_format \"{filePath ?? "."}\"";
        // Run ffprobe and capture the output
        var output = MKVUtil.RunffProbe(args);
        // Look for COPYRIGHT tag
        var match = Regex.Match(output.Item1 ?? string.Empty, @"""TAG:" + MKVUtil.ProcessedTagName + """\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);

        // If we don't find the tag, or if the value doesn't match our expected processed value, return false
        if (!match.Success)
            return false;

        // Extract the tag value and compare it to our expected processed value (case-insensitive)
        string value = match.Groups[1].Value;

        // Return true if the value matches our expected processed tag value, ignoring case
        return value.Equals(MKVUtil.ProcessedTagValue, StringComparison.OrdinalIgnoreCase);
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
}
