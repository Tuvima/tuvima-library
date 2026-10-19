using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Previews and history of approvals (Developer tools spec D-C). Server administrators only: a signed-in person who is an
/// effective administrator always holds the permission behind each item's own action (plugins.manage, ai.manage,
/// identity.applications.write), so this one policy is the strictest that applies. Applications get their preview from the
/// approval-required 409 of the action itself.
/// </summary>
internal static class ApprovalEndpoints
{
    internal static RouteGroupBuilder MapApprovalEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/approvals")
            .WithTags("Approvals")
            .RequireEffectiveAdministrator();

        group.MapPost("/preview", PreviewAsync)
            .WithName("PreviewApproval")
            .WithSummary("Describe, in plain words, what an item can do and whether anything leaves the machine, with the hash to confirm it.")
            .Produces<ApprovalPreviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", ListAsync)
            .WithName("ListApprovals")
            .WithSummary("The recorded approvals for one item, newest first.")
            .Produces<IReadOnlyList<ApprovalRecordResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }

    internal static async Task<IResult> PreviewAsync(
        ApprovalPreviewRequest request,
        HttpContext http,
        [FromServices] ApprovalService approvals,
        [FromServices] IRequestAuthorityResolver authorityResolver,
        [FromServices] ApplicationAdministrationService applications,
        [FromServices] PluginCatalog plugins,
        [FromServices] IModelDownloadManager downloads,
        CancellationToken ct)
    {
        try
        {
            var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
            var preview = request.Kind switch
            {
                ApprovalKinds.Plugin => approvals.PreviewPlugin(FindPlugin(plugins, request)),
                ApprovalKinds.AiFeature => approvals.PreviewAiFeature(RequireItemId(request), ApprovalService.IsFeatureModelReady(downloads)),
                ApprovalKinds.ApplicationCredential => await PreviewCredentialAsync(request, authority, approvals, applications, ct).ConfigureAwait(false),
                ApprovalKinds.Webhook => PreviewWebhook(request, approvals),
                _ => throw new ArgumentException($"Unknown kind '{request.Kind}'."),
            };
            return Results.Ok(preview);
        }
        catch (ArgumentException ex)
        {
            return ApiErrors.BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return ApiErrors.NotFound(ex.Message);
        }
        catch (ApplicationAdministrationException ex)
        {
            return ApplicationEndpoints.MapError(ex);
        }
    }

    private static PluginRegistration FindPlugin(PluginCatalog plugins, ApprovalPreviewRequest request)
    {
        var id = RequireItemId(request);
        return plugins.Get(id) ?? throw new KeyNotFoundException($"Plugin '{id}' not found.");
    }

    private static async Task<ApprovalPreviewResponse> PreviewCredentialAsync(
        ApprovalPreviewRequest request,
        RequestAuthority authority,
        ApprovalService approvals,
        ApplicationAdministrationService applications,
        CancellationToken ct)
    {
        var input = request.Credential ?? throw new ArgumentException("Name the application the key is for.");
        var application = await applications.GetApplicationAsync(authority, input.ApplicationId, ct).ConfigureAwait(false);
        return approvals.PreviewCredential(application, input.ExpiresAt);
    }

    private static ApprovalPreviewResponse PreviewWebhook(ApprovalPreviewRequest request, ApprovalService approvals)
    {
        var input = request.Webhook ?? throw new ArgumentException("Describe the webhook to preview.");
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var receiver) || string.IsNullOrEmpty(receiver.Host))
        {
            throw new ArgumentException("Enter the receiver's full web address.");
        }

        return approvals.PreviewWebhook(receiver, input.EventTypes ?? [], input.AllowLocalNetwork, request.ItemId?.Trim() ?? "");
    }

    private static string RequireItemId(ApprovalPreviewRequest request) =>
        string.IsNullOrWhiteSpace(request.ItemId) ? throw new ArgumentException("Name the item to preview.") : request.ItemId.Trim();

    internal static async Task<IResult> ListAsync(
        [FromQuery] string? kind,
        [FromQuery(Name = "item_id")] string? itemKey,
        [FromServices] ApprovalService approvals,
        [FromServices] PluginCatalog plugins,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(itemKey))
        {
            return ApiErrors.BadRequest("Give both kind and item_id.");
        }

        if (kind is not (ApprovalKinds.Plugin or ApprovalKinds.AiFeature or ApprovalKinds.ApplicationCredential or ApprovalKinds.Webhook))
        {
            return ApiErrors.BadRequest($"Unknown kind '{kind}'.");
        }

        // Plugin ids are matched without regard to case elsewhere; records are stored under the manifest's own spelling.
        var itemId = kind == ApprovalKinds.Plugin
            ? plugins.Get(itemKey)?.Manifest.Id ?? itemKey.Trim()
            : itemKey.Trim();
        return Results.Ok(await approvals.ListAsync(kind, itemId, ct).ConfigureAwait(false));
    }
}
