using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ScheduledAccess.Configuration;
using Jellyfin.Plugin.ScheduledAccess.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ScheduledAccess;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ITaskManager taskManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _taskManager = taskManager;
    }

    /// <inheritdoc />
    public override string Name => "Scheduled Access";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("65e8ae1e-ea44-4b8c-a2c7-16f46a158eb4");

    /// <inheritdoc />
    public override string Description => "Restringe el acceso a bibliotecas segun el dia de la semana.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Guardar la configuracion no aplica nada por si solo: quien reescribe las
    /// politicas de usuario es la tarea programada. Encolarla aqui hace que un
    /// cambio de reglas surta efecto de inmediato, en vez de esperar al
    /// siguiente disparador (medianoche o la comprobacion horaria).
    /// </remarks>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Las instantaneas son estado del servidor, no configuracion editable.
        //
        // Nunca deben aceptarse del cliente: la pagina las lee al abrirse y las
        // reenviaria desde una copia potencialmente obsoleta. Si la tarea creo
        // una instantanea despues de cargar la pagina, guardar la borraria, y la
        // siguiente ejecucion tomaria otra sobre la politica YA restringida,
        // registrando el estado restringido como si fuera el original. El dato
        // que permite deshacer quedaria corrupto de forma irreversible.
        if (configuration is PluginConfiguration incoming)
        {
            incoming.Snapshots = Configuration.Snapshots;
        }

        base.UpdateConfiguration(configuration);

        // Si ya se esta ejecutando, la que corre leera igualmente la config
        // recien guardada, asi que no hace falta reencolar.
        _taskManager.QueueIfNotRunning<ApplyTagScheduleTask>();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
