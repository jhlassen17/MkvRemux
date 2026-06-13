using System.Text;

namespace MkvRemux;

/// <summary>
/// Builds ffmpeg video-codec arguments for HEVC Main10 encoding.
///
/// NVENC   : hevc_nvenc  -preset p4  -rc vbr  -cq {cq}  -b:v 0  -pix_fmt p010le
/// QSV     : hevc_qsv    -preset medium  -global_quality {cq}  -look_ahead 1  -pix_fmt p010le
/// x265    : libx265     -preset medium  -crf {cq}  -pix_fmt yuv420p10le
///
/// HDR metadata is passed for all three encoders when the source is HDR.
/// For x265, mastering display and MaxCLL go into -x265-params.
/// </summary>
static class VideoArgBuilder
{
    /// <summary>
    /// Builds the ffmpeg argument string for the specified encoder and video stream.
    /// </summary>
    /// <param name="video">The video stream information.</param>
    /// <param name="encoder">The hardware encoder to use.</param>
    /// <param name="cq">The constant quality or CRF value.</param>
    /// <param name="nvencPreset">The NVENC preset to use.</param>
    /// <returns>The ffmpeg argument string for the specified encoder and video stream.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no encoder is available.</exception>
    public static string Build(
        VideoStreamInfo video,
        HwEncoder       encoder,
        int             cq          = 19,
        string          nvencPreset = "p4")
    {
        // Validate encoder selection
        if (encoder == HwEncoder.None)
            throw new InvalidOperationException("No encoder available.");

        // Build the ffmpeg argument string based on the selected encoder and video properties
        var sb = new StringBuilder();

        // Common settings for all encoders: HEVC Main10 profile and 10-bit pixel format
        switch (encoder)
        {
            // ── NVENC ────────────────────────────────────────────────────────
            case HwEncoder.NvencHevc:
                sb.Append($"-c:v hevc_nvenc -preset:v {nvencPreset}");
                sb.Append($" -rc vbr -cq {cq} -b:v 0");
                sb.Append(" -profile:v main10 -pix_fmt p010le");
                AppendColorMetadata(sb, video);
                // Mastering display and MaxCLL data are carried as AVFrame side data
                // (AV_FRAME_DATA_MASTERING_DISPLAY_METADATA / _CONTENT_LIGHT_LEVEL) from
                // the software HEVC decoder and are embedded in the output SEI by NVENC
                // automatically — no separate flag needed or accepted here.
                break;

            // ── QuickSync ────────────────────────────────────────────────────
            case HwEncoder.QsvHevc:
                sb.Append($"-c:v hevc_qsv -preset medium");
                sb.Append($" -global_quality {cq} -look_ahead 1");
                sb.Append(" -profile:v main10 -pix_fmt p010le");
                AppendColorMetadata(sb, video);
                // Same as NVENC: mastering display / MaxCLL flow through AVFrame side data.
                break;

            // ── libx265 (software) ───────────────────────────────────────────
            case HwEncoder.SoftwareX265:
                sb.Append($"-c:v libx265 -preset medium -crf {cq}");
                // x265 requires planar 10-bit (not packed p010le)
                sb.Append(" -pix_fmt yuv420p10le");
                AppendColorMetadata(sb, video);
                // HDR metadata goes into -x265-params, not separate ffmpeg flags
                if (video.IsHdr)
                    sb.Append($" -x265-params \"{BuildX265Params(video)}\"");
                break;
        }

        // Return the built argument string
        return sb.ToString();
    }

    /// <summary>
    /// Prints a summary of the encoding settings and source video properties to the console.
    /// </summary>
    /// <param name="video">The video stream information.</param>
    /// <param name="encoder">The hardware encoder used.</param>
    /// <param name="cq">The constant quality or CRF value.</param>
    /// <param name="nvencPreset">The NVENC preset used.</param>
    public static void PrintSummary(List<VideoStreamInfo> video, HwEncoder encoder, int cq, string nvencPreset)
    {
        // Get a user-friendly name for the encoder
        string name = encoder switch
        {
            HwEncoder.NvencHevc    => "NVIDIA NVENC (hevc_nvenc)",
            HwEncoder.QsvHevc      => "Intel QuickSync (hevc_qsv)",
            HwEncoder.SoftwareX265 => "Software libx265 (CPU)",
            _                      => "unknown"
        };

        // Determine the appropriate label for the quality setting based on the encoder
        string qualityLabel = encoder == HwEncoder.QsvHevc ? "GQ" : "CQ/CRF";

        // Print the summary to the console
        Console.WriteLine($"    Encoder    : {name}");
        Console.WriteLine($"    Profile    : HEVC Main10");
        Console.WriteLine($"    {qualityLabel,-11}: {cq}");

        // NVENC has multiple presets, so include that in the summary
        if (encoder == HwEncoder.NvencHevc)
            Console.WriteLine($"    Preset     : {nvencPreset}");
        // Warn about software encoding performance
        if (encoder == HwEncoder.SoftwareX265)
            Console.WriteLine("    [NOTE] Software encoding is significantly slower than GPU.");

        Console.WriteLine();

        foreach (var videoStream in video)
        {

            // Print the source video properties
            Console.WriteLine($"    Source     : {videoStream.Codec.ToUpper()} {videoStream.Resolution} {videoStream.PixFmt}");

            // Print HDR information if the source is HDR, otherwise indicate it's SDR
            if (videoStream.IsHdr)
            {
                // Determine the HDR type based on the video properties
                string hdrType = videoStream.IsDolbyVision ? "Dolby Vision"
                               : videoStream.IsHdr10 ? "HDR10"
                               : videoStream.IsHlg ? "HLG"
                               : "HDR";

                // Print the HDR type and relevant metadata
                Console.WriteLine($"    HDR        : {hdrType}  ({videoStream.ColorTransfer})");
                if (videoStream.MasteringDisplay is not null)
                    Console.WriteLine($"    MasterDisp : {videoStream.MasteringDisplay.ToFfmpegString()}");
                if (videoStream.MaxCll is not null)
                    Console.WriteLine($"    MaxCLL     : {videoStream.MaxCll.ToFfmpegString()}");
                if (videoStream.IsDolbyVision)
                    Console.WriteLine("    [WARN] Dolby Vision dynamic metadata cannot survive re-encoding.");
            }
            else
            {
                // Source is SDR, so indicate that in the summary
                Console.WriteLine("    HDR        : none (SDR)");
            }
        }
    }

    // ── private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends color metadata flags to the argument string if the video is HDR.
    /// </summary>
    /// <param name="sb">The StringBuilder to append the flags to.</param>
    /// <param name="video">The video stream information.</param>
    private static void AppendColorMetadata(StringBuilder sb, VideoStreamInfo video)
    {
        // Only append color metadata if the source video is HDR, since SDR sources may not have valid values
        if (!video.IsHdr) return;

        // Append color metadata flags if the corresponding properties are not null or empty
        if (!string.IsNullOrEmpty(video.ColorPrimaries))
            sb.Append($" -color_primaries {video.ColorPrimaries}");
        if (!string.IsNullOrEmpty(video.ColorTransfer))
            sb.Append($" -color_trc {video.ColorTransfer}");
        if (!string.IsNullOrEmpty(video.ColorSpace))
            sb.Append($" -colorspace {video.ColorSpace}");
    }

    /// <summary>
    /// Builds the colon-separated x265-params string for HDR10 metadata.
    /// Uses the same numeric values as the ffmpeg -master_display flag.
    /// </summary>
    /// <param name="video">The video stream information.</param>
    /// <returns>The colon-separated x265-params string for HDR10 metadata.</returns>
    private static string BuildX265Params(VideoStreamInfo video)
    {
        // Start with the mandatory HDR10 flags for x265, which enable HDR10 metadata output and repeat it on every frame
        var parts = new List<string> { "hdr-opt=1", "repeat-headers=1" };

        // Append color metadata parameters if the corresponding properties are not null or empty
        if (!string.IsNullOrEmpty(video.ColorPrimaries))
            parts.Add($"colorprim={video.ColorPrimaries}");
        if (!string.IsNullOrEmpty(video.ColorTransfer))
            parts.Add($"transfer={video.ColorTransfer}");
        if (!string.IsNullOrEmpty(video.ColorSpace))
            parts.Add($"colormatrix={video.ColorSpace}");
        if (video.MasteringDisplay is not null)
            parts.Add($"master-display={video.MasteringDisplay.ToFfmpegString()}");
        if (video.MaxCll is not null)
            parts.Add($"max-cll={video.MaxCll.ToFfmpegString()}");

        // Join all the parameters with colons, which is the format x265 expects for -x265-params
        return string.Join(":", parts);
    }
}
