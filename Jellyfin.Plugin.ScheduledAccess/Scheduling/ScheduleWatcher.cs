using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduledAccess.Scheduling;

/// <summary>
/// Servicio de fondo que despierta justo en cada limite de franja.
/// </summary>
/// <remarks>
/// Es la alternativa a un disparador por intervalo. Una tarea programada cada
/// pocos minutos seria imprecisa y llenaria el historial de tareas del panel
/// con cientos de entradas diarias; este servicio calcula el proximo limite y
/// duerme exactamente hasta el, sin dejar rastro en ese historial.
///
/// El sueno se limita a una hora como red de seguridad: si el equipo suspende,
/// cambia la hora del sistema o entra el horario de verano, la espera larga
/// podria despertar tarde. Volver a calcular cada hora corrige la deriva sin
/// coste apreciable, porque una pasada sin cambios no escribe nada.
/// </remarks>
public class ScheduleWatcher : BackgroundService
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    private readonly ScheduleEnforcer _enforcer;
    private readonly ILogger<ScheduleWatcher> _logger;

    // Se releva en cada vuelta para poder interrumpir el sueno cuando cambia
    // la configuracion: si acaban de anadir una franja que empieza antes del
    // proximo despertar, hay que recalcular ya.
    private CancellationTokenSource? _wakeUp;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleWatcher"/> class.
    /// </summary>
    /// <param name="enforcer">Instance of the <see cref="ScheduleEnforcer"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public ScheduleWatcher(ScheduleEnforcer enforcer, ILogger<ScheduleWatcher> logger)
    {
        _enforcer = enforcer;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Schedule watcher started");

        Plugin.ConfigurationUpdated += OnConfigurationUpdated;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;

                try
                {
                    await _enforcer.ApplyAsync(now, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
                    // Un fallo aplicando no debe matar el servicio: si muere,
                    // las franjas dejan de conmutar en silencio hasta el
                    // siguiente reinicio del servidor.
                    _logger.LogError(ex, "Failed to apply the schedule; will retry at the next boundary");
                }
#pragma warning restore CA1031

                await SleepUntilNextBoundaryAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Plugin.ConfigurationUpdated -= OnConfigurationUpdated;
            _logger.LogInformation("Schedule watcher stopped");
        }
    }

    private async Task SleepUntilNextBoundaryAsync(CancellationToken stoppingToken)
    {
        var rules = Plugin.Instance?.Configuration.Rules ?? [];
        var now = DateTime.Now;
        var next = ScheduleResolver.NextBoundary(rules, now);

        var delay = next - now;
        if (delay > MaxSleep)
        {
            delay = MaxSleep;
        }
        else if (delay <= TimeSpan.Zero)
        {
            // No deberia ocurrir, pero dormir cero giraria en vacio quemando CPU.
            delay = TimeSpan.FromSeconds(1);
        }

        _logger.LogDebug(
            "Next schedule check in {Delay} (boundary at {Next})",
            delay,
            next.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Interlocked.Exchange(ref _wakeUp, wake)?.Dispose();

        try
        {
            await Task.Delay(delay, wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // O paran el servicio, o cambio la configuracion. En ambos casos
            // el bucle decide que hacer mirando stoppingToken.
        }
    }

    private void OnConfigurationUpdated(object? sender, EventArgs e)
    {
        _logger.LogDebug("Configuration changed; re-evaluating the schedule");
        Volatile.Read(ref _wakeUp)?.Cancel();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Interlocked.Exchange(ref _wakeUp, null)?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
