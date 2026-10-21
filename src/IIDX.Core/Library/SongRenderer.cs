using System.Diagnostics;
using System.Globalization;
using IIDX.Core.Audio;
using IIDX.Core.Formats;
using IIDX.Core.Media;
using Microsoft.Extensions.Logging;

namespace IIDX.Core.Library;

public sealed record RenderSummary(Difficulty Chart, double DurationSeconds, int Voices, TimeSpan AudioTime, TimeSpan MuxTime, bool VideoCopied);

/// <summary>Renders one song: chart + keysounds to audio, then muxes it over the background movie.</summary>
public sealed class SongRenderer
{
    private readonly IidxSettings _settings;
    private readonly FFmpeg _ffmpeg;
    private readonly ILogger _logger;

    public SongRenderer(IidxSettings settings, FFmpeg ffmpeg, ILogger logger)
    {
        _settings = settings;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>Renders the song to <paramref name="output"/>, working in <paramref name="workDir"/>.</summary>
    public async Task<RenderSummary> RenderAsync(SongEntry song, SongSource source, MovieRef? movie, string workDir, string output, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(workDir);
        var (chartBytes, keysoundBytes) = source.Load();
        var chart = IidxChart.Parse(chartBytes).Pick(_settings.ChartPreference, out var chosen)
            ?? throw new InvalidDataException($"{song.SongId} has no playable chart");

        var archive = KeysoundArchive.Parse(keysoundBytes);
        var clips = await KeysoundDecoder.DecodeAllAsync(archive, _ffmpeg, Path.Combine(workDir, "keys"), ct).ConfigureAwait(false);
        var render = ChartRenderer.Render(chart, clips, _settings.Render);
        var wav = Path.Combine(workDir, "audio.wav");
        ChartRenderer.WriteWav(wav, render.Samples);
        var audioTime = sw.Elapsed;

        bool copied = await MuxAsync(wav, render.DurationSeconds, movie, song, output, ct).ConfigureAwait(false);
        File.Delete(wav);
        return new RenderSummary(chosen, render.DurationSeconds, render.TriggerCount, audioTime, sw.Elapsed - audioTime, copied);
    }

    /// <summary>
    /// Writes the stand-in file Jellyfin indexes until the song is played: silent FLAC audio for the
    /// song's length (~30 KB for 3 minutes) and deliberately no video stream. Jellyfin re-probes a video
    /// item without a video stream right before playback, which is when the real render is swapped in,
    /// so players always get the render's actual stream details.
    /// </summary>
    public Task WritePlaceholderAsync(string output, double durationSeconds, CancellationToken ct)
    {
        string dur = Math.Max(1, durationSeconds).ToString("F3", CultureInfo.InvariantCulture);
        return _ffmpeg.RunAsync(
            [
                "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
                "-c:a", "flac",
                "-t", dur,
                "-metadata", "comment=iidx-placeholder",
                "-f", string.Equals(Path.GetExtension(output), ".mkv", StringComparison.OrdinalIgnoreCase) ? "matroska" : "mp4",
                output,
            ],
            ct);
    }

    /// <summary>Grabs a frame of the background movie for the library artwork.</summary>
    public async Task<bool> WriteThumbnailAsync(MovieRef movie, string thumb, CancellationToken ct)
    {
        var duration = await _ffmpeg.ProbeDurationAsync(movie.FFmpegInput, ct).ConfigureAwait(false) ?? 0;
        double at = duration > 0 ? Math.Min(30, duration / 3) : 0;
        try
        {
            await _ffmpeg.RunAsync(
                ["-ss", at.ToString("F2", CultureInfo.InvariantCulture), "-i", movie.FFmpegInput, "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-q:v", "3", thumb],
                ct).ConfigureAwait(false);
            return File.Exists(thumb);
        }
        catch (FFmpegException ex)
        {
            _logger.LogDebug("Thumbnail failed for {Movie}: {Message}", movie.FFmpegInput, ex.Message);
            return false;
        }
    }

    private async Task<bool> MuxAsync(string wav, double duration, MovieRef? movie, SongEntry song, string output, CancellationToken ct)
    {
        string dur = duration.ToString("F3", CultureInfo.InvariantCulture);
        List<string> audioArgs = _settings.AudioCodec == "flac" ? ["-c:a", "flac", "-compression_level", "1"] : ["-c:a", "aac", "-b:a", $"{_settings.AudioBitrateKbps}k"];
        bool mkv = string.Equals(Path.GetExtension(output), ".mkv", StringComparison.OrdinalIgnoreCase);
        List<string> container = mkv ? ["-f", "matroska"] : ["-f", "mp4", "-movflags", "+faststart"];
        List<string> meta =
        [
            "-metadata", $"title={(string.IsNullOrWhiteSpace(song.Title) ? song.TitleAscii : song.Title)}",
            "-metadata", $"artist={song.Artist}",
            "-metadata", $"album={GameVersions.DisplayName(song.GameVersion)}",
            "-metadata", $"genre={song.Genre}",
        ];
        List<string> encode = ["-c:v", "libx264", "-preset", _settings.VideoPreset, "-crf", _settings.VideoCrf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"];

        if (movie == null)
        {
            await _ffmpeg.RunAsync(
                ["-f", "lavfi", "-i", "color=c=black:s=1280x720:r=1", "-i", wav, "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "stillimage", "-pix_fmt", "yuv420p", .. audioArgs, "-t", dur, .. meta, .. container, output],
                ct).ConfigureAwait(false);
            return false;
        }

        string input = movie.FFmpegInput;
        double delay = _settings.BgaDelayUnit == BgaDelayUnit.Frames60 ? song.BgaDelay / 60.0 : song.BgaDelay / 1000.0;
        var movieInput = new List<string>();
        if (_settings.LoopShortMovies)
        {
            var movieDuration = await _ffmpeg.ProbeDurationAsync(input, ct).ConfigureAwait(false);
            if (movieDuration is double md && md + 0.5 < duration + Math.Max(0, -delay))
            {
                movieInput.AddRange(["-stream_loop", "-1"]);
            }
        }

        // Negative delay: the movie starts before the music, so skip into it. Positive: hold it back.
        if (delay < 0)
        {
            movieInput.AddRange(["-ss", (-delay).ToString("F3", CultureInfo.InvariantCulture)]);
        }
        else if (delay > 0)
        {
            movieInput.AddRange(["-itsoffset", delay.ToString("F3", CultureInfo.InvariantCulture)]);
        }

        movieInput.AddRange(["-i", input]);
        List<string> common = ["-i", wav, "-map", "0:v:0", "-map", "1:a:0", "-t", dur];

        if (_settings.CopyVideoWhenPossible)
        {
            var codec = await _ffmpeg.ProbeVideoCodecAsync(input, ct).ConfigureAwait(false);
            if (codec != null && (mkv || IidxSettings.IsMp4VideoCodec(codec)))
            {
                try
                {
                    await _ffmpeg.RunAsync([.. movieInput, .. common, "-c:v", "copy", .. audioArgs, .. meta, .. container, output], ct).ConfigureAwait(false);
                    return true;
                }
                catch (FFmpegException ex)
                {
                    _logger.LogDebug("Stream copy failed for {Movie}, re-encoding: {Message}", input, ex.Message);
                }
            }
        }

        await _ffmpeg.RunAsync([.. movieInput, .. common, .. encode, .. audioArgs, .. meta, .. container, output], ct).ConfigureAwait(false);
        return false;
    }
}
