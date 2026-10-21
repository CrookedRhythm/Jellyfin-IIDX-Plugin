using IIDX.Core.Formats;
using IIDX.Core.Library;
using Jellyfin.Plugin.IIDX.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IIDX;

/// <summary>Owns the <see cref="IidxLibrary"/> and rebuilds it when the plugin settings change.</summary>
public sealed class IidxLibraryService
{
    private readonly IServerApplicationPaths _paths;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _lock = new();
    private IidxLibrary? _library;
    private string? _key;

    public IidxLibraryService(IServerApplicationPaths paths, IMediaEncoder mediaEncoder, ILoggerFactory loggerFactory)
    {
        _paths = paths;
        _mediaEncoder = mediaEncoder;
        _loggerFactory = loggerFactory;
    }

    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public string StatePath => Path.Combine(_paths.DataPath, "iidx");

    public string LibraryPath => string.IsNullOrWhiteSpace(Config.LibraryPath) ? Path.Combine(StatePath, "library") : Config.LibraryPath;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Config.GamePath);

    /// <summary>Gets the library for the current settings, or null while no game folder is configured.</summary>
    public IidxLibrary? Library
    {
        get
        {
            if (!IsConfigured)
            {
                return null;
            }

            var settings = BuildSettings(Config);
            string key = System.Text.Json.JsonSerializer.Serialize(new { settings.GamePath, settings.MusicDataPath, settings.LibraryPath, settings.FFmpegPath, f = settings.RenderSettingsFingerprint, settings.CacheTtl, settings.CacheMaxBytes, settings.RatingChart, settings.MaxParallel });
            lock (_lock)
            {
                if (_library == null || key != _key)
                {
                    _library = new IidxLibrary(settings, _loggerFactory.CreateLogger<IidxLibrary>());
                    _key = key;
                }

                return _library;
            }
        }
    }

    private IidxSettings BuildSettings(PluginConfiguration c)
    {
        var prefs = c.ChartPreference.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseChart)
            .OfType<Difficulty>()
            .ToList();

        return new IidxSettings
        {
            GamePath = c.GamePath.Trim(),
            MusicDataPath = string.IsNullOrWhiteSpace(c.MusicDataPath) ? null : c.MusicDataPath.Trim(),
            LibraryPath = LibraryPath,
            StatePath = StatePath,
            FFmpegPath = string.IsNullOrEmpty(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath,
            FFprobePath = string.IsNullOrEmpty(_mediaEncoder.ProbePath) ? null : _mediaEncoder.ProbePath,
            ChartPreference = prefs.Count > 0 ? prefs : IidxSettings.DefaultChartPreference,
            AudioCodec = string.Equals(c.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase) ? "aac" : "flac",
            AudioBitrateKbps = Math.Clamp(c.AudioBitrateKbps, 96, 512),
            CopyVideoWhenPossible = c.CopyVideoWhenPossible,
            VideoCrf = Math.Clamp(c.VideoCrf, 0, 51),
            LoopShortMovies = c.LoopShortMovies,
            BgaDelayUnit = string.Equals(c.BgaDelayUnit, "frames", StringComparison.OrdinalIgnoreCase) ? BgaDelayUnit.Frames60 : BgaDelayUnit.Milliseconds,
            RatingChart = c.RatingChart,
            Render = new global::IIDX.Core.Audio.RenderOptions { IncludeBgm = c.IncludeBgm, CutRetriggeredSamples = c.CutRetriggeredSamples },
            MaxParallel = Math.Clamp(c.IndexParallelism, 1, 16),
            CacheTtl = TimeSpan.FromMinutes(Math.Max(5, c.CacheTtlMinutes)),
            CacheMaxBytes = Math.Max(0, c.CacheMaxMegabytes) * 1024L * 1024L,
        };
    }

    private static Difficulty? ParseChart(string s) => s.ToUpperInvariant() switch
    {
        "SPB" => Difficulty.SpBeginner,
        "SPN" => Difficulty.SpNormal,
        "SPH" => Difficulty.SpHyper,
        "SPA" => Difficulty.SpAnother,
        "SPL" => Difficulty.SpLeggendaria,
        "DPB" => Difficulty.DpBeginner,
        "DPN" => Difficulty.DpNormal,
        "DPH" => Difficulty.DpHyper,
        "DPA" => Difficulty.DpAnother,
        "DPL" => Difficulty.DpLeggendaria,
        _ => null,
    };
}
