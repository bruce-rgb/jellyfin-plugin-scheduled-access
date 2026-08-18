using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ScheduledAccess.Configuration;

namespace Jellyfin.Plugin.ScheduledAccess.Scheduling;

/// <summary>
/// Decide que regla esta vigente en un instante dado y cuando toca volver a mirar.
/// </summary>
/// <remarks>
/// Logica pura, sin dependencias del servidor, para poder razonarla y probarla
/// por separado. Es donde se concentran los casos raros: franjas que cruzan
/// medianoche, solapamientos y el calculo del siguiente limite.
/// </remarks>
public static class ScheduleResolver
{
    /// <summary>
    /// Cuanto se pasa de largo cada limite al dormir, para no despertar antes
    /// de tiempo. Ver <see cref="NextWakeUp"/>.
    /// </summary>
    public static readonly TimeSpan WakeMargin = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Minutos transcurridos desde medianoche.
    /// </summary>
    /// <param name="moment">Instante a convertir.</param>
    /// <returns>Minutos desde medianoche, de 0 a 1439.</returns>
    public static int MinuteOfDay(DateTime moment) => (moment.Hour * 60) + moment.Minute;

    /// <summary>
    /// Duracion de la franja en minutos. Una franja vacia o de dia completo
    /// cuenta como el dia entero.
    /// </summary>
    /// <param name="rule">Regla a medir.</param>
    /// <returns>Duracion en minutos, de 1 a 1440.</returns>
    public static int DurationMinutes(ScheduleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var span = rule.EndMinutes - rule.StartMinutes;
        if (span <= 0)
        {
            // Fin igual o anterior al inicio: la franja cruza medianoche.
            // Si ademas coinciden, se interpreta como dia completo.
            span += ScheduleRule.MinutesPerDay;
        }

        return span;
    }

    /// <summary>
    /// Indica si la regla esta activa en el instante dado.
    /// </summary>
    /// <param name="rule">Regla a evaluar.</param>
    /// <param name="moment">Instante de referencia, en hora local.</param>
    /// <returns><c>true</c> si la regla aplica en ese instante.</returns>
    public static bool IsActiveAt(ScheduleRule rule, DateTime moment)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Days is null || rule.Days.Length == 0)
        {
            return false;
        }

        var minute = MinuteOfDay(moment);
        var today = moment.DayOfWeek;

        // Dia completo: basta con que el dia este marcado.
        if (DurationMinutes(rule) >= ScheduleRule.MinutesPerDay)
        {
            return Array.IndexOf(rule.Days, today) >= 0;
        }

        if (rule.StartMinutes < rule.EndMinutes)
        {
            return Array.IndexOf(rule.Days, today) >= 0
                && minute >= rule.StartMinutes
                && minute < rule.EndMinutes;
        }

        // Franja que cruza medianoche. El dia marcado es el de INICIO, asi que
        // la cola de la madrugada pertenece al dia anterior: una regla de
        // domingo 22:00-06:00 sigue vigente el lunes a las 02:00.
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);

        return (Array.IndexOf(rule.Days, today) >= 0 && minute >= rule.StartMinutes)
            || (Array.IndexOf(rule.Days, yesterday) >= 0 && minute < rule.EndMinutes);
    }

    /// <summary>
    /// Devuelve la regla vigente para cada usuario en el instante dado.
    /// </summary>
    /// <remarks>
    /// Cuando varias reglas del mismo usuario se solapan gana **la mas corta**,
    /// para que una franja concreta pueda anular a una general sin tener que
    /// recortar esta ultima. A igualdad de duracion gana la primera declarada,
    /// de modo que el resultado sea siempre determinista.
    /// </remarks>
    /// <param name="rules">Reglas configuradas.</param>
    /// <param name="moment">Instante de referencia, en hora local.</param>
    /// <returns>Como mucho una regla por usuario.</returns>
    public static IReadOnlyList<ScheduleRule> ActiveRules(IEnumerable<ScheduleRule> rules, DateTime moment)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return rules
            .Where(r => IsActiveAt(r, moment))
            .GroupBy(r => r.UserId)
            .Select(group => group
                .Select((rule, index) => (rule, index))
                .OrderBy(x => DurationMinutes(x.rule))
                .ThenBy(x => x.index)
                .First().rule)
            .ToArray();
    }

    /// <summary>
    /// Momento en que cambia el estado por proxima vez.
    /// </summary>
    /// <param name="rules">Reglas configuradas.</param>
    /// <param name="moment">Instante de referencia.</param>
    /// <returns>
    /// El siguiente limite de franja, o la proxima medianoche si no hay
    /// ninguno despues. Nunca devuelve un instante en el pasado ni el actual.
    /// </returns>
    public static DateTime NextBoundary(IEnumerable<ScheduleRule> rules, DateTime moment)
        => NextAfter(Boundaries(rules, 0), moment);

    /// <summary>
    /// Instante en que toca despertar, un pelo DESPUES del limite.
    /// </summary>
    /// <remarks>
    /// Dos cosas distintas, y las dos importan.
    ///
    /// Con aviso previo no basta con despertar en los limites: hay que llegar
    /// unos minutos antes para poder avisar. Se adelantan TODOS los limites,
    /// no solo los finales de franja, porque lo que corta una reproduccion es
    /// que empiece una restriccion, no que termine.
    ///
    /// Y sobre el limite se suma <see cref="WakeMargin"/>, que no es cosmetico.
    /// Aqui todo se decide por minutos enteros, y <c>Task.Delay</c> puede
    /// adelantarse unos milisegundos: despertar a las 22:14:59.999 para el
    /// limite de las 22:15 hace que la regla se lea como todavia no vigente,
    /// que el aviso caiga fuera de su ventana por un milisegundo, y -- lo peor
    /// -- que el calculo siguiente ya parta de las 22:15 y descarte ese limite
    /// por pasado. La franja no llegaba a aplicarse hasta la pasada de
    /// seguridad, hasta una hora despues.
    /// </remarks>
    /// <param name="rules">Reglas configuradas.</param>
    /// <param name="moment">Instante de referencia.</param>
    /// <param name="warningMinutes">Minutos de adelanto. Cero no adelanta nada.</param>
    /// <returns>El siguiente instante en que hay algo que hacer.</returns>
    public static DateTime NextWakeUp(IEnumerable<ScheduleRule> rules, DateTime moment, int warningMinutes)
        => NextAfter(Boundaries(rules, warningMinutes), moment) + WakeMargin;

    private static SortedSet<int> Boundaries(IEnumerable<ScheduleRule> rules, int warningMinutes)
    {
        ArgumentNullException.ThrowIfNull(rules);

        // La medianoche entra siempre: es cuando cambia el dia de la semana,
        // aunque ninguna regla tenga un limite ahi.
        var boundaries = new SortedSet<int> { 0 };
        var warning = Math.Clamp(warningMinutes, 0, ScheduleRule.MinutesPerDay - 1);

        foreach (var rule in rules)
        {
            foreach (var edge in new[] { rule.StartMinutes, rule.EndMinutes })
            {
                var minute = Math.Clamp(edge, 0, ScheduleRule.MinutesPerDay);
                boundaries.Add(minute);

                if (warning > 0)
                {
                    // El adelanto puede cruzar la medianoche hacia atras: un
                    // limite a las 00:10 con diez minutos de aviso despierta a
                    // las 00:00, y con veinte, a las 23:50 del dia anterior.
                    boundaries.Add((((minute - warning) % ScheduleRule.MinutesPerDay) + ScheduleRule.MinutesPerDay) % ScheduleRule.MinutesPerDay);
                }
            }
        }

        return boundaries;
    }

    private static DateTime NextAfter(SortedSet<int> boundaries, DateTime moment)
    {
        var minute = MinuteOfDay(moment);
        var midnight = moment.Date;

        foreach (var boundary in boundaries)
        {
            if (boundary > minute)
            {
                return midnight.AddMinutes(boundary);
            }
        }

        // Ya pasaron todos los de hoy: el siguiente es el primero de manana.
        return midnight.AddDays(1).AddMinutes(boundaries.Min);
    }
}
