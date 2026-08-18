using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ScheduledAccess.Localization;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduledAccess.Scheduling;

/// <summary>
/// Avisa y corta las reproducciones que una regla deja de permitir.
/// </summary>
/// <remarks>
/// Existe porque cambiar la politica de un usuario no alcanza a un stream ya
/// abierto. Jellyfin comprueba los permisos al listar, al resolver un item y al
/// autorizar una reproduccion nueva -- con la restriccion puesta, darle a play
/// devuelve cero fuentes --, pero no vuelve a comprobarlos segmento a segmento.
/// Lo que estaba sonando cuando entro la restriccion sigue sonando.
///
/// El corte se dispara cuando EMPIEZA una restriccion, no cuando termina: al
/// terminar una franja el contenido vuelve a estar permitido y no hay nada que
/// interrumpir.
/// </remarks>
public class PlaybackGuard
{
    private readonly ISessionManager _sessionManager;
    private readonly IServerConfigurationManager _serverConfiguration;
    private readonly ILogger<PlaybackGuard> _logger;

    // Un aviso por sesion e item, para que las pasadas de seguridad que caen
    // dentro de la ventana no lo repitan cada hora.
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);
    private long _warnedBoundary;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackGuard"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="serverConfiguration">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public PlaybackGuard(
        ISessionManager sessionManager,
        IServerConfigurationManager serverConfiguration,
        ILogger<PlaybackGuard> logger)
    {
        _sessionManager = sessionManager;
        _serverConfiguration = serverConfiguration;
        _logger = logger;
    }

    private string Culture => _serverConfiguration.Configuration.UICulture;

    /// <summary>
    /// Revisa lo que se esta reproduciendo y actua si procede.
    /// </summary>
    /// <param name="moment">Instante de referencia, en hora local.</param>
    /// <param name="cancellationToken">Token de cancelacion.</param>
    /// <returns>Tarea que completa cuando se han revisado todas las sesiones.</returns>
    public async Task EnforceAsync(DateTime moment, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;

        // Sin la opcion activada el plugin no toca ninguna sesion, que es como
        // se comporto hasta la version que introdujo esto.
        if (config is null || !config.Enabled || !config.StopPlayback)
        {
            return;
        }

        var active = ScheduleResolver.ActiveRules(config.Rules, moment)
            .ToDictionary(rule => rule.UserId);

        // El limite real de la franja, sin el adelanto del aviso: es el momento
        // que hay que anunciar y contra el que se predice.
        var boundary = ScheduleResolver.NextBoundary(config.Rules, moment);
        var withinWarningWindow = config.WarningMinutes > 0
            && boundary - moment <= TimeSpan.FromMinutes(config.WarningMinutes);

        var upcoming = withinWarningWindow
            ? ScheduleResolver.ActiveRules(config.Rules, boundary).ToDictionary(rule => rule.UserId)
            : null;

        ForgetWarningsFromOtherBoundaries(boundary);

        foreach (var session in _sessionManager.Sessions.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = session.FullNowPlayingItem;
            if (item is null)
            {
                continue;
            }

            var tags = item.GetInheritedTags();
            var libraryId = item.GetTopParent()?.Id ?? Guid.Empty;

            active.TryGetValue(session.UserId, out var rule);

            try
            {
                if (!ContentVisibility.IsAllowed(tags, libraryId, rule))
                {
                    await StopAsync(session, item, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (upcoming is null)
                {
                    continue;
                }

                upcoming.TryGetValue(session.UserId, out var next);

                if (!ContentVisibility.IsAllowed(tags, libraryId, next))
                {
                    await WarnAsync(session, item, boundary, cancellationToken).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                // Una sesion que se cae mientras se la avisa no debe impedir
                // que se revisen las demas.
                _logger.LogError(ex, "Failed to act on session {SessionId}", session.Id);
            }
#pragma warning restore CA1031
        }
    }

    private static bool Supports(SessionInfo session, GeneralCommandType command)
    {
        IReadOnlyList<GeneralCommandType>? supported = session.SupportedCommands;
        return supported is not null && supported.Contains(command);
    }

    private void ForgetWarningsFromOtherBoundaries(DateTime boundary)
    {
        if (Interlocked.Exchange(ref _warnedBoundary, boundary.Ticks) != boundary.Ticks)
        {
            _warned.Clear();
        }
    }

    private async Task StopAsync(SessionInfo session, BaseItem item, CancellationToken cancellationToken)
    {
        if (!session.SupportsRemoteControl)
        {
            // Se registra como aviso y no como informacion: para quien configuro
            // el corte, un cliente que lo ignora es justo lo que necesita saber.
            _logger.LogWarning(
                "Cannot stop {Item} for {User}: the {Client} client does not accept remote control",
                item.Name,
                session.UserName,
                session.Client);
            return;
        }

        // El mensaje va antes del corte: despues, el reproductor suele cambiar
        // de pantalla y el aviso pasaria desapercibido.
        await SendMessageAsync(
            session,
            PluginStrings.Get(Culture, "PlaybackStoppedHeader", "Not available now"),
            PluginStrings.Get(Culture, "PlaybackStoppedText", "This content is outside the allowed schedule."),
            cancellationToken).ConfigureAwait(false);

        await _sessionManager.SendPlaystateCommand(
            null,
            session.Id,
            new PlaystateRequest { Command = PlaystateCommand.Stop },
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Stopped {Item} for {User} on {Client}: outside the allowed schedule",
            item.Name,
            session.UserName,
            session.Client);
    }

    private async Task WarnAsync(SessionInfo session, BaseItem item, DateTime boundary, CancellationToken cancellationToken)
    {
        var key = session.Id + "|" + item.Id.ToString("N", CultureInfo.InvariantCulture);
        if (!_warned.TryAdd(key, 0))
        {
            return;
        }

        var at = boundary.ToString("HH:mm", CultureInfo.InvariantCulture);
        var text = string.Format(
            CultureInfo.InvariantCulture,
            PluginStrings.Get(Culture, "PlaybackWarningText", "Playback will stop at {0}."),
            at);

        await SendMessageAsync(
            session,
            PluginStrings.Get(Culture, "PlaybackWarningHeader", "Playback will stop soon"),
            text,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Warned {User} that {Item} will stop at {Boundary}",
            session.UserName,
            item.Name,
            at);
    }

    private async Task SendMessageAsync(SessionInfo session, string header, string text, CancellationToken cancellationToken)
    {
        if (!Supports(session, GeneralCommandType.DisplayMessage))
        {
            _logger.LogDebug(
                "The {Client} client cannot display messages; skipping the notice for {User}",
                session.Client,
                session.UserName);
            return;
        }

        await _sessionManager.SendMessageCommand(
            null,
            session.Id,
            new MessageCommand
            {
                Header = header,
                Text = text,
                TimeoutMs = 15000
            },
            cancellationToken).ConfigureAwait(false);
    }
}
