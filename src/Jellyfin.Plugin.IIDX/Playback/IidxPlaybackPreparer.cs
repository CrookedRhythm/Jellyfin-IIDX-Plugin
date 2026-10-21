using IIDX.Core.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IIDX.Playback;

/// <summary>
/// Renders a song at the moment Jellyfin prepares it for playback. Placeholders have no video stream,
/// so Jellyfin refreshes the item with remote probing just before building the playback info; this
/// provider runs first in that refresh (before the ffprobe provider), so the probe sees the real render.
/// Ordinary library scans never set <see cref="MetadataRefreshOptions.EnableRemoteContentProbe"/> and
/// are left alone.
/// </summary>
public class IidxPlaybackPreparer : ICustomMetadataProvider<MusicVideo>, ICustomMetadataProvider<Video>, IPreRefreshProvider, IForcedProvider, IHasOrder
{
    private readonly IidxLibraryService _service;
    private readonly ILogger<IidxPlaybackPreparer> _logger;

    public IidxPlaybackPreparer(IidxLibraryService service, ILogger<IidxPlaybackPreparer> logger)
    {
        _service = service;
        _logger = logger;
    }

    public string Name => "beatmania IIDX renderer";

    // Before the built-in probe provider (order 100).
    public int Order => 0;

    public Task<ItemUpdateType> FetchAsync(MusicVideo item, MetadataRefreshOptions options, CancellationToken cancellationToken) =>
        PrepareAsync(item, options, cancellationToken);

    public Task<ItemUpdateType> FetchAsync(Video item, MetadataRefreshOptions options, CancellationToken cancellationToken) =>
        PrepareAsync(item, options, cancellationToken);

    private async Task<ItemUpdateType> PrepareAsync(Video item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (!options.EnableRemoteContentProbe || !IidxLibrary.TryGetSongId(item.Path ?? string.Empty, out int songId))
        {
            return ItemUpdateType.None;
        }

        var library = _service.Library;
        if (library == null || !library.IsInLibrary(item.Path))
        {
            return ItemUpdateType.None;
        }

        try
        {
            await library.EnsureRenderedAsync(songId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not render IIDX song {SongId} for playback", songId);
        }

        return ItemUpdateType.None;
    }
}
