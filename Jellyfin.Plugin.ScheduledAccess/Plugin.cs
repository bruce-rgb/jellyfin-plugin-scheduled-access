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
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Raised after the configuration is saved, so the schedule watcher can
    /// re-evaluate immediately instead of waiting for its next wake-up.
    /// </summary>
    public static event EventHandler? ConfigurationUpdated;

    /// <inheritdoc />
    public override string Name => "Scheduled Access";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("65e8ae1e-ea44-4b8c-a2c7-16f46a158eb4");

    /// <inheritdoc />
    public override string Description => "Restricts what content each user can see based on the day of the week, using library tags.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the languages shipped with the plugin. The first one is the
    /// fallback used when the user's language has no translation file.
    /// </summary>
    /// <remarks>
    /// Must be kept in sync with the SUPPORTED array in configPage.html.
    /// </remarks>
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["en", "es"];

    /// <inheritdoc />
    /// <remarks>
    /// Guardar la configuracion no aplica nada por si solo: quien reescribe las
    /// politicas de usuario es el vigilante de franjas. Avisarle aqui hace que
    /// un cambio de reglas surta efecto de inmediato, en vez de esperar a su
    /// siguiente despertar, que puede estar horas por delante.
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

        ConfigurationUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Besides the configuration page, this registers one entry per language
    /// file. Jellyfin has no localization framework for plugins, and it does
    /// not expose its own <c>Globalize</c> module to plugin pages, so the
    /// translations have to be served and applied by the plugin itself.
    ///
    /// Registering them here serves them at
    /// <c>web/ConfigurationPage?name={Name}</c>, which is the only way to
    /// expose a plugin's own resources over HTTP without writing an API
    /// controller.
    /// </remarks>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
        };

        foreach (var language in SupportedLanguages)
        {
            yield return new PluginPageInfo
            {
                // El nombre lleva prefijo del plugin porque el espacio de
                // nombres de paginas es global a todo el servidor.
                Name = string.Format(CultureInfo.InvariantCulture, "scheduledaccess.{0}.json", language),
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Locale.{1}.json", GetType().Namespace, language)
            };
        }
    }
}
