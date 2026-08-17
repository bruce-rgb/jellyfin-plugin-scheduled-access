using System;

namespace Jellyfin.Plugin.ScheduledAccess.Configuration;

// CA1819 (no exponer arrays en propiedades) se suprime a proposito en los tipos
// de configuracion: deben viajar por XmlSerializer (config en disco) y por
// System.Text.Json (pagina web del dashboard). Las colecciones de solo lectura
// funcionan con el primero pero no de forma fiable con el segundo, y perder
// reglas en silencio al guardar seria peor que el olor de diseno. El propio
// UserPolicy de Jellyfin expone arrays por la misma razon.
#pragma warning disable CA1819

/// <summary>
/// Regla que restringe a un usuario en ciertos dias de la semana.
/// </summary>
public class ScheduleRule
{
    /// <summary>
    /// Gets or sets el id del usuario afectado.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets el modo de filtrado a aplicar en los dias indicados.
    /// </summary>
    public TagFilterMode Mode { get; set; }

    /// <summary>
    /// Gets or sets los dias de la semana en los que aplica la restriccion.
    /// </summary>
    public DayOfWeek[] Days { get; set; } = Array.Empty<DayOfWeek>();

    /// <summary>
    /// Gets or sets las etiquetas a bloquear o permitir, segun <see cref="Mode"/>.
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();
}

#pragma warning restore CA1819
