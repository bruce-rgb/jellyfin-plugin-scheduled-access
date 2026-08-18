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
    private readonly PlaybackGuard _guard;
    private readonly ILogger<ScheduleWatcher> _logger;

    // Se releva en cada vuelta para poder interrumpir el sueno cuando cambia
    // la configuracion: si acaban de anadir una franja que empieza antes del
    // proximo despertar, hay que recalcular ya.
    private CancellationTokenSource? _wakeUp;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleWatcher"/> class.
    /// </summary>
    /// <param name="enforcer">Instance of the <see cref="ScheduleEnforcer"/>.</param>
    /// <param name="guard">Instance of the <see cref="PlaybackGuard"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public ScheduleWatcher(ScheduleEnforcer enforcer, PlaybackGuard guard, ILogger<ScheduleWatcher> logger)
    {
        _enforcer = enforcer;
        _guard = guard;
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

                    // Despues de aplicar, nunca antes: cortar una reproduccion
                    // que la politica todavia permite dejaria al usuario mirando
                    // un item que sigue viendo en la biblioteca.
                    await _guard.EnforceAsync(now, stoppingToken).ConfigureAwait(false);
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

                await SleepUntilNextWakeUpAsync(now, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Plugin.ConfigurationUpdated -= OnConfigurationUpdated;
            _logger.LogInformation("Schedule watcher stopped");
        }
    }

    /// <param name="moment">
    /// El instante que acaba de evaluarse. El proximo despertar se calcula
    /// desde EL, no desde la hora actual: aplicar y revisar sesiones lleva su
    /// tiempo, y releer el reloj despues podria haber cruzado ya el limite que
    /// toca atender, con lo que se descartaria por pasado.
    /// </param>
    /// <param name="stoppingToken">Token de parada del servicio.</param>
    private async Task SleepUntilNextWakeUpAsync(DateTime moment, CancellationToken stoppingToken)
    {
        // El token se crea ANTES de leer la configuracion. Si alguien guarda
        // justo mientras se calcula el proximo despertar, la cancelacion cae
        // sobre este token y no sobre uno ya descartado, que se perderia y
        // dejaria al vigilante durmiendo con reglas viejas.
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Interlocked.Exchange(ref _wakeUp, wake)?.Dispose();

        var config = Plugin.Instance?.Configuration;
        var rules = config?.Rules ?? [];

        // Con aviso previo hay que despertar antes del limite. Solo cuenta si
        // el corte esta activado: sin el no hay nada que anunciar.
        var warning = config is not null && config.StopPlayback ? config.WarningMinutes : 0;

        var next = ScheduleResolver.NextWakeUp(rules, moment, warning);

        // La espera se mide contra el reloj real, aunque el objetivo salga de
        // moment: entre una cosa y otra puede haber pasado tiempo.
        var delay = next - DateTime.Now;
        if (delay > MaxSleep)
        {
            delay = MaxSleep;
        }
        else if (delay <= TimeSpan.Zero)
        {
            // La pasada tardo mas que lo que quedaba. Se da una vuelta corta y
            // se recalcula, en vez de dormir cero y girar en vacio.
            delay = TimeSpan.FromSeconds(1);
        }

        _logger.LogDebug(
            "Next schedule check in {Delay} (waking at {Next})",
            delay,
            next.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

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

        try
        {
            Volatile.Read(ref _wakeUp)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // El vigilante no esta durmiendo, esta en plena pasada: su token ya
            // se desecho. No hay nada que despertar, y la vuelta en curso leera
            // la configuracion nueva al calcular el siguiente despertar.
            //
            // Sin este catch la excepcion sube hasta UpdateConfiguration y el
            // guardado falla desde la pagina de configuracion.
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Interlocked.Exchange(ref _wakeUp, null)?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
