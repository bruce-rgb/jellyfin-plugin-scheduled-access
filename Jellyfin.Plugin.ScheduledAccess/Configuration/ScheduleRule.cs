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
/// Regla que restringe a un usuario en ciertos dias y en una franja horaria.
/// </summary>
public class ScheduleRule
{
    /// <summary>
    /// Minutos que tiene un dia. Una franja de 0 a este valor cubre el dia entero.
    /// </summary>
    public const int MinutesPerDay = 1440;

    /// <summary>
    /// Gets or sets el id del usuario afectado.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets el modo de filtrado a aplicar en los dias indicados.
    /// </summary>
    public TagFilterMode Mode { get; set; }

    /// <summary>
    /// Gets or sets el inicio de la franja, en minutos desde medianoche.
    /// </summary>
    /// <remarks>
    /// Se usan minutos y no horas decimales para que los limites sean exactos
    /// y para que encajen directamente con un &lt;input type="time"&gt;.
    /// </remarks>
    public int StartMinutes { get; set; }

    /// <summary>
    /// Gets or sets el fin de la franja, en minutos desde medianoche (exclusivo).
    /// </summary>
    /// <remarks>
    /// El valor inicial cubre el dia entero a proposito: las reglas guardadas
    /// por versiones anteriores no llevan este elemento en el XML, y
    /// XmlSerializer deja intacto lo que no aparece. Asi una regla antigua
    /// sigue aplicando todo el dia en vez de no aplicar nunca.
    /// </remarks>
    public int EndMinutes { get; set; } = MinutesPerDay;

    /// <summary>
    /// Gets or sets los dias de la semana en los que aplica la restriccion.
    /// </summary>
    /// <remarks>
    /// En una franja que cruza medianoche, el dia se refiere al de INICIO:
    /// una regla de domingo 22:00 a 06:00 sigue activa el lunes a las 02:00.
    /// </remarks>
    public DayOfWeek[] Days { get; set; } = Array.Empty<DayOfWeek>();

    /// <summary>
    /// Gets or sets las etiquetas a bloquear o permitir, segun <see cref="Mode"/>.
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets las unicas bibliotecas visibles mientras la regla este activa.
    /// </summary>
    /// <remarks>
    /// Vacio significa no tocar las bibliotecas, que es lo que necesitan las
    /// reglas guardadas por versiones anteriores y las que solo filtran por
    /// etiquetas. Los dos filtros son independientes y se combinan: se puede
    /// limitar a una biblioteca y ademas filtrar por etiquetas dentro de ella.
    /// </remarks>
    public Guid[] LibraryIds { get; set; } = Array.Empty<Guid>();
}

#pragma warning restore CA1819
