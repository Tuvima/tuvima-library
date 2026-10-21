using System.Globalization;
using System.Text.Json;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Events;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>
/// Decides who hears about something and how (Developer tools spec WP23). The Dashboard inbox gets the full message. Anything that
/// leaves the computer is held to four rules: it goes to administrators only; it stays silent during quiet hours; it is limited
/// to one message per channel and event a minute (the next one says how many were held back); and, unless the administrator
/// chose otherwise, it carries only a short generic title. A channel that fails five times in a row switches itself off and
/// shows Needs attention until it is turned back on.
/// </summary>
internal sealed class NotificationService(
    INotificationRepository repository,
    NotificationSettingsAccessor settingsStore,
    IEnumerable<INotificationChannel> builtInChannels,
    IEnumerable<INotificationChannelSource> channelSources,
    IAccountRepository accounts,
    IApplicationEventProducer events,
    TimeProvider clock,
    ILogger<NotificationService> logger)
{
    public const int FailuresBeforeOff = 5;
    public const int InboxLimit = 200;
    public const int MaxDetailLength = 500;
    public static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinimumSendInterval = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<INotificationChannel> _builtInChannels = builtInChannels.ToList();

    /// <summary>Built-in channels plus whatever switched-on plugins offer right now, so switching a plugin on or off changes the list without a restart.</summary>
    private IReadOnlyList<INotificationChannel> _channels => [.. _builtInChannels, .. channelSources.SelectMany(source => source.GetChannels())];

    /// <summary>Handles one event: writes the inbox message and sends to the routed channels. Never throws for a channel problem.</summary>
    public async Task DispatchAsync(NotificationRequest request, CancellationToken ct)
    {
        var definition = NotificationEventCatalogue.Find(request.EventId);
        if (definition is null)
        {
            logger.LogWarning("Notification event {EventId} is not in the catalogue and was dropped.", request.EventId);
            return;
        }

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var body = string.IsNullOrWhiteSpace(request.Detail) ? definition.Description : Truncate(request.Detail.Trim(), MaxDetailLength);
        await PublishApplicationEventAsync(definition, request, body, settings.IncludeDetails, now, ct).ConfigureAwait(false);

        var routes = settings.RouteFor(definition);
        if (routes.Count == 0)
        {
            return;
        }

        if (routes.Contains(NotificationChannelIds.Dashboard, StringComparer.Ordinal))
        {
            await repository.AddOrGroupAsync(definition.Id, definition.Title, body, $"{definition.Id}|{request.Subject}", now, GroupWindow, ct).ConfigureAwait(false);
            await repository.PruneAsync(InboxLimit, ct).ConfigureAwait(false);
        }

        var external = routes.Where(id => !string.Equals(id, NotificationChannelIds.Dashboard, StringComparison.Ordinal)).ToList();
        if (external.Count == 0 || settings.IsQuiet(clock.GetLocalNow()))
        {
            return;
        }

        var recipients = definition.AdminOnly ? await AdministratorRecipientsAsync(ct).ConfigureAwait(false) : [];
        if (recipients.Count == 0)
        {
            return;
        }

        foreach (var channelId in external)
        {
            var channel = _channels.FirstOrDefault(candidate => string.Equals(candidate.Id, channelId, StringComparison.Ordinal));
            if (channel is null || !channel.IsConfigured)
            {
                continue;
            }

            var state = await repository.GetChannelStateAsync(channel.Id, ct).ConfigureAwait(false);
            if (state?.State == NotificationChannelStates.Off)
            {
                continue;
            }

            var claim = await repository.TryClaimSendAsync(channel.Id, definition.Id, now, MinimumSendInterval, ct).ConfigureAwait(false);
            if (!claim.Allowed)
            {
                continue;
            }

            var outgoing = Outgoing(definition, body, settings.IncludeDetails, claim.HeldBack);
            var sent = await SendAndRecordAsync(channel, outgoing, recipients, ct).ConfigureAwait(false);
            if (sent.Success && claim.HeldBack > 0)
            {
                await repository.ConfirmSendAsync(channel.Id, definition.Id, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends a test message straight to one channel, ignoring routing, quiet hours and the rate limit.</summary>
    public async Task<NotificationTestResponse> SendTestAsync(string channelId, NotificationRecipient recipient, CancellationToken ct)
    {
        if (string.Equals(channelId, NotificationChannelIds.Dashboard, StringComparison.Ordinal))
        {
            await repository.AddOrGroupAsync(NotificationEventIds.SystemTest, NotificationEventCatalogue.TestTitle, "This is a test message. Nothing is wrong.", $"{NotificationEventIds.SystemTest}|{Guid.NewGuid():N}", clock.GetUtcNow(), GroupWindow, ct).ConfigureAwait(false);
            return new NotificationTestResponse { Success = true, Message = "The test message is in your inbox." };
        }

        var channel = _channels.FirstOrDefault(candidate => string.Equals(candidate.Id, channelId, StringComparison.Ordinal));
        if (channel is null)
        {
            return new NotificationTestResponse { Success = false, Message = "That channel was not found." };
        }

        if (!channel.IsConfigured)
        {
            return new NotificationTestResponse { Success = false, Message = $"{channel.Name} is not set up yet." };
        }

        var result = await SendAndRecordAsync(
            channel,
            new OutgoingNotification(NotificationEventIds.SystemTest, NotificationEventCatalogue.TestTitle, "This is a test message from your Library. Nothing is wrong."),
            [recipient],
            ct).ConfigureAwait(false);
        if (result.Success)
        {
            await repository.ResetChannelAsync(channel.Id, ct).ConfigureAwait(false);
        }

        return new NotificationTestResponse
        {
            Success = result.Success,
            Message = result.Success ? $"A test message was sent to {recipient.Email}." : result.Error ?? "The test message could not be sent.",
        };
    }

    public Task EnableChannelAsync(string channelId, CancellationToken ct) => repository.ResetChannelAsync(channelId, ct);

    public async Task<NotificationSettingsResponse> GetSettingsAsync(CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var states = (await repository.ListChannelStatesAsync(ct).ConfigureAwait(false))
            .ToDictionary(state => state.ChannelId, StringComparer.Ordinal);

        var response = new NotificationSettingsResponse
        {
            IncludeDetails = settings.IncludeDetails,
            QuietHours = new NotificationQuietHoursDto { Enabled = settings.QuietHoursEnabled, Start = settings.QuietStart, End = settings.QuietEnd },
        };
        foreach (var definition in NotificationEventCatalogue.All)
        {
            response.Events.Add(new NotificationEventDto
            {
                Id = definition.Id,
                Title = definition.Title,
                Description = definition.Description,
                Channels = [.. settings.RouteFor(definition)],
            });
        }

        response.Channels.Add(new NotificationChannelDto
        {
            Id = NotificationChannelIds.Dashboard,
            Name = "Dashboard inbox",
            IsExternal = false,
            IsConfigured = true,
            State = NotificationChannelStates.Healthy,
            StateLabel = "Working",
        });
        foreach (var channel in _channels)
        {
            states.TryGetValue(channel.Id, out var state);
            response.Channels.Add(new NotificationChannelDto
            {
                Id = channel.Id,
                Name = channel.Name,
                IsExternal = true,
                IsConfigured = channel.IsConfigured,
                State = state?.State ?? NotificationChannelStates.Healthy,
                StateLabel = StateLabel(channel, state),
                LastSuccessAt = state?.LastSuccessAt,
            });
        }

        return response;
    }

    /// <summary>Saves the changes. Returns a plain-words problem, or null when everything was saved.</summary>
    public async Task<string?> UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken ct)
    {
        var knownChannels = _channels.Select(channel => channel.Id).Append(NotificationChannelIds.Dashboard).ToHashSet(StringComparer.Ordinal);
        foreach (var (eventId, routed) in request.Routes)
        {
            if (NotificationEventCatalogue.Find(eventId) is null)
            {
                return $"'{eventId}' is not something the Library notifies about.";
            }

            if (routed.Any(id => !knownChannels.Contains(id)))
            {
                return "One of the channels does not exist.";
            }
        }

        if (request.QuietHours is { } quiet && (!IsClockTime(quiet.Start) || !IsClockTime(quiet.End)))
        {
            return "Quiet hours need times like 22:00 and 07:00.";
        }

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        foreach (var (eventId, routed) in request.Routes)
        {
            settings.Routes[eventId] = [.. routed.Distinct(StringComparer.Ordinal)];
        }

        if (request.QuietHours is { } hours)
        {
            settings.QuietHoursEnabled = hours.Enabled;
            settings.QuietStart = hours.Start;
            settings.QuietEnd = hours.End;
        }

        if (request.IncludeDetails is { } include)
        {
            settings.IncludeDetails = include;
        }

        await settingsStore.SaveAsync(settings, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Tells webhooks and plugin subscribers about the event too, because notifications share the one event list with them
    /// (spec D-W). Software outside the Library only gets the generic title unless "include details" is on, the same privacy
    /// rule as any message that leaves the computer. A problem here never stops the inbox or the channels.
    /// </summary>
    private async Task PublishApplicationEventAsync(NotificationEventDefinition definition, NotificationRequest request, string body, bool includeDetails, DateTimeOffset now, CancellationToken ct)
    {
        if (LibraryEvents.Find(definition.Id) is not { PublishedByNotifications: true })
        {
            return;
        }

        try
        {
            var payload = includeDetails
                ? JsonSerializer.SerializeToElement(new { title = definition.Title, detail = body })
                : JsonSerializer.SerializeToElement(new { title = definition.Title });
            await events.PublishAsync(new ApplicationEventDraft(
                definition.Id, 1, now, new ApplicationEventSubject("system-component", string.IsNullOrWhiteSpace(request.Subject) ? "library" : request.Subject), payload), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Notification event {EventId} could not be offered to webhooks and plugins.", definition.Id);
        }
    }

    private async Task<ChannelSendResult> SendAndRecordAsync(INotificationChannel channel, OutgoingNotification message, IReadOnlyList<NotificationRecipient> recipients, CancellationToken ct)
    {
        ChannelSendResult result;
        try
        {
            result = await channel.SendAsync(message, recipients, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification channel {Channel} threw while sending.", channel.Id);
            result = new ChannelSendResult(false, "The channel could not send the message.");
        }

        var state = await repository.RecordChannelResultAsync(channel.Id, result.Success, result.Error, clock.GetUtcNow(), FailuresBeforeOff, ct).ConfigureAwait(false);
        if (state.State == NotificationChannelStates.Off)
        {
            logger.LogWarning("Notification channel {Channel} was switched off after {Count} failures in a row.", channel.Id, state.ConsecutiveFailures);
        }

        return result;
    }

    private static OutgoingNotification Outgoing(NotificationEventDefinition definition, string body, bool includeDetails, int heldBack)
    {
        var text = includeDetails ? body : "Open your Library to see what happened.";
        if (heldBack > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $"\n\n{heldBack} more like this were held back in the last minute.");
        }

        return new OutgoingNotification(definition.Id, definition.Title, text);
    }

    private async Task<IReadOnlyList<NotificationRecipient>> AdministratorRecipientsAsync(CancellationToken ct)
    {
        var all = await accounts.GetAllAsync(ct).ConfigureAwait(false);
        return all
            .Where(account => account is { IsAdministrator: true, IsEnabled: true, HasEmail: true })
            .Select(account => new NotificationRecipient(account.Id, account.Email!))
            .ToList();
    }

    private static string StateLabel(INotificationChannel channel, StoredChannelState? state)
    {
        if (!channel.IsConfigured)
        {
            return "Not set up";
        }

        return state?.State switch
        {
            NotificationChannelStates.Off => "Switched off after repeated failures. Turn it back on once it is fixed.",
            NotificationChannelStates.NeedsAttention => string.IsNullOrWhiteSpace(state.LastError) ? "Needs attention" : $"Needs attention: {state.LastError}",
            _ => "Working",
        };
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static bool IsClockTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
