using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Notifications;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Contracts.Paging;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The notification service's actions (Developer tools spec WP23): the Dashboard inbox, the routing and quiet-hours settings,
/// and "Send test" for a channel. Administrators only; a profile or paired app never sees another person's alerts.
/// </summary>
public static class NotificationEndpoints
{
    private const int DefaultListLimit = 50;
    private const int MaxListLimit = 200;

    internal static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/notifications").WithTags("Notifications").RequireEffectiveAdministrator();

        group.MapGet("", async ([FromServices] INotificationRepository repository, int? limit, CancellationToken ct) =>
        {
            var page = PagedRequest.From(null, limit, DefaultListLimit, MaxListLimit);
            var items = await repository.ListAsync(page.Limit, ct).ConfigureAwait(false);
            var unread = await repository.CountUnreadAsync(ct).ConfigureAwait(false);
            return Results.Ok(new NotificationListResponse
            {
                UnreadCount = unread,
                Items = [.. items.Select(item => new NotificationItemDto
                {
                    Id = item.Id,
                    EventId = item.EventId,
                    Title = item.Title,
                    Body = item.Body,
                    OccurrenceCount = item.OccurrenceCount,
                    UpdatedAt = item.UpdatedAt,
                    IsRead = item.ReadAt is not null,
                })],
            });
        })
        .WithName("ListNotifications")
        .WithSummary("The Dashboard inbox, newest first, with how many messages are unread.")
        .Produces<NotificationListResponse>(StatusCodes.Status200OK);

        group.MapPost("/read", async (MarkNotificationsReadRequest request, [FromServices] INotificationRepository repository, [FromServices] TimeProvider clock, CancellationToken ct) =>
        {
            await repository.MarkReadAsync(request.Ids, clock.GetUtcNow(), ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("MarkNotificationsRead")
        .WithSummary("Marks the given inbox messages read, or every unread message when no ids are given.")
        .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/settings", async ([FromServices] NotificationService service, CancellationToken ct) =>
            Results.Ok(await service.GetSettingsAsync(ct).ConfigureAwait(false)))
        .WithName("GetNotificationSettings")
        .WithSummary("Where each kind of event goes, how each channel is doing, quiet hours, and whether details may leave this computer.")
        .Produces<NotificationSettingsResponse>(StatusCodes.Status200OK);

        group.MapPut("/settings", async (UpdateNotificationSettingsRequest request, [FromServices] NotificationService service, CancellationToken ct) =>
        {
            var problem = await service.UpdateSettingsAsync(request, ct).ConfigureAwait(false);
            return problem is null
                ? Results.Ok(await service.GetSettingsAsync(ct).ConfigureAwait(false))
                : ApiErrors.BadRequest(problem);
        })
        .WithName("UpdateNotificationSettings")
        .WithSummary("Changes where events go, quiet hours or the include-details choice. Events left out of the request keep their routes.")
        .Produces<NotificationSettingsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/channels/{channelId}/test", async (
            string channelId,
            HttpContext context,
            [FromServices] IRequestAuthorityResolver authorityResolver,
            [FromServices] MediaEngine.Domain.Contracts.IAccountRepository accounts,
            [FromServices] NotificationService service,
            CancellationToken ct) =>
        {
            var authority = await authorityResolver.ResolveAsync(context, ct).ConfigureAwait(false);
            var account = authority.AccountId is { } accountId
                ? await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false)
                : null;
            var recipient = new NotificationRecipient(account?.Id ?? Guid.Empty, account?.Email ?? string.Empty);
            if (channelId != NotificationChannelIds.Dashboard && string.IsNullOrWhiteSpace(recipient.Email))
            {
                return Results.Ok(new NotificationTestResponse { Success = false, Message = "Your account has no email address to send the test to." });
            }

            return Results.Ok(await service.SendTestAsync(channelId, recipient, ct).ConfigureAwait(false));
        })
        .WithName("SendNotificationTest")
        .WithSummary("Sends a test message through one channel to the signed-in administrator. A working test also turns a switched-off channel back on.")
        .Produces<NotificationTestResponse>(StatusCodes.Status200OK);

        group.MapPost("/channels/{channelId}/enable", async (string channelId, [FromServices] NotificationService service, CancellationToken ct) =>
        {
            await service.EnableChannelAsync(channelId, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("EnableNotificationChannel")
        .WithSummary("Turns a channel that switched itself off after repeated failures back on.")
        .Produces(StatusCodes.Status204NoContent);

        return routes;
    }
}
