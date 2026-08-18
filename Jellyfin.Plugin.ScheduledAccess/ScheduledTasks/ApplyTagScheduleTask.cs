using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ScheduledAccess.Scheduling;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ScheduledAccess.ScheduledTasks;

/// <summary>
/// Permite forzar la aplicacion del horario desde el panel de control.
/// </summary>
/// <remarks>
/// Quien conmuta las franjas en el dia a dia es <see cref="ScheduleWatcher"/>,
/// que despierta justo en cada limite. Esta tarea existe por dos motivos: dar
/// un boton para lanzarlo a mano al diagnosticar, y servir de red de seguridad
/// diaria por si el servicio de fondo no llegara a arrancar.
///
/// Por eso no lleva disparador por intervalo: duplicaria el trabajo del
/// vigilante y llenaria el historial de tareas del panel.
/// </remarks>
public class ApplyTagScheduleTask : IScheduledTask
{
    private readonly ScheduleEnforcer _enforcer;
    private readonly PlaybackGuard _guard;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplyTagScheduleTask"/> class.
    /// </summary>
    /// <param name="enforcer">Instance of the <see cref="ScheduleEnforcer"/>.</param>
    /// <param name="guard">Instance of the <see cref="PlaybackGuard"/>.</param>
    public ApplyTagScheduleTask(ScheduleEnforcer enforcer, PlaybackGuard guard)
    {
        _enforcer = enforcer;
        _guard = guard;
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
    public string Description => "Adjusts each user's allowed or blocked tags according to the current day and time slot.";

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

        // A medianoche: red de seguridad diaria, por si el vigilante no arranco.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.Zero.Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var now = DateTime.Now;

        await _enforcer.ApplyAsync(now, cancellationToken).ConfigureAwait(false);
        await _guard.EnforceAsync(now, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }
}
