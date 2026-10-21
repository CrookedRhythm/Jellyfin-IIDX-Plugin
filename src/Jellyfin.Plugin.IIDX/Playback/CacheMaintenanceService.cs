using IIDX.Core.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IIDX.Playback;

/// <summary>
/// Keeps renders alive while they are being watched and turns expired ones back into placeholders.
/// </summary>
public sealed class CacheMaintenanceService : IHostedService, IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly IidxLibraryService _service;
    private readonly ISessionManager _sessions;
    private readonly ILogger<CacheMaintenanceService> _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public CacheMaintenanceService(IidxLibraryService service, ISessionManager sessions, ILogger<CacheMaintenanceService> logger)
    {
        _service = service;
        _sessions = sessions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlayback;
        _sessions.PlaybackProgress += OnPlayback;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlayback;
        _sessions.PlaybackProgress -= OnPlayback;
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop != null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose() => _stop.Dispose();

    private void OnPlayback(object? sender, PlaybackProgressEventArgs e) => Touch(e.Item?.Path);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) => Touch(e.Item?.Path);

    private void Touch(string? path)
    {
        if (path != null && IidxLibrary.TryGetSongId(path, out int id) && _service.Library is { } lib && lib.IsInLibrary(path))
        {
            lib.Touch(id);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (_service.Library is { } lib)
                {
                    int n = await lib.ExpireAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
                    if (n > 0)
                    {
                        _logger.LogInformation("Released {Count} expired IIDX renders", n);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "IIDX cache sweep failed");
            }
        }
    }
}
