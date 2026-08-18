using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ScheduledAccess.Configuration;

/// <summary>
/// Como se aplica la lista de etiquetas en un dia restringido.
/// </summary>
public enum TagFilterMode
{
    /// <summary>
    /// Lista negra: oculta los items que lleven alguna de las etiquetas.
    /// Falla abierto (contenido nuevo sin etiquetar sigue visible).
    /// </summary>
    Block,

    /// <summary>
    /// Lista blanca estricta: solo muestra los items que lleven al menos una
    /// de las etiquetas. Falla cerrado (contenido nuevo sin etiquetar se oculta).
    /// </summary>
    AllowOnly
}

#pragma warning disable CA1819

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether la aplicacion de reglas esta activa.
    /// Al desactivarlo, la siguiente ejecucion restaura todas las instantaneas.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets las reglas configuradas.
    /// </summary>
    public ScheduleRule[] Rules { get; set; } = Array.Empty<ScheduleRule>();

    /// <summary>
    /// Gets or sets a value indicating whether hay que cortar lo que se este
    /// reproduciendo cuando una regla deja de permitirlo.
    /// </summary>
    /// <remarks>
    /// Apagado por defecto a proposito. Jellyfin no vuelve a comprobar la
    /// politica en un stream ya abierto, asi que hasta ahora lo empezado
    /// terminaba; encender esto de golpe al actualizar cambiaria el
    /// comportamiento de instalaciones existentes sin avisar.
    /// </remarks>
    public bool StopPlayback { get; set; }

    /// <summary>
    /// Gets or sets los minutos de aviso previo al corte. Cero no avisa.
    /// </summary>
    /// <remarks>
    /// Solo tiene efecto con <see cref="StopPlayback"/> encendido: avisar de un
    /// corte que no va a producirse seria mentir.
    /// </remarks>
    public int WarningMinutes { get; set; }

    /// <summary>
    /// Gets or sets las instantaneas de politicas pendientes de restaurar.
    /// Las gestiona el plugin; la pagina de configuracion las reenvia tal cual.
    /// </summary>
    public PolicySnapshot[] Snapshots { get; set; } = Array.Empty<PolicySnapshot>();
}

#pragma warning restore CA1819
