using Jellyfin.Plugin.ScheduledAccess.Scheduling;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ScheduledAccess;

/// <summary>
/// Registers the plugin's services in the server's DI container.
/// </summary>
/// <remarks>
/// Jellyfin discovers this at startup. It is the only way for a plugin to add
/// long-lived services, which is what the schedule watcher needs to be.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ScheduleEnforcer>();
        serviceCollection.AddHostedService<ScheduleWatcher>();
    }
}
