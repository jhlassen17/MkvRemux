using System.Diagnostics;

namespace MkvRemux;

/// <summary>
/// Probes the local system to find which HEVC encoder is available.
/// Priority: NVENC → QSV → libx265 (only when allowSwFallback is true).
/// Each test runs a 1-frame null encode with Main10 / 10-bit settings.
/// </summary>
static class EncoderDetector
{
    /// <summary>
    /// Detects the best available HEVC encoder on the system, with an optional
    /// software fallback to libx265 if no hardware encoders are available.
    /// </summary>
    /// <param name="allowSwFallback">Whether to allow software fallback to libx265.</param>
    /// <returns>The best available HEVC encoder.</returns>
    public static HwEncoder Detect(bool allowSwFallback = false)
    {
        // Note: The order of tests is important for prioritization. We test NVENC first,
     
        Console.WriteLine("  Testing encoders...");

        // Test NVENC first, as it's the most desirable for performance. We use a preset
        if (TestEncoder("hevc_nvenc", "-preset p4"))
        {
            Console.WriteLine("  [OK] NVIDIA NVENC (hevc_nvenc)");
            return HwEncoder.NvencHevc;
        }
        Console.WriteLine("  [--] NVENC not available");

        // Next, test QuickSync. We use the "medium" preset for a more comprehensive test, as some QSV implementations may be picky about certain settings.
        if (TestEncoder("hevc_qsv", "-preset medium"))
        {
            Console.WriteLine("  [OK] Intel QuickSync (hevc_qsv)");
            return HwEncoder.QsvHevc;
        }
        Console.WriteLine("  [--] QuickSync not available");

        // Finally, if allowed, test libx265 as a software fallback. We use the "medium" preset for a more comprehensive test, as some libx265 builds may be picky about certain settings.
        if (allowSwFallback)
        {
            if (TestEncoder("libx265", "-preset medium", pixFmt: "yuv420p10le"))
            {
                Console.WriteLine("  [OK] Software libx265 (CPU fallback)");
                return HwEncoder.SoftwareX265;
            }
            Console.WriteLine("  [--] libx265 not available");
        }

        // If we reach this point, no suitable encoder was found.
        return HwEncoder.None;
    }

    // ── private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tests whether a given encoder is available by running a 1-frame null encode with Main10 / 10-bit settings. We use a simple black frame as input, and discard the output to minimize overhead. 
    /// The test checks for a successful exit code to determine availability.
    /// </summary>
    /// <param name="encoder">The encoder to test.</param>
    /// <param name="extraArgs">Additional arguments to pass to ffmpeg.</param>
    /// <param name="pixFmt">The pixel format to use for the test.</param>
    /// <returns>True if the encoder is available, false otherwise.</returns>
    private static bool TestEncoder(string encoder, string extraArgs = "",
                                    string pixFmt = "p010le")
    {
        // We use a very short input (1 frame, 0.04s duration) to minimize test time.
        // The color filter generates a simple black frame of the required size and format.
        string args =
            "-f lavfi -i \"color=c=black:s=320x240:r=1:d=0.04\" " +
            $"-frames:v 1 -c:v {encoder} -profile:v main10 -pix_fmt {pixFmt} " +
            $"{extraArgs} -f null -";

        // Run the ffmpeg process with the constructed arguments and check the exit code for success.
        var (output, exitCode) = MKVUtil.RunffMpeg(args);
        return exitCode == 0;
    }
}
