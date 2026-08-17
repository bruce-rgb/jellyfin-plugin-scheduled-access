using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ScheduledAccess.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduledAccess.ScheduledTasks;

/// <summary>
/// Aplica o retira las restricciones por etiquetas segun el dia de la semana.
/// </summary>
/// <remarks>
/// La tarea es idempotente: calcula el estado que deberia tener hoy y lo
/// escribe. Puede ejecutarse tantas veces como haga falta sin acumular
/// efectos, por eso ademas del disparador diario lleva uno horario que la
/// hace auto-reparable si el servidor estuvo apagado al cambiar el dia.
/// </remarks>
public class ApplyTagScheduleTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly ILogger<ApplyTagScheduleTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplyTagScheduleTask"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public ApplyTagScheduleTask(IUserManager userManager, ILogger<ApplyTagScheduleTask> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    // Estas cadenas y las del log van en ingles a proposito. El nombre de una
    // tarea programada es unico para todo el servidor, no por usuario, asi que
    // no admite localizacion: mostrarlo en ingles es la convencion del
    // ecosistema. Lo que si se localiza es la pagina de configuracion, que es
    // lo que ve cada usuario.

    /// <inheritdoc />
    public string Name => "Apply day-of-week restrictions";

    /// <inheritdoc />
    public string Key => "ScheduledAccessApplyTags";

    /// <inheritdoc />
    public string Description => "Adjusts each user's allowed or blocked tags according to the day of the week.";

    /// <inheritdoc />
    public string Category => "Scheduled Access";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Al arrancar: recupera el estado correcto si el servidor estuvo apagado.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        // A medianoche: el cambio de dia real.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.Zero.Ticks
        };

        // Cada hora: red de seguridad ante suspensiones o cambios de hora.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(1).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("Plugin instance is not available; skipping run");
            return;
        }

        var config = plugin.Configuration;
        var snapshots = config.Snapshots.ToList();
        var today = DateTime.Now.DayOfWeek;

        // Fase 1: aplicar las reglas vigentes hoy.
        var restrictedUserIds = new HashSet<Guid>();

        if (config.Enabled)
        {
            foreach (var rule in config.Rules)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Array.IndexOf(rule.Days, today) < 0)
                {
                    continue;
                }

                if (await ApplyRuleAsync(snapshots, rule, today, cancellationToken).ConfigureAwait(false))
                {
                    restrictedUserIds.Add(rule.UserId);
                }
            }
        }

        progress.Report(50);

        // Fase 2: deshacer toda instantanea que no respalde una restriccion
        // vigente. La restauracion la conducen las INSTANTANEAS, no las reglas:
        // si la condujeran las reglas, borrar una regla dejaria al usuario
        // restringido para siempre, porque ya no habria nada que recorrer.
        // Esto cubre a la vez regla borrada, dia desmarcado, usuario cambiado
        // y plugin desactivado.
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
        progress.Report(100);
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
        DayOfWeek today,
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
        if (snapshot is null)
        {
            snapshot = new PolicySnapshot
            {
                UserId = rule.UserId,
                AllowedTags = policy.AllowedTags.ToArray(),
                BlockedTags = policy.BlockedTags.ToArray()
            };

            snapshots.Add(snapshot);

            _logger.LogInformation(
                "Policy snapshot saved for {Username} (allowed={Allowed}, blocked={Blocked})",
                user.Username,
                snapshot.AllowedTags.Length,
                snapshot.BlockedTags.Length);
        }

        // El estado deseado se calcula SIEMPRE desde la instantanea, nunca
        // desde la politica actual, para que reejecutar no acumule etiquetas.
        if (rule.Mode == TagFilterMode.Block)
        {
            policy.AllowedTags = snapshot.AllowedTags.ToArray();
            policy.BlockedTags = snapshot.BlockedTags.Union(rule.Tags, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        else
        {
            policy.AllowedTags = rule.Tags.ToArray();
            policy.BlockedTags = snapshot.BlockedTags.ToArray();
        }

        await _userManager.UpdatePolicyAsync(rule.UserId, policy).ConfigureAwait(false);

        _logger.LogInformation(
            "Restriction applied to {Username} for {Day} in {Mode} mode with {Count} tags",
            user.Username,
            today,
            rule.Mode,
            rule.Tags.Length);

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

        await _userManager.UpdatePolicyAsync(snapshot.UserId, policy).ConfigureAwait(false);

        _logger.LogInformation("Policy restored for {Username}", user.Username);
        return true;
    }
}
