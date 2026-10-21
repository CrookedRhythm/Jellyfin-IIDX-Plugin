using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.IIDX.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the game's <c>contents</c> folder as seen by the Jellyfin server (inside the container).</summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional explicit music_data.bin; empty finds it under data/info.</summary>
    public string MusicDataPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder the library lives in; empty uses &lt;jellyfin data&gt;/iidx/library.</summary>
    public string LibraryPath { get; set; } = string.Empty;

    public string LibraryName { get; set; } = "beatmania IIDX";

    public bool CreateLibrary { get; set; } = true;

    /// <summary>Gets or sets the chart order for rendering, as short names (SPA, SPH, ...).</summary>
    public string ChartPreference { get; set; } = "SPA,SPH,SPN,SPL,SPB,DPA,DPH,DPN,DPL,DPB";

    /// <summary>Gets or sets the render audio codec: "flac" (lossless, fastest to start) or "aac".</summary>
    public string AudioCodec { get; set; } = "flac";

    public int AudioBitrateKbps { get; set; } = 320;

    public bool CopyVideoWhenPossible { get; set; } = true;

    public int VideoCrf { get; set; } = 20;

    public bool LoopShortMovies { get; set; } = true;

    /// <summary>Gets or sets how bga_delay is read: "ms" or "frames".</summary>
    public string BgaDelayUnit { get; set; } = "ms";

    public bool IncludeBgm { get; set; } = true;

    public bool CutRetriggeredSamples { get; set; } = true;

    /// <summary>Gets or sets which chart level becomes the item rating (SPA, SPH, MAXSP, MAXDP, NONE...).</summary>
    public string RatingChart { get; set; } = "SPA";

    /// <summary>Gets or sets how long a render is kept after it was last played, in minutes.</summary>
    public int CacheTtlMinutes { get; set; } = 360;

    /// <summary>Gets or sets the space renders may use together, in MB; 0 means no limit.</summary>
    public int CacheMaxMegabytes { get; set; } = 4096;

    public int IndexParallelism { get; set; } = 4;
}
