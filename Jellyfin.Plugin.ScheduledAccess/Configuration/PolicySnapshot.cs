using System;

namespace Jellyfin.Plugin.ScheduledAccess.Configuration;

#pragma warning disable CA1819

/// <summary>
/// Copia de las etiquetas que tenia el usuario antes de que el plugin
/// tocara su politica. Es lo que permite restaurar el estado original.
/// </summary>
/// <remarks>
/// Se persiste en el XML de configuracion del plugin a proposito: si el
/// servidor se apaga con una restriccion activa, la instantanea sobrevive
/// y la siguiente ejecucion puede deshacerla. Sin esto, un usuario podria
/// quedar restringido indefinidamente.
/// </remarks>
public class PolicySnapshot
{
    /// <summary>
    /// Gets or sets el id del usuario al que pertenece la instantanea.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets las etiquetas permitidas originales.
    /// </summary>
    public string[] AllowedTags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets las etiquetas bloqueadas originales.
    /// </summary>
    public string[] BlockedTags { get; set; } = Array.Empty<string>();
}

#pragma warning restore CA1819
