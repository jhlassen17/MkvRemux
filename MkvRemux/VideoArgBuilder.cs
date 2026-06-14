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
///
/// When multiple video streams are present, each receives its own stream-qualified
/// argument block (e.g. -c:v:0 hevc_nvenc ... -c:v:1 hevc_nvenc ...) so that
/// per-stream HDR metadata and pixel-format options are applied independently.
/// </summary>
static class VideoArgBuilder
{
    /// <summary>
    /// Builds the ffmpeg argument string for all supplied video streams.
    /// Each stream at output index <c>i</c> receives fully stream-qualified options
    /// (e.g. <c>-c:v:0</c>, <c>-pix_fmt:v:0</c>, <c>-color_primaries:v:0</c>).
    /// The returned string can be appended directly to the ffmpeg command line.
    /// </summary>
    /// <param name="videos">Ordered list of video streams to encode (output index = list index).</param>
    /// <param name="encoder">The hardware encoder to use.</param>
    /// <param name="cq">Constant quality / CRF value (0-51, lower = better).</param>
    /// <param name="nvencPreset">NVENC speed preset (p1–p7, default p4).</param>
    /// <returns>Combined per-stream ffmpeg argument string, or empty if the list is empty.</returns>
    /// <exception cref="InvalidOperationException">Thrown when encoder is <see cref="HwEncoder.None"/>.</exception>
    public static string Build(
        List<VideoStreamInfo> videos,
        HwEncoder             encoder,
        int                   cq          = 19,
        string                nvencPreset = "p4")
    {
        if (encoder == HwEncoder.None)
            throw new InvalidOperationException("No encoder available.");

        if (videos.Count == 0)
            return string.Empty;

        // Filter out attached pics / image streams — they are stream-copied by CommandBuilder
        // and must not receive HEVC encode args (doing so would corrupt/drop the cover art tags).
        var encodable = videos.Where(v => !v.Disposition.IsImageStream).ToList();

        var sb = new StringBuilder();
        for (int i = 0; i < encodable.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(BuildForStream(encodable[i], i, encoder, cq, nvencPreset));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Prints a summary of the encoding settings and source video properties to the console.
    /// </summary>
    /// <param name="videos">The list of video streams to summarise.</param>
    /// <param name="encoder">The hardware encoder used.</param>
    /// <param name="cq">The constant quality or CRF value.</param>
    /// <param name="nvencPreset">The NVENC preset used.</param>
    public static void PrintSummary(List<VideoStreamInfo> videos, HwEncoder encoder, int cq, string nvencPreset)
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

        foreach (var videoStream in videos.Where(v => !v.Disposition.IsImageStream))
        {
            // Print the source video properties
            Console.WriteLine($"    Source     : [{videoStream.GlobalIndex}] {videoStream.Codec.ToUpper()} {videoStream.Resolution} {videoStream.PixFmt}");

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
    /// Builds the fully stream-qualified ffmpeg argument block for a single video stream
    /// at the given output index.
    /// </summary>
    /// <param name="video">The video stream to encode.</param>
    /// <param name="outIdx">The zero-based output stream index (determines the :v:N specifier).</param>
    /// <param name="encoder">The encoder to use.</param>
    /// <param name="cq">Constant quality / CRF value.</param>
    /// <param name="nvencPreset">NVENC preset string.</param>
    /// <returns>A ffmpeg argument fragment for this stream, e.g. <c>-c:v:0 hevc_nvenc ...</c>.</returns>
    private static string BuildForStream(
        VideoStreamInfo video,
        int             outIdx,
        HwEncoder       encoder,
        int             cq,
        string          nvencPreset)
    {
        // Build the stream specifier suffix, e.g. ":v:0" for the first video output stream.
        // This is appended to each option name so ffmpeg scopes it to this stream only.
        string spec = $":v:{outIdx}";
        var sb = new StringBuilder();

        switch (encoder)
        {
            // ── NVENC ────────────────────────────────────────────────────────
            case HwEncoder.NvencHevc:
                sb.Append($"-c{spec} hevc_nvenc -preset{spec} {nvencPreset}");
                sb.Append($" -rc{spec} vbr -cq{spec} {cq} -b{spec} 0");
                sb.Append($" -profile{spec} main10 -pix_fmt{spec} p010le");
                AppendColorMetadata(sb, video, spec);
                // Mastering display and MaxCLL data are carried as AVFrame side data
                // (AV_FRAME_DATA_MASTERING_DISPLAY_METADATA / _CONTENT_LIGHT_LEVEL) from
                // the software HEVC decoder and are embedded in the output SEI by NVENC
                // automatically — no separate flag needed or accepted here.
                break;

            // ── QuickSync ────────────────────────────────────────────────────
            case HwEncoder.QsvHevc:
                sb.Append($"-c{spec} hevc_qsv -preset{spec} medium");
                sb.Append($" -global_quality{spec} {cq} -look_ahead{spec} 1");
                sb.Append($" -profile{spec} main10 -pix_fmt{spec} p010le");
                AppendColorMetadata(sb, video, spec);
                // Same as NVENC: mastering display / MaxCLL flow through AVFrame side data.
                break;

            // ── libx265 (software) ───────────────────────────────────────────
            case HwEncoder.SoftwareX265:
                sb.Append($"-c{spec} libx265 -preset{spec} medium -crf{spec} {cq}");
                // x265 requires planar 10-bit (not packed p010le)
                sb.Append($" -pix_fmt{spec} yuv420p10le");
                AppendColorMetadata(sb, video, spec);
                // HDR metadata goes into -x265-params, not separate ffmpeg flags.
                // The option itself accepts a stream specifier in modern ffmpeg.
                if (video.IsHdr)
                    sb.Append($" -x265-params{spec} \"{BuildX265Params(video)}\"");
                break;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends stream-qualified color metadata flags to the argument string if the video is HDR.
    /// </summary>
    /// <param name="sb">The StringBuilder to append the flags to.</param>
    /// <param name="video">The video stream information.</param>
    /// <param name="spec">The stream specifier suffix to qualify each option (e.g. ":v:0").</param>
    private static void AppendColorMetadata(StringBuilder sb, VideoStreamInfo video, string spec)
    {
        // Only append color metadata if the source video is HDR, since SDR sources may not have valid values
        if (!video.IsHdr) return;

        // Append color metadata flags if the corresponding properties are not null or empty
        if (!string.IsNullOrEmpty(video.ColorPrimaries))
            sb.Append($" -color_primaries{spec} {video.ColorPrimaries}");
        if (!string.IsNullOrEmpty(video.ColorTransfer))
            sb.Append($" -color_trc{spec} {video.ColorTransfer}");
        if (!string.IsNullOrEmpty(video.ColorSpace))
            sb.Append($" -colorspace{spec} {video.ColorSpace}");
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
