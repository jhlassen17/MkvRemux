using System;
using System.Collections.Generic;
using System.Text;

namespace MkvRemux;

using System.Text.Json.Serialization;

/// <summary>
/// Maps the "disposition" object from ffprobe JSON stream output.
/// ffprobe represents all flags as integers: 1 = set, 0 = not set.
/// </summary>
public record VideoStreamDisposition(
    // ── Universal ──────────────────────────────────────────────────────────
    [property: JsonPropertyName("default")] int Default = 0,
    [property: JsonPropertyName("forced")] int Forced = 0,
    [property: JsonPropertyName("comment")] int Comment = 0,
    [property: JsonPropertyName("metadata")] int Metadata = 0,

    // ── Video-specific ─────────────────────────────────────────────────────
    [property: JsonPropertyName("attached_pic")] int AttachedPic = 0,  // cover art / thumbnail
    [property: JsonPropertyName("timed_thumbnails")] int TimedThumbnails = 0,  // DASH-style tiled thumbnails
    [property: JsonPropertyName("still_image")] int StillImage = 0,  // single-frame non-attached (ffmpeg 6.1+)
    [property: JsonPropertyName("dependent")] int Dependent = 0,  // dependent view (e.g. MVC right eye)
    [property: JsonPropertyName("non_diegetic")] int NonDiegetic = 0,  // out-of-scene overlay video/audio

    // ── Audio-specific ─────────────────────────────────────────────────────
    [property: JsonPropertyName("dub")] int Dub = 0,
    [property: JsonPropertyName("original")] int Original = 0,
    [property: JsonPropertyName("lyrics")] int Lyrics = 0,
    [property: JsonPropertyName("karaoke")] int Karaoke = 0,
    [property: JsonPropertyName("hearing_impaired")] int HearingImpaired = 0,
    [property: JsonPropertyName("visual_impaired")] int VisualImpaired = 0,
    [property: JsonPropertyName("clean_effects")] int CleanEffects = 0,
    [property: JsonPropertyName("descriptions")] int Descriptions = 0,

    // ── Subtitle-specific ──────────────────────────────────────────────────
    [property: JsonPropertyName("captions")] int Captions = 0
)
{
    // ── Convenience bool properties ────────────────────────────────────────

    [JsonIgnore] public bool IsDefault => Default != 0;
    [JsonIgnore] public bool IsForced => Forced != 0;
    [JsonIgnore] public bool IsComment => Comment != 0;
    [JsonIgnore] public bool IsMetadata => Metadata != 0;

    [JsonIgnore] public bool IsAttachedPic => AttachedPic != 0;
    [JsonIgnore] public bool IsTimedThumbnails => TimedThumbnails != 0;
    [JsonIgnore] public bool IsStillImage => StillImage != 0;
    [JsonIgnore] public bool IsDependent => Dependent != 0;
    [JsonIgnore] public bool IsNonDiegetic => NonDiegetic != 0;

    [JsonIgnore] public bool IsDub => Dub != 0;
    [JsonIgnore] public bool IsOriginal => Original != 0;
    [JsonIgnore] public bool IsLyrics => Lyrics != 0;
    [JsonIgnore] public bool IsKaraoke => Karaoke != 0;
    [JsonIgnore] public bool IsHearingImpaired => HearingImpaired != 0;
    [JsonIgnore] public bool IsVisualImpaired => VisualImpaired != 0;
    [JsonIgnore] public bool IsCleanEffects => CleanEffects != 0;
    [JsonIgnore] public bool IsDescriptions => Descriptions != 0;
    [JsonIgnore] public bool IsCaptions => Captions != 0;

    /// <summary>
    /// True when this video stream is a cover art or thumbnail image
    /// and should not be treated as a playable video track.
    /// </summary>
    [JsonIgnore] public bool IsImageStream => IsAttachedPic || IsStillImage || IsTimedThumbnails;

    // ── ffmpeg argument helpers ────────────────────────────────────────────

    /// <summary>
    /// Builds the value string for an ffmpeg -disposition:x:N argument,
    /// e.g. "default", "attached_pic", "default+forced", or "0" (clear all).
    /// </summary>
    public string ToFfmpegValue()
    {
        var flags = new List<string>(8);

        if (IsDefault) flags.Add("default");
        if (IsForced) flags.Add("forced");
        if (IsComment) flags.Add("comment");
        if (IsMetadata) flags.Add("metadata");
        if (IsAttachedPic) flags.Add("attached_pic");
        if (IsTimedThumbnails) flags.Add("timed_thumbnails");
        if (IsStillImage) flags.Add("still_image");
        if (IsDependent) flags.Add("dependent");
        if (IsNonDiegetic) flags.Add("non_diegetic");
        if (IsDub) flags.Add("dub");
        if (IsOriginal) flags.Add("original");
        if (IsLyrics) flags.Add("lyrics");
        if (IsKaraoke) flags.Add("karaoke");
        if (IsHearingImpaired) flags.Add("hearing_impaired");
        if (IsVisualImpaired) flags.Add("visual_impaired");
        if (IsCleanEffects) flags.Add("clean_effects");
        if (IsDescriptions) flags.Add("descriptions");
        if (IsCaptions) flags.Add("captions");

        return flags.Count > 0 ? string.Join("+", flags) : "0";
    }

    /// <summary>
    /// Returns a disposition with all flags cleared (emits "0" as ffmpeg value).
    /// </summary>
    public static VideoStreamDisposition None => new();
}
