using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MkvRemux;

/// <summary>
/// Uses ffprobe to analyze the input file and extract stream information:
/// </summary>
static class StreamAnalyzer
{
    /// <summary>
    /// Returns a tuple containing the video stream info (or null if no video stream),
    /// a list of audio stream info, and a list of subtitle stream info.
    /// </summary>
    /// <param name="inputPath">The path to the input file.</param>
    /// <returns>A tuple containing the video, audio, and subtitle stream info.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static (List<VideoStreamInfo> Video,
                   List<AudioStreamInfo> Audio,
                   List<SubtitleStreamInfo> Subtitles) Analyze(string inputPath)
    {
        // Note: ffprobe must be in the PATH for this to work. We could add a config option
        Console.WriteLine("  Running ffprobe...");

        // Run ffprobe to get stream information in JSON format
        var (json, exitCode) = MKVUtil.RunffProbe(
            $"-v quiet -print_format json -show_streams \"{inputPath}\"");

        // If ffprobe returns a non-zero exit code, it indicates an error. Throw an exception with details.
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffprobe failed with exit code {exitCode}. Make sure ffprobe is in your PATH and the input file is valid.");
        }

        // If ffprobe failed or is not installed, throw an exception
        if (json is null)
            throw new InvalidOperationException(
                "ffprobe failed or is not installed. Make sure ffprobe is in your PATH.");

        // Parse the JSON output
        var doc = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("ffprobe returned invalid JSON.");
        var streams = doc["streams"]?.AsArray()
            ?? throw new InvalidOperationException("ffprobe JSON has no 'streams' array.");

        // Set up variables to hold the stream info. We assume at most one video stream, but can have multiple audio and subtitle streams.
        var video = new List<VideoStreamInfo>(2) ;              // Assumes only one video stream; if multiple are present, only the last processed will be stored
        var audio = new List<AudioStreamInfo>(10);
        var subtitles = new List<SubtitleStreamInfo>(20);

        // Iterate over the streams and extract relevant information based on the codec type
        foreach (var s in streams)
        {
            // Skip if the stream node is null (shouldn't happen, but just in case)
            if (s is null) continue;

            // Extract common properties
            string codecType = s["codec_type"]?.GetValue<string>() ?? "";
            int idx = s["index"]?.GetValue<int>() ?? 0;
            var tags = s["tags"];
            string lang = Normalize(tags?["language"]?.GetValue<string>());
            string title = tags?["title"]?.GetValue<string>() ?? "";
            string codec = s["codec_name"]?.GetValue<string>() ?? "";
            string profile = s["profile"]?.GetValue<string>() ?? "";

            // Handle each codec type accordingly
            switch (codecType)
            {
                // Video Stream
                case "video" when video is null:
                    // TODO: Allow multiple video streams and return a list, like we do for audio and subtitles. For now, just take the first one we encounter.
                    video.Add(ParseVideo(s, idx, codec));
                    break;

                // Audio Stream
                case "audio":
                    int channels = s["channels"]?.GetValue<int>() ?? 0;
                    string layout = s["channel_layout"]?.GetValue<string>() ?? "";
                    // bits_per_sample is 0 for compressed formats (DTS, AC3, etc.)
                    // and the actual depth for PCM / FLAC / ALAC
                    int bitsPerSample = s["bits_per_sample"]?.GetValue<int>() ?? 0;
                    audio.Add(new AudioStreamInfo(idx, lang, title, codec, profile,
                                                  channels, layout, bitsPerSample));
                    break;

                // Subtitle Stream
                case "subtitle":
                    // Read the hearing_impaired disposition only for subtitle streams.
                    bool isHearingImpaired =
                        int.TryParse(s["disposition"]?["hearing_impaired"]?.ToString(), out int hiVal)
                        && hiVal == 1;
                    if (!isHearingImpaired) isHearingImpaired = s["tags"]?["title"]?.ToString()?.Contains("SDH",
                        StringComparison.OrdinalIgnoreCase) ?? false;
                    subtitles.Add(new SubtitleStreamInfo(idx, lang, title, codec, isHearingImpaired));
                    break;
            }
        }

        // Return the collected stream information as a tuple
        return (video, audio, subtitles);
    }

    // ── Video ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a video stream JSON node and extracts relevant information to create a VideoStreamInfo object.
    /// </summary>
    /// <param name="s">The JSON node representing the video stream.</param>
    /// <param name="idx">The index of the video stream.</param>
    /// <param name="codec">The codec of the video stream.</param>
    /// <returns>A VideoStreamInfo object containing the extracted information.</returns>
    private static VideoStreamInfo ParseVideo(JsonNode s, int idx, string codec)
    {
        // Extract video-specific properties
        int width = s["width"]?.GetValue<int>() ?? 0;
        int height = s["height"]?.GetValue<int>() ?? 0;
        string pixFmt = s["pix_fmt"]?.GetValue<string>() ?? "";
        string colorSp = s["color_space"]?.GetValue<string>() ?? "";
        string colorPri = s["color_primaries"]?.GetValue<string>() ?? "";
        string colorTrc = s["color_transfer"]?.GetValue<string>() ?? "";
        string durationStr = s["tags"]["DURATION"]?.GetValue<string>() ?? "";
        TimeSpan duration = ParseFfprobeDuration(durationStr);

        // Initialize variables for HDR mastering display, content light level, and Dolby Vision flag
        HdrMasteringDisplay? masteringDisplay = null;
        ContentLightLevel? maxCll = null;
        bool isDovi = false;

        // Check for side data that may contain HDR mastering display info, content light level, or Dolby Vision metadata
        var sideDataList = s["side_data_list"]?.AsArray();
        if (sideDataList is not null)
        {
            // Iterate over the side data entries to find relevant HDR and Dolby Vision information
            foreach (var sd in sideDataList)
            {
                // Skip if the side data node is null (shouldn't happen, but just in case)
                if (sd is null) continue;
                string sdType = sd["side_data_type"]?.GetValue<string>() ?? "";

                // Check the type of side data and extract relevant information for mastering display, content light level, or Dolby Vision
                if (sdType.Contains("Mastering display", StringComparison.OrdinalIgnoreCase))
                {
                    masteringDisplay = new HdrMasteringDisplay(
                        Rx: ParseRational(sd["red_x"]), Ry: ParseRational(sd["red_y"]),
                        Gx: ParseRational(sd["green_x"]), Gy: ParseRational(sd["green_y"]),
                        Bx: ParseRational(sd["blue_x"]), By: ParseRational(sd["blue_y"]),
                        Wx: ParseRational(sd["white_point_x"]), Wy: ParseRational(sd["white_point_y"]),
                        MaxLuminance: ParseRational(sd["max_luminance"]),
                        MinLuminance: ParseRational(sd["min_luminance"]));
                }
                else if (sdType.Contains("Content light level", StringComparison.OrdinalIgnoreCase))
                {
                    maxCll = new ContentLightLevel(
                        MaxContent: sd["max_content"]?.GetValue<int>() ?? 0,
                        MaxAverage: sd["max_average"]?.GetValue<int>() ?? 0);
                }
                else if (sdType.Contains("DOVI", StringComparison.OrdinalIgnoreCase) ||
                         sdType.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
                {
                    isDovi = true;
                }
            }
        }

        // Create and return a VideoStreamInfo object with the extracted information
        return new VideoStreamInfo(idx, codec, width, height, pixFmt,
                                   colorSp, colorPri, colorTrc,
                                   masteringDisplay, maxCll, isDovi, duration);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a rational number from a JSON node, which may be in the form of "num/denom" or just "num".
    /// </summary>
    /// <param name="node">The JSON node containing the rational number.</param>
    /// <returns>The parsed integer value, or 0 if parsing fails.</returns>
    private static int ParseRational(JsonNode? node)
    {
        //  If the node is null, return 0 as a default value
        if (node is null) return 0;
        //  Get the raw string value from the node, which may be in the form of "num/denom" or just "num"
        string raw = node.GetValue<string>();
        if (raw == null) return 0;
        //  Find the index of the '/' character, if it exists, to separate the numerator and denominator
        int slash = raw.IndexOf('/');
        string num = slash >= 0 ? raw[..slash] : raw;
        return int.TryParse(num, out int n) ? n : 0;
    }

    /// <summary>
    /// Normalizes a language code by trimming whitespace, converting to lowercase, 
    /// and replacing empty or whitespace-only strings with "und" (undefined).
    /// </summary>
    /// <param name="raw">The raw language code to normalize.</param>
    /// <returns>The normalized language code.</returns>
    private static string Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "und" : raw.Trim().ToLowerInvariant();

    /// <summary>
    /// Parses an ffprobe DURATION tag, which may be:
    ///   "02:33:33.371000000"  (HH:mm:ss + up to 9 fractional digits)
    ///   "9213.371"            (plain decimal seconds)
    ///   ""  / null            (missing tag)
    /// </summary>
    private static TimeSpan ParseFfprobeDuration(string? s)
    {
        // Skip parsing if the string is null, empty, or whitespace, and return a default duration of zero.
        if (string.IsNullOrWhiteSpace(s))
            return TimeSpan.Zero;

        // Case 1: plain decimal seconds  e.g. "9213.371"
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double secs))
            return TimeSpan.FromSeconds(secs);

        // Case 2: HH:mm:ss.fffffffff
        // TimeSpan only supports 7 fractional digits — truncate any excess.
        int dotIdx = s.LastIndexOf('.');
        if (dotIdx >= 0)
        {
            ReadOnlySpan<char> frac = s.AsSpan(dotIdx + 1);
            if (frac.Length > 7)
                s = string.Concat(s.AsSpan(0, dotIdx + 1), frac[..7]);
        }

        // Try to parse the string as a TimeSpan using invariant culture.
        // If parsing fails, return a default duration of zero.
        return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out TimeSpan ts)
            ? ts
            : TimeSpan.Zero;
    }
}
