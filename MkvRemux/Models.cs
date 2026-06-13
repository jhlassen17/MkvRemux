namespace MkvRemux;

// ─────────────────────────────────────────────────────────────────────────────
  #region Audio
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Audio stream properties extracted by ffprobe.
/// </summary>
/// <param name="GlobalIndex">The global index of the audio stream.</param>
/// <param name="Language">The language of the audio stream.</param>
/// <param name="Title">The title of the audio stream.</param>
/// <param name="Codec">The codec of the audio stream.</param>
/// <param name="Profile">The profile of the audio stream.</param>
/// <param name="Channels">The number of channels in the audio stream.</param>
/// <param name="ChannelLayout">The channel layout of the audio stream.</param>
/// <param name="BitsPerSample">The number of bits per sample in the audio stream.</param>
public record AudioStreamInfo(
    int GlobalIndex,
    string Language,
    string Title,
    string Codec,
    string Profile,
    int Channels,
    string ChannelLayout,
    int BitsPerSample) : IEquatable<AudioStreamInfo>, IComparable<AudioStreamInfo>
{
    /// <summary>
    /// Codec priority for sorting and selection. Lower values indicate higher priority. 
    /// The order is determined by common audio codecs and their typical quality, with special 
    /// handling for certain profiles (e.g. TrueHD with Atmos is prioritized highest). 
    /// Unrecognized codecs are assigned a default low priority of 99.
    /// Lower = higher priority. 1 = best, 99 = unknown / worst.
    /// </summary>
    public int CodecPriority => (Codec.ToLower(), Profile.ToLower()) switch
    {
        ("truehd", var p)       when p.Contains("atmos")    => 1,
        ("truehd", _)                                       => 2,
        ("dts", var p)          when p.Contains("ma")       => 3,   // DTS-HD MA
        ("dts", var p)          when p.Contains(":x")       => 4,   // DTS:X
        ("dts", var p)          when p.Contains("hra")      => 5,   // DTS-HD HRA
        ("eac3", _)                                         => 6,
        ("dts", _)                                          => 7,   // DTS core
        ("ac3", _)                                          => 8,
        ("aac", _)                                          => 9,
        ("flac", var p)         when p.Contains("flac")     => 10,
        ("alac", var p)         when p.Contains("alac")     => 11,
        ("mp3", _)                                          => 12,
        ("pcm", var p)          when p.StartsWith("pcm")    => 13,
        ("vorbis", _)                                       => 14,
        ("opus", _)                                         => 15,
        ("mp2", _)                                          => 16,
        _                                                   => 99
    };

    /// <summary>
    /// Returns a user-friendly display name for the audio stream, e.g. "DTS-HD MA 5.1" or "AAC 2.0", 
    /// based on the codec and channel layout.
    /// </summary>
    public string DisplayName
    {
        get
        {
            // Map common codecs and profiles to user-friendly labels, e.g. "DTS-HD MA" instead of just "dts".
            string codec = (Codec.ToLower(), Profile.ToLower()) switch
            {
                ("truehd", var p)       when p.Contains("atmos")    => "TrueHD Atmos",
                ("truehd", _)                                       => "TrueHD",
                ("dts", var p)          when p.Contains("ma")       => "DTS-HD MA",
                ("dts", var p)          when p.Contains(":x")       => "DTS:X",
                ("dts", var p)          when p.Contains("hra")      => "DTS-HD HRA",
                ("eac3", _)                                         => "E-AC3",
                ("dts", _)                                          => "DTS",
                ("ac3", _)                                          => "AC3",
                ("aac", _)                                          => "AAC",
                ("flac", var p)         when p.Contains("flac")     => "FLAC",
                ("alac", var p)         when p.Contains("alac")     => "ALAC",
                ("mp3", _)                                          => "MP3",
                ("pcm", var p)          when p.StartsWith("pcm")    => "PCM",
                ("mp2", _)                                          => "MP2",
                ("vorbis", _)                                       => "Vorbis",
                ("opus", _)                                         => "Opus",
                _                                                   => Codec.ToUpper()
            };

            // Return a string like "DTS-HD MA 5.1" or "AAC 2.0" for display in the summary.
            return $"{codec} {ChannelDesc}";
        }
    }

    /// <summary>
    /// Returns a user-friendly channel description based on the channel layout or channel count.
    /// </summary>
    public string ChannelDesc
    {
        get
        {
            // First try to parse the channel layout string, which is more descriptive than just the channel count.
            if (!string.IsNullOrWhiteSpace(ChannelLayout))
            {
                var l = ChannelLayout.ToLower();
                if (l.StartsWith("7.1")) return "7.1";
                if (l.StartsWith("6.1")) return "6.1";
                if (l.StartsWith("5.1")) return "5.1";
                if (l.StartsWith("4.0")) return "4.0";
                if (l.StartsWith("3.0")) return "3.0";      // I don't think this exists but just in case
                if (l.StartsWith("2.1")) return "2.1";
                if (l == "stereo") return "2.0";
                if (l == "mono") return "1.0";
            }

            // Fallback to channel count if layout is unavailable or unrecognized.
            return Channels switch
            {
                8 => "7.1",
                7 => "6.1",
                6 => "5.1",
                4 => "4.0",
                3 => "2.1",
                2 => "2.0",
                1 => "1.0",
                _ => $"{Channels}ch"
            };
        }
    }

    /// <summary>
    /// Recommended AAC bitrate for the given channel count, based on common practice and ffmpeg defaults.
    /// </summary>
    public int AacSurroundBitrate => Channels switch
    {
        >= 8 => 768,
        >= 6 => 640,
        >= 4 => 384,
        >= 2 => 256,
        _ => 192
    };

    /// <summary>
    /// 24-bit fallback for compressed formats (DTS-HD / TrueHD decode to 24-bit PCM)
    /// </summary>
    public int EffectiveBitDepth => BitsPerSample > 0 ? BitsPerSample : 24;

    /// <summary>
    /// Compares this AudioStreamInfo to another for sorting purposes. The sorting order is determined by Codec,
    /// Language, Title, DisplayName, GlobalIndex, Channels, BitsPerSample, EffectiveBitDepth, and EqualityContract.
    /// </summary>
    /// <param name="other">The other AudioStreamInfo to compare to.</param>
    /// <returns>An integer that indicates the relative order of the objects being compared.</returns>
    public int CompareTo(AudioStreamInfo? other)
    {
        // Initialize the result variable to 0 (equal) and update it at each comparison step. If any comparison yields a non-zero result, that will be returned immediately.
        int tmpResult = 0;

        // Compare reference equality first for a quick path when both references point to the same object. If they are the same instance, they are considered equal, so return 0.
        if (Object.ReferenceEquals(this, other)) return 0;

        // Base check: if the other object is null, this instance is considered greater (i.e. comes first in sorting), so return -1.
        if (other == null || other is not AudioStreamInfo) return -1;

        // Compare Codec first (e.g. PGS vs SRT), then Language, then Title, then IsHearingImpaired
        // (SDH before non-SDH), then GlobalIndex (lower index first), and finally DisplayName as a tiebreaker.
        tmpResult = this.Codec.CompareTo(other.Codec);

        // We are equal so far
        if (tmpResult == 0)
        {
            tmpResult = this.ChannelDesc.CompareTo(other.ChannelDesc);

            if (tmpResult == 0)
            {
                tmpResult = this.DisplayName.CompareTo(other.DisplayName);

                if (tmpResult == 0)
                {
                    tmpResult = this.Language.CompareTo(other.Language);

                    if (tmpResult == 0)
                    {
                        tmpResult = this.Channels.CompareTo(other.Channels);

                        if (tmpResult == 0)
                        {
                            tmpResult = this.BitsPerSample.CompareTo(other.BitsPerSample);

                            if (tmpResult == 0)
                            {
                                tmpResult = this.GlobalIndex.CompareTo(other.GlobalIndex);

                                if (tmpResult == 0)
                                {
                                    tmpResult = this.Title.CompareTo(other.Title);

                                    if (tmpResult == 0)
                                    {
                                        tmpResult = this.Profile.CompareTo(other.Profile);

                                        if (tmpResult == 0)
                                        {
                                            tmpResult = this.ChannelDesc.CompareTo(other.ChannelDesc);

                                            if (tmpResult == 0)
                                            {
                                                tmpResult = this.ChannelLayout.CompareTo(other.ChannelLayout);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Return the result of the first non-zero comparison, or 0 if all comparisons are equal.
        return tmpResult;
    }

    /// <summary>
    /// Hash code must be consistent with Equals. Since we are using the synthesized record equality, 
    /// which compares all properties, we should combine all properties in the hash code as well to 
    /// maintain consistency. This ensures that if two AudioStreamInfo instances are considered equal by the 
    /// synthesized Equals method, they will also have the same hash code, which is important for correct 
    /// behavior in hash-based collections like dictionaries and hash sets.
    /// </summary>
    /// <returns></returns>
    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(this.Codec, this.Language, this.Title, this.DisplayName, this.GlobalIndex),
            HashCode.Combine(this.AacSurroundBitrate, this.ChannelDesc, this.ChannelLayout, this.Profile),
            HashCode.Combine(this.Channels, this.BitsPerSample, this.EffectiveBitDepth, this.EqualityContract)
        );
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
#region Subtitle
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Subtitle stream properties extracted by ffprobe.
/// </summary>
/// <param name="GlobalIndex">The global index of the subtitle stream.</param>
/// <param name="Language">The language of the subtitle stream.</param>
/// <param name="Title">The title of the subtitle stream.</param>
/// <param name="Codec">The codec of the subtitle stream.</param>
/// <param name="IsHearingImpaired">Whether the subtitle stream is flagged as hearing 
/// impaired (SDH) by ffmpeg.</param>
public record SubtitleStreamInfo(
    int GlobalIndex,
    string Language,
    string Title,
    string Codec,
    bool IsHearingImpaired) : IEquatable<SubtitleStreamInfo>, IComparable<SubtitleStreamInfo>
{

    /// <summary>
    /// True if this track is SDH — either by disposition flag or title keyword.
    /// </summary>
    public bool IsSDH =>
        IsHearingImpaired ||
        Title.Contains("SDH", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a user-friendly display name for the subtitle stream, e.g. "PGS (ENG) — Commentary" or "SRT (UND)".
    /// </summary>
    public string DisplayName
    {
        get
        {
            // Map common subtitle codecs to user-friendly labels.
            string label = Codec.ToLower() switch
            {
                "hdmv_pgs_subtitle" or "pgssub" => "PGS",
                "dvd_subtitle" or "dvdsub" => "DVD/VOB",
                "subrip" or "srt" => "SRT",
                "ass" or "ssa" => "ASS",
                "mov_text" => "MOV Text",
                "webvtt" => "WebVTT",
                "dvb_subtitle" => "DVB",
                _ => Codec.ToUpper()
            };

            // If Title is empty or whitespace, omit it from the display name.
            return string.IsNullOrWhiteSpace(Title)
                ? $"{label} ({Language})"
                : $"{label} ({Language}) — {Title}";
        }
    }

    /// <summary>
    /// Compares this SubtitleStreamInfo to another for sorting purposes. The sorting order is determined by Codec, 
    /// then Language, then Title, then IsHearingImpaired, then GlobalIndex, and finally DisplayName.
    /// </summary>
    /// <param name="other">The other SubtitleStreamInfo to compare to.</param>
    /// <returns>An integer that indicates the relative order of the objects being compared.</returns>
    public int CompareTo(SubtitleStreamInfo? other)
    {
        // Initialize the result variable to 0 (equal) and update it at each comparison step. If any comparison yields a non-zero result, that will be returned immediately.
        int tmpResult = 0;

        // Compare reference equality first for a quick path when both references point to the same object. If they are the same instance, they are considered equal, so return 0.
        if (Object.ReferenceEquals(this, other)) return 0;

        // Base check: if the other object is null, this instance is considered greater (i.e. comes first in sorting), so return -1.
        if (other == null || other is not SubtitleStreamInfo) return -1;

        // Compare Codec first (e.g. PGS vs SRT), then Language, then Title, then IsHearingImpaired
        // (SDH before non-SDH), then GlobalIndex (lower index first), and finally DisplayName as a tiebreaker.
        tmpResult = this.Codec.CompareTo(other.Codec);

        // We are equal so far
        if (tmpResult == 0)
        {
            // Compare Language next (e.g. ENG before JPN)
            tmpResult = this.Language.CompareTo(other.Language);

            if (tmpResult == 0)
            {
                // Compare Title next (e.g. Commentary before Director's Cut)
                tmpResult = this.Title.CompareTo(other.Title);

                if (tmpResult == 0)
                {
                    // Finally, compare IsHearingImpaired (SDH before non-SDH)
                    tmpResult = this.IsHearingImpaired.CompareTo(other.IsHearingImpaired);

                    if (tmpResult == 0)
                    {
                        // Then compare GlobalIndex (lower index first)
                        tmpResult = this.GlobalIndex.CompareTo(other.GlobalIndex);

                        if (tmpResult == 0)
                        {
                            // As a final tiebreaker, compare the DisplayName, which includes codec, language, and title information.
                            tmpResult = this.DisplayName.CompareTo(other.DisplayName);
                        }
                    }
                }
            }
        }

        // Return the result of the first non-zero comparison, or 0 if all comparisons are equal.
        return tmpResult;
    }


    // Note: Records auto-generate Equals based on all properties/parameters.
    // For IEquatable<T> compliance with CompareTo, we can optionally use explicit interface implementation.
    // However, this conflicts with record's own Equals, so we rely on the synthesized implementation.

    /// <summary>
    /// Hash code must be consistent with Equals. Since we are using the synthesized record equality,
    /// we rely on the synthesized GetHashCode implementation.
    /// </summary>
    /// <returns>An integer hash code.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(this.Codec, this.Language, this.Title, this.IsHearingImpaired,
            this.GlobalIndex, this.DisplayName, this.EqualityContract);
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
#region Video / HDR
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// HDR10 mastering display chromaticity and luminance.
/// All xy values are in 1/50000 units; luminance in 1/10000 cd/m².
/// This is the raw integer form ffmpeg -master_display expects.
/// </summary>
/// <param name="Rx">Red primary x coordinate.</param>
/// <param name="Ry">Red primary y coordinate.</param>
/// <param name="Gx">Green primary x coordinate.</param>
/// <param name="Gy">Green primary y coordinate.</param>
/// <param name="Bx">Blue primary x coordinate.</param>
/// <param name="By">Blue primary y coordinate.</param>
/// <param name="Wx">White point x coordinate.</param>
/// <param name="Wy">White point y coordinate.</param>
/// <param name="MaxLuminance">Maximum luminance.</param>
/// <param name="MinLuminance">Minimum luminance.</param>
public record HdrMasteringDisplay(
    int Rx, int Ry,
    int Gx, int Gy,
    int Bx, int By,
    int Wx, int Wy,
    int MaxLuminance,
    int MinLuminance) : IEquatable<HdrMasteringDisplay>, IComparable<HdrMasteringDisplay>
{
    /// <summary>
    /// Compares this HdrMasteringDisplay to another for sorting purposes. The sorting order is determined 
    /// by Bx, then By, then Gx, then Gy, then Rx, then Ry, then Wx, then Wy, then MaxLuminance, and finally MinLuminance.
    /// </summary>
    /// <param name="other">The other HdrMasteringDisplay to compare to.</param>
    /// <returns>An integer that indicates the relative order of the objects being compared.</returns>
    public int CompareTo(HdrMasteringDisplay? other)
    {
        // Initialize the result variable to 0 (equal) and update it at each comparison step. If any comparison yields a non-zero result, that will be returned immediately.
        int tmpResult = 0;

        // Compare reference equality first for a quick path when both references point to the same object. If they are the same instance, they are considered equal, so return 0.
        if (Object.ReferenceEquals(this, other)) return 0;

        // Base check: if the other object is null, this instance is considered greater (i.e. comes first in sorting), so return -1.
        if (other == null || other is not HdrMasteringDisplay) return -1;

        // Compare Codec first (e.g. PGS vs SRT), then Language, then Title, then IsHearingImpaired
        // (SDH before non-SDH), then GlobalIndex (lower index first), and finally DisplayName as a tiebreaker.
        tmpResult = this.Bx.CompareTo(other.Bx);

        // We are equal so far
        if (tmpResult == 0)
        {
            //
            tmpResult = this.By.CompareTo(other.By);

            if (tmpResult == 0)
            {
                tmpResult = this.Gx.CompareTo(other.Gx);
                if (tmpResult == 0)
                {
                    tmpResult = this.Gy.CompareTo(other.Gy);
                    if (tmpResult == 0)
                    {
                        tmpResult = this.Rx.CompareTo(other.Rx);
                        if (tmpResult == 0)
                        {
                            tmpResult = this.Ry.CompareTo(other.Ry);
                            if (tmpResult == 0)
                            {
                                tmpResult = this.Wx.CompareTo(other.Wx);
                                if (tmpResult == 0)
                                {
                                    tmpResult = this.Wy.CompareTo(other.Wy);
                                    if (tmpResult == 0)
                                    {
                                        tmpResult = this.MaxLuminance.CompareTo(other.MaxLuminance);
                                        if (tmpResult == 0)
                                        {
                                            tmpResult = this.MinLuminance.CompareTo(other.MinLuminance);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Return the result of the first non - zero comparison, or 0 if all comparisons are equal.
        return tmpResult;
    }

    // Note: Records auto-generate Equals based on all properties/parameters.
    // The synthesized equality already uses value-based comparison.

    /// <summary>
    /// Hash code must be consistent with Equals. Since we are using the synthesized record equality,
    /// we rely on the synthesized GetHashCode implementation.
    /// </summary>
    /// <returns>An integer hash code.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(this.Gy, this.Gx, this.Bx, this.By, this.Rx, this.Ry, this.Wx, this.Wy),
            HashCode.Combine(this.MaxLuminance, this.MinLuminance, this.EqualityContract));

    /// <summary>
    /// Formats the -master_display argument value for ffmpeg.
    /// </summary>
    /// <returns>A string formatted for the -master_display argument in ffmpeg.</returns>
    public string ToFfmpegString() =>
        $"G({Gx},{Gy})B({Bx},{By})R({Rx},{Ry})WP({Wx},{Wy})L({MaxLuminance},{MinLuminance})";
}

/// <summary>
/// Content Light Level (MaxCLL / MaxFALL) from HDR10 SEI.
/// </summary>
/// <param name="MaxContent">The maximum content light level.</param>
/// <param name="MaxAverage">The maximum frame-average light level.</param>
public record ContentLightLevel(int MaxContent, int MaxAverage) : IEquatable<ContentLightLevel>, IComparable<ContentLightLevel>
{
    /// <summary>
    /// Compares this ContentLightLevel to another for sorting purposes. The sorting order is determined by 
    /// MaxContent, then MaxAverage.
    /// </summary>
    /// <param name="other">The other ContentLightLevel to compare to.</param>
    /// <returns>An integer indicating the relative order of the objects.</returns>
    public int CompareTo(ContentLightLevel? other)
    {
        // Initialize the result variable to 0 (equal) and update it at each comparison step. If any comparison yields a non-zero result, that will be returned immediately.
        int tmpResult = 0;

        // Compare reference equality first for a quick path when both references point to the same object. If they are the same instance, they are considered equal, so return 0.
        if (Object.ReferenceEquals(this, other)) return 0;

        // Base check: if the other object is null, this instance is considered greater (i.e. comes first in sorting), so return -1.
        if (other == null || other is not ContentLightLevel) return -1;

        // Compare Codec first (e.g. PGS vs SRT), then Language, then Title, then IsHearingImpaired
        // (SDH before non-SDH), then GlobalIndex (lower index first), and finally DisplayName as a tiebreaker.
        tmpResult = this.MaxContent.CompareTo(other.MaxContent);

        // We are equal so far
        if (tmpResult == 0)
        {
            // Compare MaxAverage next
            tmpResult = this.MaxAverage.CompareTo(other.MaxAverage);
        }

        // Return the result of the first non-zero comparison, or 0 if all comparisons are equal.
        return tmpResult;
    }

    /// <summary>
    /// Hash code must be consistent with Equals. Since we are using the synthesized record equality,
    /// we rely on the synthesized GetHashCode implementation.
    /// </summary>
    /// <returns>An integer hash code.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(this.EqualityContract, this.MaxContent, this.MaxAverage));


    /// <summary>
    /// Formats the -max_cll argument value for ffmpeg, e.g. "1000,400" for MaxCLL=1000 cd/m² and MaxFALL=400 cd/m².
    /// </summary>
    /// <returns>A string formatted for the -max_cll argument in ffmpeg.</returns>
    public string ToFfmpegString() => $"{MaxContent},{MaxAverage}";
}

/// <summary>
/// Video stream properties extracted by ffprobe.
/// </summary>
/// <param name="GlobalIndex">The stream's global index in the media file.</param>
/// <param name="Codec">The codec used by the video stream.</param>
/// <param name="Width">The width of the video in pixels.</param>
/// <param name="Height">The height of the video in pixels.</param>
/// <param name="PixFmt">The pixel format of the video stream.</param>
/// <param name="ColorSpace">The color space of the video stream.</param>
/// <param name="ColorPrimaries">The color primaries of the video stream.</param>
/// <param name="ColorTransfer">The color transfer characteristics of the video stream.</param>
/// <param name="MasteringDisplay">The HDR10 mastering display metadata.</param>
/// <param name="MaxCll">The content light level (MaxCLL / MaxFALL) from HDR10 SEI.</param>
/// <param name="IsDolbyVision">Indicates if the stream is Dolby Vision.</param>
/// <param name="StreamDuration">The duration of the video stream.</param>
public record VideoStreamInfo(
    int GlobalIndex,
    string Codec,
    int Width,
    int Height,
    string PixFmt,
    string ColorSpace,
    string ColorPrimaries,
    string ColorTransfer,
    HdrMasteringDisplay? MasteringDisplay,
    ContentLightLevel? MaxCll,
    bool IsDolbyVision,
    TimeSpan StreamDuration, 
    VideoStreamDisposition Disposition) : IEquatable<VideoStreamInfo>, IComparable<VideoStreamInfo>
{
    /// <summary>
    /// True when the stream carries HDR metadata or uses an HDR transfer curve.
    /// </summary>															   
    public bool IsHdr =>
        ColorTransfer is "smpte2084" or "arib-std-b67" or "smpte428" ||
        ColorPrimaries == "bt2020" ||
        MasteringDisplay is not null;

    /// <summary>
    /// True when the stream uses the PQ (Perceptual Quantizer) transfer curve defined in SMPTE ST 2084,
    /// </summary>
    public bool IsHdr10 => ColorTransfer == "smpte2084";

    /// <summary>
    /// True when the stream uses the HLG (Hybrid Log-Gamma) transfer curve defined in ARIB STD-B67.
    /// </summary>
    public bool IsHlg => ColorTransfer == "arib-std-b67";

    /// <summary>
    /// True when the source pixel format is already 10-bit.
    /// </summary>																			   
    public bool IsSource10Bit =>
        PixFmt.Contains("10") || PixFmt is "p010le" or "p010be" or "yuv420p10le";

    /// <summary>
    /// Returns a string like "1920x1080" for display in the summary. The actual ffmpeg arguments use Width and Height separately.
    /// </summary>
    public string Resolution => $"{Width}x{Height}";

    /// <summary>
    /// Compares this VideoStreamInfo to another for sorting purposes. The sorting order is determined by 
    /// Codec, then ColorPrimaries, then Resolution, then IsHdr10, then IsDolbyVision, then GlobalIndex, 
    /// then PixFmt, then ColorSpace, then ColorTransfer, and finally MasteringDisplay.
    /// </summary>
    /// <param name="other">The other VideoStreamInfo to compare to.</param>
    /// <returns>An integer that indicates the relative order of the objects being compared.</returns>
    public int CompareTo(VideoStreamInfo? other)
    {
        // Initialize the result variable to 0 (equal) and update it at each comparison step. If any comparison yields a non-zero result, that will be returned immediately.
        int tmpResult = 0;

        // Compare reference equality first for a quick path when both references point to the same object. If they are the same instance, they are considered equal, so return 0.
        if (Object.ReferenceEquals(this, other)) return 0;

        // Base check: if the other object is null, this instance is considered greater (i.e. comes first in sorting), so return -1.
        if (other == null || other is not VideoStreamInfo) return -1;

        // Compare Codec first (e.g. PGS vs SRT), then Language, then Title, then IsHearingImpaired
        // (SDH before non-SDH), then GlobalIndex (lower index first), and finally DisplayName as a tiebreaker.
        tmpResult = this.Codec.CompareTo(other.Codec);

        // We are equal so far
        if (tmpResult == 0)
        {
            // Check ColorPrimaries next (e.g. bt2020 before bt709)
            tmpResult = this.ColorPrimaries.CompareTo(other.ColorPrimaries);

            if (tmpResult == 0)
            {
                // Then compare Resolution (higher resolution first, e.g. 4K before 1080p).
                // We can compare the Resolution strings directly since they are in "WIDTHxHEIGHT" format, which will sort correctly as strings.
                tmpResult = this.Resolution.CompareTo(other.Resolution);
                if (tmpResult == 0)
                {
                    // Then compare IsHdr10 (HDR10 before non-HDR10)
                    tmpResult = this.IsHdr10.CompareTo(other.IsHdr10);

                    if (tmpResult == 0)
                    {
                        // Then compare IsDolbyVision (Dolby Vision before non-Dolby Vision)
                        tmpResult = this.IsDolbyVision.CompareTo(other.IsDolbyVision);

                        if (tmpResult == 0)
                        {
                            // Then compare GlobalIndex (lower index first)
                            tmpResult = this.GlobalIndex.CompareTo(other.GlobalIndex);

                            if (tmpResult == 0)
                            {
                                //  Finally, compare PixFmt, then ColorSpace, then ColorTransfer, and
                                //  finally MasteringDisplay as tiebreakers for streams that are otherwise
                                //  identical. This ensures a deterministic sort order even when all other
                                //  properties are the same.
                                tmpResult = this.PixFmt.CompareTo(other.PixFmt);

                                if (tmpResult == 0)
                                {
                                    // Compare ColorSpace next
                                    tmpResult = this.ColorSpace.CompareTo(other.ColorSpace);

                                    if (tmpResult == 0)
                                    {
                                        // Then compare ColorTransfer
                                        tmpResult = this.ColorTransfer.CompareTo(other.ColorTransfer);
                                        if (tmpResult == 0)
                                        {
                                            // Compare MasteringDisplay next, which is a complex type. We can use its CompareTo method for this.
                                            if (this.MasteringDisplay is not null && other.MasteringDisplay is not null)
                                            {
                                                tmpResult = this.MasteringDisplay.CompareTo(other.MasteringDisplay);
                                            }
                                            else if (this.MasteringDisplay is not null)
                                            {
                                                // If this has MasteringDisplay but the other doesn't, consider this greater (i.e. comes first in sorting).
                                                tmpResult = -1;
                                            }
                                            else if (other.MasteringDisplay is not null)
                                            {
                                                // If the other has MasteringDisplay but this doesn't, consider the other greater.
                                                tmpResult = 1;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Return the result of the first non-zero comparison, or 0 if all comparisons are equal.
        return tmpResult;
    }

    // Note: Records auto-generate Equals based on all properties/parameters.
    // The synthesized equality already uses value-based comparison.

    /// <summary>
    /// Hash code must be consistent with Equals. Since we are using the synthesized record equality,
    /// the hash code is generated based on all properties/parameters.
    /// </summary>
    /// <returns>The hash code for the current object.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(this.Codec, this.Width, this.Height, this.Resolution,
            this.ColorPrimaries, this.IsHdr10), this.Disposition, this.StreamDuration);
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
#region Enums
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Encoder detection result from VideoArgBuilder. The actual encoder selection logic is in VideoArgBuilder.Build(),
/// </summary>
public enum HwEncoder
{
    NvencHevc,      // NVIDIA NVENC  hevc_nvenc
    QsvHevc,        // Intel QSV     hevc_qsv
    SoftwareX265,   // CPU           libx265  (fallback)
    None
}

/// <summary>
/// Lossless audio format options for additional output tracks.
/// </summary>																			  
public enum LosslessFormat
{
    Flac,       // FLAC codec (default for lossless)
    Alac,       // ALAC codec (Apple Lossless)
    Pcm         // Uncompressed PCM (WAV)
}

#endregion
