using Jellyfin.Plugin.IIDX.Playback;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.IIDX;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IidxLibraryService>();
        serviceCollection.AddHostedService<CacheMaintenanceService>();
    }
}
