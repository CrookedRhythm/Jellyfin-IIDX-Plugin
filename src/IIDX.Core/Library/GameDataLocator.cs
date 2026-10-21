using IIDX.Core.Formats;

namespace IIDX.Core.Library;

/// <summary>Where a song's chart and keysounds live: loose files, or entries inside a per-song .ifs.</summary>
public sealed class SongSource
{
    public required string ChartName { get; init; }

    public required string KeysoundName { get; init; }

    /// <summary>Gets the .ifs archive holding the files, or null when they are loose files on disk.</summary>
    public string? IfsPath { get; init; }

    public string? ChartPath { get; init; }

    public string? KeysoundPath { get; init; }

    /// <summary>Gets a string that changes whenever the source files change, used to skip unchanged songs.</summary>
    public string Fingerprint
    {
        get
        {
            static string F(string? p) => p != null && File.Exists(p) ? $"{new FileInfo(p).Length}:{File.GetLastWriteTimeUtc(p).Ticks}" : "-";
            return IfsPath != null ? $"ifs:{F(IfsPath)}" : $"{F(ChartPath)}|{F(KeysoundPath)}";
        }
    }

    public (byte[] Chart, byte[] Keysounds) Load()
    {
        if (IfsPath == null)
        {
            return (File.ReadAllBytes(ChartPath!), File.ReadAllBytes(KeysoundPath!));
        }

        using var ifs = IfsArchive.Open(IfsPath);
        return (ifs.Read(ChartName), ifs.Read(KeysoundName));
    }
}

/// <summary>A background movie: a loose file, or a byte range inside an .ifs archive.</summary>
public sealed record MovieRef(string Path, long Offset = 0, long Size = 0, string? EntryName = null)
{
    public bool InArchive => EntryName != null;

    /// <summary>Gets the ffmpeg input for the movie; archive members are read in place with the subfile protocol.</summary>
    public string FFmpegInput => InArchive
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"subfile,,start,{Offset},end,{Offset + Size},,:{Path}")
        : Path;

    public string Fingerprint => File.Exists(Path) ? $"{EntryName}:{new FileInfo(Path).Length}:{File.GetLastWriteTimeUtc(Path).Ticks}" : "-";
}

/// <summary>
/// Finds the music database, song data and background movies inside an IIDX <c>contents</c> folder.
/// Folders under <c>data_mods</c> (omnimix and similar) take priority over <c>data</c>.
/// </summary>
public sealed class GameDataLocator
{
    private static readonly string[] MovieExtensions = [".mp4", ".wmv", ".m4v", ".avi", ".mpg", ".mpeg", ".m2v", ".mov", ".mkv", ".webm"];

    private readonly List<string> _dataRoots = new();
    private readonly object _movieLock = new();
    private Dictionary<string, MovieRef>? _movies;

    public GameDataLocator(string gamePath, string? musicDataPath = null)
    {
        if (!Directory.Exists(gamePath))
        {
            throw new DirectoryNotFoundException($"Game data folder '{gamePath}' does not exist");
        }

        string contents = gamePath;
        if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(gamePath)), "data", StringComparison.OrdinalIgnoreCase))
        {
            contents = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(gamePath))!;
            _dataRoots.Add(gamePath);
        }
        else if (Directory.Exists(Path.Combine(gamePath, "data")))
        {
            _dataRoots.Add(Path.Combine(gamePath, "data"));
        }
        else
        {
            _dataRoots.Add(gamePath);
        }

        var mods = Path.Combine(contents, "data_mods");
        if (Directory.Exists(mods))
        {
            foreach (var mod in Directory.GetDirectories(mods).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Reverse())
            {
                _dataRoots.Insert(0, mod);
            }
        }

        MusicDataPath = musicDataPath is { Length: > 0 } ? musicDataPath : FindMusicData()
            ?? throw new FileNotFoundException($"Could not find music_data.bin under '{gamePath}' (looked in data/info and data_mods/*/info)");
    }

    public string MusicDataPath { get; }

    public IReadOnlyList<string> DataRoots => _dataRoots;

    public SongSource? FindSong(SongEntry song)
    {
        string id = song.FileId;
        foreach (var root in _dataRoots)
        {
            var sound = Path.Combine(root, "sound");
            if (!Directory.Exists(sound))
            {
                continue;
            }

            var dir = Path.Combine(sound, id);
            if (Directory.Exists(dir))
            {
                var chart = Path.Combine(dir, id + ".1");
                var keys = PickKeysounds(Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>(), id);
                if (File.Exists(chart) && keys != null)
                {
                    return new SongSource { ChartName = id + ".1", KeysoundName = keys, ChartPath = chart, KeysoundPath = Path.Combine(dir, keys) };
                }
            }

            var ifsPath = Path.Combine(sound, id + ".ifs");
            if (File.Exists(ifsPath))
            {
                using var ifs = IfsArchive.Open(ifsPath);
                var chartName = ifs.Files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), id + ".1", StringComparison.OrdinalIgnoreCase));
                if (chartName == null)
                {
                    continue;
                }

                var folder = chartName.Contains('/', StringComparison.Ordinal) ? chartName[..chartName.LastIndexOf('/')] + "/" : string.Empty;
                var siblings = ifs.Files.Where(f => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).Select(f => f[folder.Length..]).Where(f => !f.Contains('/', StringComparison.Ordinal));
                var keys = PickKeysounds(siblings, id);
                if (keys != null)
                {
                    return new SongSource { ChartName = chartName, KeysoundName = folder + keys, IfsPath = ifsPath };
                }
            }
        }

        return null;
    }

    /// <summary>Finds the background movie named by the song's bga_filename, or null.</summary>
    public MovieRef? FindMovie(SongEntry song)
    {
        if (string.IsNullOrWhiteSpace(song.BgaFilename))
        {
            return null;
        }

        lock (_movieLock)
        {
            _movies ??= IndexMovies();
        }

        var name = Path.GetFileName(song.BgaFilename.Trim());
        var stem = Path.HasExtension(name) && MovieExtensions.Contains(Path.GetExtension(name).ToLowerInvariant())
            ? Path.GetFileNameWithoutExtension(name)
            : name;
        return _movies.TryGetValue(stem.ToLowerInvariant(), out var path) ? path : null;
    }

    private static string? PickKeysounds(IEnumerable<string> names, string id)
    {
        var list = names.ToList();
        string? Exact(string n) => list.FirstOrDefault(f => string.Equals(f, n, StringComparison.OrdinalIgnoreCase));
        return Exact(id + ".s3p")
            ?? Exact(id + ".2dx")
            ?? list.FirstOrDefault(f => f.EndsWith(".s3p", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(f => f.EndsWith(".2dx", StringComparison.OrdinalIgnoreCase) && !f.Contains("_pre", StringComparison.OrdinalIgnoreCase));
    }

    private string? FindMusicData()
    {
        foreach (var root in _dataRoots)
        {
            foreach (var candidate in new[] { Path.Combine(root, "info", "0", "music_data.bin"), Path.Combine(root, "info", "music_data.bin") })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var info = Path.Combine(root, "info");
            if (Directory.Exists(info))
            {
                var any = Directory.EnumerateFiles(info, "music_data.bin", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
                if (any != null)
                {
                    return any;
                }
            }
        }

        return null;
    }

    private Dictionary<string, MovieRef> IndexMovies()
    {
        var map = new Dictionary<string, MovieRef>(StringComparer.Ordinal);

        // Later passes (earlier roots, i.e. mods) override; within a root loose files beat archived
        // ones, and the extension order above breaks ties.
        for (int r = _dataRoots.Count - 1; r >= 0; r--)
        {
            var movie = Path.Combine(_dataRoots[r], "movie");
            if (!Directory.Exists(movie))
            {
                continue;
            }

            var found = new Dictionary<string, (int Rank, MovieRef Ref)>(StringComparer.Ordinal);
            void Offer(string name, int rank, MovieRef m)
            {
                int ext = Array.IndexOf(MovieExtensions, Path.GetExtension(name).ToLowerInvariant());
                if (ext < 0)
                {
                    return;
                }

                rank = (rank * 100) + ext;
                var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                if (!found.TryGetValue(stem, out var cur) || rank < cur.Rank)
                {
                    found[stem] = (rank, m);
                }
            }

            foreach (var file in Directory.EnumerateFiles(movie, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".ifs", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var ifs = IfsArchive.Open(file);
                        foreach (var entry in ifs.Files)
                        {
                            if (ifs.TryGetRange(entry, out long offset, out int size))
                            {
                                Offer(Path.GetFileName(entry), 1, new MovieRef(file, offset, size, entry));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
                    {
                        // Not every .ifs under movie/ has to be readable; skip the broken ones.
                    }
                }
                else
                {
                    Offer(file, 0, new MovieRef(file));
                }
            }

            foreach (var (stem, v) in found)
            {
                map[stem] = v.Ref;
            }
        }

        return map;
    }
}
