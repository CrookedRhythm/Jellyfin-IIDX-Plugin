using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IIDX.Core.Formats;
using IIDX.Core.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IIDX.Core.Library;

/// <summary>What the library remembers about one song between runs.</summary>
public sealed class SongState
{
    public int SongId { get; set; }

    public string MediaPath { get; set; } = string.Empty;

    public string SourceFingerprint { get; set; } = string.Empty;

    public double BpmMin { get; set; }

    public double BpmMax { get; set; }

    public Difficulty Chart { get; set; }

    public int NoteCount { get; set; }

    public double EstimatedSeconds { get; set; }

    public bool HasMovie { get; set; }

    public string? ThumbnailFingerprint { get; set; }

    public string? MovieFingerprint { get; set; }

    public string? MovieCodec { get; set; }

    /// <summary>Gets or sets the fingerprint of the rendered media, or null while the placeholder is in place.</summary>
    public string? RenderFingerprint { get; set; }

    public DateTime? LastAccessUtc { get; set; }

    public long RenderedBytes { get; set; }

    public int PlaceholderVersion { get; set; }
}

/// <summary>What <see cref="IidxLibrary.InspectAsync"/> found. Unreadable lists the songs whose data could not be read.</summary>
public sealed record InspectReport(MusicDatabase Db, string MusicDataPath, int WithCharts, int WithMovies, IReadOnlyList<string> Unreadable);

public sealed record IndexReport(int Total, int Indexed, int Skipped, int Removed, IReadOnlyList<string> Failures, TimeSpan Elapsed)
{
    public override string ToString() =>
        $"{Total} songs: {Indexed} indexed, {Skipped} without chart data, {Removed} removed, {Failures.Count} failed in {Elapsed.TotalSeconds:F0}s";
}

public sealed record CacheStatus(int Songs, int Rendered, long RenderedBytes);

/// <summary>
/// The IIDX library on disk. Indexing writes one folder per song with metadata, artwork and a tiny
/// placeholder video, without converting any game data. When a song is played, the placeholder is
/// swapped for a full render made straight from the game files; renders expire back to placeholders
/// after <see cref="IidxSettings.CacheTtl"/>.
/// </summary>
public sealed partial class IidxLibrary
{
    /// <summary>Bump when rendering changes so existing renders are redone.</summary>
    public const int RenderVersion = 1;

    /// <summary>Bump when the placeholder format changes so placeholders are rewritten.</summary>
    public const int PlaceholderVersion = 2;

    private readonly IidxSettings _settings;
    private readonly ILogger _logger;
    private readonly FFmpeg _ffmpeg;
    private readonly SongRenderer _renderer;
    private readonly ConcurrentDictionary<int, SongState> _states = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private Catalog? _catalog;

    public IidxLibrary(IidxSettings settings, ILogger? logger = null)
    {
        _settings = settings;
        _logger = logger ?? NullLogger.Instance;
        _ffmpeg = new FFmpeg(settings.FFmpegPath, settings.FFprobePath);
        _renderer = new SongRenderer(settings, _ffmpeg, _logger);
        LoadStates();
    }

    public IidxSettings Settings => _settings;

    private string StateDir => Path.Combine(_settings.StatePath, "songs");

    private string WorkDir => Path.Combine(_settings.StatePath, "work");

    /// <summary>Gets the song id encoded in a library media file name ("01234 Title.mp4").</summary>
    public static bool TryGetSongId(string path, out int songId)
    {
        var m = SongIdPattern().Match(Path.GetFileName(path));
        songId = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return m.Success;
    }

    public static string SafeFileName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length > 80 ? s[..80].Trim() : s;
    }

    public static string FolderName(SongEntry s)
    {
        var title = string.IsNullOrWhiteSpace(s.TitleAscii) ? s.Title : s.TitleAscii;
        return SafeFileName($"{s.FileId} {title}");
    }

    public bool IsInLibrary(string? path) =>
        !string.IsNullOrEmpty(path)
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(_settings.LibraryPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public CacheStatus GetStatus()
    {
        var rendered = _states.Values.Where(s => s.RenderFingerprint != null).ToList();
        return new CacheStatus(_states.Count, rendered.Count, rendered.Sum(s => s.RenderedBytes));
    }

    /// <summary>Loads the music database and reports what it contains, without writing anything.</summary>
    public async Task<InspectReport> InspectAsync(CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(true, ct).ConfigureAwait(false);
        int charts = 0, movies = 0;
        var unreadable = new List<string>();
        foreach (var song in catalog.Songs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (catalog.Locator.FindSong(song) != null)
                {
                    charts++;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Cannot read the data of IIDX song {Id}: {Message}", song.SongId, ex.Message);
                unreadable.Add(ex.Message);
            }

            if (catalog.Locator.FindMovie(song) != null)
            {
                movies++;
            }
        }

        return new InspectReport(catalog.Db, catalog.Locator.MusicDataPath, charts, movies, unreadable);
    }

    /// <summary>Writes or refreshes every song folder. Game data is only read.</summary>
    public async Task<IndexReport> IndexAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var catalog = await GetCatalogAsync(true, ct).ConfigureAwait(false);
        Directory.CreateDirectory(_settings.LibraryPath);
        Directory.CreateDirectory(StateDir);

        var songs = catalog.Songs.Where(s => _settings.OnlySongIds.Count == 0 || _settings.OnlySongIds.Contains(s.SongId)).ToList();
        int done = 0, indexed = 0, skipped = 0;
        var failures = new ConcurrentBag<string>();
        var present = new ConcurrentDictionary<int, bool>();

        await Parallel.ForEachAsync(
            songs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _settings.MaxParallel), CancellationToken = ct },
            async (song, token) =>
            {
                var gate = _locks.GetOrAdd(song.SongId, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (await IndexSongAsync(catalog, song, token).ConfigureAwait(false))
                    {
                        present[song.SongId] = true;
                        Interlocked.Increment(ref indexed);
                    }
                    else
                    {
                        Interlocked.Increment(ref skipped);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to index IIDX song {Id} {Title}", song.SongId, song.TitleAscii);
                    failures.Add($"{song.SongId} {song.TitleAscii}: {ex.Message}");
                    present[song.SongId] = true;
                }
                finally
                {
                    gate.Release();
                    progress?.Report(100.0 * Interlocked.Increment(ref done) / Math.Max(1, songs.Count));
                }
            }).ConfigureAwait(false);

        int removed = _settings.OnlySongIds.Count == 0 ? RemoveOrphans(present) : 0;
        TryDelete(WorkDir);
        var report = new IndexReport(songs.Count, indexed, skipped, removed, failures.ToList(), sw.Elapsed);
        _logger.LogInformation("IIDX library index finished: {Report}", report);
        return report;
    }

    /// <summary>
    /// Makes sure the full render of a song is in the library, rendering it if needed.
    /// Returns true when a new render was written.
    /// </summary>
    public async Task<bool> EnsureRenderedAsync(int songId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(songId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var catalog = await GetCatalogAsync(false, ct).ConfigureAwait(false);
            var song = catalog.Songs.FirstOrDefault(s => s.SongId == songId)
                ?? throw new KeyNotFoundException($"Song {songId} is not in the music database");
            var source = catalog.Locator.FindSong(song) ?? throw new FileNotFoundException($"No chart data for song {songId}");
            var movie = catalog.Locator.FindMovie(song);
            string fingerprint = RenderFingerprint(song, source, movie);

            if (!_states.TryGetValue(songId, out var state) || !File.Exists(state.MediaPath) || state.MovieFingerprint != (movie?.Fingerprint ?? "-"))
            {
                await IndexSongAsync(catalog, song, ct).ConfigureAwait(false);
                state = _states[songId];
            }

            if (state.RenderFingerprint == fingerprint && File.Exists(state.MediaPath))
            {
                state.LastAccessUtc = DateTime.UtcNow;
                SaveState(state);
                return false;
            }

            var work = Path.Combine(WorkDir, $"{song.FileId}-{Guid.NewGuid():N}");
            try
            {
                var tmp = Path.Combine(work, "render" + Path.GetExtension(state.MediaPath));
                var summary = await _renderer.RenderAsync(song, source, movie, work, tmp, ct).ConfigureAwait(false);
                File.Move(tmp, state.MediaPath, true);
                state.RenderFingerprint = fingerprint;
                state.RenderedBytes = new FileInfo(state.MediaPath).Length;
                state.LastAccessUtc = DateTime.UtcNow;
                SaveState(state);
                _logger.LogInformation(
                    "Rendered IIDX song {Id} {Title}: {Duration:F0}s from {Chart}, audio {Audio:F1}s, mux {Mux:F1}s{Copy}",
                    song.SongId,
                    song.TitleAscii,
                    summary.DurationSeconds,
                    summary.Chart,
                    summary.AudioTime.TotalSeconds,
                    summary.MuxTime.TotalSeconds,
                    summary.VideoCopied ? " (video copied)" : string.Empty);
                await EnforceSizeLimitAsync(songId, ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                TryDelete(work);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Marks a song as recently used so the cache keeps it.</summary>
    public void Touch(int songId)
    {
        if (_states.TryGetValue(songId, out var s) && s.RenderFingerprint != null)
        {
            s.LastAccessUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Puts placeholders back for renders not used within the TTL. Returns how many expired.</summary>
    public async Task<int> ExpireAsync(DateTime nowUtc, CancellationToken ct)
    {
        int expired = 0;
        foreach (var state in _states.Values.Where(s => s.RenderFingerprint != null && (s.LastAccessUtc ?? DateTime.MinValue) + _settings.CacheTtl < nowUtc).ToList())
        {
            if (await RevertAsync(state.SongId, ct).ConfigureAwait(false))
            {
                expired++;
            }
        }

        return expired;
    }

    /// <summary>Drops every render back to its placeholder.</summary>
    public async Task<int> ClearCacheAsync(CancellationToken ct)
    {
        int n = 0;
        foreach (var state in _states.Values.Where(s => s.RenderFingerprint != null).ToList())
        {
            if (await RevertAsync(state.SongId, ct).ConfigureAwait(false))
            {
                n++;
            }
        }

        return n;
    }

    private async Task EnforceSizeLimitAsync(int keep, CancellationToken ct)
    {
        if (_settings.CacheMaxBytes <= 0)
        {
            return;
        }

        var rendered = _states.Values.Where(s => s.RenderFingerprint != null).OrderBy(s => s.LastAccessUtc ?? DateTime.MinValue).ToList();
        long total = rendered.Sum(s => s.RenderedBytes);
        foreach (var s in rendered)
        {
            if (total <= _settings.CacheMaxBytes)
            {
                break;
            }

            if (s.SongId == keep)
            {
                continue;
            }

            // The caller already holds the lock of `keep`; other songs are locked inside RevertAsync.
            long bytes = s.RenderedBytes;
            if (await RevertAsync(s.SongId, ct).ConfigureAwait(false))
            {
                total -= bytes;
            }
        }
    }

    private async Task<bool> RevertAsync(int songId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(songId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return false; // busy rendering or being indexed; try again next sweep
        }

        try
        {
            if (!_states.TryGetValue(songId, out var state) || state.RenderFingerprint == null)
            {
                return false;
            }

            await WritePlaceholderAsync(state, ct).ConfigureAwait(false);
            state.RenderFingerprint = null;
            state.RenderedBytes = 0;
            SaveState(state);
            _logger.LogDebug("Expired render of IIDX song {Id}", songId);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> IndexSongAsync(Catalog catalog, SongEntry song, CancellationToken ct)
    {
        var source = catalog.Locator.FindSong(song);
        if (source == null)
        {
            return false;
        }

        var movie = catalog.Locator.FindMovie(song);
        _states.TryGetValue(song.SongId, out var state);
        state ??= new SongState { SongId = song.SongId };
        string movieFp = movie?.Fingerprint ?? "-";
        if (state.MovieFingerprint != movieFp)
        {
            state.MovieCodec = movie == null ? null : await _ffmpeg.ProbeVideoCodecAsync(movie.FFmpegInput, ct).ConfigureAwait(false);
            state.MovieFingerprint = movieFp;
        }

        string folderName = FolderName(song);
        string folder = Path.Combine(_settings.LibraryPath, folderName);
        string media = Path.Combine(folder, folderName + _settings.MediaExtensionFor(movie != null, state.MovieCodec));
        Directory.CreateDirectory(folder);

        // A renamed song (new title) or a codec change moves the media file; drop the old one.
        if (!string.IsNullOrEmpty(state.MediaPath) && state.MediaPath != media)
        {
            TryDeleteFile(state.MediaPath);
            var oldFolder = Path.GetDirectoryName(state.MediaPath);
            if (oldFolder != null && oldFolder != folder && Directory.Exists(oldFolder))
            {
                TryDelete(oldFolder);
            }

            state.RenderFingerprint = null;
        }

        state.MediaPath = media;
        state.HasMovie = movie != null;
        string sourceFp = source.Fingerprint;
        if (state.SourceFingerprint != sourceFp || state.NoteCount == 0)
        {
            var chart = IidxChart.Parse(source.Load().Chart).Pick(_settings.ChartPreference, out var chosen);
            if (chart == null)
            {
                return false;
            }

            state.BpmMin = chart.BpmMin;
            state.BpmMax = chart.BpmMax;
            state.Chart = chosen;
            state.NoteCount = chart.NoteCount;
            state.EstimatedSeconds = (chart.EndTime / 1000.0) + 2;
            state.SourceFingerprint = sourceFp;
        }

        bool rendered = state.RenderFingerprint != null && state.RenderFingerprint == RenderFingerprint(song, source, movie) && File.Exists(media);
        if (!rendered && (state.RenderFingerprint != null || !File.Exists(media) || state.PlaceholderVersion != PlaceholderVersion))
        {
            await WritePlaceholderAsync(state, ct).ConfigureAwait(false);
            state.RenderFingerprint = null;
            state.RenderedBytes = 0;
        }

        if (_settings.WriteThumbnails && movie != null)
        {
            string thumb = Path.Combine(folder, folderName + "-thumb.jpg");
            if (state.ThumbnailFingerprint != movie.Fingerprint || !File.Exists(thumb))
            {
                if (await _renderer.WriteThumbnailAsync(movie, thumb, ct).ConfigureAwait(false))
                {
                    File.Copy(thumb, Path.Combine(folder, "folder.jpg"), true);
                    state.ThumbnailFingerprint = movie.Fingerprint;
                }
            }
        }

        var info = new SongInfo(song, state.BpmMin, state.BpmMax, state.Chart, state.NoteCount, state.HasMovie);
        WriteIfChanged(Path.Combine(folder, folderName + ".nfo"), NfoWriter.Build(info, _settings.RatingChart));
        _states[song.SongId] = state;
        SaveState(state);
        return true;
    }

    private async Task WritePlaceholderAsync(SongState state, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);
        var tmp = Path.Combine(WorkDir, $"{state.SongId:D5}-{Guid.NewGuid():N}-placeholder{Path.GetExtension(state.MediaPath)}");
        try
        {
            await _renderer.WritePlaceholderAsync(tmp, state.EstimatedSeconds, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(state.MediaPath)!);
            File.Move(tmp, state.MediaPath, true);
            state.PlaceholderVersion = PlaceholderVersion;
        }
        finally
        {
            TryDeleteFile(tmp);
        }
    }

    private int RemoveOrphans(ConcurrentDictionary<int, bool> present)
    {
        int removed = 0;
        foreach (var state in _states.Values.Where(s => !present.ContainsKey(s.SongId)).ToList())
        {
            var folder = Path.GetDirectoryName(state.MediaPath);
            if (folder != null && IsInLibrary(state.MediaPath))
            {
                TryDelete(folder);
            }

            TryDeleteFile(Path.Combine(StateDir, $"{state.SongId}.json"));
            _states.TryRemove(state.SongId, out _);
            removed++;
        }

        return removed;
    }

    private string RenderFingerprint(SongEntry song, SongSource source, MovieRef? movie) => string.Join(
        "|",
        RenderVersion,
        source.Fingerprint,
        movie?.Fingerprint ?? "-",
        song.BgaDelay,
        _settings.RenderSettingsFingerprint);

    private async Task<Catalog> GetCatalogAsync(bool reload, CancellationToken ct)
    {
        await _catalogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalog == null || reload)
            {
                var locator = new GameDataLocator(_settings.GamePath, _settings.MusicDataPath);
                var db = MusicDatabase.Load(locator.MusicDataPath);
                if (db.EntrySize != db.KnownEntrySize)
                {
                    _logger.LogWarning(
                        "music_data.bin v{Version} entries are {Size} bytes; this plugin knows {Known}. Reading the known fields only.",
                        db.DataVersion,
                        db.EntrySize,
                        db.KnownEntrySize);
                }

                var songs = db.Songs.Where(s => !s.IsPlaceholder).GroupBy(s => s.SongId).Select(g => g.First()).ToList();
                _logger.LogInformation("Loaded {Path}: data version {Version}, {Count} songs", locator.MusicDataPath, db.DataVersion, songs.Count);
                _catalog = new Catalog(db, locator, songs);
            }

            return _catalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private void LoadStates()
    {
        if (!Directory.Exists(StateDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(StateDir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<SongState>(File.ReadAllText(file));
                if (s != null)
                {
                    _states[s.SongId] = s;
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private void SaveState(SongState state)
    {
        Directory.CreateDirectory(StateDir);
        var path = Path.Combine(StateDir, $"{state.SongId}.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, path, true);
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^(\d{5}) ")]
    private static partial Regex SongIdPattern();

    private sealed record Catalog(MusicDatabase Db, GameDataLocator Locator, List<SongEntry> Songs);
}
