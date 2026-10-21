using IIDX.Core.Audio;
using IIDX.Core.Formats;

namespace IIDX.Core.Library;

public enum BgaDelayUnit
{
    Milliseconds,
    Frames60,
}

/// <summary>Everything the indexer and renderer need to know, independent of Jellyfin.</summary>
public sealed class IidxSettings
{
    /// <summary>Gets the game's <c>contents</c> (or <c>contents/data</c>) folder. It is only ever read.</summary>
    public required string GamePath { get; init; }

    public string? MusicDataPath { get; init; }

    /// <summary>Gets the folder Jellyfin scans: one small folder per song with NFO, thumbnails and the media file.</summary>
    public required string LibraryPath { get; init; }

    /// <summary>Gets the folder for per-song state and temporary work files.</summary>
    public required string StatePath { get; init; }

    public required string FFmpegPath { get; init; }

    public string? FFprobePath { get; init; }

    public static IReadOnlyList<Difficulty> DefaultChartPreference { get; } =
    [
        Difficulty.SpAnother, Difficulty.SpHyper, Difficulty.SpNormal, Difficulty.SpLeggendaria, Difficulty.SpBeginner,
        Difficulty.DpAnother, Difficulty.DpHyper, Difficulty.DpNormal, Difficulty.DpLeggendaria, Difficulty.DpBeginner,
    ];

    /// <summary>Gets the charts to try, in order, when picking the one to render.</summary>
    public IReadOnlyList<Difficulty> ChartPreference { get; init; } = DefaultChartPreference;

    /// <summary>
    /// Gets the audio codec of renders: "flac" (default: lossless and ~8x faster to encode, so playback
    /// starts sooner; Jellyfin converts it on the fly for clients without FLAC) or "aac".
    /// </summary>
    public string AudioCodec { get; init; } = "flac";

    public int AudioBitrateKbps { get; init; } = 320;

    /// <summary>Gets a value indicating whether H.264/HEVC movies are copied without re-encoding.</summary>
    public bool CopyVideoWhenPossible { get; init; } = true;

    public int VideoCrf { get; init; } = 20;

    public string VideoPreset { get; init; } = "veryfast";

    public bool LoopShortMovies { get; init; } = true;

    public BgaDelayUnit BgaDelayUnit { get; init; } = BgaDelayUnit.Milliseconds;

    /// <summary>Gets which chart level becomes the Jellyfin rating: SPA, SPH, MAXSP, MAXDP, NONE, ...</summary>
    public string RatingChart { get; init; } = "SPA";

    public RenderOptions Render { get; init; } = new();

    /// <summary>Gets how many songs the indexer processes at once.</summary>
    public int MaxParallel { get; init; } = 4;

    public bool WriteThumbnails { get; init; } = true;

    /// <summary>Gets how long a rendered song is kept after it was last played.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Gets the most disk space rendered songs may use together; 0 means no limit.</summary>
    public long CacheMaxBytes { get; init; }

    /// <summary>Gets an optional set of song ids to index; empty means every song.</summary>
    public IReadOnlySet<int> OnlySongIds { get; init; } = new HashSet<int>();

    /// <summary>Gets a value indicating whether a video codec can be copied into MP4 for broad direct play.</summary>
    public static bool IsMp4VideoCodec(string? codec) => codec is "h264" or "hevc" or "av1";

    /// <summary>
    /// Gets the container for a song: MP4 normally; MKV when the movie's codec (e.g. WMV) cannot go in
    /// MP4 and copying is enabled, so the render is instant and Jellyfin converts while streaming.
    /// </summary>
    public string MediaExtensionFor(bool hasMovie, string? movieCodec) =>
        hasMovie && CopyVideoWhenPossible && movieCodec != null && !IsMp4VideoCodec(movieCodec) ? ".mkv" : ".mp4";

    /// <summary>Gets a string that changes whenever a setting that affects rendered output changes.</summary>
    public string RenderSettingsFingerprint => string.Join(
        ",",
        AudioCodec,
        AudioBitrateKbps,
        CopyVideoWhenPossible,
        VideoCrf,
        VideoPreset,
        LoopShortMovies,
        BgaDelayUnit,
        string.Join("/", ChartPreference),
        Render.IncludeBgm,
        Render.CutRetriggeredSamples,
        Render.Gain);
}
