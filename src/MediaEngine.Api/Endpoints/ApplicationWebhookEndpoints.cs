using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Webhooks;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

internal static class ApplicationWebhookEndpoints
{
    public static IEndpointRouteBuilder MapApplicationWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/access/applications/{applicationId:guid}/webhooks")
            .WithTags("Application Webhooks")
            .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityApplicationsWrite)
            .AddEndpointFilter(new WebhookFailureFilter());
        group.MapGet("/event-types", (ApplicationWebhookService service) => Results.Ok(service.EventTypes))
            .WithName("ListWebhookEventTypes").Produces<IReadOnlyList<string>>();
        group.MapGet("/", async (Guid applicationId, ApplicationWebhookService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(applicationId, ct)))
            .WithName("ListApplicationWebhooks").Produces<IReadOnlyList<ApplicationWebhookResponse>>();
        group.MapPost("/", async (Guid applicationId, SaveApplicationWebhookRequest request, HttpContext http,
            IRequestAuthorityResolver authorityResolver, ApplicationWebhookService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var authority = await authorityResolver.ResolveAsync(http, ct);
            return Results.Ok(await service.SaveAsync(applicationId, null, request, authority, ct));
        }).WithName("CreateApplicationWebhook").Produces<ApplicationWebhookSecretResponse>()
            .Produces<ApprovalRequiredResponse>(StatusCodes.Status409Conflict);
        group.MapPut("/{webhookId:guid}", async (Guid applicationId, Guid webhookId, SaveApplicationWebhookRequest request,
            HttpContext http, IRequestAuthorityResolver authorityResolver, ApplicationWebhookService service, CancellationToken ct) =>
        {
            var authority = await authorityResolver.ResolveAsync(http, ct);
            return Results.Ok(await service.SaveAsync(applicationId, webhookId, request, authority, ct));
        }).WithName("UpdateApplicationWebhook").Produces<ApplicationWebhookSecretResponse>()
            .Produces<ApprovalRequiredResponse>(StatusCodes.Status409Conflict);
        group.MapPost("/{webhookId:guid}/rotate", async (Guid applicationId, Guid webhookId, HttpContext http,
            ApplicationWebhookService service, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.RotateAsync(applicationId, webhookId, ct));
        }).WithName("RotateWebhookSigningSecret").Produces<ApplicationWebhookSecretResponse>();
        group.MapGet("/{webhookId:guid}/attempts", async (Guid applicationId, Guid webhookId, int? limit,
            ApplicationWebhookService service, CancellationToken ct) =>
            Results.Ok(await service.ListAttemptsAsync(applicationId, webhookId,
                PagedRequest.From(null, limit, ApplicationWebhookService.DefaultAttemptCount, ApplicationWebhookService.MaximumAttemptCount).Limit, ct)))
            .WithName("ListApplicationWebhookAttempts").Produces<IReadOnlyList<ApplicationWebhookAttemptResponse>>();
        group.MapPost("/{webhookId:guid}/test", async (Guid applicationId, Guid webhookId,
            ApplicationWebhookTestSender sender, CancellationToken ct) =>
            Results.Ok(await sender.SendAsync(applicationId, webhookId, ct)))
            .WithName("SendApplicationWebhookTest").Produces<ApplicationWebhookAttemptResponse>()
            .Produces(StatusCodes.Status409Conflict);
        group.MapDelete("/{webhookId:guid}", async (Guid applicationId, Guid webhookId, ApplicationWebhookService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(applicationId, webhookId, ct);
            return Results.NoContent();
        }).WithName("DeleteApplicationWebhook").Produces(StatusCodes.Status204NoContent);
        return routes;
    }

    private sealed class WebhookFailureFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try { return await next(context); }
            catch (ApprovalRequiredException ex) { return ApiErrors.ApprovalRequired(ex.Approvals); }
            catch (WebhookDestinationException) { return ApiErrors.BadRequest("Use an HTTPS destination without credentials, query parameters, or a fragment. Private-network receivers require explicit approval; host and metadata addresses are blocked."); }
            catch (KeyNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return ApiErrors.Conflict(ex.Message); }
        }
    }
}
