using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ScheduledAccess.Configuration;

namespace Jellyfin.Plugin.ScheduledAccess.Scheduling;

/// <summary>
/// Decide si una regla deja ver un item concreto.
/// </summary>
/// <remarks>
/// Es el espejo de lo que <see cref="ScheduleEnforcer"/> escribe en la
/// politica, resuelto sobre un solo item en vez de sobre el usuario entero.
/// Hace falta porque para cortar una reproduccion hay que responder dos
/// preguntas que la politica ya aplicada no responde: si lo que suena ahora
/// sigue permitido, y si lo seguira estando tras el proximo limite. La segunda
/// es una prediccion, y Jellyfin solo sabe evaluar el presente.
///
/// Usar el mismo predicado para el aviso y para el corte es deliberado: si el
/// aviso lo diera este codigo y el corte lo diera Jellyfin, bastaria una
/// diferencia minima entre ambos para avisar de cortes que no llegan, o cortar
/// sin haber avisado. Vale mas que los dos se equivoquen igual que que se
/// contradigan.
///
/// Solo mira lo que el plugin restringe. Lo que el usuario ya tuviera
/// bloqueado por su cuenta no se consulta: si eso ocultara el item, no se
/// habria podido empezar a reproducir.
/// </remarks>
public static class ContentVisibility
{
    /// <summary>
    /// Indica si la regla permite ver el item.
    /// </summary>
    /// <param name="tags">Etiquetas del item, incluidas las heredadas.</param>
    /// <param name="libraryId">
    /// Biblioteca a la que pertenece el item. <see cref="Guid.Empty"/> si no se
    /// ha podido determinar, en cuyo caso el filtro por bibliotecas no aplica.
    /// </param>
    /// <param name="rule">
    /// Regla vigente para el usuario, o <c>null</c> si no hay ninguna, que es
    /// tanto como decir que el plugin no le esta restringiendo nada.
    /// </param>
    /// <returns><c>true</c> si el item puede verse.</returns>
    public static bool IsAllowed(IReadOnlyList<string>? tags, Guid libraryId, ScheduleRule? rule)
    {
        if (rule is null)
        {
            return true;
        }

        // Una biblioteca sin identificar no puede compararse con la lista, y
        // cortar por sospecha es peor que dejar terminar de mas: el filtro de
        // bibliotecas simplemente no se aplica.
        if (rule.LibraryIds.Length > 0
            && libraryId != Guid.Empty
            && Array.IndexOf(rule.LibraryIds, libraryId) < 0)
        {
            return false;
        }

        if (rule.Tags.Length == 0)
        {
            // Sin etiquetas no hay filtro que aplicar, ni siquiera en modo
            // lista blanca: es como deja Jellyfin una lista de permitidas
            // vacia, y el motor lo interpreta igual.
            return true;
        }

        var carries = tags is not null
            && tags.Intersect(rule.Tags, StringComparer.OrdinalIgnoreCase).Any();

        return rule.Mode == TagFilterMode.AllowOnly ? carries : !carries;
    }
}
