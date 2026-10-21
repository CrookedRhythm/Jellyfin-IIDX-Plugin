using IIDX.Core.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IIDX.Playback;

/// <summary>
/// Hooks Jellyfin's playback path. Every client asks for media sources before streaming, so this
/// makes sure the render is in place. It adds no sources of its own: Jellyfin serves the library
/// file like any other video, so direct play, remuxing and transcoding work for every client.
/// </summary>
public class IidxMediaSourceProvider : IMediaSourceProvider
{
    private readonly IidxLibraryService _service;
    private readonly ILogger<IidxMediaSourceProvider> _logger;

    public IidxMediaSourceProvider(IidxLibraryService service, ILogger<IidxMediaSourceProvider> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Video video || !IidxLibrary.TryGetSongId(video.Path ?? string.Empty, out int songId))
        {
            return [];
        }

        var library = _service.Library;
        if (library == null || !library.IsInLibrary(video.Path))
        {
            return [];
        }

        try
        {
            // Normally IidxPlaybackPreparer has rendered the song already; this catches renders that
            // expired after Jellyfin last probed the item.
            await library.EnsureRenderedAsync(songId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not render IIDX song {SongId} for playback", songId);
        }

        return [];
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
