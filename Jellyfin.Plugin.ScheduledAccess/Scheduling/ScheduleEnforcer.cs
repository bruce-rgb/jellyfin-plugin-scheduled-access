using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ScheduledAccess.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduledAccess.Scheduling;

/// <summary>
/// Aplica y deshace las restricciones. Es el unico sitio que escribe politicas.
/// </summary>
/// <remarks>
/// Lo invocan tanto el vigilante de franjas como la tarea programada, asi que
/// serializa sus ejecuciones: dos pasadas simultaneas podrian entrelazar la
/// lectura y la escritura de las instantaneas y dejar registrado como "estado
/// original" uno ya restringido, que es la corrupcion irreversible contra la
/// que existe todo este mecanismo.
/// </remarks>
public class ScheduleEnforcer : IDisposable
{
    private readonly IUserManager _userManager;
    private readonly ILogger<ScheduleEnforcer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleEnforcer"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public ScheduleEnforcer(IUserManager userManager, ILogger<ScheduleEnforcer> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Lleva las politicas al estado que corresponde al instante indicado.
    /// </summary>
    /// <param name="moment">Instante de referencia, en hora local.</param>
    /// <param name="cancellationToken">Token de cancelacion.</param>
    /// <returns>Tarea que completa cuando el estado esta aplicado.</returns>
    public async Task ApplyAsync(DateTime moment, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyCoreAsync(moment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyCoreAsync(DateTime moment, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("Plugin instance is not available; skipping run");
            return;
        }

        var config = plugin.Configuration;
        var snapshots = config.Snapshots.ToList();
        var restrictedUserIds = new HashSet<Guid>();

        // Fase 1: aplicar la regla vigente ahora mismo para cada usuario.
        if (config.Enabled)
        {
            foreach (var rule in ScheduleResolver.ActiveRules(config.Rules, moment))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await ApplyRuleAsync(snapshots, rule, moment, cancellationToken).ConfigureAwait(false))
                {
                    restrictedUserIds.Add(rule.UserId);
                }
            }
        }

        // Fase 2: deshacer toda instantanea que no respalde una restriccion
        // vigente. La restauracion la conducen las INSTANTANEAS, no las reglas:
        // si la condujeran las reglas, borrar una regla dejaria al usuario
        // restringido para siempre, porque ya no habria nada que recorrer.
        // Esto cubre a la vez regla borrada, franja terminada, dia desmarcado,
        // usuario cambiado y plugin desactivado.
        foreach (var snapshot in snapshots.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (restrictedUserIds.Contains(snapshot.UserId))
            {
                continue;
            }

            // Solo se descarta si la restauracion se completo. Si fallo, la
            // instantanea sobrevive y el siguiente disparo lo reintenta:
            // descartarla perderia el estado original de forma irreversible.
            if (await RestoreAsync(snapshot, cancellationToken).ConfigureAwait(false))
            {
                snapshots.Remove(snapshot);
            }
        }

        config.Snapshots = snapshots.ToArray();
        plugin.SaveConfiguration();
    }

    /// <summary>
    /// Aplica una regla a su usuario.
    /// </summary>
    /// <returns>
    /// <c>true</c> si la restriccion quedo aplicada; <c>false</c> si no se pudo
    /// (usuario inexistente o politica ilegible), en cuyo caso la instantanea
    /// asociada debe restaurarse en la fase 2.
    /// </returns>
    private async Task<bool> ApplyRuleAsync(
        List<PolicySnapshot> snapshots,
        ScheduleRule rule,
        DateTime moment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = _userManager.GetUserById(rule.UserId);
        if (user is null)
        {
            _logger.LogWarning("Rule targets a non-existent user {UserId}; ignoring it", rule.UserId);
            return false;
        }

        var policy = _userManager.GetUserDto(user).Policy;
        if (policy is null)
        {
            _logger.LogWarning("Could not read the policy for {Username}", user.Username);
            return false;
        }

        var snapshot = snapshots.Find(s => s.UserId == rule.UserId);

        // Primera vez que restringimos a este usuario: guardamos su estado
        // original antes de tocarlo, y lo persistimos para poder deshacerlo
        // aunque el servidor se apague con la restriccion puesta.
        //
        // Al encadenar dos franjas seguidas NO se retoma: la instantanea solo
        // se crea si no existe, y solo se borra cuando ninguna regla aplica.
        if (snapshot is null)
        {
            snapshot = new PolicySnapshot
            {
                UserId = rule.UserId,
                AllowedTags = policy.AllowedTags.ToArray(),
                BlockedTags = policy.BlockedTags.ToArray(),
                HasFolderState = true,
                EnableAllFolders = policy.EnableAllFolders,
                EnabledFolders = policy.EnabledFolders.ToArray()
            };

            snapshots.Add(snapshot);

            _logger.LogInformation(
                "Policy snapshot saved for {Username} (allowed={Allowed}, blocked={Blocked}, allFolders={AllFolders}, folders={Folders})",
                user.Username,
                snapshot.AllowedTags.Length,
                snapshot.BlockedTags.Length,
                snapshot.EnableAllFolders,
                snapshot.EnabledFolders.Length);
        }

        // El estado deseado se calcula SIEMPRE desde la instantanea, nunca
        // desde la politica actual, para que reejecutar no acumule etiquetas.
        var desiredAllowed = rule.Mode == TagFilterMode.AllowOnly
            ? rule.Tags.ToArray()
            : snapshot.AllowedTags.ToArray();

        var desiredBlocked = rule.Mode == TagFilterMode.Block
            ? snapshot.BlockedTags.Union(rule.Tags, StringComparer.OrdinalIgnoreCase).ToArray()
            : snapshot.BlockedTags.ToArray();

        // Sin bibliotecas en la regla se devuelven las de la instantanea, no se
        // dejan como esten: asi quitar las bibliotecas de una regla las
        // restaura, igual que quitar etiquetas.
        var restrictsLibraries = rule.LibraryIds.Length > 0;
        var desiredAllFolders = restrictsLibraries ? false : snapshot.EnableAllFolders;
        var desiredFolders = restrictsLibraries
            ? rule.LibraryIds.ToArray()
            : snapshot.EnabledFolders.ToArray();

        // Con franjas horarias esta pasada corre muchas mas veces que antes, y
        // casi siempre sin cambios. Escribir la politica invalida cachés del
        // servidor y genera ruido en el log, asi que solo se escribe si algo
        // cambia de verdad.
        if (SameTags(policy.AllowedTags, desiredAllowed)
            && SameTags(policy.BlockedTags, desiredBlocked)
            && policy.EnableAllFolders == desiredAllFolders
            && SameGuids(policy.EnabledFolders, desiredFolders))
        {
            return true;
        }

        policy.AllowedTags = desiredAllowed;
        policy.BlockedTags = desiredBlocked;
        policy.EnableAllFolders = desiredAllFolders;
        policy.EnabledFolders = desiredFolders;

        await _userManager.UpdatePolicyAsync(rule.UserId, policy).ConfigureAwait(false);

        _logger.LogInformation(
            "Restriction applied to {Username} at {Time} in {Mode} mode with {Tags} tags and {Libraries} libraries",
            user.Username,
            moment.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            rule.Mode,
            rule.Tags.Length,
            restrictsLibraries ? rule.LibraryIds.Length : 0);

        return true;
    }

    /// <summary>
    /// Devuelve la politica de un usuario al estado guardado en su instantanea.
    /// </summary>
    /// <returns>
    /// <c>true</c> si la instantanea puede descartarse (se restauro, o el
    /// usuario ya no existe); <c>false</c> si debe conservarse para reintentar.
    /// </returns>
    private async Task<bool> RestoreAsync(PolicySnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = _userManager.GetUserById(snapshot.UserId);
        if (user is null)
        {
            // El usuario ya no existe: no hay politica que restaurar, asi que
            // la instantanea solo es ruido.
            _logger.LogInformation("Discarded orphaned snapshot for {UserId}", snapshot.UserId);
            return true;
        }

        var policy = _userManager.GetUserDto(user).Policy;
        if (policy is null)
        {
            _logger.LogWarning(
                "Could not read the policy for {Username}; keeping the snapshot to retry",
                user.Username);
            return false;
        }

        policy.AllowedTags = snapshot.AllowedTags.ToArray();
        policy.BlockedTags = snapshot.BlockedTags.ToArray();

        // Las bibliotecas solo se tocan si la instantanea las capturo. Una
        // guardada por una version anterior no las lleva, y aplicar sus valores
        // por defecto dejaria al usuario sin acceso a ninguna.
        if (snapshot.HasFolderState)
        {
            policy.EnableAllFolders = snapshot.EnableAllFolders;
            policy.EnabledFolders = snapshot.EnabledFolders.ToArray();
        }
        else
        {
            _logger.LogWarning(
                "Snapshot for {Username} predates library support; restoring tags only and leaving libraries untouched",
                user.Username);
        }

        await _userManager.UpdatePolicyAsync(snapshot.UserId, policy).ConfigureAwait(false);

        _logger.LogInformation("Policy restored for {Username}", user.Username);
        return true;
    }

    private static bool SameTags(string[] left, string[] right)
        => left.Length == right.Length
            && !left.Except(right, StringComparer.OrdinalIgnoreCase).Any();

    private static bool SameGuids(Guid[] left, Guid[] right)
        => left.Length == right.Length && !left.Except(right).Any();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the resources used by this instance.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }
    }
}
