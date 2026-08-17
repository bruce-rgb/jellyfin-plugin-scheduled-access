using System;

namespace Jellyfin.Plugin.ScheduledAccess.Configuration;

#pragma warning disable CA1819

/// <summary>
/// Copia de lo que tenia el usuario antes de que el plugin tocara su
/// politica. Es lo que permite restaurar el estado original.
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

    /// <summary>
    /// Gets or sets a value indicating whether esta instantanea incluye el
    /// estado de bibliotecas.
    /// </summary>
    /// <remarks>
    /// NO es redundante, y quitarlo deja usuarios sin ninguna biblioteca.
    ///
    /// Las instantaneas guardadas antes de que el plugin manejara bibliotecas
    /// no llevan esos elementos en el XML, asi que al releerlas quedan con los
    /// valores por defecto: sin carpetas y con EnableAllFolders en false.
    /// Restaurar eso literalmente le quitaria al usuario el acceso a todo.
    /// Con este marcador, una instantanea antigua solo restaura etiquetas y
    /// deja las bibliotecas como esten.
    /// </remarks>
    public bool HasFolderState { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether el usuario tenia acceso a todas
    /// las bibliotecas. Solo es valido si <see cref="HasFolderState"/> es true.
    /// </summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets las bibliotecas a las que tenia acceso. Solo es valido si
    /// <see cref="HasFolderState"/> es true.
    /// </summary>
    public Guid[] EnabledFolders { get; set; } = Array.Empty<Guid>();
}

#pragma warning restore CA1819
