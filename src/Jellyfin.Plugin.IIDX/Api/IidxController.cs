using System.Net.Mime;
using Jellyfin.Plugin.IIDX.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.IIDX.Api;

/// <summary>Admin endpoints used by the plugin settings page.</summary>
[ApiController]
[Route("IIDX")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class IidxController : ControllerBase
{
    private readonly IidxLibraryService _service;
    private readonly ITaskManager _taskManager;

    public IidxController(IidxLibraryService service, ITaskManager taskManager)
    {
        _service = service;
        _taskManager = taskManager;
    }

    /// <summary>Reads the configured music database and reports what was found.</summary>
    [HttpGet("Inspect")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Inspect(CancellationToken ct)
    {
        var lib = _service.Library;
        if (lib == null)
        {
            return Ok(new { ok = false, message = "Set the game data folder first." });
        }

        try
        {
            var (db, path, charts, movies) = await lib.InspectAsync(ct).ConfigureAwait(false);
            return Ok(new
            {
                ok = true,
                musicData = path,
                dataVersion = db.DataVersion,
                entries = db.Songs.Count,
                withCharts = charts,
                withMovies = movies,
                entrySize = db.EntrySize,
                knownEntrySize = db.KnownEntrySize,
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>Gets cache usage and paths.</summary>
    [HttpGet("Status")]
    public ActionResult<object> Status()
    {
        var status = _service.Library?.GetStatus();
        return Ok(new
        {
            configured = _service.IsConfigured,
            libraryPath = _service.LibraryPath,
            statePath = _service.StatePath,
            songs = status?.Songs ?? 0,
            rendered = status?.Rendered ?? 0,
            renderedBytes = status?.RenderedBytes ?? 0,
        });
    }

    /// <summary>Starts the library update task.</summary>
    [HttpPost("Index")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Index()
    {
        _taskManager.QueueIfNotRunning<IndexLibraryTask>();
        return NoContent();
    }

    /// <summary>Releases every cached render.</summary>
    [HttpPost("ClearCache")]
    public async Task<ActionResult<object>> ClearCache(CancellationToken ct)
    {
        int n = _service.Library == null ? 0 : await _service.Library.ClearCacheAsync(ct).ConfigureAwait(false);
        return Ok(new { released = n });
    }
}
