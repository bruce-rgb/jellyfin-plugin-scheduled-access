using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.ScheduledAccess.Localization;

/// <summary>
/// Lee del lado del servidor los mismos archivos de idioma que usa la pagina
/// de configuracion.
/// </summary>
/// <remarks>
/// Los mensajes que se envian a un reproductor los lee alguien que a lo mejor
/// no ha visto nunca el panel de administracion -- tipicamente un menor --, asi
/// que mandarlos siempre en ingles seria una regresion respecto al resto del
/// plugin, que ya esta traducido.
///
/// El idioma sale de <c>UICulture</c> del servidor y no del usuario: Jellyfin
/// no asocia un idioma a una sesion, solo a una preferencia del cliente web que
/// no viaja hasta aqui. Es una aproximacion, pero acierta en el caso habitual
/// de una familia con un servidor.
/// </remarks>
internal static class PluginStrings
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Traduce una clave al idioma del servidor.
    /// </summary>
    /// <param name="culture">Cultura configurada, por ejemplo <c>es-ES</c>.</param>
    /// <param name="key">Clave del archivo de idioma.</param>
    /// <param name="fallback">Texto a devolver si la clave no esta traducida.</param>
    /// <returns>El texto traducido, o <paramref name="fallback"/>.</returns>
    public static string Get(string? culture, string key, string fallback)
    {
        var strings = Cache.GetOrAdd(Normalize(culture), Load);

        return strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string Normalize(string? culture)
    {
        var tag = (culture ?? string.Empty).Split('-', '_')[0];

        foreach (var language in Plugin.SupportedLanguages)
        {
            if (string.Equals(language, tag, StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        return Plugin.SupportedLanguages[0];
    }

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var name = string.Format(
            CultureInfo.InvariantCulture,
            "{0}.Locale.{1}.json",
            typeof(PluginStrings).Namespace?.Replace(".Localization", string.Empty, StringComparison.Ordinal),
            language);

        try
        {
            using var stream = typeof(PluginStrings).Assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return new Dictionary<string, string>();
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            // Un archivo de idioma ilegible no puede tumbar el servicio: sin
            // traduccion los mensajes salen en ingles, que es aceptable.
            return new Dictionary<string, string>();
        }
    }
}
