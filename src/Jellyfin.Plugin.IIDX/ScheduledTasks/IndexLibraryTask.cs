using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IIDX.ScheduledTasks;

/// <summary>
/// Reads the music database and writes the library folders (metadata, thumbnails, placeholders),
/// then makes sure the Jellyfin library exists and scans it.
/// </summary>
public class IndexLibraryTask : IScheduledTask
{
    private readonly IidxLibraryService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<IndexLibraryTask> _logger;

    public IndexLibraryTask(IidxLibraryService service, ILibraryManager libraryManager, ILogger<IndexLibraryTask> logger)
    {
        _service = service;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "Update beatmania IIDX library";

    public string Key => "IIDXIndexLibrary";

    public string Description => "Reads the IIDX music database and refreshes song metadata, thumbnails and the library. Game files are only read.";

    public string Category => "beatmania IIDX";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var library = _service.Library;
        if (library == null)
        {
            _logger.LogWarning("beatmania IIDX: set the game data folder in the plugin settings first");
            return;
        }

        var report = await library.IndexAsync(new Progress<double>(p => progress.Report(p * 0.9)), cancellationToken).ConfigureAwait(false);
        foreach (var failure in report.Failures.Take(20))
        {
            _logger.LogWarning("IIDX index failure: {Failure}", failure);
        }

        await EnsureLibraryAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks },
    ];

    private async Task EnsureLibraryAsync(CancellationToken ct)
    {
        var config = IidxLibraryService.Config;
        string path = Path.GetFullPath(_service.LibraryPath);
        bool exists = _libraryManager.GetVirtualFolders()
            .Any(f => f.Locations.Any(l => string.Equals(Path.GetFullPath(l).TrimEnd('/', '\\'), path.TrimEnd('/', '\\'), StringComparison.Ordinal)));

        if (!exists && config.CreateLibrary)
        {
            _logger.LogInformation("Creating Jellyfin library '{Name}' at {Path}", config.LibraryName, path);
            var options = new LibraryOptions
            {
                PathInfos = [new MediaPathInfo(path)],
                EnableRealtimeMonitor = false,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                SaveLocalMetadata = false,
                EnableEmbeddedTitles = false,
                TypeOptions =
                [
                    new TypeOptions { Type = "MusicVideo", MetadataFetchers = [], ImageFetchers = [] },
                ],
            };
            await _libraryManager.AddVirtualFolder(config.LibraryName, CollectionTypeOptions.musicvideos, options, true).ConfigureAwait(false);
            return;
        }

        if (exists)
        {
            await _libraryManager.ValidateMediaLibrary(new Progress<double>(), ct).ConfigureAwait(false);
        }
    }
}
