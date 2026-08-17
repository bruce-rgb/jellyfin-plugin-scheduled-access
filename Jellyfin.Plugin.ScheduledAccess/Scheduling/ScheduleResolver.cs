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
    /// Momento en que cambia el estado por proxima vez, para dormir hasta el.
    /// </summary>
    /// <param name="rules">Reglas configuradas.</param>
    /// <param name="moment">Instante de referencia.</param>
    /// <returns>
    /// El siguiente limite de franja, o la proxima medianoche si no hay
    /// ninguno despues. Nunca devuelve un instante en el pasado ni el actual.
    /// </returns>
    public static DateTime NextBoundary(IEnumerable<ScheduleRule> rules, DateTime moment)
    {
        ArgumentNullException.ThrowIfNull(rules);

        // La medianoche entra siempre: es cuando cambia el dia de la semana,
        // aunque ninguna regla tenga un limite ahi.
        var boundaries = new SortedSet<int> { 0 };

        foreach (var rule in rules)
        {
            boundaries.Add(Math.Clamp(rule.StartMinutes, 0, ScheduleRule.MinutesPerDay));
            boundaries.Add(Math.Clamp(rule.EndMinutes, 0, ScheduleRule.MinutesPerDay));
        }

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
