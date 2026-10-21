using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using IIDX.Core.Formats;
using IIDX.Core.Library;
using Microsoft.Extensions.Logging;

// Command line front end for the IIDX core library, for checking game data without Jellyfin.
var opts = ParseArgs(args.Skip(1));
string command = args.FirstOrDefault() ?? "help";
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(opts.ContainsKey("verbose") ? LogLevel.Debug : LogLevel.Information));
var logger = loggerFactory.CreateLogger("iidx");

switch (command)
{
    case "db":
    {
        var db = MusicDatabase.Load(Required("music-data"));
        Console.WriteLine($"data version {db.DataVersion}, {db.Songs.Count} entries, entry size {db.EntrySize} (known {db.KnownEntrySize})");
        if (opts.ContainsKey("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(db.Songs, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        else
        {
            foreach (var s in db.Songs.Take(int.Parse(opts.GetValueOrDefault("limit", "20"), CultureInfo.InvariantCulture)))
            {
                Console.WriteLine($"{s.SongId,6} v{s.GameVersion,-2} {s.Title} / {s.Artist} [{s.Genre}] SPA{s.Level(Difficulty.SpAnother)} bga={s.BgaFilename} delay={s.BgaDelay}");
            }
        }

        break;
    }

    case "inspect":
    {
        var lib = new IidxLibrary(Settings(), logger);
        var (db, path, charts, movies) = await lib.InspectAsync(CancellationToken.None);
        Console.WriteLine($"{path}: version {db.DataVersion}, {db.Songs.Count} entries, {charts} with chart data, {movies} with movies");
        break;
    }

    case "index":
    {
        var lib = new IidxLibrary(Settings(), logger);
        var report = await lib.IndexAsync(new Progress<double>(p => Console.Error.Write($"\r{p:F1}%   ")), CancellationToken.None);
        Console.Error.WriteLine();
        Console.WriteLine(report);
        foreach (var f in report.Failures)
        {
            Console.WriteLine("  " + f);
        }

        break;
    }

    case "render":
    {
        var lib = new IidxLibrary(Settings(), logger);
        var sw = Stopwatch.StartNew();
        foreach (var id in Required("id").Split(','))
        {
            bool rendered = await lib.EnsureRenderedAsync(int.Parse(id, CultureInfo.InvariantCulture), CancellationToken.None);
            Console.WriteLine($"{id}: {(rendered ? "rendered" : "already cached")} after {sw.Elapsed.TotalSeconds:F1}s");
        }

        break;
    }

    case "expire":
    {
        var lib = new IidxLibrary(Settings(), logger);
        int n = opts.ContainsKey("all") ? await lib.ClearCacheAsync(CancellationToken.None) : await lib.ExpireAsync(DateTime.UtcNow, CancellationToken.None);
        Console.WriteLine($"{n} renders reverted to placeholders");
        break;
    }

    default:
        Console.WriteLine("""
            iidx-tool db --music-data <music_data.bin> [--json] [--limit N]
            iidx-tool inspect --game <contents> [--music-data <file>]
            iidx-tool index   --game <contents> --library <dir> --state <dir> [--ids 1000,1001] [--jobs N]
            iidx-tool render  --game <contents> --library <dir> --state <dir> --id <songid[,songid]>
            iidx-tool expire  --game <contents> --library <dir> --state <dir> [--ttl-minutes N] [--all]
            common: --ffmpeg <path> --codec flac|aac --verbose
            """);
        break;
}

IidxSettings Settings() => new()
{
    GamePath = Required("game"),
    MusicDataPath = opts.GetValueOrDefault("music-data"),
    LibraryPath = opts.GetValueOrDefault("library", "iidx-library"),
    StatePath = opts.GetValueOrDefault("state", "iidx-state"),
    FFmpegPath = opts.GetValueOrDefault("ffmpeg", "ffmpeg"),
    AudioCodec = opts.GetValueOrDefault("codec", "flac"),
    MaxParallel = int.Parse(opts.GetValueOrDefault("jobs", "4"), CultureInfo.InvariantCulture),
    CacheTtl = TimeSpan.FromMinutes(double.Parse(opts.GetValueOrDefault("ttl-minutes", "360"), CultureInfo.InvariantCulture)),
    OnlySongIds = opts.TryGetValue("ids", out var ids) ? ids.Split(',').Select(i => int.Parse(i, CultureInfo.InvariantCulture)).ToHashSet() : new HashSet<int>(),
};

string Required(string key) => opts.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required");

static Dictionary<string, string> ParseArgs(IEnumerable<string> raw)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    var list = raw.ToList();
    for (int i = 0; i < list.Count; i++)
    {
        if (!list[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = list[i][2..];
        d[key] = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal) ? list[++i] : "true";
    }

    return d;
}
